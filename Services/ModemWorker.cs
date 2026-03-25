using GsmAgent.Models;
using System.Collections.Concurrent;

namespace GsmAgent.Services;

/// <summary>
/// ModemWorker — 1 thread riêng cho mỗi modem.
/// Xử lý hàng đợi SMS, retry logic, cooldown.
/// Tương tự PortWorker trong simsmart-gsm (Java).
/// </summary>
public class ModemWorker : IDisposable
{
    private readonly AtCommandHelper _helper;
    private readonly BlockingCollection<SmsTask> _queue = new(100);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly SimCard _sim;
    private readonly Action<string, string, bool, string?> _onSmsResult;
    private readonly Action<SimCard> _onSimUpdated;
    private readonly Action<SmsTask>? _onRetryNeeded; // retry on different SIM
    private readonly Action<string, string, string, DateTime>? _onIncomingSms; // comPort, sender, content, time
    private readonly int _cooldownMs;
    private readonly int _maxRetries;
    private bool _disposed;

    public SimCard Sim => _sim;
    public bool IsRunning { get; private set; }
    public int QueueSize => _queue.Count;

    public ModemWorker(
        SimCard sim,
        int cooldownMs = 2000,
        int maxRetries = 3,
        Action<string, string, bool, string?>? onSmsResult = null,
        Action<SimCard>? onSimUpdated = null,
        Action<SmsTask>? onRetryNeeded = null,
        Action<string, string, string, DateTime>? onIncomingSms = null)
    {
        _sim = sim;
        _cooldownMs = cooldownMs;
        _maxRetries = maxRetries;
        _onSmsResult = onSmsResult ?? ((_, _, _, _) => { });
        _onSimUpdated = onSimUpdated ?? (_ => { });
        _onRetryNeeded = onRetryNeeded;
        _onIncomingSms = onIncomingSms;
        _helper = new AtCommandHelper(sim.ComPort);

        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = $"ModemWorker-{sim.ComPort}"
        };
    }

    public bool Start()
    {
        if (!_helper.Open())
        {
            _sim.Status = SimStatus.Offline;
            return false;
        }

        // Enable URC for incoming SMS detection
        _helper.EnableUrc();

        IsRunning = true;
        _sim.Status = SimStatus.Online;
        _thread.Start();
        return true;
    }

    public void EnqueueSms(SmsTask task)
    {
        _queue.Add(task);
        _sim.QueueSize = _queue.Count;
    }

    public void Stop()
    {
        IsRunning = false;
        _cts.Cancel();
        _queue.CompleteAdding();
    }

    private void RunLoop()
    {
        System.Diagnostics.Debug.WriteLine($"▶️ ModemWorker started: {_sim.ComPort}");

        while (IsRunning && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                // Check for incoming SMS (URC)
                if (_helper.CheckForNewSms())
                {
                    ReadIncomingSms();
                }

                // Process send queue
                if (_queue.TryTake(out var task, 500, _cts.Token))
                {
                    ProcessSmsTask(task);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Worker {_sim.ComPort} error: {ex.Message}");
                Thread.Sleep(3000);

                // Try reconnect
                if (!_helper.IsAlive())
                {
                    _helper.Close();
                    Thread.Sleep(1000);
                    if (!_helper.Open())
                    {
                        _sim.Status = SimStatus.Error;
                        _onSimUpdated(_sim);
                        break;
                    }
                }
            }
        }

        _sim.Status = SimStatus.Offline;
        _onSimUpdated(_sim);
        _helper.Close();
        System.Diagnostics.Debug.WriteLine($"⏹ ModemWorker stopped: {_sim.ComPort}");
    }

    private void ProcessSmsTask(SmsTask task)
    {
        _sim.Status = SimStatus.Busy;
        _sim.QueueSize = _queue.Count;
        _onSimUpdated(_sim);

        bool success = false;
        string? error = null;

        try
        {
            // Check rate limit before sending
            if (_sim.IsRateLimited)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"⛔ [{_sim.ComPort}] SIM đạt giới hạn {_sim.DailyLimit} SMS/ngày — skip");
                _onRetryNeeded?.Invoke(task); // try another SIM
                _sim.Status = SimStatus.Online;
                _onSimUpdated(_sim);
                return;
            }

            if (_sim.IsBlocked)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"🚫 [{_sim.ComPort}] SIM bị block đến {_sim.BlockedUntil:HH:mm:ss} — skip");
                _onRetryNeeded?.Invoke(task);
                _sim.Status = SimStatus.Online;
                _onSimUpdated(_sim);
                return;
            }

            System.Diagnostics.Debug.WriteLine(
                $"📤 [{_sim.ComPort}] Sending SMS to {task.DestAddr} (retry #{task.RetryCount}, daily {_sim.DailySentCount}/{_sim.DailyLimit})");

            success = _helper.SendSms(task.DestAddr, task.Content);

            if (success)
            {
                _sim.RecordSuccess();
                System.Diagnostics.Debug.WriteLine(
                    $"✅ [{_sim.ComPort}] SMS sent to {task.DestAddr} (daily: {_sim.DailySentCount}/{_sim.DailyLimit})");
            }
            else
            {
                error = "AT command returned failure";
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            System.Diagnostics.Debug.WriteLine(
                $"❌ [{_sim.ComPort}] Error: {ex.Message}");

            if (!_helper.IsAlive())
            {
                _helper.Close();
                Thread.Sleep(1000);
                _helper.Open();
            }
        }

        _sim.Status = SimStatus.Online;
        _onSimUpdated(_sim);

        if (!success)
        {
            // Retry on DIFFERENT SIM
            task.TriedPorts.Add(_sim.ComPort);
            task.RetryCount++;
            _sim.RecordFailure();

            if (_sim.IsBlocked)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"🚫 [{_sim.ComPort}] SIM blocked do 5 fails liên tiếp — block 10 phút");
            }

            if (task.RetryCount < _maxRetries && _onRetryNeeded != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"🔄 [{_sim.ComPort}] SMS {task.MessageId} FAIL → retry #{task.RetryCount} trên SIM khác (đã thử: {string.Join(",", task.TriedPorts)})");
                _onRetryNeeded(task);
            }
            else
            {
                _sim.TotalFailed++;
                _onSmsResult(task.MessageId, "FAILED", false, error);
                System.Diagnostics.Debug.WriteLine(
                    $"❌ [{_sim.ComPort}] SMS {task.MessageId} FAILED sau {task.RetryCount} lần retry");
            }
        }
        else
        {
            _onSmsResult(task.MessageId, "DELIVERED", true, null);
        }

        // Cooldown between SMS
        Thread.Sleep(_cooldownMs);
    }

    private void ReadIncomingSms()
    {
        try
        {
            var messages = _helper.ReadAllSms();
            foreach (var (sender, content, time) in messages)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"📨 [{_sim.ComPort}] Incoming SMS from {sender}: {content}");
                _onIncomingSms?.Invoke(_sim.ComPort, sender, content, time);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ ReadSMS error: {ex.Message}");
        }
    }

    /// <summary>Refresh SIM info (signal, status).</summary>
    public void RefreshSimInfo()
    {
        if (!_helper.IsOpen) return;
        _sim.SignalLevel = _helper.GetSignalLevel();
        _onSimUpdated(_sim);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _helper.Dispose();
            _queue.Dispose();
            _cts.Dispose();
            _disposed = true;
        }
    }
}

public class SmsTask
{
    public string MessageId { get; set; } = "";
    public string SourceAddr { get; set; } = "";
    public string DestAddr { get; set; } = "";
    public string Content { get; set; } = "";
    public int AccountId { get; set; }
    public string SystemId { get; set; } = "";
    public DateTime QueuedAt { get; set; } = DateTime.Now;
    public int RetryCount { get; set; }
    public HashSet<string> TriedPorts { get; set; } = new();
}
