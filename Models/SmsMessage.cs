namespace GsmAgent.Models;

public class SmsMessage
{
    public long Id { get; set; }
    public string MessageId { get; set; } = "";
    public string ComPort { get; set; } = "";
    public string SourceAddr { get; set; } = "";
    public string DestAddr { get; set; } = "";
    public string Content { get; set; } = "";
    public string Direction { get; set; } = "OUT"; // IN or OUT
    public string Status { get; set; } = "PENDING"; // PENDING, SENT, DELIVERED, FAILED
    public string? ErrorMessage { get; set; }
    public string? SystemId { get; set; } // SMPP account
    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? SentAt { get; set; }

    public string StatusIcon => Status switch
    {
        "SENT" or "DELIVERED" => "✅",
        "FAILED" => "❌",
        "PENDING" => "⏳",
        _ => "❓"
    };
}
