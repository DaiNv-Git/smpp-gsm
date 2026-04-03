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
    public event Action<string, string, bool>? DiscoveryLog; // comPort, message, isSuccess

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
    /// 🔥 v2: Bounded parallelism (4 concurrent) + DetectPhoneNumberFast → ~4x nhanh hơn.
    /// </summary>
    public async Task<List<SimCard>> ScanAllAsync()
    {
        if (_scanning) return _sims.Values.ToList();
        _scanning = true;

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            System.Diagnostics.Debug.WriteLine("🔍 Bắt đầu scan COM ports...");

            // Stop all workers first, and wait until they ACTUALLY stop and release ports.
            var activeWorkers = _workers.Values.ToList();
            StopAllWorkers();
            
            // Wait up to 3 seconds for all ports to be officially released
            int waitLoops = 0;
            while(activeWorkers.Any(w => w.IsRunning) && waitLoops < 15)
            {
                await Task.Delay(200);
                waitLoops++;
            }
            await Task.Delay(500); // extra grace period for serial driver to flush

            var portNames = SerialPort.GetPortNames().Distinct().OrderBy(p => p).ToArray();
            System.Diagnostics.Debug.WriteLine($"📋 Tìm thấy {portNames.Length} COM ports");

            var newSims = new ConcurrentBag<SimCard>();
            var failedPorts = new ConcurrentBag<string>();
            int pass1Ok = 0;
            int retryOk = 0;

            // 🔥 Bounded parallelism: 6 concurrent scans
            // USB hub xử lý tốt 4-6 serial I/O đồng thời
            const int maxConcurrent = 6;
            const int perPortTimeoutMs = 15_000; // 15s timeout cho mỗi port (tránh stuck/held port)
            using var semaphore = new SemaphoreSlim(maxConcurrent);

            var tasks = portNames.Select(async port =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // ⏱️ Timeout wrapper: port bị hold/stuck sẽ bị bỏ qua sau 15s
                    var sim = await ScanOnePortWithTimeout(port, perPortTimeoutMs);

                    if (sim != null)
                    {
                        newSims.Add(sim);
                        Interlocked.Increment(ref pass1Ok);
                        SimScanned?.Invoke(sim);
                        System.Diagnostics.Debug.WriteLine(
                            $"✅ {port}: CCID={sim.Ccid?[..Math.Min(10, sim.Ccid.Length)]}... Phone={sim.PhoneNumber ?? "N/A"} Provider={sim.Provider}");
                    }
                    else
                    {
                        // Retry 1 lần cho port fail (cũng có timeout)
                        await Task.Delay(300);
                        sim = await ScanOnePortWithTimeout(port, perPortTimeoutMs);

                        if (sim != null)
                        {
                            newSims.Add(sim);
                            Interlocked.Increment(ref retryOk);
                            SimScanned?.Invoke(sim);
                            System.Diagnostics.Debug.WriteLine(
                                $"✅ [RETRY] {port}: CCID={sim.Ccid?[..Math.Min(10, sim.Ccid.Length)]}... Phone={sim.PhoneNumber ?? "N/A"}");
                        }
                        else
                        {
                            failedPorts.Add(port);
                            System.Diagnostics.Debug.WriteLine($"❌ {port}: scan thất bại sau 2 lần");
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks);

            // Update cache
            _sims.Clear();
            foreach (var sim in newSims)
                _sims[sim.ComPort] = sim;

            // 📊 Scan statistics
            int totalPorts = portNames.Length;
            int totalOk = pass1Ok + retryOk;
            int totalFail = totalPorts - totalOk;
            int phoneFound = newSims.Count(s => !string.IsNullOrWhiteSpace(s.PhoneNumber));

            sw.Stop();
            LastScanStats = $"📊 {totalPorts} cổng COM | ✅ {totalOk} thành công | ❌ {totalFail} thất bại | 📱 {phoneFound} có số ĐT | ⏱️ {sw.Elapsed.TotalSeconds:F1}s";
            System.Diagnostics.Debug.WriteLine(
                $"✅ Scan hoàn tất: {LastScanStats} (pass1={pass1Ok}, retry={retryOk})");

            var result = _sims.Values.ToList();
            ScanCompleted?.Invoke(result);

            // 📞 Pass 3: Self-SMS discovery — chạy BACKGROUND cho SIM thiếu số
            var simsWithoutPhone = result.Where(s => string.IsNullOrWhiteSpace(s.PhoneNumber)).ToList();
            if (simsWithoutPhone.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"📞 {simsWithoutPhone.Count} SIM thiếu số → sẽ chạy Self-SMS Discovery sau khi start workers");
            }

            return result;
        }
        finally
        {
            _scanning = false;
        }
    }
    /// <summary>
    /// ⏱️ Wrapper: chạy ScanOnePort với timeout cứng.
    /// Port bị hold/stuck sẽ bị bỏ qua thay vì block toàn bộ scan.
    /// </summary>
    private async Task<SimCard?> ScanOnePortWithTimeout(string comPort, int timeoutMs)
    {
        try
        {
            var scanTask = Task.Run(() => ScanOnePort(comPort));
            var timeoutTask = Task.Delay(timeoutMs);

            var completed = await Task.WhenAny(scanTask, timeoutTask);
            if (completed == scanTask)
            {
                return await scanTask; // Completed in time
            }
            else
            {
                // ⏱️ Port stuck/held → timeout
                System.Diagnostics.Debug.WriteLine(
                    $"⏱️ [{comPort}] Scan TIMEOUT sau {timeoutMs / 1000}s — port có thể bị hold/stuck");

                // 🔥 Force close port nếu scan task vẫn đang chạy
                // Không await scanTask vì nó có thể bị block vĩnh viễn
                _ = Task.Run(() =>
                {
                    try
                    {
                        // Đợi thêm 2s rồi force close
                        if (!scanTask.IsCompleted)
                        {
                            Thread.Sleep(2000);
                            // Port sẽ được dispose bởi ScanOnePort's using statement khi thread kết thúc
                            System.Diagnostics.Debug.WriteLine(
                                $"⚠️ [{comPort}] Force-abandoned stuck scan task");
                        }
                    }
                    catch { /* ignore cleanup errors */ }
                });

                return null;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ [{comPort}] ScanWithTimeout error: {ex.Message}");
            return null;
        }
    }

    /// <summary>Thử nhiều baud rate phổ biến để open port thành công.
    /// Fix: modem có thể hardcoded baud 9600 thay vì 115200.</summary>
    private AtCommandHelper? TryOpenPort(string comPort, int preferredBaud)
    {
        // 🔥 Chỉ thử 2 baud rate phổ biến nhất (giảm từ 6 → 2 cho scan nhanh)
        int[] baudRates = { preferredBaud, 115200, 9600 };
        baudRates = baudRates.Distinct().ToArray();

        foreach (var baud in baudRates)
        {
            var helper = new AtCommandHelper(comPort, baud);
            try
            {
                if (helper.Open() && helper.IsAlive())
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"🔌 [{comPort}] Opened @ baud {baud}");
                    return helper;
                }
            }
            catch
            {
                // Baud này không được, thử baud tiếp theo
            }
            finally
            {
                if (!helper.IsOpen) helper.Dispose();
            }
        }
        return null;
    }

    private SimCard? ScanOnePort(string comPort)
    {
        using var helper = TryOpenPort(comPort, _settings.BaudRate);
        if (helper == null) return null;

        try
        {
            // 1. CCID (required) — kiểm tra SIM có thật sự cắm không
            var ccid = helper!.GetCcid();
            if (string.IsNullOrWhiteSpace(ccid)) return null;

            // 2. IMSI
            var imsi = helper!.GetImsi();

            // 3. Phone number — ⚡ Dùng DetectPhoneNumberFast (CNUM 1 lần + phonebook 1-5)
            // USSD và extended retry sẽ chạy sau scan (Self-SMS Discovery)
            var phone = helper!.DetectPhoneNumberFast();

            // 🔒 Preserve existing phone number if scan failed to detect it
            if (string.IsNullOrWhiteSpace(phone))
            {
                if (_sims.TryGetValue(comPort, out var existingSim) &&
                    !string.IsNullOrWhiteSpace(existingSim.PhoneNumber))
                {
                    phone = existingSim.PhoneNumber;
                    System.Diagnostics.Debug.WriteLine(
                        $"📱 [{comPort}] Giữ số cũ từ cache: {phone} (CCID={ccid?.Substring(0, Math.Min(10, ccid.Length))})");
                }
            }

            // 4. Fallback: query MongoDB trực tiếp (ưu tiên) hoặc BE API
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
                            try { helper!.WritePhoneToSimPhonebook(phone); }
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
            var signal = helper!.GetSignalLevel();

            // 6. Provider — IMSI prefix first, AT+COPS? là fallback
            provider ??= AtCommandHelper.DetectProvider(imsi);
            if (provider == "Unknown")
                provider = helper!.QueryOperator() ?? "Unknown";

            return new SimCard
            {
                ComPort = comPort,
                Ccid = ccid,
                Imsi = imsi,
                PhoneNumber = phone,
                Provider = provider,
                SignalLevel = signal,
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
                // 📞 Self-SMS Discovery: intercepte "DISCOVER:{ccid}" messages
                if (content.StartsWith("DISCOVER:") && !string.IsNullOrWhiteSpace(sender))
                {
                    var ccid = content.Substring("DISCOVER:".Length).Trim();
                    HandlePhoneDiscovery(ccid, sender);
                    return true; // Không forward nội bộ lên API
                }

                IncomingSms?.Invoke(sender, content, time);

                // Persist local queue first, API forward still happens asynchronously.
                var receiver = sim.PhoneNumber ?? comPort;
                return _forwarder.Enqueue(sender, receiver, content, comPort);
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
                    AllowRedispatch = false,
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

    /// <summary>
    /// Tạm dừng worker trên 1 COM port cụ thể (để nhường cho voice call).
    /// Returns: SimCard nếu đang có worker, null nếu port tự do.
    /// </summary>
    public SimCard? StopWorkerForPort(string comPort)
    {
        if (_workers.TryRemove(comPort, out var worker))
        {
            var sim = worker.Sim;
            System.Diagnostics.Debug.WriteLine($"⏸️ Pausing worker on {comPort} for voice call");
            try { worker.Dispose(); } catch { }

            // 🔥 FIX: Đợi thread worker THỰC SỰ kết thúc trước khi mở port
            // Trước: chỉ Sleep(500) → không đủ, worker thread có thể vẫn đang gửi AT command
            // Sau: join thread với timeout 3s
            int waited = 0;
            while (worker.IsRunning && waited < 3000)
            {
                Thread.Sleep(100);
                waited += 100;
            }
            if (worker.IsRunning)
                System.Diagnostics.Debug.WriteLine($"⚠️ [{comPort}] Worker thread still running after 3s — proceeding anyway");
            else
                System.Diagnostics.Debug.WriteLine($"✅ [{comPort}] Worker stopped after {waited}ms");

            return sim;
        }
        return _sims.TryGetValue(comPort, out var s) ? s : null;
    }

    /// <summary>
    /// Khởi động lại worker sau khi voice call kết thúc.
    /// </summary>
    public void RestartWorkerForPort(string comPort)
    {
        if (_sims.TryGetValue(comPort, out var sim) && sim.Status != SimStatus.Error)
        {
            // Đảm bảo port đã release
            Thread.Sleep(500);
            StartWorker(sim);
            System.Diagnostics.Debug.WriteLine($"▶️ Worker resumed on {comPort}");
        }
    }

    public List<ModemWorker> GetActiveWorkers()
    {
        return _workers.Values.ToList();
    }

    /// <summary>
    /// 📞 Self-SMS Discovery: SIM chưa biết số gửi SMS đến SIM đã biết số.
    /// Receiver bắt sender number → đó chính là số của SIM không biết.
    /// Gọi SAU khi StartAllWorkers() (cần receiver worker đang chạy để nhận SMS).
    /// </summary>
    public async Task DiscoverPhoneBySelfSmsAsync()
    {
        var simsWithoutPhone = _sims.Values
            .Where(s => string.IsNullOrWhiteSpace(s.PhoneNumber))
            .ToList();

        if (simsWithoutPhone.Count == 0)
        {
            System.Diagnostics.Debug.WriteLine("📞 Tất cả SIM đã có số — không cần discovery");
            return;
        }

        // Tìm SIM có số làm receiver
        var receiverSim = _sims.Values.FirstOrDefault(s =>
            !string.IsNullOrWhiteSpace(s.PhoneNumber) &&
            _workers.ContainsKey(s.ComPort));

        if (receiverSim == null)
        {
            System.Diagnostics.Debug.WriteLine(
                $"⚠️ Không có SIM nào có số + đang chạy worker → không thể Self-SMS Discovery. " +
                $"Cần ít nhất 1 SIM có số điện thoại (nhập thủ công hoặc có sẵn trong MongoDB).");
            return;
        }

        System.Diagnostics.Debug.WriteLine(
            $"📞 Self-SMS Discovery: {simsWithoutPhone.Count} SIM thiếu số → gửi đến {receiverSim.PhoneNumber} ({receiverSim.ComPort})");

        int discovered = 0;
        foreach (var sim in simsWithoutPhone)
        {
            // Skip nếu worker đang chạy trên port này (không mở 2 helper cùng lúc)
            if (_workers.ContainsKey(sim.ComPort))
            {
                // Dùng worker hiện tại để gửi
                if (_workers.TryGetValue(sim.ComPort, out var worker) && worker.IsRunning)
                {
                    var tcs = new TaskCompletionSource<bool>();
                    worker.EnqueueSms(new SmsTask
                    {
                        MessageId = $"DISCOVER-{sim.Ccid}",
                        DestAddr = receiverSim.PhoneNumber!,
                        Content = $"DISCOVER:{sim.Ccid}",
                        AllowRedispatch = false,
                        CompletionSource = tcs,
                    });

                    // Đợi gửi xong (timeout 30s)
                    var timeout = Task.Delay(30000);
                    await Task.WhenAny(tcs.Task, timeout);
                    if (tcs.Task.IsCompletedSuccessfully && tcs.Task.Result)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"📞 [{sim.ComPort}] Discovery SMS sent → đợi receiver bắt sender number...");
                        DiscoveryLog?.Invoke(sim.ComPort, $"📤 Gửi DISCOVER đến {receiverSim.PhoneNumber}", true);
                        discovered++;
                    }
                    else
                    {
                        DiscoveryLog?.Invoke(sim.ComPort, "❌ Gửi DISCOVER thất bại (timeout)", false);
                    }
                }
            }
            else
            {
                // Port chưa có worker → mở helper tạm
                try
                {
                    using var helper = new AtCommandHelper(sim.ComPort, _settings.BaudRate);
                    if (helper.Open() && helper.IsAlive())
                    {
                        var sent = helper.SendSms(receiverSim.PhoneNumber!, $"DISCOVER:{sim.Ccid}");
                        if (sent)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"📞 [{sim.ComPort}] Discovery SMS sent via temp helper");
                            DiscoveryLog?.Invoke(sim.ComPort, $"📤 Gửi DISCOVER đến {receiverSim.PhoneNumber} (temp)", true);
                            discovered++;
                        }
                        else
                        {
                            DiscoveryLog?.Invoke(sim.ComPort, "❌ Gửi DISCOVER thất bại", false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"⚠️ [{sim.ComPort}] Discovery SMS failed: {ex.Message}");
                }
            }

            // Đợi giữa các SMS tránh spam
            await Task.Delay(3000);
        }

        // Đợi receiver xử lý tất cả discovery SMS
        if (discovered > 0)
        {
            System.Diagnostics.Debug.WriteLine(
                $"📞 Đã gửi {discovered} discovery SMS → đợi 10s cho receiver xử lý...");
            await Task.Delay(10000);

            var stillMissing = _sims.Values.Count(s => string.IsNullOrWhiteSpace(s.PhoneNumber));
            System.Diagnostics.Debug.WriteLine(
                $"📞 Discovery hoàn tất: {discovered - stillMissing} số mới tìm được, {stillMissing} vẫn thiếu");
        }
    }

    /// <summary>
    /// Xử lý SMS discovery: map CCID → sender phone number.
    /// Được gọi từ onIncomingSms callback khi nhận tin "DISCOVER:{ccid}".
    /// </summary>
    private void HandlePhoneDiscovery(string ccid, string senderPhone)
    {
        System.Diagnostics.Debug.WriteLine(
            $"📞 Discovery received: CCID={ccid} → Phone={senderPhone}");

        // Tìm SIM theo CCID
        var targetSim = _sims.Values.FirstOrDefault(s =>
            s.Ccid != null && s.Ccid.Contains(ccid.Length >= 18 ? ccid[..18] : ccid));

        if (targetSim == null)
        {
            System.Diagnostics.Debug.WriteLine(
                $"⚠️ Discovery: không tìm thấy SIM với CCID={ccid}");
            return;
        }

        // Update phone number
        var normalizedPhone = AtCommandHelper.NormalizeNumber(senderPhone);
        targetSim.PhoneNumber = normalizedPhone;
        SimUpdated?.Invoke(targetSim);
        DiscoveryLog?.Invoke(targetSim.ComPort, $"✅ Tìm được số: {normalizedPhone}", true);

        System.Diagnostics.Debug.WriteLine(
            $"✅ Discovery: {targetSim.ComPort} CCID={ccid} → Phone={normalizedPhone}");

        // Ghi vào SIM phonebook để lần scan sau đọc được
        if (_workers.TryGetValue(targetSim.ComPort, out _))
        {
            // Worker đang chạy → không mở helper mới, skip phonebook write
        }
        else
        {
            try
            {
                using var helper = new AtCommandHelper(targetSim.ComPort, _settings.BaudRate);
                if (helper.Open())
                    helper.WritePhoneToSimPhonebook(normalizedPhone);
            }
            catch { /* ignore */ }
        }

        // Persist vào MongoDB nếu có
        if (_mongoDb != null)
        {
            try
            {
                var doc = _mongoDb.FindByCcidFuzzy(ccid);
                if (doc != null)
                {
                    doc.PhoneNumber = normalizedPhone;
                    _mongoDb.Save(doc);
                    System.Diagnostics.Debug.WriteLine(
                        $"💾 Discovery: saved to MongoDB: {normalizedPhone}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"⚠️ Discovery: MongoDB save failed: {ex.Message}");
            }
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
