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

    /// <summary>Gửi AT command và đọc response.</summary>
    public string SendAndRead(string command, int timeoutMs = 2000)
    {
        lock (_lock)
        {
            if (!_port.IsOpen) return "";
            try
            {
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
                        if (sb.ToString().Contains("OK") || sb.ToString().Contains("ERROR"))
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

            // Pattern 1: +COPS: 0,0,"Softbank",2
            var m = Regex.Match(resp, @"\+COPS:\s*\d+,\d+,""([^""]+)""");
            if (m.Success) return DecodeUcs2IfNeeded(m.Groups[1].Value);

            // Pattern 2: +COPS: "Softbank"
            m = Regex.Match(resp, @"\+COPS:\s*""([^""]+)""");
            if (m.Success) return DecodeUcs2IfNeeded(m.Groups[1].Value);

            // Pattern 3: Numeric MCC+MNC: +COPS: 0,2,"44020"
            m = Regex.Match(resp, @"\+COPS:\s*\d+,\d+,""(\d{5,6})""");
            if (m.Success) return MapMccMnc(m.Groups[1].Value);

            // Try switching to alphanumeric format
            if (resp.Contains("+COPS:"))
            {
                SendAndRead("AT+COPS=3,0", 2000);
                Thread.Sleep(200);
                resp = SendAndRead("AT+COPS?", 3000);
                m = Regex.Match(resp, @"\+COPS:\s*\d+,\d+,""([^""]+)""");
                if (m.Success) return DecodeUcs2IfNeeded(m.Groups[1].Value);
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
        "44020" => "SoftBank",
        "44010" => "NTT DoCoMo",
        "44050" or "44051" or "44053" or "44054" => "KDDI/AU",
        "44000" or "44001" or "44002" or "44003" => "Y!mobile",
        "44011" => "Rakuten Mobile",
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
                // Set text mode
                SendAndRead("AT+CMGF=1", 500);

                bool isUnicode = !Regex.IsMatch(content, @"^[\x00-\x7F]*$");

                string actualContent;
                string normalizedDest;

                if (isUnicode)
                {
                    SendAndRead("AT+CSCS=\"UCS2\"", 500);
                    SendAndRead("AT+CSMP=17,167,0,8", 500);
                    actualContent = EncodeUcs2(content);
                    normalizedDest = EncodeUcs2(NormalizeNumber(destNumber));
                }
                else
                {
                    SendAndRead("AT+CSCS=\"GSM\"", 500);
                    actualContent = content;
                    normalizedDest = NormalizeNumber(destNumber).Replace("+", "");
                }

                // CMGS
                _port.DiscardInBuffer();
                _port.Write($"AT+CMGS=\"{normalizedDest}\"\r");
                Thread.Sleep(1000);

                // Read prompt >
                var prompt = _port.ReadExisting();
                if (!prompt.Contains(">") && prompt.Contains("ERROR"))
                    return false;

                // Send content + Ctrl+Z
                _port.Write(actualContent);
                Thread.Sleep(300);
                _port.Write(new byte[] { 0x1A }, 0, 1); // Ctrl+Z

                // Wait for result
                var sb = new StringBuilder();
                var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
                while (DateTime.Now < deadline)
                {
                    if (_port.BytesToRead > 0)
                    {
                        sb.Append(_port.ReadExisting());
                        var result = sb.ToString();
                        if (result.Contains("OK") || result.Contains("+CMGS"))
                            return true;
                        if (result.Contains("ERROR"))
                            return false;
                    }
                    Thread.Sleep(100);
                }

                return sb.ToString().Contains("OK") || sb.ToString().Contains("+CMGS");
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

    /// <summary>Đọc SMS từ modem — trả kèm index để xóa sau khi đọc.</summary>
    public List<(int index, string sender, string content, DateTime time)> ReadAllSmsWithIndex()
    {
        var messages = new List<(int, string, string, DateTime)>();
        try
        {
            SendAndRead("AT+CMGF=1", 500);
            SendAndRead("AT+CSCS=\"UCS2\"", 500);
            var resp = SendAndRead("AT+CMGL=\"ALL\"", 10000);

            // Parse +CMGL: index,status,"sender","name","timestamp"
            var lines = resp.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var headerMatch = Regex.Match(lines[i], @"\+CMGL:\s*(\d+),""[^""]*"",""([^""]*)"",""[^""]*"",""([^""]*)""");
                if (headerMatch.Success && i + 1 < lines.Length)
                {
                    int index = int.Parse(headerMatch.Groups[1].Value);
                    string sender = DecodeUcs2IfNeeded(headerMatch.Groups[2].Value);
                    string timestamp = headerMatch.Groups[3].Value;
                    string content = lines[i + 1].Trim();
                    string decodedContent = DecodeUcs2IfNeeded(content);

                    // Parse timestamp: "26/03/26,10:30:00+36" → DateTime
                    var time = ParseSmsTimestamp(timestamp);

                    if (!string.IsNullOrWhiteSpace(decodedContent))
                        messages.Add((index, sender, decodedContent, time));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ ReadSMS failed: {ex.Message}");
        }
        return messages;
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

    /// <summary>Check URC buffer for +CMTI (new SMS).</summary>
    public bool CheckForNewSms()
    {
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

    private static DateTime ParseSmsTimestamp(string ts)
    {
        try
        {
            // Format: "26/03/26,10:30:00+36" or "2026/03/26,10:30:00+36"
            var parts = ts.Replace("\"", "").Split('+')[0].Split('-')[0];
            if (DateTime.TryParse(parts, out var dt)) return dt;
        }
        catch { }
        return DateTime.Now;
    }

    // ==================== Utilities ====================

    public static string NormalizeNumber(string phone)
    {
        phone = phone.Trim().Replace(" ", "");
        if (phone.StartsWith("0") && phone.Length >= 10)
            phone = "+81" + phone[1..]; // Japan format
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
