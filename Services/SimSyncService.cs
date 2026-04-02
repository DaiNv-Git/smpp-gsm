using GsmAgent.Models;
using System.Text.RegularExpressions;

namespace GsmAgent.Services;

/// <summary>
/// SimSyncService — Scan SIM và đồng bộ vào MongoDB collection "sims".
/// Port từ SimSyncService.java trong simsmart-gsm.
/// 
/// Cơ chế:
/// 1. Scan tất cả COM ports → lấy CCID, IMSI, phone, signal, provider (AT commands)
/// 2. So sánh với MongoDB → upsert (tạo mới hoặc cập nhật)
/// 3. SIM missing → tăng missCount → INACTIVE (≥2) → REPLACED (≥5)
/// 4. Cập nhật lastUpdated mỗi lần scan
/// 5. Lặp lại mỗi 5 phút
/// </summary>
public class SimSyncService : IDisposable
{
    private readonly MongoDbService _mongoDb;
    private readonly SerialPortManager _portManager;
    private readonly AppSettings _settings;
    private System.Timers.Timer? _syncTimer;
    private bool _syncInProgress;
    private bool _disposed;

    private const int MISS_THRESHOLD_INACTIVE = 2;
    private const int MISS_THRESHOLD_REPLACED = 5;
    private const int CCID_FUZZY_MATCH_LENGTH = 18;

    public event Action<string>? LogMessage;
    public event Action<int, int>? SyncCompleted; // synced count, total scanned

    public SimSyncService(MongoDbService mongoDb, SerialPortManager portManager, AppSettings settings)
    {
        _mongoDb = mongoDb;
        _portManager = portManager;
        _settings = settings;
    }

    /// <summary>
    /// Khởi động scheduled sync (mỗi 5 phút).
    /// Giống @Scheduled(fixedRate = 300_000) trong Java.
    /// </summary>
    public void StartScheduledSync()
    {
        _syncTimer?.Dispose();

        // Sync ngay lập tức lần đầu
        _ = Task.Run(() => SyncSimsToMongo());

        // Timer mỗi 5 phút
        var intervalMs = _settings.ScanIntervalSec * 1000; // Default 300s = 5min
        _syncTimer = new System.Timers.Timer(intervalMs);
        _syncTimer.Elapsed += async (s, e) =>
        {
            await SyncSimsToMongo();
        };
        _syncTimer.Start();

        Logger.Info($"🔄 SimSyncService started — sync mỗi {_settings.ScanIntervalSec}s");
        LogMessage?.Invoke($"🔄 SIM sync đã bật (mỗi {_settings.ScanIntervalSec / 60} phút)");
    }

    public void StopScheduledSync()
    {
        _syncTimer?.Dispose();
        _syncTimer = null;
        Logger.Info("⏹ SimSyncService stopped");
    }

    /// <summary>
    /// 🔍 Core logic: Scan SIM → Sync MongoDB.
    /// Giống scanSimsOnly() + syncScannedToDb() trong Java.
    /// </summary>
    public async Task SyncSimsToMongo()
    {
        if (_syncInProgress)
        {
            Logger.Info("⏭️ Sync đang chạy, bỏ qua...");
            return;
        }

        if (!_mongoDb.IsConnected)
        {
            Logger.Warn("⚠️ MongoDB chưa kết nối, skip sync");
            return;
        }

        _syncInProgress = true;
        try
        {
            var deviceName = _settings.AgentId;
            Logger.Info($"🔍 Bắt đầu sync SIM cho device '{deviceName}'...");

            // 1️⃣ Lấy danh sách SIM đang scan từ SerialPortManager (in-memory) và refresh trạng thái realtime
            // Refresh signal level và trạng thái mới nhất từ ModemWorker
            foreach (var worker in _portManager.GetActiveWorkers())
            {
                worker.RefreshSimInfo();
            }

            var scannedSims = _portManager.Sims.Values.ToList();

            // 🔥 Tự động thử resolve SIM thiếu SĐT qua MongoDB mỗi lần sync
            int resolvedPhones = 0;
            foreach (var sim in scannedSims.Where(s => string.IsNullOrWhiteSpace(s.PhoneNumber) && !string.IsNullOrWhiteSpace(s.Ccid)))
            {
                var dbSim = _mongoDb.FindByCcidFuzzy(sim.Ccid);
                if (dbSim != null && !string.IsNullOrWhiteSpace(dbSim.PhoneNumber))
                {
                    sim.PhoneNumber = dbSim.PhoneNumber;
                    sim.Provider = dbSim.SimProvider;
                    resolvedPhones++;
                    
                    // Thử lưu vào phonebook
                    try {
                        using var helper = new AtCommandHelper(sim.ComPort, _settings.BaudRate);
                        if (helper.Open()) helper.WritePhoneToSimPhonebook(sim.PhoneNumber);
                    } catch { }
                }
            }
            if (resolvedPhones > 0)
            {
                Logger.Info($"📞 Background Sync: Đã tự động lookup và lấy được SĐT cho {resolvedPhones} SIM từ MongoDB.");
                // Báo UI update vì local app reference được sửa
                System.Diagnostics.Debug.WriteLine("Cập nhật lại giao diện vì SĐT thay đổi...");
                // (Giao diện sẽ tự nhận ra có số thông qua INotifyPropertyChanged hoặc do loop qua update)
            }

            if (scannedSims.Count == 0)
            {
                Logger.Info("⚠️ Không có SIM nào trong memory, thử scan lại...");
                scannedSims = (await _portManager.ScanAllAsync()).ToList();
            }

            // 2️⃣ Load tất cả SIM từ DB
            var allDbSims = _mongoDb.FindAll();

            // Map theo CCID để tìm nhanh
            var dbMapByCcid = allDbSims
                .Where(s => !string.IsNullOrWhiteSpace(s.Ccid))
                .GroupBy(s => s.Ccid!)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.LastUpdated ?? DateTime.MinValue).First());

