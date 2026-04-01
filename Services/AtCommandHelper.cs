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

                bool isUnicode = !Regex.IsMatch(content, @"^[\x00-\x7F]*$");
                string normalizedDest = NormalizeNumber(destNumber);

                var cmgfResp = SendAndRead("AT+CMGF=1", 500);

                string cmgsDest;
                string actualContent;

                if (isUnicode)
                {
                    // 🔥 Unicode: CSCS=UCS2, số ĐT cũng phải encode UCS2 hex
                    var cscsResp = SendAndRead("AT+CSCS=\"UCS2\"", 500);
                    var csmpResp = SendAndRead("AT+CSMP=17,167,0,8", 500);
                    cmgsDest = EncodeUcs2(normalizedDest);  // "+81..." → "002B003800310030..."
                    actualContent = EncodeUcs2(content);
                    System.Diagnostics.Debug.WriteLine(
                        $"📤 SendSms SETUP: unicode=true, CMGF={cmgfResp.Contains("OK")}, CSCS={cscsResp.Contains("OK")}, CSMP={csmpResp.Contains("OK")}");
                }
                else
                {
                    // ASCII: CSCS=GSM, số ĐT plain text
                    var cscsResp = SendAndRead("AT+CSCS=\"GSM\"", 500);
                    var csmpResp = SendAndRead("AT+CSMP=17,167,0,0", 500);
                    cmgsDest = normalizedDest;
                    actualContent = content;
                    System.Diagnostics.Debug.WriteLine(
                        $"📤 SendSms SETUP: unicode=false, CMGF={cmgfResp.Contains("OK")}, CSCS={cscsResp.Contains("OK")}, CSMP={csmpResp.Contains("OK")}");
                }

                System.Diagnostics.Debug.WriteLine(
                    $"📤 SendSms: dest={normalizedDest}, unicode={isUnicode}, contentLen={content.Length}");

                // Đọc buffer trước, check URC trước khi discard
                if (_port.BytesToRead > 0)
                {
                    var pending = _port.ReadExisting();
                    if (pending.Contains("+CMTI:") || pending.Contains("+CMT:"))
                        _pendingUrc = true;
                }
                _port.DiscardInBuffer();
                _port.Write($"AT+CMGS=\"{cmgsDest}\"\r");

                // Đợi prompt > với timeout
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
                                $"❌ SendSms: AT+CMGS ERROR: {promptStr}");
                            return false;
                        }
                    }
                    Thread.Sleep(100);
                }

                if (!gotPrompt)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"❌ SendSms: No '>' prompt after 5s (got: {promptSb})");
                    try { _port.Write(new byte[] { 0x1B }, 0, 1); } catch { }
                    return false;
                }

                // Gửi content + Ctrl+Z
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
                        if (result.Contains("+CMGS:"))
                            gotCmgs = true;

                        if (result.Contains("OK"))
                        {
                            var cleanResult = result.Replace("\r", " ").Replace("\n", " ").Trim();
                            if (gotCmgs)
                            {
                                // ✅ Chuẩn: có +CMGS: <mr> + OK → network đã chấp nhận tin nhắn
                                var mrMatch = Regex.Match(result, @"\+CMGS:\s*(\d+)");
                                var mr = mrMatch.Success ? mrMatch.Groups[1].Value : "?";
                                System.Diagnostics.Debug.WriteLine(
                                    $"✅ SendSms OK to {normalizedDest} (MR={mr}, unicode={isUnicode}) | Response: {cleanResult}");
                                return true;
                            }
                            else
                            {
                                // ⚠️ Modem trả OK nhưng KHÔNG có +CMGS: → tin nhắn có thể chưa thực sự gửi đi
                                System.Diagnostics.Debug.WriteLine(
                                    $"⚠️ SendSms: modem trả OK nhưng thiếu +CMGS: → coi là FAIL | dest={normalizedDest} | Response: {cleanResult}");
                                return false;
                            }
                        }
                        if (result.Contains("ERROR"))
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"❌ SendSms ERROR: {result.Replace("\r", " ").Replace("\n", " ")}");
                            return false;
                        }
                    }
                    Thread.Sleep(100);
                }

                var finalResult = sb.ToString();
                System.Diagnostics.Debug.WriteLine(
                    $"⚠️ SendSms timeout (gotCmgs={gotCmgs}): {finalResult.Replace("\r", " ").Replace("\n", " ")}");

                // Nếu timeout nhưng đã có +CMGS: → có thể modem chậm trả OK, coi là thành công
                return gotCmgs;
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

    /// <summary>Set text mode + UCS2 charset — gọi trước khi ListUnreadSms.
    /// UCS2 charset để modem trả content tiếng Nhật/Trung/Việt dạng hex (decode được).
    /// AT command keywords như "REC UNREAD" không bị ảnh hưởng bởi CSCS.</summary>
    public void PrepareForRead()
    {
        try
        {
            SendAndRead("AT+CMGF=1", 500);
            SendAndRead("AT+CSCS=\"UCS2\"", 500);
        }
        catch { }
    }

    /// <summary>Đọc SMS UNREAD only (giống Java: listUnreadSmsText).</summary>
    public List<(int index, string sender, string content, DateTime time)> ListUnreadSms(int timeoutMs = 5000)
    {
        var messages = new List<(int, string, string, DateTime)>();
        try
        {
            // Khi CSCS=UCS2, mọi chuỗi text trong ngoặc kép phải ở dạng UCS2 hex.
            // "REC UNREAD" -> "00520045004300200055004E0052004500410044"
            string unreadHex = EncodeUcs2("REC UNREAD");
            var resp = SendAndRead($"AT+CMGL=\"{unreadHex}\"", timeoutMs);
            
            if (resp.Contains("ERROR") || string.IsNullOrWhiteSpace(resp))
            {
                // Fallback 1: Thử integer '0' (0 = REC UNREAD trong PDU mode nhưng nhiều modem text mode hỗ trợ)
                resp = SendAndRead("AT+CMGL=0", timeoutMs);
            }

            if (resp.Contains("ERROR") || (string.IsNullOrWhiteSpace(resp) && !resp.Contains("+CMGL")))
            {
                // Fallback 2: Thử đọc "ALL" bằng UCS2 hex ("ALL" -> 0041004C004C) 
                resp = SendAndRead($"AT+CMGL=\"{EncodeUcs2("ALL")}\"", timeoutMs);
            }

            if (resp.Contains("ERROR") || (string.IsNullOrWhiteSpace(resp) && !resp.Contains("+CMGL")))
            {
                // Fallback 3: Thử đọc "ALL" bằng ASCII thường (nhiều modem bỏ qua CSCS cho chữ ALL)
                resp = SendAndRead("AT+CMGL=\"ALL\"", timeoutMs);
            }

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
                string sender = quotedFields.ElementAtOrDefault(1) ?? (quotedFields.Count > 0 ? quotedFields[0] : "");
                string timestamp = quotedFields.LastOrDefault(f =>
                    Regex.IsMatch(f, @"^\d{2,4}/\d{2}/\d{2},\d{2}:\d{2}:\d{2}(?:[+-]\d{2})?$")) ?? "";
                
                string content = lines[i + 1].Trim();
                // Content không nằm trong ngoặc kép theo format CMGL, nên phải tự decode riêng phần này
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
            {
                // Decode ngay lập tức vì khi CSCS=UCS2, modem có thể trả về MỌI field (kể cả timestamp) dạng UCS2 hex
                fields.Add(DecodeUcs2IfNeeded(match.Groups[1].Value));
            }
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

    // ==================== VOICE CALL ====================

    /// <summary>Gọi điện thoại (voice call). Semicolon quan trọng để modem hiểu là voice call.</summary>
    public bool MakeVoiceCall(string destNumber)
    {
        lock (_lock)
        {
            try
            {
                if (!_port.IsOpen) return false;
                var normalized = NormalizeNumber(destNumber);

                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();
                _port.Write($"ATD{normalized};\r");

                // Đợi response (OK = dialing started, ERROR = failed)
                var sb = new StringBuilder();
                var deadline = DateTime.Now.AddMilliseconds(10000);
                while (DateTime.Now < deadline)
                {
                    if (_port.BytesToRead > 0)
                    {
                        sb.Append(_port.ReadExisting());
                        var resp = sb.ToString();
                        if (resp.Contains("OK")) return true;
                        if (resp.Contains("ERROR") || resp.Contains("NO CARRIER") || resp.Contains("NO DIALTONE"))
                            return false;
                    }
                    Thread.Sleep(100);
                }

                // Nhiều modem không trả OK ngay mà bắt đầu gọi luôn → coi như thành công
                return !sb.ToString().Contains("ERROR");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ MakeVoiceCall failed: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Cúp máy.</summary>
    public bool HangUp()
    {
        try
        {
            var resp = SendAndRead("ATH", 3000);
            return resp.Contains("OK");
        }
        catch
        {
            // Force hangup bằng cách gửi ATH nhiều lần
            try
            {
                _port.Write("ATH\r");
                Thread.Sleep(500);
                _port.Write("ATH\r");
            }
            catch { }
            return false;
        }
    }

    /// <summary>
    /// Poll trạng thái cuộc gọi bằng AT+CLCC.
    /// Returns: -1=no call, 0=active, 2=dialing, 3=alerting/ringing, 6=released.
    /// </summary>
    public int GetCallStatus()
    {
        lock (_lock)
        {
            try
            {
                if (!_port.IsOpen) return -1;

                _port.DiscardInBuffer();
                _port.Write("AT+CLCC\r");
                Thread.Sleep(300);

                var sb = new StringBuilder();
                var deadline = DateTime.Now.AddMilliseconds(1500);
                while (DateTime.Now < deadline)
                {
                    if (_port.BytesToRead > 0)
                        sb.Append(_port.ReadExisting());
                    if (sb.ToString().Contains("OK") || sb.ToString().Contains("ERROR"))
                        break;
                    Thread.Sleep(50);
                }

                var resp = sb.ToString();
                // +CLCC: <idx>,<dir>,<stat>,<mode>,<mpty>[,<number>,<type>]
                var match = Regex.Match(resp, @"\+CLCC:\s*\d+,\d+,(\d+)");
                if (match.Success)
                    return int.Parse(match.Groups[1].Value);

                // Không có +CLCC line = không có cuộc gọi nào
                return -1;
            }
            catch { return -1; }
        }
    }

    /// <summary>Bật CLCC unsolicited result code (tùy chọn).</summary>
    public bool EnableClcc()
    {
        var resp = SendAndRead("AT+CLCC=1", 1000);
        return resp.Contains("OK");
    }

    /// <summary>
    /// Bắt đầu ghi âm cuộc gọi trên modem.
    /// Thử Quectel (AT+QAUDREC) → SIMCom (AT+CREC).
    /// Returns: true nếu modem hỗ trợ ghi âm.
    /// </summary>
    public bool StartCallRecording(string filename, int durationSec)
    {
        // Try 1: Quectel style — AT+QAUDREC=1,0,2,<duration>
        // Params: 1=start, 0=no play, 2=both directions, duration
        var resp = SendAndRead($"AT+QAUDREC=1,0,2,{durationSec}", 2000);
        if (resp.Contains("OK"))
        {
            System.Diagnostics.Debug.WriteLine("🎙️ Quectel recording started");
            return true;
        }

        // Try 2: SIMCom SIM7600 style — AT+CREC=1,"filename",0,100
        resp = SendAndRead($"AT+CREC=1,\"{filename}\",0,100", 2000);
        if (resp.Contains("OK"))
        {
            System.Diagnostics.Debug.WriteLine("🎙️ SIMCom recording started");
            return true;
        }

        // Try 3: Generic — AT+CREC=1
        resp = SendAndRead("AT+CREC=1", 2000);
        if (resp.Contains("OK"))
        {
            System.Diagnostics.Debug.WriteLine("🎙️ Generic recording started");
            return true;
        }

        System.Diagnostics.Debug.WriteLine("⚠️ Modem không hỗ trợ ghi âm");
        return false;
    }

    /// <summary>Dừng ghi âm.</summary>
    public bool StopCallRecording()
    {
        // Try Quectel
        var resp = SendAndRead("AT+QAUDREC=0", 2000);
        if (resp.Contains("OK")) return true;

        // Try SIMCom
        resp = SendAndRead("AT+CREC=0", 2000);
        if (resp.Contains("OK")) return true;

        return false;
    }

    /// <summary>
    /// Tải file ghi âm từ modem về local.
    /// Thử Quectel AT+QFDWL → SIMCom AT+FSREAD.
    /// Returns: binary data hoặc null nếu không hỗ trợ.
    /// </summary>
    public byte[]? DownloadRecordingFile(string modemFilePath, int timeoutMs = 30000)
    {
        lock (_lock)
        {
            if (!_port.IsOpen) return null;
            try
            {
                _port.DiscardInBuffer();
                _port.Write($"AT+QFDWL=\"{modemFilePath}\"\r");

                // Đợi CONNECT <filesize>
                var headerSb = new StringBuilder();
                var deadline = DateTime.Now.AddMilliseconds(5000);
                while (DateTime.Now < deadline)
                {
                    if (_port.BytesToRead > 0)
                    {
                        headerSb.Append(_port.ReadExisting());
                        var header = headerSb.ToString();
                        if (header.Contains("CONNECT")) break;
                        if (header.Contains("ERROR")) return null;
                    }
                    Thread.Sleep(50);
                }

                var connectMatch = Regex.Match(headerSb.ToString(), @"CONNECT\s+(\d+)");
                if (!connectMatch.Success) return null;
                var fileSize = int.Parse(connectMatch.Groups[1].Value);

                System.Diagnostics.Debug.WriteLine($"📥 Downloading {fileSize} bytes from modem...");

                // Đọc binary data
                var data = new byte[fileSize];
                int totalRead = 0;
                deadline = DateTime.Now.AddMilliseconds(timeoutMs);

                while (totalRead < fileSize && DateTime.Now < deadline)
                {
                    if (_port.BytesToRead > 0)
                    {
                        int toRead = Math.Min(_port.BytesToRead, fileSize - totalRead);
                        int read = _port.Read(data, totalRead, toRead);
                        totalRead += read;
                    }
                    Thread.Sleep(10);
                }

                if (totalRead < fileSize)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"⚠️ Download incomplete: {totalRead}/{fileSize} bytes");
                    return null;
                }

                System.Diagnostics.Debug.WriteLine($"✅ Download complete: {totalRead} bytes");
                return data;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ DownloadRecordingFile failed: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Xóa file trên modem.</summary>
    public bool DeleteModemFile(string filename)
    {
        try
        {
            var resp = SendAndRead($"AT+QFDEL=\"{filename}\"", 2000);
            return resp.Contains("OK");
        }
        catch { return false; }
    }

    /// <summary>Liệt kê file trên modem (debug).</summary>
    public string ListModemFiles()
    {
        return SendAndRead("AT+QFLST", 3000);
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
