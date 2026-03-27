using MongoDB.Driver;
using GsmAgent.Models;
using System.Text.RegularExpressions;

namespace GsmAgent.Services;

/// <summary>
/// MongoDbService — Kết nối MongoDB, CRUD cho collection "sims".
/// Tương tự SimRepository + MongoTemplate trong simsmart-gsm (Java).
/// 
/// URI: mongodb://admin:MongoAdmin@2026!Secure@72.60.41.168:27017/JapanSim?authSource=admin
/// </summary>
public class MongoDbService : IDisposable
{
    private readonly IMongoClient _client;
    private readonly IMongoDatabase _db;
    private readonly IMongoCollection<SimDocument> _simsCollection;
    private bool _disposed;

    // CCID fuzzy match: trùng 18 số liên tục là coi như match (giống Java)
    private const int CCID_FUZZY_MATCH_LENGTH = 18;

    public bool IsConnected { get; private set; }

    public MongoDbService(string mongoUri)
    {
        try
        {
            _client = new MongoClient(mongoUri);
            _db = _client.GetDatabase("JapanSim");
            _simsCollection = _db.GetCollection<SimDocument>("sims");

            // Test connection
            _db.RunCommand<MongoDB.Bson.BsonDocument>(
                new MongoDB.Bson.BsonDocument("ping", 1));
            IsConnected = true;

            Logger.Info("✅ MongoDB connected → JapanSim.sims");
        }
        catch (Exception ex)
        {
            IsConnected = false;
            Logger.Error($"❌ MongoDB connection failed: {ex.Message}");
            throw;
        }
    }

    // ================== FIND ==================

    /// <summary>Tìm SIM theo CCID exact match.</summary>
    public SimDocument? FindByCcid(string ccid)
    {
        if (string.IsNullOrWhiteSpace(ccid)) return null;
        return _simsCollection.Find(s => s.Ccid == ccid).FirstOrDefault();
    }

    /// <summary>
    /// 🆕 Tìm SIM bằng CCID fuzzy match (18 ký tự liên tục).
    /// Giống findSimByCcidFuzzy() trong SimSyncService.java.
    /// Xử lý case: AT→CCID có trailing F, import từ Excel không có F, có prefix.
    /// </summary>
    public SimDocument? FindByCcidFuzzy(string scannedCcid)
    {
        if (string.IsNullOrWhiteSpace(scannedCcid)) return null;

        // 1️⃣ Exact match trước (nhanh nhất)
        var exactMatch = FindByCcid(scannedCcid);
        if (exactMatch != null && !string.IsNullOrWhiteSpace(exactMatch.PhoneNumber))
            return exactMatch;

        // 2️⃣ Variant match: bỏ trailing F
        var ccidDigitsOnly = Regex.Replace(scannedCcid, @"[^0-9]", "");
        var variants = new HashSet<string> { scannedCcid };
        if (ccidDigitsOnly != scannedCcid)
            variants.Add(ccidDigitsOnly);
        if (scannedCcid.EndsWith("F", StringComparison.OrdinalIgnoreCase))
            variants.Add(scannedCcid[..^1]);

        foreach (var variant in variants)
        {
            var filter = Builders<SimDocument>.Filter.And(
                Builders<SimDocument>.Filter.Eq(s => s.Ccid, variant),
                Builders<SimDocument>.Filter.Ne(s => s.PhoneNumber, null)
            );
            var found = _simsCollection.Find(filter).FirstOrDefault();
            if (found != null && !string.IsNullOrWhiteSpace(found.PhoneNumber))
            {
                Logger.Info($"🔗 CCID variant match! phone={found.PhoneNumber} từ CCID={found.Ccid} → scan CCID={scannedCcid}");
                return found;
            }
        }

        // 3️⃣ Regex fuzzy match (18 ký tự liên tục) — giống Java
        if (ccidDigitsOnly.Length >= CCID_FUZZY_MATCH_LENGTH)
        {
            for (int i = 0; i <= ccidDigitsOnly.Length - CCID_FUZZY_MATCH_LENGTH; i++)
            {
                var sub18 = ccidDigitsOnly.Substring(i, CCID_FUZZY_MATCH_LENGTH);
                var regexFilter = Builders<SimDocument>.Filter.And(
                    Builders<SimDocument>.Filter.Regex(s => s.Ccid, new MongoDB.Bson.BsonRegularExpression(sub18)),
                    Builders<SimDocument>.Filter.Ne(s => s.PhoneNumber, null)
                );
                var found = _simsCollection.Find(regexFilter).FirstOrDefault();
                if (found != null && !string.IsNullOrWhiteSpace(found.PhoneNumber)
                    && found.Ccid != scannedCcid)
                {
                    Logger.Info($"🔗 CCID fuzzy match! phone={found.PhoneNumber} từ CCID={found.Ccid} → scan={scannedCcid} (trùng 18 số: {sub18})");
                    return found;
                }
            }
        }

        // Trả về exact match (dù không phone) hoặc null
        return exactMatch;
    }

