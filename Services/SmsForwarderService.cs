using Microsoft.Data.Sqlite;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using System.Collections.Concurrent;
using System.IO;

namespace GsmAgent.Services;

/// <summary>
/// 📤 Forward SMS nhận được lên API server — xử lý tuần tự qua queue.
/// - Nhiều SMS đến cùng lúc → enqueue → xử lý 1-by-1
/// - Retry tối đa 3 lần nếu API lỗi
/// - Delay 500ms giữa các request tránh rate limit
/// </summary>
public partial class SmsForwarderService : IDisposable
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private const string API_URL = "https://be-user-sms-global.smsglobalhub.com/api/seller/receive-sms";
    private const int MAX_RETRIES = 3;
    private const int DELAY_BETWEEN_MS = 500; // delay giữa các request
    private const int REQUEUE_FAILED_DELAY_MS = 30000;
    private readonly string _dbPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "sms-forward-queue.db");

    // 🔥 Queue xử lý tuần tự (thread-safe, unbounded)
    private readonly Channel<SmsForwardTask> _channel = Channel.CreateUnbounded<SmsForwardTask>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processingTask;
    private readonly ConcurrentDictionary<long, byte> _scheduledIds = new();
    private readonly object _dbLock = new();

    // Event để notify UI khi forward xong
    public event Action<string, bool, string>? OnForwardResult; // sender, success, message

    public int PendingCount => _channel.Reader.Count;

    public SmsForwarderService()
    {
        InitializeStore();
        LoadPendingTasks();
        // Bắt đầu background worker xử lý queue
        _processingTask = Task.Run(ProcessQueueAsync);
        System.Diagnostics.Debug.WriteLine("📤 SmsForwarderService started (queue mode)");
    }

    /// <summary>
    /// Enqueue SMS để forward — không block, xử lý tuần tự trong background.
    /// </summary>
    public bool Enqueue(string sender, string receiver, string message, string comPort)
    {
        try
        {
            var task = SaveTask(sender, receiver, message, comPort);
            if (_channel.Writer.TryWrite(task))
            {
                _scheduledIds[task.Id] = 0;
                System.Diagnostics.Debug.WriteLine(
                    $"📥 [{comPort}] SMS queued for forward: {sender} → API (pending: {PendingCount})");
                return true;
            }

            System.Diagnostics.Debug.WriteLine(
                $"❌ [{comPort}] Failed to queue SMS forward (channel closed)");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"❌ [{comPort}] Persist SMS forward failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Background worker — đọc queue và xử lý 1-by-1 với retry.
    /// </summary>
    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var task in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                _scheduledIds.TryRemove(task.Id, out _);
                bool success = false;
                string resultMsg = "";

                for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
                {
                    try
                    {
                        var payload = new
                        {
                            sender = task.Sender,
                            receiver = task.Receiver,
                            message = task.Message
                        };

                        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                        {
                            // 🔥 FIX: Giữ nguyên ký tự Unicode (tiếng Việt/Nhật) thay vì escape \uXXXX
                            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                        });
                        var content = new StringContent(json, Encoding.UTF8, "application/json");

                        System.Diagnostics.Debug.WriteLine(
                            $"📤 [{task.ComPort}] Forward attempt {attempt}/{MAX_RETRIES}: {task.Sender} → API");

                        var response = await _http.PostAsync(API_URL, content, _cts.Token);
                        var responseBody = await response.Content.ReadAsStringAsync();

                        if (response.IsSuccessStatusCode)
                        {
                            success = true;
                            resultMsg = $"✅ Forwarded: {(int)response.StatusCode}";
                            DeleteTask(task.Id);
                            System.Diagnostics.Debug.WriteLine(
                                $"✅ [{task.ComPort}] SMS forwarded OK: {task.Sender} → {(int)response.StatusCode}");
                            break; // Thành công → thoát retry loop
                        }
                        else
                        {
                            resultMsg = $"❌ API {(int)response.StatusCode}: {responseBody}";
                            System.Diagnostics.Debug.WriteLine(
                                $"⚠️ [{task.ComPort}] Forward attempt {attempt} failed: {(int)response.StatusCode} | {responseBody}");
                        }
                    }
                    catch (TaskCanceledException) when (!_cts.Token.IsCancellationRequested)
                    {
                        resultMsg = "❌ Timeout";
                        System.Diagnostics.Debug.WriteLine(
                            $"⚠️ [{task.ComPort}] Forward attempt {attempt} timeout");
                    }
                    catch (Exception ex)
                    {
                        resultMsg = $"❌ {ex.Message}";
                        System.Diagnostics.Debug.WriteLine(
                            $"⚠️ [{task.ComPort}] Forward attempt {attempt} error: {ex.Message}");
                    }

                    // Retry delay (exponential: 1s, 2s, 4s)
                    if (attempt < MAX_RETRIES)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                        System.Diagnostics.Debug.WriteLine(
                            $"🔄 [{task.ComPort}] Retrying in {delay.TotalSeconds}s...");
                        await Task.Delay(delay, _cts.Token);
                    }
                }

                if (!success)
                {
                    UpdateTaskFailure(task.Id, resultMsg);
                    ScheduleRetry(task.Id);
                    System.Diagnostics.Debug.WriteLine(
                        $"❌ [{task.ComPort}] SMS forward FAILED after {MAX_RETRIES} retries: {task.Sender} | {task.Message}");
                }

                // Notify UI
                OnForwardResult?.Invoke(task.Sender, success, resultMsg);

                // Delay giữa các request tránh rate limit
                await Task.Delay(DELAY_BETWEEN_MS, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("📤 SmsForwarderService stopped");
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { _processingTask.Wait(3000); } catch { }
        _cts.Dispose();
    }
}

