using System.Linq;
using GsmAgent.Services;

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

    /// <summary>Thông tin lỗi khi scan (COM port hỏng, modem không phản hồi, v.v.)</summary>
    public string? ErrorMessage { get; set; }

    // 🩺 SIM Diagnostics
    /// <summary>Kết quả chẩn đoán SIM chi tiết (từ AT+CPIN?, AT+CREG?)</summary>
    public DiagnosticCategory Diagnostic { get; set; } = DiagnosticCategory.Unknown;
    /// <summary>Mô tả chi tiết chẩn đoán</summary>
    public string? DiagnosticDetail { get; set; }
    /// <summary>Trạng thái PIN SIM (READY, SIM PIN, NOT INSERTED, v.v.)</summary>
    public string? SimPinStatus { get; set; }
    /// <summary>Trạng thái đăng ký mạng (0-5, -1=unknown)</summary>
    public int NetworkRegStatus { get; set; } = -1;

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

    /// <summary>SIM đã scan nhưng không đọc được số điện thoại.</summary>
    public bool IsMissingPhone => Status != SimStatus.Error && string.IsNullOrWhiteSpace(PhoneNumber) && !string.IsNullOrWhiteSpace(Ccid);

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

    /// <summary>Trạng thái text thuần (không emoji) — cho bảng kiểu Dangs Modem.</summary>
    public string StatusText => this switch
    {
        { Diagnostic: DiagnosticCategory.ComPortBroken } => "COM hỏng",
        { Diagnostic: DiagnosticCategory.ComPortNoSim } => "Không có SIM",
        { Diagnostic: DiagnosticCategory.SimBroken } => "SIM hỏng",
        { Diagnostic: DiagnosticCategory.SimNeedPin } => "Cần PIN",
        { Diagnostic: DiagnosticCategory.SimLocked } => "SIM khóa",
        { Diagnostic: DiagnosticCategory.SimNoNetwork } => "Mất mạng",
        { Diagnostic: DiagnosticCategory.SimDenied } => "Bị từ chối",
        { Diagnostic: DiagnosticCategory.SimSearching } => "Tìm mạng",
        { Diagnostic: DiagnosticCategory.SimBusy } => "SIM bận",
        { Diagnostic: DiagnosticCategory.SimNotReady } => "Chưa sẵn sàng",
        { Status: SimStatus.Error } => "Hỏng",
        { IsMissingPhone: true, Status: SimStatus.Online } => "Thiếu SĐT",
        { IsBlocked: true } => "Bị chặn",
        { IsRateLimited: true } => "Giới hạn",
        { Status: SimStatus.Online } => "Hoạt động",
        { Status: SimStatus.Busy } => "Đang gửi",
        { Status: SimStatus.Offline } => "Chưa kết nối",
        _ => "Ổn định"
    };

    /// <summary>Icon chẩn đoán — hiện ở cột trạng thái</summary>
    public string DiagnosticIcon => Diagnostic switch
    {
        DiagnosticCategory.ComPortBroken => "🔴",
        DiagnosticCategory.ComPortNoSim => "⚫",
        DiagnosticCategory.SimBroken => "💀",
        DiagnosticCategory.SimNeedPin => "🔐",
        DiagnosticCategory.SimLocked => "🔒",
        DiagnosticCategory.SimNoNetwork => "📵",
        DiagnosticCategory.SimDenied => "🚫",
        DiagnosticCategory.SimSearching => "🔍",
        DiagnosticCategory.SimBusy => "⏳",
        DiagnosticCategory.SimNotReady => "⏳",
        DiagnosticCategory.SimOk => "✅",
        _ => "⚪"
    };

    /// <summary>Mô tả đăng ký mạng</summary>
    public string NetworkRegDisplay => NetworkRegStatus switch
    {
        0 => "Chưa đăng ký",
        1 => "Home",
        2 => "Đang tìm...",
        3 => "Bị từ chối",
        4 => "Không rõ",
        5 => "Roaming",
        _ => "N/A"
    };

    /// <summary>Slot hiển thị dựa trên COM port number (COM33 → 1/31).</summary>
    public string SlotDisplay
    {
        get
        {
            var digits = new string(ComPort.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var num) && num > 0)
            {
                // Dự đoán: SIM slot = (port - base) / 2 + 1, chuẩn modem pool 8/16/32 port
                return $"1/{(num % 2 == 0 ? "2G" : "4G")}";
            }
            return "";
        }
    }

    /// <summary>Quốc gia dựa trên IMSI prefix.</summary>
    public string CountryDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Imsi)) return "";
            if (Imsi.StartsWith("440") || Imsi.StartsWith("441")) return "🇯🇵 Nhật Bản";
            if (Imsi.StartsWith("452")) return "🇻🇳 Việt Nam";
            if (Imsi.StartsWith("310") || Imsi.StartsWith("311")) return "🇺🇸 Mỹ";
            if (Imsi.StartsWith("460")) return "🇨🇳 Trung Quốc";
            return Imsi[..3];
        }
    }

    public string DailyCountDisplay => $"{DailySentCount}/{DailyLimit}";

    /// <summary>Display name cho dropdown: "COM67 | Rakuten Mobile (JP) | +817083290870"</summary>
    public string SimDisplayName =>
        $"{ComPort} | {Provider ?? "?"} | {PhoneNumber ?? "Chưa có số"}";

    public override string ToString() => SimDisplayName;

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
