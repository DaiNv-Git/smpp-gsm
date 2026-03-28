using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;

namespace GsmAgent.Services;

/// <summary>
/// AT Command helper — giao tiếp với GSM modem qua COM port.
/// Logic tương tự AtCommandHelper trong simsmart-gsm (Java).
/// </summary>
public class AtCommandHelper : IDisposable
{
    private readonly SerialPort _port;
    private readonly object _lock = new();
    private bool _disposed;

    public bool IsOpen => _port.IsOpen;
    public string PortName => _port.PortName;

    public AtCommandHelper(string comPort, int baudRate = 115200)
    {
        _port = new SerialPort(comPort, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 3000,
            WriteTimeout = 3000,
            Encoding = Encoding.ASCII,
            NewLine = "\r\n",
            DtrEnable = true,
            RtsEnable = true,
        };
    }

    public bool Open()
    {
        try
        {
            if (!_port.IsOpen)
                _port.Open();
            Thread.Sleep(200);
            // Init modem
            SendAndRead("ATZ", 500);
            SendAndRead("ATE0", 300);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Open {_port.PortName} failed: {ex.Message}");
            return false;
        }
    }

    public void Close()
    {
        try { if (_port.IsOpen) _port.Close(); } catch { }
    }

    // 🔥 Flag lưu URC phát hiện trong buffer trước khi DiscardInBuffer
    private volatile bool _pendingUrc;

    /// <summary>Gửi AT command và đọc response.</summary>
    public string SendAndRead(string command, int timeoutMs = 2000)
    {
        lock (_lock)
        {
            if (!_port.IsOpen) return "";
            try
            {
                // 🔥 Check URC trong buffer TRƯỚC khi discard
                if (_port.BytesToRead > 0)
                {
                    var pending = _port.ReadExisting();
                    if (pending.Contains("+CMTI:") || pending.Contains("+CMT:"))
                    {
                        _pendingUrc = true;
                    }
                }
                // LUÔN discard để đảm bảo buffer sạch cho command mới
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();
                _port.Write(command + "\r");
                Thread.Sleep(Math.Min(timeoutMs, 300));

                var sb = new StringBuilder();
                var deadline = DateTime.Now.AddMilliseconds(timeoutMs);

                while (DateTime.Now < deadline)
                {
                    if (_port.BytesToRead > 0)
                    {
                        sb.Append(_port.ReadExisting());
                        var resp = sb.ToString();
                        if (resp.Contains("+CMTI:") || resp.Contains("+CMT:"))
                            _pendingUrc = true;
                        if (resp.Contains("OK") || resp.Contains("ERROR"))
                            break;
                    }
                    Thread.Sleep(50);
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"⚠️ AT {command}: {ex.Message}");
                return "";
            }
        }
    }

    /// <summary>Kiểm tra modem sống.</summary>
    public bool IsAlive()
    {
        var resp = SendAndRead("AT", 500);
        return resp.Contains("OK");
    }