            // Map theo phoneNumber
            var dbMapByPhone = allDbSims
                .Where(s => !string.IsNullOrWhiteSpace(s.PhoneNumber))
                .GroupBy(s => s.PhoneNumber!)
                .ToDictionary(g => g.Key, g =>
                {
                    // Ưu tiên SIM có CCID (từ local app scan)
                    var withCcid = g.Where(s => !string.IsNullOrWhiteSpace(s.Ccid)).OrderByDescending(s => s.LastUpdated ?? DateTime.MinValue).FirstOrDefault();
                    return withCcid ?? g.OrderByDescending(s => s.LastUpdated ?? DateTime.MinValue).First();
                });

            var scannedCcids = new HashSet<string>();
            var toSave = new List<SimDocument>();
            int newCount = 0, updatedCount = 0;

            // 3️⃣ Sync từng SIM đã scan
            foreach (var sim in scannedSims)
            {
                if (string.IsNullOrWhiteSpace(sim.Ccid)) continue;

                scannedCcids.Add(sim.Ccid);

                // Tìm SIM trong DB (fuzzy match CCID 18 ký tự)
                var dbSim = FuzzyMatchCcidInMap(sim.Ccid, dbMapByCcid);

                if (dbSim != null)
                {
                    // UPDATE existing
                    UpdateSimFromScan(dbSim, sim, deviceName);
                    toSave.Add(dbSim);
                    updatedCount++;
                }
                else if (!string.IsNullOrWhiteSpace(sim.PhoneNumber)
                         && dbMapByPhone.TryGetValue(NormalizeNumber(sim.PhoneNumber), out var existingByPhone))
                {
                    // MERGE: SIM đã import từ seller (chưa có CCID)
                    Logger.Info($"🔀 MERGE SIM: phone={sim.PhoneNumber} đã có trong DB, thêm CCID={sim.Ccid}");
                    existingByPhone.Ccid = sim.Ccid;
                    UpdateSimFromScan(existingByPhone, sim, deviceName);
                    toSave.Add(existingByPhone);
                    updatedCount++;
                }
                else
                {
                    // CREATE new
                    var newDoc = CreateNewSimDocument(sim, deviceName);
                    toSave.Add(newDoc);
                    newCount++;
                    Logger.Info($"🆕 SIM mới: ccid={sim.Ccid}, phone={sim.PhoneNumber ?? "N/A"}, com={sim.ComPort}");
                }
            }

            // 4️⃣ Handle missing SIMs (SIM trong DB nhưng không scan thấy)
            var deviceSims = allDbSims.Where(s => s.DeviceName == deviceName).ToList();
            foreach (var dbSim in deviceSims)
            {
                if (string.IsNullOrWhiteSpace(dbSim.Ccid)) continue;
                if (scannedCcids.Contains(dbSim.Ccid)) continue;

                // SIM đã missing
                dbSim.MissCount++;
                dbSim.LastUpdated = DateTime.UtcNow;

                var oldStatus = dbSim.Status;
                if (dbSim.MissCount >= MISS_THRESHOLD_REPLACED)
                {
                    dbSim.Status = "REPLACED";
                    if (oldStatus != "REPLACED")
                        Logger.Info($"🔄 SIM {dbSim.Ccid} → REPLACED (missCount≥{MISS_THRESHOLD_REPLACED})");
                }
                else if (dbSim.MissCount >= MISS_THRESHOLD_INACTIVE)
                {
                    dbSim.Status = "INACTIVE";
                    if (oldStatus != "INACTIVE")
                        Logger.Info($"🔄 SIM {dbSim.Ccid} → INACTIVE (missCount≥{MISS_THRESHOLD_INACTIVE})");
                }

                toSave.Add(dbSim);
            }

