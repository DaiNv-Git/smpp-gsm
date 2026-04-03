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
    // 🔥 FIX: Adaptive polling — poll nhanh để tránh miss SMS mới
    // - Idle (queue rỗng): poll mỗi 1500ms (từ 3000ms — SMS có thể đến bất cứ lúc nào)
    // - Active (queue có task): poll sau mỗi task → phát hiện SMS mới nhanh
    // - After SMS received: poll lại sau 500ms (SMS có thể đến liên tục)
    private const int SmsScanIntervalIdleMs = 1500;
    private const int SmsScanIntervalActiveMs = 500;
    private readonly AtCommandHelper _helper;
    private readonly BlockingCollection<SmsTask> _queue = new(500); // 🔥 500 capacity for burst traffic
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly SimCard _sim;
    private readonly Action<string, string, bool, string?> _onSmsResult;
    private readonly Action<SimCard> _onSimUpdated;
    private readonly Action<SmsTask>? _onRetryNeeded; // retry on different SIM
    private readonly Func<string, string, string, DateTime, bool>? _onIncomingSms; // comPort, sender, content, time
    private readonly int _cooldownMs;
    private readonly int _maxRetries;
    private bool _disposed;

    public SimCard Sim => _sim;
    public bool IsRunning { get; private set; }
    public int QueueSize => _queue.Count;

    public ModemWorker(
        SimCard sim,
        int cooldownMs = 1500,
        int maxRetries = 3,
        Action<string, string, bool, string?>? onSmsResult = null,
        Action<SimCard>? onSimUpdated = null,
        Action<SmsTask>? onRetryNeeded = null,
        Func<string, string, string, DateTime, bool>? onIncomingSms = null)
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

    private int _lastSmsCount = -1;
    private DateTime _lastSmsCountCheck = DateTime.MinValue;

    // 🔥 Ported from Java PortWorker
    private int _scanCount = 0; // Track scan iterations for periodic cleanup
    private readonly ConcurrentDictionary<string, DateTime> _processedSmsCache = new(); // TTL-based cache
    private static readonly TimeSpan SmsCacheTtl = TimeSpan.FromMinutes(60); // 🔥 Tăng từ 5 → 60 phút tránh lặp tin cũ

    public bool Start()
    {
        if (!_helper.Open())
        {
            _sim.Status = SimStatus.Offline;
            return false;
        }

        // 🔥 Bỏ URC — không đáng tin cậy, dùng polling thích ứng thay thế
        // Tuy nhiên VẪN CẦN BẬT URC để modem biết cách định tuyến SMS và kích hoạt CheckForNewSms nhanh
        _helper.EnableUrc();

        // Đảm bảo SMSC đã được cấu hình — nếu thiếu, SMS sẽ gửi đi nhưng không đến
        _helper.EnsureSmscConfigured();

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

    /// <summary>Dừng worker và đợi thread thật sự kết thúc.</summary>
    public void Stop()
    {
        IsRunning = false;
        _cts.Cancel();
        _queue.CompleteAdding();
        // 🔥 FIX: Join thread — đợi worker thread thật sự kết thúc trước khi return
        // Caller (SerialPortManager.StopWorkerForPort) cần biết thread đã dừng thật
        _thread.Join(3000);
    }

    private void RunLoop()
    {
        System.Diagnostics.Debug.WriteLine($"▶️ ModemWorker started: {_sim.ComPort}");

        // 🔥 FIX: Adaptive polling — poll NHANH khi queue rỗng (idle), poll CHẬM khi đang active
        // Bỏ URC vì: miss khi modem reset, gộp nhiều SMS thành 1 CMTI
        while (IsRunning && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                // 1. Poll SMS count — interval thích ứng với trạng thái worker
                var elapsed = (DateTime.Now - _lastSmsCountCheck).TotalMilliseconds;
                int interval = _queue.Count > 0 ? SmsScanIntervalActiveMs : SmsScanIntervalIdleMs;

                // 🔥 FIX: Check URC TRƯỚC — phát hiện SMS mới từ buffer SendAndRead
                // Khi gửi SMS nội bộ (SIM A → SIM B cùng hệ thống), URC +CMTI đến
                // trong lúc SendSms đang chạy → bị SendAndRead bắt vào _pendingUrcCount
                // → Nếu không check URC ở đây, tin nhắn sẽ bị miss cho đến poll cycle tiếp theo
                bool urcDetected = _helper.CheckForNewSms();
                if (urcDetected)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"📬 [{_sim.ComPort}] URC detected — đọc SMS ngay!");
                    ReadIncomingSms();
                    _lastSmsCountCheck = DateTime.Now;
                    _lastSmsCount = 0; // Reset baseline sau khi đọc
                }
                else if (elapsed >= interval)
                {
                    _lastSmsCountCheck = DateTime.Now;
                    var currentCount = _helper.GetSmsCount();

                    // Scan khi count TĂNG (có SMS mới đến)
                    // Hoặc count > 0 với baseline đã có (đảm bảo scan lần đầu)
                    if (currentCount > _lastSmsCount || (currentCount > 0 && _lastSmsCount >= 0))
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"📬 [{_sim.ComPort}] SMS count: {_lastSmsCount} → {currentCount}");
                        ReadIncomingSms();
                        _lastSmsCount = currentCount;
                    }
                    else if (currentCount == 0)
                    {
                        // Count về 0 → đã xử lý hết trong scan trước, reset baseline
                        _lastSmsCount = 0;
                    }
                }

                // 2. Process send queue — non-blocking
                if (_queue.TryTake(out var task, 500, _cts.Token))
                {
                    ProcessSmsTask(task);

                    // 🔥 Check URC + SMS count ngay sau gửi xong
                    // SMS nội bộ có thể đến ngay lập tức khi SIM khác vừa gửi xong
                    if (_helper.CheckForNewSms())
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"📬 [{_sim.ComPort}] URC after send — đọc SMS ngay!");
                        ReadIncomingSms();
                    }
                    else
                    {
                        _lastSmsCount = _helper.GetSmsCount();
                        if (_lastSmsCount > 0)
                            ReadIncomingSms();
                    }
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
                task.TriedPorts.Add(_sim.ComPort);
                task.RetryCount++;
                if (task.AllowRedispatch && task.RetryCount < _maxRetries && _onRetryNeeded != null)
                    _onRetryNeeded(task);
                else
                    task.CompletionSource?.TrySetResult(false);
                _sim.Status = SimStatus.Online;
                _onSimUpdated(_sim);
                return;
            }

            if (_sim.IsBlocked)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"🚫 [{_sim.ComPort}] SIM bị block đến {_sim.BlockedUntil:HH:mm:ss} — skip");
                task.TriedPorts.Add(_sim.ComPort);
                task.RetryCount++;
                if (task.AllowRedispatch && task.RetryCount < _maxRetries && _onRetryNeeded != null)
                    _onRetryNeeded(task);
                else
                    task.CompletionSource?.TrySetResult(false);
                _sim.Status = SimStatus.Online;
                _onSimUpdated(_sim);
                return;
            }

            System.Diagnostics.Debug.WriteLine(
                $"📤 [{_sim.ComPort}] Sending SMS to {task.DestAddr} (retry #{task.RetryCount}, daily {_sim.DailySentCount}/{_sim.DailyLimit})");

            success = _helper.SendSms(task.DestAddr, task.Content);

            // 🔥 Restore UCS2 cho đọc SMS tiếng Nhật (giống Java: setCharset("UCS2"))
            _helper.RestoreUcs2Mode();

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

            if (task.AllowRedispatch && task.RetryCount < _maxRetries && _onRetryNeeded != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"🔄 [{_sim.ComPort}] SMS {task.MessageId} FAIL → retry #{task.RetryCount} trên SIM khác (đã thử: {string.Join(",", task.TriedPorts)})");
                task.CompletionSource?.TrySetResult(false); // 🔥 Unblock manual sender ngay
                task.CompletionSource = null; // Tránh set lại lần nữa ở SIM khác
                _onRetryNeeded(task);
            }
            else
            {
                _onSmsResult(task.MessageId, "FAILED", false, error);
                System.Diagnostics.Debug.WriteLine(
                    $"❌ [{_sim.ComPort}] SMS {task.MessageId} FAILED sau {task.RetryCount} lần retry");
                task.CompletionSource?.TrySetResult(false); // 🆕 Notify manual sender
            }
        }
        else
        {
            _onSmsResult(task.MessageId, "SENT", true, null);
            task.CompletionSource?.TrySetResult(true); // 🆕 Notify manual sender
        }

        // Cooldown between SMS
        Thread.Sleep(_cooldownMs);
    }

    /// <summary>
    /// 🔥 SMS scan — chỉ đọc UNREAD, xóa tin đã đọc định kỳ.
    /// 1. Dual storage: scan cả ME + SM
    /// 2. Chỉ đọc UNREAD (không dùng ALL)
    /// 3. Periodic cleanup mỗi 20 scans — xóa tất cả tin đã đọc
    /// 4. TTL-based duplicate cache (60 phút)
    /// </summary>
    private void ReadIncomingSms()
    {
        try
        {
            _scanCount++;

            // 🧹 Periodic cleanup: xóa tất cả SMS đã đọc mỗi 20 scans
            if (_scanCount % 20 == 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"🧹 [{_sim.ComPort}] Periodic cleanup — xóa SMS đã đọc (scan #{_scanCount})");
                foreach (var storage in new[] { "ME", "SM" })
                {
                    if (_helper.SetStorage(storage))
                        _helper.CleanupReadSms();
                }

                // Cleanup expired cache entries
                var expired = _processedSmsCache
                    .Where(kv => DateTime.Now - kv.Value > SmsCacheTtl)
                    .Select(kv => kv.Key).ToList();
                foreach (var key in expired)
                    _processedSmsCache.TryRemove(key, out _);
            }

            var allMessages = new List<(string storage, int index, string sender, string content, DateTime time)>();

            // 🔥 Scan cả ME + SM storage — mỗi storage cần PrepareForRead riêng
            foreach (var storage in new[] { "ME", "SM" })
            {
                try
                {
                    if (!_helper.SetStorage(storage))
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"⚠️ [{_sim.ComPort}] Cannot set storage to {storage}");
                        continue;
                    }

                    // 🔥 FIX: Gọi PrepareForRead TRƯỚC mỗi storage — modem có thể reset mode khi switch storage
                    _helper.PrepareForRead();
                    var smsInStore = _helper.ListUnreadSms(5000);

                    allMessages.AddRange(smsInStore.Select(m => (storage, m.index, m.sender, m.content, m.time)));
                    if (smsInStore.Count > 0)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"📬 [{_sim.ComPort}] Found {smsInStore.Count} UNREAD SMS in {storage}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"⚠️ [{_sim.ComPort}] Error scanning {storage}: {ex.Message}");
                }
            }

            if (allMessages.Count == 0)
            {
                // Không có SMS → vẫn update baseline count để tránh spam scan
                _lastSmsCount = Math.Max(0, _lastSmsCount);
                return;
            }

            int processedCount = 0;

            string? activeStorage = null;
            var occurrenceMap = new Dictionary<string, int>();

            bool EnsureStorage(string storage)
            {
                if (activeStorage == storage)
                    return true;

                if (_helper.SetStorage(storage))
                {
                    activeStorage = storage;
                    // Re-prepare sau khi switch storage
                    _helper.PrepareForRead();
                    return true;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"⚠️ [{_sim.ComPort}] Cannot switch back to storage {storage} for delete");
                return false;
            }

            foreach (var (storage, index, sender, content, time) in allMessages)
            {
                var normalizedSender = NormalizeSender(sender);
                var normalizedContent = NormalizeContent(content);
                var exactTime = time.ToUniversalTime().ToString("yyyyMMddHHmmss");
                
                var baseKey = $"{normalizedSender}|{normalizedContent}|{exactTime}";
                
                if (!occurrenceMap.ContainsKey(baseKey))
                    occurrenceMap[baseKey] = 0;
                
                int occurrence = occurrenceMap[baseKey]++;
                var cacheKey = $"{baseKey}#{occurrence}";

                if (_processedSmsCache.ContainsKey(cacheKey))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"⏭️ [{_sim.ComPort}] Skip duplicate SMS (already processed): {sender} | {content.Substring(0, Math.Min(30, content.Length))}...");
                    if (EnsureStorage(storage))
                        _helper.DeleteSms(index);
                    continue;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"📨 [{_sim.ComPort}] SMS from {sender}: {content}");
                var accepted = _onIncomingSms?.Invoke(_sim.ComPort, sender, content, time) ?? true;
                if (!accepted)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"⚠️ [{_sim.ComPort}] Failed to persist incoming SMS, keeping index {index} in {storage}");
                    continue;
                }

                _processedSmsCache[cacheKey] = DateTime.Now;
                processedCount++;

                // Delete SMS only after it has been persisted to local forward queue
                if (EnsureStorage(storage))
                    _helper.DeleteSms(index);
            }

            // 🔥 Luôn update count baseline sau scan — để next poll cycle không re-scan
            // Dùng count từ storage cuối cùng đã scan (chính xác hơn gọi GetSmsCount riêng)
            _lastSmsCount = 0; // Sau khi xóa hết unread trong list → count về 0
            if (processedCount > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"✅ [{_sim.ComPort}] Processed {processedCount}/{allMessages.Count} SMS");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ [{_sim.ComPort}] ReadSMS error: {ex.Message}");
        }
    }

    // BuildDuplicateKeys method removed since deduplication logic is now inline

    private static string NormalizeSender(string sender)
    {
        if (string.IsNullOrWhiteSpace(sender))
            return "";

        var digits = new string(sender.Where(char.IsDigit).ToArray());
        if (digits.Length >= 6)
        {
            if (digits.StartsWith("81") && !sender.TrimStart().StartsWith("+", StringComparison.Ordinal))
                return "+" + digits;
            if (sender.Contains('+'))
                return "+" + digits;
            return digits;
        }

        return sender.Trim();
    }

    private static string NormalizeContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";

        return string.Join(
            " ",
            content
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Trim();
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
    public bool AllowRedispatch { get; set; } = true;

    /// <summary>🆕 Nếu set, ProcessSmsTask sẽ complete khi gửi xong (cho manual SMS await kết quả).</summary>
    public TaskCompletionSource<bool>? CompletionSource { get; set; }
}
