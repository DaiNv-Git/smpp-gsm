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
    private const int SmsScanIntervalMs = 2000; // 🔥 Giảm từ 5000 → 2000 để phát hiện SMS mới nhanh hơn
    private readonly AtCommandHelper _helper;
    private readonly BlockingCollection<SmsTask> _queue = new(100);
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
        int cooldownMs = 2000,
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

    private bool _urcSupported;
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

        // Test URC support → fallback to polling if not supported
        _urcSupported = _helper.TestUrcSupport();
        if (_urcSupported)
        {
            _helper.EnableUrc();
            System.Diagnostics.Debug.WriteLine($"✅ [{_sim.ComPort}] URC supported — real-time SMS detection");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"⚠️ [{_sim.ComPort}] URC not supported — using Simulated URC (SMS count polling)");
        }

        // 🔥 Đảm bảo SMSC đã được cấu hình — nếu thiếu, SMS sẽ gửi đi nhưng không đến
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
                bool hasNewSms = false;

                // Ưu tiên URC khi modem có phát
                if (_urcSupported)
                    hasNewSms = _helper.CheckForNewSms();

                // 🔥 FIX: Luôn scan khi count > 0 — duplicate được filter bởi _processedSmsCache (TTL 60 phút)
                // Cũ: count != _lastSmsCount → bỏ sót SMS khi count giữ nguyên (1 đến + 1 xóa cùng lúc)
                if (!hasNewSms &&
                    (DateTime.Now - _lastSmsCountCheck).TotalMilliseconds >= SmsScanIntervalMs)
                {
                    _lastSmsCountCheck = DateTime.Now;
                    var currentCount = _helper.GetSmsCount();
                    if (currentCount > 0)
                    {
                        if (currentCount != _lastSmsCount)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"📬 [{_sim.ComPort}] SMS count: {_lastSmsCount} → {currentCount}");
                        }
                        _lastSmsCount = currentCount;
                        hasNewSms = true;
                    }
                    else if (currentCount == 0 && _lastSmsCount > 0)
                    {
                        _lastSmsCount = 0; // Reset baseline
                    }
                }

                if (hasNewSms)
                {
                    ReadIncomingSms();

                    // 🔥 Re-check ngay: SMS có thể đến trong lúc đang scan (10s+ dual-storage)
                    // Loop tối đa 2 lần tránh infinite loop
                    for (int recheck = 0; recheck < 2; recheck++)
                    {
                        if (_helper.CheckForNewSms())
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"📬 [{_sim.ComPort}] Re-check #{recheck + 1}: thêm SMS mới đến trong lúc scan!");
                            ReadIncomingSms();
                        }
                        else break;
                    }
                }

                // Process send queue — giảm block time từ 500ms → 100ms
                // để poll incoming SMS thường xuyên hơn
                if (_queue.TryTake(out var task, 100, _cts.Token))
                {
                    ProcessSmsTask(task);

                    // 🔥 Check incoming SMS ngay sau khi gửi xong (không đợi poll cycle tiếp)
                    // Fix: cũ block 30s+ khi gửi → bỏ lỡ incoming SMS
                    if (_helper.CheckForNewSms())
                        ReadIncomingSms();
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
    /// 4. TTL-based duplicate cache (5 phút)
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
            var scannedStorages = new HashSet<string>(StringComparer.Ordinal);

            // Set text mode + UCS2 charset 1 lần trước khi scan
            _helper.PrepareForRead();

            // 🔥 Chỉ đọc UNREAD — không fallback ALL
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

                    scannedStorages.Add(storage);
                    var smsInStore = _helper.ListUnreadSms(3000); // 🔥 Giảm timeout từ 5000 → 3000ms

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
                return;

            int processedCount = 0;

            string? activeStorage = null;
            var seenThisScan = new HashSet<string>(StringComparer.Ordinal);

            bool EnsureStorage(string storage)
            {
                if (activeStorage == storage)
                    return true;

                if (_helper.SetStorage(storage))
                {
                    activeStorage = storage;
                    return true;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"⚠️ [{_sim.ComPort}] Cannot switch back to storage {storage} for delete");
                return false;
            }

            foreach (var (storage, index, sender, content, time) in allMessages)
            {
                var cacheKeys = BuildDuplicateKeys(sender, content, time);
                if (cacheKeys.Any(seenThisScan.Contains) || cacheKeys.Any(_processedSmsCache.ContainsKey))
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

                foreach (var key in cacheKeys)
                {
                    seenThisScan.Add(key);
                    _processedSmsCache[key] = DateTime.Now;
                }
                processedCount++;

                // Delete SMS only after it has been persisted to local forward queue
                if (EnsureStorage(storage))
                    _helper.DeleteSms(index);
            }

            // 🔥 Bỏ CleanupReadSms() sau mỗi scan — chỉ giữ periodic cleanup (mỗi 20 scans)
            // Fix: AT+CMGD=1,3 có thể xóa SMS mới đến trong lúc đang scan

            // Luôn update count baseline sau scan (không chỉ khi processedCount > 0)
            // để tránh race condition khi SMS mới đến đúng lúc scan
            _lastSmsCount = Math.Max(0, _helper.GetSmsCount());
            if (processedCount > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"✅ [{_sim.ComPort}] Processed {processedCount}/{allMessages.Count} SMS (remaining: {_lastSmsCount})");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ [{_sim.ComPort}] ReadSMS error: {ex.Message}");
        }
    }

    private static string[] BuildDuplicateKeys(string sender, string content, DateTime time)
    {
        var normalizedSender = NormalizeSender(sender);
        var normalizedContent = NormalizeContent(content);
        var exactTime = time.ToUniversalTime().ToString("yyyyMMddHHmmss");

        // Một số modem duplicate cùng SMS giữa ME/SM nhưng timestamp lệch nhẹ vài chục giây.
        var fuzzyBucket = time.ToUniversalTime().ToString("yyyyMMddHHmm");

        return
        [
            $"{normalizedSender}|{normalizedContent}|{exactTime}",
            $"{normalizedSender}|{normalizedContent}|{fuzzyBucket}"
        ];
    }

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
