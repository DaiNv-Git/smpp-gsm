namespace GsmAgent.Models;

public class AppSettings
{
    public string ServerUrl { get; set; } = "http://72.60.41.168:3000";
    public string AgentId { get; set; } = Environment.MachineName;
    public int SmsCooldownMs { get; set; } = 500; // 🔥 Reduced for high-throughput (AT cmd itself takes 3-8s)
    public int MaxRetries { get; set; } = 3;
    public int MinSignalLevel { get; set; } = 8;
    public int DailyLimitPerSim { get; set; } = 150;
    public int ScanIntervalSec { get; set; } = 300; // 5 phút
    public bool AutoConnect { get; set; } = true;
    public bool AutoScan { get; set; } = true;
    public bool AutoStartWithWindows { get; set; } = false;
    public int BaudRate { get; set; } = 115200;

    // MongoDB — cùng URI với simsmart-gsm (Java)
    public string MongoDbUri { get; set; } = "mongodb://admin:MongoAdmin%402026%21Secure@72.60.41.168:27017/JapanSim?authSource=admin";
    public bool EnableMongoSync { get; set; } = true;
}
