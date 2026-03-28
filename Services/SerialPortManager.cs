using System;
using GsmAgent.Models;
using System.Collections.Concurrent;
using System.IO.Ports;

namespace GsmAgent.Services;

/// <summary>
/// SerialPortManager — Scan COM ports, phát hiện modem GSM, quản lý workers.
/// Tương tự SimSyncService trong simsmart-gsm (Java).
/// </summary>
public class SerialPortManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ModemWorker> _workers = new();
    private readonly ConcurrentDictionary<string, SimCard> _sims = new();
    private readonly AppSettings _settings;
    private bool _scanning;
    private MongoDbService? _mongoDb;

    public IReadOnlyDictionary<string, SimCard> Sims => _sims;
    public IReadOnlyDictionary<string, ModemWorker> Workers => _workers;
    public bool IsScanning => _scanning;
    public string LastScanStats { get; private set; } = "";

    public event Action<SimCard>? SimUpdated;
    public event Action<SimCard>? SimScanned; // 🆕 Fired immediately when each SIM is scanned (realtime)
    public event Action<List<SimCard>>? ScanCompleted;
    public event Action<string, string, DateTime>? IncomingSms; // sender, content, time
    public event Action<string, string, bool, string?>? SmsResult; // messageId, status, success, error

    // 📤 SMS Forwarder — queue xử lý tuần tự (shared across all workers)
    private readonly SmsForwarderService _forwarder = new();
    public SmsForwarderService Forwarder => _forwarder;

    public SerialPortManager(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>Inject MongoDbService để lookup phone number trực tiếp từ DB khi scan.</summary>
    public void SetMongoDb(MongoDbService mongoDb)
    {
        _mongoDb = mongoDb;
    }

    /// <summary>
    /// Scan tất cả COM ports, phát hiện modem GSM.
    /// Logic: mở port → AT → lấy CCID → IMSI → phone → signal → provider.
    /// </summary>
    public async Task<List<SimCard>> ScanAllAsync()
    {
        if (_scanning) return _sims.Values.ToList();
        _scanning = true;

        try
        {
            System.Diagnostics.Debug.WriteLine("🔍 Bắt đầu scan COM ports...");

            // Stop all workers first
            StopAllWorkers();
            await Task.Delay(500);

            var portNames = SerialPort.GetPortNames().Distinct().OrderBy(p => p).ToArray();
            System.Diagnostics.Debug.WriteLine($"📋 Tìm thấy {portNames.Length} COM ports");

            var newSims = new ConcurrentBag<SimCard>();
            var failedPorts = new ConcurrentBag<string>();
            int pass1Ok = 0;

            // Pass 1: Scan parallel — fire SimScanned immediately for realtime UI
            var tasks = portNames.Select(port => Task.Run(() =>
            {
                var sim = ScanOnePort(port);
                if (sim != null)
                {
                    newSims.Add(sim);
                    Interlocked.Increment(ref pass1Ok);
                    // 🔥 Progressive loading: push SIM to UI immediately
                    SimScanned?.Invoke(sim);
                    System.Diagnostics.Debug.WriteLine(
                        $"✅ {port}: CCID={sim.Ccid?[..Math.Min(10, sim.Ccid.Length)]}... Phone={sim.PhoneNumber ?? "N/A"} Provider={sim.Provider}");
                }
                else
                {
                    failedPorts.Add(port);
                }
            }));

            await Task.WhenAll(tasks);

            // Pass 2: Retry failed ports (like old Java system)
            int retryOk = 0;
            if (failedPorts.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"🔁 SECOND PASS: {failedPorts.Count} ports chưa scan được, retry...");
                await Task.Delay(300);

                var retryTasks = failedPorts.Select(port => Task.Run(() =>
                {
                    var sim = ScanOnePort(port);
                    if (sim != null)
                    {
                        newSims.Add(sim);
                        Interlocked.Increment(ref retryOk);
                        // 🔥 Progressive: push retry results to UI too
                        SimScanned?.Invoke(sim);
                        System.Diagnostics.Debug.WriteLine(
                            $"✅ [RETRY] {port}: CCID={sim.Ccid?[..Math.Min(10, sim.Ccid.Length)]}... Phone={sim.PhoneNumber ?? "N/A"}");
                    }
                }));

                await Task.WhenAll(retryTasks);
            }

            // Update cache
            _sims.Clear();
            foreach (var sim in newSims)
                _sims[sim.ComPort] = sim;

            // 📊 Scan statistics
            int totalPorts = portNames.Length;
            int totalOk = pass1Ok + retryOk;
            int totalFail = totalPorts - totalOk;
            int phoneFound = newSims.Count(s => !string.IsNullOrWhiteSpace(s.PhoneNumber));

            LastScanStats = $"📊 {totalPorts} cổng COM | ✅ {totalOk} thành công | ❌ {totalFail} thất bại | 📱 {phoneFound} có số ĐT";
            System.Diagnostics.Debug.WriteLine(
                $"✅ Scan hoàn tất: {LastScanStats} (pass1={pass1Ok}, retry={retryOk})");

            var result = _sims.Values.ToList();
            ScanCompleted?.Invoke(result);
            return result;
        }
        finally
        {
            _scanning = false;
        }
    }

    private SimCard? ScanOnePort(string comPort)
    {
        using var helper = new AtCommandHelper(comPort, _settings.BaudRate);
        try
        {
            if (!helper.Open()) return null;
            if (!helper.IsAlive()) return null;

            // 1. CCID (required)
            var ccid = helper.GetCcid();
            if (string.IsNullOrWhiteSpace(ccid)) return null;

            // 2. IMSI
            var imsi = helper.GetImsi();

            // 3. Phone number — AT command first
            var phone = helper.DetectPhoneNumber();

            // 4. Fallback: query MongoDB trực tiếp (ưu tiên) hoặc BE API
            // Giống cơ chế của SimSyncService.java: AT command → DB fallback
            string? provider = null;
            if (string.IsNullOrWhiteSpace(phone))
            {
                // 4a. Try MongoDB trực tiếp (nhanh hơn, không cần server)
                if (_mongoDb != null)
                {
                    try
                    {
                        var dbSim = _mongoDb.FindByCcidFuzzy(ccid);
                        if (dbSim != null && !string.IsNullOrWhiteSpace(dbSim.PhoneNumber))
                        {
                            phone = dbSim.PhoneNumber;
                            provider = dbSim.SimProvider;
                            System.Diagnostics.Debug.WriteLine(
                                $"📱 [{comPort}] Số từ MongoDB: {phone} (CCID fuzzy match)");

                            // Ghi số vào SIM phonebook để lần sau AT command đọc được
                            try { helper.WritePhoneToSimPhonebook(phone); }
                            catch { /* ignore */ }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"⚠️ [{comPort}] MongoDB lookup failed: {ex.Message}");
                    }
                }

                // 4b. Fallback: query via BE API (nếu MongoDB không có)
                if (string.IsNullOrWhiteSpace(phone))
                {
                    try
                    {
                        var lookup = LookupSimByCcid(ccid);
                        if (lookup != null)
                        {
                            phone = lookup.Value.phone;
                            provider = lookup.Value.provider;
                            System.Diagnostics.Debug.WriteLine(
                                $"📱 [{comPort}] Số từ API: {phone} (HTTP fallback)");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"⚠️ [{comPort}] API lookup failed: {ex.Message}");
                    }
                }
            }

            // 5. Signal level
            var signal = helper.GetSignalLevel();

            // 6. Provider — IMSI prefix first (giống Java cũ), AT+COPS? là fallback
            // Lý do: AT+COPS? trả về mạng đang kết nối (có thể roaming KDDI)
            //        IMSI prefix trả về nhà mạng phát hành SIM (Docomo/Rakuten) → đúng hơn
            provider ??= AtCommandHelper.DetectProvider(imsi);
            if (provider == "Unknown")
                provider = helper.QueryOperator() ?? "Unknown";

            return new SimCard
            {
                ComPort = comPort,
                Ccid = ccid,
                Imsi = imsi,
                PhoneNumber = phone,
                Provider = provider,
                SignalLevel = signal,
                // SIM is Online if it has CCID (physically present), regardless of phone number
                Status = SimStatus.Online,
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Scan {comPort}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Tra MongoDB qua BE API: GET /api/dashboard/sim-lookup?ccid=xxx&fuzzy=true
    /// Fuzzy mode: match 18 ký tự liên tục của CCID (like old Java system).</summary>
    private (string phone, string? provider)? LookupSimByCcid(string ccid)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            // Fuzzy match: gửi fuzzy=true để BE match 18 ký tự liên tục (CCID có thể khác vài digits đầu/cuối)
            var url = $"{_settings.ServerUrl.TrimEnd('/')}/api/dashboard/sim-lookup?ccid={Uri.EscapeDataString(ccid)}&fuzzy=true";
            var response = http.GetStringAsync(url).Result;
            var json = System.Text.Json.JsonDocument.Parse(response);
            var root = json.RootElement;

            if (root.TryGetProperty("found", out var found) && found.GetBoolean())
            {
                var phone = root.GetProperty("phoneNumber").GetString();
                var provider = root.TryGetProperty("provider", out var p) ? p.GetString() : null;
                if (!string.IsNullOrWhiteSpace(phone))
                    return (phone, provider);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"⚠️ LookupSimByCcid error: {ex.Message}");
        }
        return null;
    }

    /// <summary>Start workers cho tất cả SIM online.</summary>
    public void StartAllWorkers()
    {
        foreach (var sim in _sims.Values.Where(s => s.Status == SimStatus.Online))
        {
            StartWorker(sim);
        }
    }

    public void StartWorker(SimCard sim)
    {
        if (_workers.ContainsKey(sim.ComPort)) return;

        var worker = new ModemWorker(
            sim,
            _settings.SmsCooldownMs,
            _settings.MaxRetries,
            onSmsResult: (msgId, status, success, error) =>
                SmsResult?.Invoke(msgId, status, success, error),
            onSimUpdated: s => SimUpdated?.Invoke(s),
            onRetryNeeded: task =>
            {
                System.Diagnostics.Debug.WriteLine(
                    $"\ud83d\udd04 Re-dispatching {task.MessageId} to different SIM (tried: {string.Join(",", task.TriedPorts)})");
                DispatchSms(task);
            },
            onIncomingSms: (comPort, sender, content, time) =>
            {
                IncomingSms?.Invoke(sender, content, time);

                // 📤 Enqueue SMS để forward lên API (xử lý tuần tự, có retry)
                var receiver = sim.PhoneNumber ?? comPort;
                _forwarder.Enqueue(sender, receiver, content, comPort);
            }
        );

        if (worker.Start())
        {
            _workers[sim.ComPort] = worker;
            System.Diagnostics.Debug.WriteLine($"▶️ Worker started: {sim.ComPort}");
        }
        else
        {
            worker.Dispose();
        }
    }

    /// <summary>Dispatch SMS to best available modem (excluding already-tried ports).</summary>
    public bool DispatchSms(SmsTask task)
    {
        // Find best modem: available (online + not blocked + not rate-limited), NOT in tried list
        var bestWorker = _workers.Values
            .Where(w => w.IsRunning && w.Sim.IsAvailable)
            .Where(w => !task.TriedPorts.Contains(w.Sim.ComPort))
            .Where(w => w.Sim.SignalLevel >= _settings.MinSignalLevel || w.Sim.SignalLevel < 0)
            .OrderBy(w => w.QueueSize)
            .ThenByDescending(w => w.Sim.SignalLevel)
            .FirstOrDefault();

        if (bestWorker == null)
        {
            System.Diagnostics.Debug.WriteLine("⚠️ No available modem for SMS dispatch");
            return false;
        }

        bestWorker.EnqueueSms(task);
        System.Diagnostics.Debug.WriteLine(
            $"📤 Dispatched {task.MessageId} to {bestWorker.Sim.ComPort} (queue={bestWorker.QueueSize})");
        return true;
    }

    /// <summary>
    /// 📤 Gửi SMS thủ công qua 1 SIM cụ thể (theo COM port).
    /// Đợi kết quả thật từ AT command (không return ngay khi enqueue).
    /// </summary>
    public async Task<(bool success, string message)> SendSmsViaPort(string comPort, string destNumber, string content)
    {
        try
        {
            // 1. Gửi qua worker đang chạy — ĐỢI kết quả thật
            if (_workers.TryGetValue(comPort, out var worker) && worker.IsRunning)
            {
                var tcs = new TaskCompletionSource<bool>();
                var task = new SmsTask
                {
                    MessageId = $"MANUAL-{DateTime.Now.Ticks}",
                    DestAddr = destNumber,
                    SourceAddr = _sims.TryGetValue(comPort, out var s) ? s.PhoneNumber ?? "" : "",
                    Content = content,
                    CompletionSource = tcs, // 🆕 Đợi kết quả thật
                };
                worker.EnqueueSms(task);

                // Đợi worker gửi xong (timeout 60s, cancel timer khi hoàn thành)
                using var timeoutCts = new CancellationTokenSource();
                var timeoutTask = Task.Delay(60000, timeoutCts.Token);
                var completed = await Task.WhenAny(tcs.Task, timeoutTask);
                timeoutCts.Cancel(); // Cancel timer nếu tcs hoàn thành trước

                if (completed == tcs.Task)
                {
                    var ok = await tcs.Task;
                    return ok
                        ? (true, $"✅ SMS đã gửi thành công qua {comPort}")
                        : (false, $"❌ Gửi SMS thất bại qua {comPort}");
                }
                else
                {
                    return (false, $"⏰ Timeout 60s — SMS có thể đang gửi qua {comPort}");
                }
            }

            // 2. Mở port trực tiếp và gửi (khi worker chưa chạy)
            return await Task.Run(() =>
            {
                using var helper = new AtCommandHelper(comPort, _settings.BaudRate);
                if (!helper.Open())
                    return (false, $"❌ Không thể mở {comPort}");

                if (!helper.IsAlive())
                    return (false, $"❌ Modem {comPort} không phản hồi");

                var ok = helper.SendSms(destNumber, content);
                return ok
                    ? (true, $"✅ SMS đã gửi thành công qua {comPort}")
                    : (false, $"❌ Gửi SMS thất bại qua {comPort}");
            });
        }
        catch (Exception ex)
        {
            return (false, $"❌ Lỗi: {ex.Message}");
        }
    }

    public void StopAllWorkers()
    {
        foreach (var worker in _workers.Values)
        {
            try { worker.Dispose(); } catch { }
        }
        _workers.Clear();
    }

    public void Dispose()
    {
        StopAllWorkers();
        _forwarder.Dispose();
    }
}
