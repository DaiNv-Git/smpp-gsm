namespace GsmAgent.Models;

public class AppSettings
{
    public string ServerUrl { get; set; } = "http://localhost:3000";
    public string AgentId { get; set; } = Environment.MachineName;
    public int SmsCooldownMs { get; set; } = 2000;
    public int MaxRetries { get; set; } = 3;
    public int MinSignalLevel { get; set; } = 8;
    public int DailyLimitPerSim { get; set; } = 150;
    public int ScanIntervalSec { get; set; } = 300;
    public bool AutoConnect { get; set; } = true;
    public bool AutoScan { get; set; } = true;
    public bool AutoStartWithWindows { get; set; } = false;
    public int BaudRate { get; set; } = 115200;
}