            // 5️⃣ Batch save
            if (toSave.Count > 0)
            {
                try
                {
                    _mongoDb.SaveAll(toSave);
                    Logger.Info($"💾 Đã sync {toSave.Count} SIM vào MongoDB (mới:{newCount}, update:{updatedCount}, missing:{toSave.Count - newCount - updatedCount})");
                }
                catch (Exception ex)
                {
                    Logger.Error($"❌ Batch save failed: {ex.Message}. Retrying individually...");
                    foreach (var doc in toSave)
                    {
                        try { _mongoDb.Save(doc); }
                        catch (Exception ex2)
                        {
                            Logger.Error($"❌ Save SIM {doc.Ccid} failed: {ex2.Message}");
                        }
                    }
                }
            }

            LogMessage?.Invoke($"✅ Sync: {scannedSims.Count} SIM → MongoDB (new:{newCount}, update:{updatedCount})");
            SyncCompleted?.Invoke(toSave.Count, scannedSims.Count);
        }
        catch (Exception ex)
        {
            Logger.Error($"❌ SyncSimsToMongo error: {ex.Message}");
            LogMessage?.Invoke($"❌ Sync lỗi: {ex.Message}");
        }
        finally
        {
            _syncInProgress = false;
        }
    }

    /// <summary>
    /// Tạo SimDocument mới từ kết quả scan.
    /// Giống createNewSim() trong Java.
    /// </summary>
    private SimDocument CreateNewSimDocument(SimCard sim, string deviceName)
    {
        return new SimDocument
        {
            Id = Guid.NewGuid().ToString(),
            Ccid = sim.Ccid,
            Imsi = sim.Imsi,
            PhoneNumber = !string.IsNullOrWhiteSpace(sim.PhoneNumber)
                ? NormalizeNumber(sim.PhoneNumber) : null,
            SimProvider = sim.Provider,
            DeviceName = deviceName,
            ComName = sim.ComPort,
            AgentId = _settings.AgentId,
            Status = !string.IsNullOrWhiteSpace(sim.PhoneNumber) ? "ACTIVE" : "INACTIVE",
            MissCount = 0,
            LastUpdated = DateTime.UtcNow,
            CountryCode = "JP",
        };
    }

    /// <summary>
    /// Cập nhật SimDocument từ kết quả scan.
    /// Giống updateSimFromScan() trong Java.
    /// </summary>
    private void UpdateSimFromScan(SimDocument dbSim, SimCard scanned, string deviceName)
    {
        dbSim.Imsi = scanned.Imsi ?? dbSim.Imsi;
        dbSim.ComName = scanned.ComPort;
        dbSim.DeviceName = deviceName;
        dbSim.AgentId = _settings.AgentId;

        // Chỉ cập nhật phoneNumber nếu scan được số mới
        if (!string.IsNullOrWhiteSpace(scanned.PhoneNumber))
        {
            var normalized = NormalizeNumber(scanned.PhoneNumber);
            if (dbSim.PhoneNumber != normalized)
            {
                Logger.Info($"📞 Phone updated: {dbSim.PhoneNumber ?? "null"} → {normalized} (CCID: {dbSim.Ccid})");
            }
            dbSim.PhoneNumber = normalized;
        }

        // Provider: ưu tiên AT+COPS? (realtime), fallback IMSI prefix
        // Hệ thống cũ chỉ dùng IMSI prefix, hệ thống mới dùng cả AT+COPS? (tốt hơn)
        if (!string.IsNullOrWhiteSpace(scanned.Provider) && scanned.Provider != "Unknown")
            dbSim.SimProvider = scanned.Provider;

        // Reset miss count khi scan thấy lại
        if (dbSim.MissCount > 0)
        {
            Logger.Info($"🔄 Reset missCount cho SIM {dbSim.Ccid} (từ {dbSim.MissCount} về 0)");
            dbSim.MissCount = 0;
        }

        // Cập nhật status
        dbSim.Status = !string.IsNullOrWhiteSpace(dbSim.PhoneNumber) ? "ACTIVE" : "INACTIVE";
        dbSim.LastUpdated = DateTime.UtcNow;
    }

    /// <summary>
    /// Fuzzy match CCID trong Map (18 ký tự liên tục).
    /// Giống fuzzyMatchCcidInMap() trong Java.
    /// </summary>
    private SimDocument? FuzzyMatchCcidInMap(string scannedCcid,
        Dictionary<string, SimDocument> dbMapByCcid)
    {
        if (string.IsNullOrWhiteSpace(scannedCcid)) return null;

        // 1️⃣ Exact match
        if (dbMapByCcid.TryGetValue(scannedCcid, out var exactMatch))
        {
            if (!string.IsNullOrWhiteSpace(exactMatch.PhoneNumber))
                return exactMatch;
        }

        // 2️⃣ Variant: bỏ trailing F
        var ccidDigits = Regex.Replace(scannedCcid, @"[^0-9]", "");
        if (ccidDigits != scannedCcid && dbMapByCcid.TryGetValue(ccidDigits, out var variantMatch))
        {
            if (!string.IsNullOrWhiteSpace(variantMatch.PhoneNumber))
                return variantMatch;
        }
        if (scannedCcid.EndsWith("F", StringComparison.OrdinalIgnoreCase))
        {
            var noF = scannedCcid[..^1];
            if (dbMapByCcid.TryGetValue(noF, out var noFMatch))
            {
                if (!string.IsNullOrWhiteSpace(noFMatch.PhoneNumber))
                    return noFMatch;
            }
        }

        // 3️⃣ Fuzzy match: 18 ký tự liên tục
        if (ccidDigits.Length >= CCID_FUZZY_MATCH_LENGTH)
        {
            var scannedSubs = new HashSet<string>();
            for (int i = 0; i <= ccidDigits.Length - CCID_FUZZY_MATCH_LENGTH; i++)
                scannedSubs.Add(ccidDigits.Substring(i, CCID_FUZZY_MATCH_LENGTH));

            SimDocument? fuzzyWithPhone = null;
            SimDocument? fuzzyAny = null;

            foreach (var (dbCcid, dbSim) in dbMapByCcid)
            {
                if (string.IsNullOrWhiteSpace(dbCcid) || dbCcid.Length < CCID_FUZZY_MATCH_LENGTH || dbCcid == scannedCcid)
                    continue;

                for (int i = 0; i <= dbCcid.Length - CCID_FUZZY_MATCH_LENGTH; i++)
                {
                    var dbSub = dbCcid.Substring(i, CCID_FUZZY_MATCH_LENGTH);
                    if (scannedSubs.Contains(dbSub))
                    {
                        if (!string.IsNullOrWhiteSpace(dbSim.PhoneNumber))
                        {
                            fuzzyWithPhone = dbSim;
                            break;
                        }
                        fuzzyAny ??= dbSim;
                    }
                }
                if (fuzzyWithPhone != null) break;
            }

            if (fuzzyWithPhone != null)
            {
                if (exactMatch != null)
                {
                    // MERGE: copy phone từ imported → scan record
                    exactMatch.PhoneNumber = fuzzyWithPhone.PhoneNumber;
                    try
                    {
                        _mongoDb.Delete(fuzzyWithPhone.Id);
                        Logger.Info($"🗑️ Xóa record import duplicate: ccid={fuzzyWithPhone.Ccid}");
                    }
                    catch { }
                    return exactMatch;
                }
                return fuzzyWithPhone;
            }

            if (exactMatch == null && fuzzyAny != null)
                return fuzzyAny;
        }

        return exactMatch;
    }

    /// <summary>
    /// Shutdown handler: set INACTIVE tất cả SIM.
    /// Giống SimShutdownHandler.java.
    /// </summary>
    public void OnShutdown()
    {
        try
        {
            _mongoDb.SetAllInactive(_settings.AgentId);
            Logger.Info($"🛑 Shutdown: set INACTIVE tất cả SIM của '{_settings.AgentId}'");
        }
        catch (Exception ex)
        {
            Logger.Error($"❌ Shutdown sync error: {ex.Message}");
        }
    }

    // ================== UTILITIES ==================

    /// <summary>
    /// Normalize số điện thoại Japan.
    /// Format chuẩn: 0X0XXXXXXXX (11 số, bắt đầu bằng 0).
    /// Giống normalizeNumber() trong Java SimSyncService.
    /// </summary>
    public static string NormalizeNumber(string num)
    {
        if (string.IsNullOrWhiteSpace(num)) return num;

        var s = num.Trim();
        s = Regex.Replace(s, @"[^0-9+]", "");

        // Bỏ prefix +81
        if (s.StartsWith("+81"))
            s = "0" + s[3..];
        // Bỏ prefix 81 (không có dấu +)
        else if (s.StartsWith("81") && s.Length == 13)
            s = "0" + s[2..];

        // Nếu số Japan thiếu số 0 đầu (70x, 80x, 90x)
        if (Regex.IsMatch(s, @"^[7-9]0\d{8}$"))
            s = "0" + s;

        return s;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            StopScheduledSync();
            _mongoDb.Dispose();
            _disposed = true;
        }
    }
}