    /// <summary>Tìm SIM theo phoneNumber.</summary>
    public SimDocument? FindByPhoneNumber(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        return _simsCollection.Find(s => s.PhoneNumber == phone).FirstOrDefault();
    }

    /// <summary>Tìm SIM theo IMSI.</summary>
    public SimDocument? FindByImsi(string imsi)
    {
        if (string.IsNullOrWhiteSpace(imsi)) return null;
        return _simsCollection.Find(s => s.Imsi == imsi).FirstOrDefault();
    }

    /// <summary>Tìm SIM theo comName.</summary>
    public SimDocument? FindByComName(string comName)
    {
        if (string.IsNullOrWhiteSpace(comName)) return null;
        return _simsCollection.Find(s => s.ComName == comName).FirstOrDefault();
    }

    /// <summary>Lấy tất cả SIM theo deviceName.</summary>
    public List<SimDocument> FindByDeviceName(string deviceName)
    {
        return _simsCollection.Find(s => s.DeviceName == deviceName).ToList();
    }

    /// <summary>Lấy tất cả SIM.</summary>
    public List<SimDocument> FindAll()
    {
        return _simsCollection.Find(_ => true).ToList();
    }

    // ================== SAVE / UPDATE ==================

    /// <summary>Upsert SIM document (insert hoặc update theo _id).</summary>
    public void Save(SimDocument doc)
    {
        var filter = Builders<SimDocument>.Filter.Eq(s => s.Id, doc.Id);
        var options = new ReplaceOptions { IsUpsert = true };
        _simsCollection.ReplaceOne(filter, doc, options);
    }

    /// <summary>Batch save nhiều SIM.</summary>
    public void SaveAll(IEnumerable<SimDocument> docs)
    {
        var bulkOps = new List<WriteModel<SimDocument>>();
        foreach (var doc in docs)
        {
            var filter = Builders<SimDocument>.Filter.Eq(s => s.Id, doc.Id);
            bulkOps.Add(new ReplaceOneModel<SimDocument>(filter, doc) { IsUpsert = true });
        }
        if (bulkOps.Count > 0)
        {
            _simsCollection.BulkWrite(bulkOps);
        }
    }

    /// <summary>Xóa SIM document.</summary>
    public void Delete(string id)
    {
        _simsCollection.DeleteOne(s => s.Id == id);
    }

    /// <summary>
    /// Set tất cả SIM của device sang INACTIVE (khi app shutdown).
    /// Giống SimShutdownHandler.java.
    /// </summary>
    public void SetAllInactive(string deviceName)
    {
        var filter = Builders<SimDocument>.Filter.Eq(s => s.DeviceName, deviceName);
        var update = Builders<SimDocument>.Update
            .Set(s => s.Status, "INACTIVE")
            .Set(s => s.LastUpdated, DateTime.UtcNow);
        var result = _simsCollection.UpdateMany(filter, update);
        Logger.Info($"🛑 Set {result.ModifiedCount} SIMs INACTIVE cho device '{deviceName}'");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            // MongoClient doesn't need explicit disposal
        }
    }
}