    /// <summary>Lấy CCID (SIM card ID).</summary>
    public string? GetCcid()
    {
        var resp = SendAndRead("AT+CCID", 2000);
        if (string.IsNullOrWhiteSpace(resp)) return null;

        // Parse: +CCID: 8981090040025215666F or just the number
        var match = Regex.Match(resp, @"(\d{18,22}F?)", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : null;
    }

    /// <summary>Lấy IMSI.</summary>
    public string? GetImsi()
    {
        var resp = SendAndRead("AT+CIMI", 1000);
        var match = Regex.Match(resp, @"(\d{15})");
        return match.Success ? match.Value : null;
    }

    /// <summary>Lấy số điện thoại từ CNUM — 3 retry, 5 regex patterns (ported from old Java).</summary>
    public string? GetCnum()
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var resp = SendAndRead("AT+CNUM", 3000);
                System.Diagnostics.Debug.WriteLine($"📱 CNUM response (attempt {attempt}): {resp.Replace("\r", " ").Replace("\n", " ")}");

                if (string.IsNullOrWhiteSpace(resp) || resp.Trim() == "OK")
                {
                    Thread.Sleep(500);
                    continue;
                }

                // Pattern 1: Standard format: +CNUM: "Name","+819012345678",145
                var m = Regex.Match(resp, @"\+?CNUM:.*?""([^""]*)"",""(\+?\d{6,20})""");
                if (m.Success) return m.Groups[2].Value;

                // Pattern 2: Without name: +CNUM: ,"+819012345678",145
                m = Regex.Match(resp, @"\+?CNUM:\s*,""(\+?\d{6,20})""");
                if (m.Success) return m.Groups[1].Value;

                // Pattern 3: Just number in quotes: +CNUM: "+819012345678"
                m = Regex.Match(resp, @"\+?CNUM:.*?""(\+?\d{6,20})""");
                if (m.Success) return m.Groups[1].Value;

                // Pattern 4: Number without quotes: +CNUM: +819012345678
                m = Regex.Match(resp, @"\+?CNUM:.*?(\+?\d{6,20})");
                if (m.Success) return m.Groups[1].Value;

                // Pattern 5: UCS2 encoded number (Japanese SIM may return hex)
                m = Regex.Match(resp, @"\+?CNUM:.*?""([0-9A-Fa-f]{16,})""");
                if (m.Success)
                {
                    var decoded = DecodeUcs2IfNeeded(m.Groups[1].Value);
                    if (Regex.IsMatch(decoded, @"\d{6,}"))
                        return decoded;
                }

                System.Diagnostics.Debug.WriteLine($"⚠️ Could not parse CNUM (attempt {attempt}): {resp}");
                Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"⚠️ CNUM failed (attempt {attempt}): {ex.Message}");
                if (attempt < 3) Thread.Sleep(1000);
            }
        }

        System.Diagnostics.Debug.WriteLine("❌ Failed to get phone number after 3 attempts");
        return null;
    }

    /// <summary>Đọc số từ phonebook SIM (SM storage).</summary>
    public string? ReadPhonebookNumber()
    {
        SendAndRead("AT+CPBS=\"SM\"", 500);
        var resp = SendAndRead("AT+CPBR=1,5", 2000);
        var match = Regex.Match(resp, @"""(\+?\d{8,15})""");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Query nhà mạng bằng AT+COPS? (ported from old Java).</summary>
    public string? QueryOperator()
    {
        try
        {
            var resp = SendAndRead("AT+COPS?", 3000);

            // Pattern 1: +COPS: 0,0,"Softbank",2  hoặc  +COPS: 0,2,"44011",7
            var m = Regex.Match(resp, @"\+COPS:\s*\d+,\d+,""([^""]+)""");
            if (m.Success)
            {
                var raw = DecodeUcs2IfNeeded(m.Groups[1].Value);
                // 🔥 FIX: Nếu kết quả là MCC+MNC dạng số → map sang tên nhà mạng
                if (Regex.IsMatch(raw, @"^\d{5,6}$"))
                    return MapMccMnc(raw);
                return raw;
            }

            // Pattern 2: +COPS: "Softbank"
            m = Regex.Match(resp, @"\+COPS:\s*""([^""]+)""");
            if (m.Success)
            {
                var raw = DecodeUcs2IfNeeded(m.Groups[1].Value);
                if (Regex.IsMatch(raw, @"^\d{5,6}$"))
                    return MapMccMnc(raw);
                return raw;
            }

            // Try switching to alphanumeric format
            if (resp.Contains("+COPS:"))
            {
                SendAndRead("AT+COPS=3,0", 2000);
                Thread.Sleep(200);
                resp = SendAndRead("AT+COPS?", 3000);
                m = Regex.Match(resp, @"\+COPS:\s*\d+,\d+,""([^""]+)""");
                if (m.Success)
                {
                    var raw = DecodeUcs2IfNeeded(m.Groups[1].Value);
                    if (Regex.IsMatch(raw, @"^\d{5,6}$"))
                        return MapMccMnc(raw);
                    return raw;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"⚠️ QueryOperator failed: {ex.Message}");
        }
        return null;
    }

    private static string MapMccMnc(string mccMnc) => mccMnc switch
    {
        "44020" => "SoftBank (JP)",
        "44010" => "NTT Docomo (JP)",
        "44050" or "44051" or "44053" or "44054" => "KDDI/AU (JP)",
        "44000" or "44001" or "44002" or "44003" => "Y!mobile (JP)",
        "44011" => "Rakuten Mobile (JP)",
        _ => mccMnc,
    };

    /// <summary>Phát hiện số điện thoại (CNUM → phonebook → null).</summary>
    public string? DetectPhoneNumber()
    {
        // 1. Try CNUM
        var phone = GetCnum();
        if (!string.IsNullOrWhiteSpace(phone)) return NormalizeNumber(phone);

        // 2. Try phonebook
        phone = ReadPhonebookNumber();
        if (!string.IsNullOrWhiteSpace(phone)) return NormalizeNumber(phone);

        return null;
    }

    /// <summary>Ghi số điện thoại vào SIM phonebook (SM) để scan nhanh hơn lần sau.</summary>
    public bool WritePhoneToSimPhonebook(string phoneNumber)
    {
        try
        {
            SendAndRead("AT+CPBS=\"SM\"", 500);
            var resp = SendAndRead($"AT+CPBW=1,\"{phoneNumber}\",145,\"MyNumber\"", 2000);
            return resp.Contains("OK");
        }
        catch { return false; }
    }

    /// <summary>Đọc cường độ tín hiệu (0-31).</summary>
    public int GetSignalLevel()
    {
        var resp = SendAndRead("AT+CSQ", 1000);
        var match = Regex.Match(resp, @"\+CSQ:\s*(\d+),");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var level))
            return level;
        return -1;
    }

    /// <summary>Nhận diện nhà mạng từ IMSI — giống detectProvider() trong Java SimSyncService.</summary>
    public static string DetectProvider(string? imsi)
    {
        if (string.IsNullOrWhiteSpace(imsi)) return "Unknown";
        if (imsi.StartsWith("45204") || imsi.StartsWith("45205")) return "Viettel (VN)";
        if (imsi.StartsWith("45201")) return "Mobifone (VN)";
        if (imsi.StartsWith("45202")) return "Vinaphone (VN)";
        if (imsi.StartsWith("44010")) return "NTT Docomo (JP)";
        if (imsi.StartsWith("44011")) return "Rakuten Mobile (JP)";
        return "Unknown";
    }

    /// <summary>Gửi SMS text mode.</summary>
    public bool SendSms(string destNumber, string content, int timeoutMs = 30000)
    {
        lock (_lock)
        {
            try
            {
                if (!_port.IsOpen) return false;

                // Set text mode
                SendAndRead("AT+CMGF=1", 500);

                bool isUnicode = !Regex.IsMatch(content, @"^[\x00-\x7F]*$");

                string actualContent;
                // 🔥 FIX #1: Số đích LUÔN dùng format chuẩn quốc tế (giữ dấu +, KHÔNG encode UCS2)
                // AT+CMGS nhận số dạng text thường, UCS2 encoding chỉ áp dụng cho NỘI DUNG SMS
                string normalizedDest = NormalizeNumber(destNumber);

                if (isUnicode)
                {
                    SendAndRead("AT+CSCS=\"UCS2\"", 500);
                    SendAndRead("AT+CSMP=17,167,0,8", 500);
                    actualContent = EncodeUcs2(content);
                }
                else
                {
                    SendAndRead("AT+CSCS=\"IRA\"", 500);
                    SendAndRead("AT+CSMP=17,167,0,0", 500);
                    actualContent = content;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"📤 SendSms: dest={normalizedDest}, unicode={isUnicode}, contentLen={content.Length}");

                // CMGS — 🔥 FIX #2: Đợi prompt > bằng loop có timeout (không fix cứng 1s)
                // Đọc buffer trước, check URC trước khi discard
                if (_port.BytesToRead > 0)
                {
                    var pending = _port.ReadExisting();
                    if (pending.Contains("+CMTI:") || pending.Contains("+CMT:"))
                        _pendingUrc = true;
                }
                _port.DiscardInBuffer();
                _port.Write($"AT+CMGS=\"{normalizedDest}\"\r");

                var promptDeadline = DateTime.Now.AddMilliseconds(5000);
                var promptSb = new StringBuilder();
                bool gotPrompt = false;
                while (DateTime.Now < promptDeadline)
                {
                    if (_port.BytesToRead > 0)
                    {
                        promptSb.Append(_port.ReadExisting());
                        var promptStr = promptSb.ToString();
                        if (promptStr.Contains(">"))
                        {
                            gotPrompt = true;
                            break;
                        }
                        if (promptStr.Contains("ERROR"))
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"❌ SendSms: AT+CMGS returned ERROR: {promptStr}");
                            return false;
                        }
                    }
                    Thread.Sleep(100);
                }

                if (!gotPrompt)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"❌ SendSms: No '>' prompt after 5s (got: {promptSb})");
                    try { _port.Write(new byte[] { 0x1B }, 0, 1); } catch { } // ESC để cancel
                    return false;
                }

                // Send content + Ctrl+Z
                _port.Write(actualContent);
                Thread.Sleep(300);
                _port.Write(new byte[] { 0x1A }, 0, 1); // Ctrl+Z

                // Wait for result
                var sb = new StringBuilder();
                var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
                bool gotCmgs = false;
                while (DateTime.Now < deadline)
                {
                    if (_port.BytesToRead > 0)
                    {
                        sb.Append(_port.ReadExisting());
                        var result = sb.ToString();
                        if (result.Contains("+CMGS"))
                            gotCmgs = true;

                        if (result.Contains("OK"))
                            return true;
                        if (result.Contains("ERROR"))
                            return false;
                    }
                    Thread.Sleep(100);
                }

                var finalResult = sb.ToString();
                if (gotCmgs && !finalResult.Contains("ERROR"))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"⚠️ SendSms: got +CMGS but no final OK before timeout: {finalResult}");
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ SendSMS failed: {ex.Message}");
                return false;
            }
            finally
            {
                // Restore UCS2 for reading Japanese SMS
                try { SendAndRead("AT+CSCS=\"UCS2\"", 300); } catch { }
            }
        }
    }


    /// <summary>Xóa 1 SMS theo index — gọi sau khi đã đọc xong.</summary>
    public bool DeleteSms(int index)
    {
        try
        {
            var resp = SendAndRead($"AT+CMGD={index}", 2000);
            return resp.Contains("OK");
        }
        catch { return false; }
    }

    /// <summary>Xóa tất cả SMS đã đọc (flag=1) hoặc tất cả (flag=4).</summary>
    public bool DeleteAllReadSms()
    {
        try
        {
            var resp = SendAndRead("AT+CMGD=1,1", 3000); // flag 1 = delete read messages
            return resp.Contains("OK");
        }
        catch { return false; }
    }

    /// <summary>Bật URC notification cho SMS mới.</summary>
    public bool EnableUrc()
    {
        var resp = SendAndRead("AT+CNMI=2,1,0,0,0", 1000);
        return resp.Contains("OK");
    }

    /// <summary>Test xem modem có hỗ trợ URC không.</summary>
    public bool TestUrcSupport()
    {
        try
        {
            var resp = SendAndRead("AT+CNMI?", 1000);
            if (resp.Contains("+CNMI:")) return true;

            // Try enable
            resp = SendAndRead("AT+CNMI=2,1,0,0,0", 1000);
            return resp.Contains("OK");
        }
        catch { return false; }
    }

    /// <summary>Check URC buffer for +CMTI (new SMS) + pending URC từ SendAndRead.</summary>
    public bool CheckForNewSms()
    {
        // 🔥 Check URC đã phát hiện trong SendAndRead (buffer trước khi discard)
        if (_pendingUrc)
        {
            _pendingUrc = false;
            return true;
        }
        if (!_port.IsOpen || _port.BytesToRead == 0) return false;
        try
        {
            var data = _port.ReadExisting();
            return data.Contains("+CMTI:") || data.Contains("+CMT:");
        }
        catch { return false; }
    }

    /// <summary>Lấy số SMS hiện tại trong storage (Simulated URC).</summary>
    public int GetSmsCount()
    {
        try
        {
            var resp = SendAndRead("AT+CPMS?", 1000);
            var match = Regex.Match(resp, @"\+CPMS:\s*""[^""]*"",(\d+),(\d+)");
            if (match.Success)
                return int.Parse(match.Groups[1].Value);
        }
        catch { }
        return -1;
    }

    // ==================== PORTED FROM JAVA ====================

    /// <summary>Chuyển SMS storage (giống Java: scan cả ME + SM).</summary>
    public bool SetStorage(string storage)
    {
        try
        {
            var cmd = $"AT+CPMS=\"{storage}\",\"{storage}\",\"{storage}\"";
            var resp = SendAndRead(cmd, 2000);
            return resp.Contains("OK");
        }
        catch { return false; }
    }

    /// <summary>Set text mode + UCS2 1 lần — gọi trước khi ListUnreadSms/ListAllSms.</summary>
    public void PrepareForRead()
    {
        try
        {
            SendAndRead("AT+CMGF=1", 500);
            SendAndRead("AT+CSCS=\"UCS2\"", 500);
        }
        catch { }
    }

    /// <summary>Đọc SMS UNREAD only — nhanh hơn ALL (giống Java: listUnreadSmsText).</summary>
    public List<(int index, string sender, string content, DateTime time)> ListUnreadSms(int timeoutMs = 5000)
    {
        var messages = new List<(int, string, string, DateTime)>();
        try
        {
            var resp = SendAndRead("AT+CMGL=\"REC UNREAD\"", timeoutMs);
            ParseCmglResponse(resp, messages);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"⚠️ ListUnreadSms failed: {ex.Message}");
        }
        return messages;
    }


    /// <summary>Parse CMGL response chung (dùng cho cả UNREAD và ALL).</summary>
    private void ParseCmglResponse(string resp, List<(int, string, string, DateTime)> messages)
    {
        var lines = resp.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("+CMGL:"))
                continue;

            var indexMatch = Regex.Match(line, @"\+CMGL:\s*(\d+)");
            if (!indexMatch.Success || i + 1 >= lines.Length)
                continue;

            var quotedFields = ExtractQuotedFields(line);
            if (quotedFields.Count == 0)
                continue;

            if (int.TryParse(indexMatch.Groups[1].Value, out var index))
            {
                string sender = DecodeUcs2IfNeeded(quotedFields.ElementAtOrDefault(1) ?? quotedFields[0]);
                string timestamp = quotedFields.LastOrDefault(f =>
                    Regex.IsMatch(f, @"^\d{2,4}/\d{2}/\d{2},\d{2}:\d{2}:\d{2}(?:[+-]\d{2})?$")) ?? "";
                string content = lines[i + 1].Trim();
                string decodedContent = DecodeUcs2IfNeeded(content);

                var time = ParseSmsTimestamp(timestamp);
                if (!string.IsNullOrWhiteSpace(decodedContent))
                    messages.Add((index, sender, decodedContent, time));
            }
        }
    }

    private static List<string> ExtractQuotedFields(string line)
    {
        var fields = new List<string>();
        foreach (Match match in Regex.Matches(line, @"""([^""]*)"""))
        {
            if (match.Groups.Count > 1)
                fields.Add(match.Groups[1].Value);
        }
        return fields;
    }

    /// <summary>Xóa tất cả SMS đã đọc trong storage hiện tại (giống Java: AT+CMGD=1,3).</summary>
    public bool CleanupReadSms()
    {
        try
        {
            var resp = SendAndRead("AT+CMGD=1,3", 3000); // flag 3 = delete read + sent + unsent
            return resp.Contains("OK");
        }
        catch { return false; }
    }

    /// <summary>Restore charset UCS2 cho đọc SMS tiếng Nhật (giống Java: setCharset("UCS2")).</summary>
    public void RestoreUcs2Mode()
    {
        try { SendAndRead("AT+CSCS=\"UCS2\"", 300); } catch { }
    }

    private static DateTime ParseSmsTimestamp(string ts)
    {
        try
        {
            // Format phổ biến: "26/03/26,10:30:00+36" hoặc "2026/03/26,10:30:00-04"
            var value = ts.Replace("\"", "").Trim();
            var match = Regex.Match(
                value,
                @"^(?<date>\d{2,4}/\d{2}/\d{2}),(?<time>\d{2}:\d{2}:\d{2})(?<offset>[+-]\d{2})?$");

            if (match.Success)
            {
                var dateTimePart = $"{match.Groups["date"].Value},{match.Groups["time"].Value}";
                var formats = new[] { "yy/MM/dd,HH:mm:ss", "yyyy/MM/dd,HH:mm:ss" };
                if (DateTime.TryParseExact(
                    dateTimePart,
                    formats,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var dt))
                {
                    return dt;
                }
            }

            if (DateTime.TryParse(value, out var fallback))
                return fallback;
        }
        catch { }
        return DateTime.Now;
    }

    // ==================== Utilities ====================

    /// <summary>Chuẩn hóa số điện thoại — hỗ trợ cả 3 format:
    /// +81xxx → giữ nguyên (quốc tế)
    /// 81xxx  → thêm + thành +81xxx (quốc tế, thiếu dấu +)
    /// 0xxx   → giữ nguyên (local Nhật)
    /// </summary>
    public static string NormalizeNumber(string phone)
    {
        phone = phone.Trim().Replace(" ", "").Replace("-", "");
        // 81xxx (country code Nhật, thiếu +) → thêm + để modem hiểu đúng international
        if (phone.StartsWith("81") && !phone.StartsWith("+") && phone.Length >= 11)
            phone = "+" + phone;
        return phone;
    }

    public static string EncodeUcs2(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text)
            sb.AppendFormat("{0:X4}", (int)c);
        return sb.ToString();
    }

    public static string DecodeUcs2IfNeeded(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        text = text.Trim();

        // Check if UCS2 encoded (hex string, length divisible by 4)
        if (Regex.IsMatch(text, @"^[0-9A-Fa-f]+$") && text.Length % 4 == 0 && text.Length >= 8)
        {
            try
            {
                var sb = new StringBuilder();
                for (int i = 0; i < text.Length; i += 4)
                {
                    int code = Convert.ToInt32(text.Substring(i, 4), 16);
                    sb.Append((char)code);
                }
                return sb.ToString();
            }
            catch { return text; }
        }
        return text;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Close();
            _port.Dispose();
            _disposed = true;
        }
    }
}
