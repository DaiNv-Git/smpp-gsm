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

    public event Action<SimCard>? SimUpdated;
    public event Action<List<SimCard>>? ScanCompleted;
    public event Action<string, string, DateTime>? IncomingSms; // sender, content, time
    public event Action<string, string, bool, string?>? SmsResult; // messageId, status, success, error

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

            // Pass 1: Scan parallel
            var tasks = portNames.Select(port => Task.Run(() =>
            {
                var sim = ScanOnePort(port);
                if (sim != null)
                {
                    newSims.Add(sim);
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

            System.Diagnostics.Debug.WriteLine(
                $"✅ Scan hoàn tất: {_sims.Count} SIM(s) phát hiện (pass1={newSims.Count - failedPorts.Count}, retry={failedPorts.Count})");

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

            // 6. Provider — prefer AT+COPS?, fallback to IMSI prefix
            provider ??= helper.QueryOperator();
            if (provider == null || provider == "Unknown" || provider == "UNKNOWN")
                provider = AtCommandHelper.DetectProvider(imsi);

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
    }
}
