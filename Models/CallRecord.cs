namespace GsmAgent.Models;

/// <summary>Lịch sử cuộc gọi.</summary>
public class CallRecord
{
    public string Id { get; set; } = $"CALL-{DateTime.Now.Ticks}";
    public string ComPort { get; set; } = "";
    public string? PhoneNumber { get; set; }
    public string DestNumber { get; set; } = "";
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? AnsweredAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public CallState State { get; set; } = CallState.Idle;
    public bool RecordingEnabled { get; set; }
    public string? RecordingPath { get; set; }
    public int TargetDurationSec { get; set; }
    public int ActualDurationSec { get; set; }

    // Display properties
    public string StateIcon => State switch
    {
        CallState.Dialing => "📞",
        CallState.Ringing => "🔔",
        CallState.Active => "🟢",
        CallState.Ended => "✅",
        CallState.NoAnswer => "❌",
        CallState.Failed => "❌",
        CallState.Busy => "🚫",
        _ => "⚪"
    };

    public string StateText => State switch
    {
        CallState.Idle => "Sẵn sàng",
        CallState.Dialing => "Đang gọi...",
        CallState.Ringing => "Đang đổ chuông...",
        CallState.Active => "Đã bắt máy",
        CallState.Ended => "Đã kết thúc",
        CallState.NoAnswer => "Không bắt máy",
        CallState.Failed => "Thất bại",
        CallState.Busy => "Máy bận",
        _ => "?"
    };

    public string DurationDisplay
    {
        get
        {
            if (ActualDurationSec <= 0) return "—";
            var ts = TimeSpan.FromSeconds(ActualDurationSec);
            return ts.TotalMinutes >= 1 ? $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}" : $"{ts.Seconds}s";
        }
    }

    public string RecordingDisplay => RecordingPath != null ? "🎙️ Có" : "—";
    public bool HasRecording => !string.IsNullOrWhiteSpace(RecordingPath) && System.IO.File.Exists(RecordingPath);
    public string TimeDisplay => StartedAt.ToString("HH:mm:ss");
}

public enum CallState
{
    Idle,
    Dialing,
    Ringing,
    Active,
    Ended,
    NoAnswer,
    Failed,
    Busy
}