/// <summary>Dữ liệu SMS cần forward.</summary>
public class SmsForwardTask
{
    public long Id { get; set; }
    public string Sender { get; set; } = "";
    public string Receiver { get; set; } = "";
    public string Message { get; set; } = "";
    public string ComPort { get; set; } = "";
    public DateTime QueuedAt { get; set; }
}

partial class SmsForwarderService
{
    private void InitializeStore()
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS sms_forward_queue (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    sender TEXT NOT NULL,
                    receiver TEXT NOT NULL,
                    message TEXT NOT NULL,
                    com_port TEXT NOT NULL,
                    queued_at TEXT NOT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    last_error TEXT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }
    }

    private void LoadPendingTasks()
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, sender, receiver, message, com_port, queued_at
                FROM sms_forward_queue
                ORDER BY id;
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var task = new SmsForwardTask
                {
                    Id = reader.GetInt64(0),
                    Sender = reader.GetString(1),
                    Receiver = reader.GetString(2),
                    Message = reader.GetString(3),
                    ComPort = reader.GetString(4),
                    QueuedAt = DateTime.TryParse(reader.GetString(5), out var queuedAt)
                        ? queuedAt
                        : DateTime.Now
                };

                if (_scheduledIds.TryAdd(task.Id, 0))
                    _channel.Writer.TryWrite(task);
            }
        }
    }

    private SmsForwardTask SaveTask(string sender, string receiver, string message, string comPort)
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sms_forward_queue(sender, receiver, message, com_port, queued_at)
                VALUES ($sender, $receiver, $message, $comPort, $queuedAt);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$sender", sender);
            cmd.Parameters.AddWithValue("$receiver", receiver);
            cmd.Parameters.AddWithValue("$message", message);
            cmd.Parameters.AddWithValue("$comPort", comPort);
            cmd.Parameters.AddWithValue("$queuedAt", DateTime.Now.ToString("O"));

            var id = (long)(cmd.ExecuteScalar() ?? 0L);
            return new SmsForwardTask
            {
                Id = id,
                Sender = sender,
                Receiver = receiver,
                Message = message,
                ComPort = comPort,
                QueuedAt = DateTime.Now,
            };
        }
    }

    private void DeleteTask(long id)
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM sms_forward_queue WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    private void UpdateTaskFailure(long id, string error)
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE sms_forward_queue
                SET attempts = attempts + 1,
                    last_error = $error
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$error", error);
            cmd.ExecuteNonQuery();
        }
    }

    private void ScheduleRetry(long id)
    {
        if (!_scheduledIds.TryAdd(id, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(REQUEUE_FAILED_DELAY_MS, _cts.Token);
                var task = TryGetTask(id);
                if (task != null && !_cts.Token.IsCancellationRequested)
                    _channel.Writer.TryWrite(task);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _scheduledIds.TryRemove(id, out _);
            }
        });
    }

    private SmsForwardTask? TryGetTask(long id)
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, sender, receiver, message, com_port, queued_at
                FROM sms_forward_queue
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            return new SmsForwardTask
            {
                Id = reader.GetInt64(0),
                Sender = reader.GetString(1),
                Receiver = reader.GetString(2),
                Message = reader.GetString(3),
                ComPort = reader.GetString(4),
                QueuedAt = DateTime.TryParse(reader.GetString(5), out var queuedAt)
                    ? queuedAt
                    : DateTime.Now
            };
        }
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}
