namespace GsmAgent.Models;

public class SimCard
{
    public string ComPort { get; set; } = "";
    public string? PhoneNumber { get; set; }
    public string? Ccid { get; set; }
    public string? Imsi { get; set; }
    public string? Provider { get; set; }
    public int SignalLevel { get; set; } = -1;
    public SimStatus Status { get; set; } = SimStatus.Offline;
    public int QueueSize { get; set; }
    public int TotalSent { get; set; }
    public int TotalFailed { get; set; }
    public DateTime? LastActiveAt { get; set; }

    // Rate limiter: 150 SMS/day per SIM
    public int DailySentCount { get; set; }
    public DateTime DailyResetDate { get; set; } = DateTime.Today;
    public int DailyLimit { get; set; } = 150;

    // Carrier blocking: fail >30% → disable 10 min
    public int ConsecutiveFails { get; set; }
    public DateTime? BlockedUntil { get; set; }

    public bool IsBlocked => BlockedUntil.HasValue && DateTime.Now < BlockedUntil.Value;

    public bool IsRateLimited
    {
        get
        {
            if (DailyResetDate.Date < DateTime.Today)
            {
                DailySentCount = 0;
                DailyResetDate = DateTime.Today;
            }
            return DailySentCount >= DailyLimit;
        }
    }

    public bool IsAvailable => Status == SimStatus.Online && !IsBlocked && !IsRateLimited;

    public double SuccessRate => TotalSent + TotalFailed > 0
        ? (double)TotalSent / (TotalSent + TotalFailed) * 100
        : 0;

    public string SignalDisplay => SignalLevel >= 0 ? $"{SignalLevel}/31" : "N/A";
    public string StatusDisplay => this switch
    {
        { IsBlocked: true } => "🚫 Blocked",
        { IsRateLimited: true } => "⛔ Rate limited",
        { Status: SimStatus.Online } => "🟢 Online",
        { Status: SimStatus.Busy } => "🟡 Đang gửi",
        { Status: SimStatus.Offline } => "🔴 Offline",
        _ => "⚪ Unknown"
    };

    public string DailyCountDisplay => $"{DailySentCount}/{DailyLimit}";

    /// <summary>Display name cho dropdown: "COM67 | Rakuten Mobile (JP) | +817083290870"</summary>
    public string SimDisplayName =>
        $"{ComPort} | {Provider ?? "?"} | {PhoneNumber ?? "Chưa có số"}";

    public void RecordSuccess()
    {
        TotalSent++;
        DailySentCount++;
        ConsecutiveFails = 0;
        LastActiveAt = DateTime.Now;
    }

    public void RecordFailure()
    {
        TotalFailed++;
        ConsecutiveFails++;
        // Block SIM 5 min if 5 consecutive fails
        if (ConsecutiveFails >= 5)
        {
            BlockedUntil = DateTime.Now.AddMinutes(5);
            ConsecutiveFails = 0;
        }
    }
}

public enum SimStatus
{
    Offline,
    Online,
    Busy,
    Error
}
