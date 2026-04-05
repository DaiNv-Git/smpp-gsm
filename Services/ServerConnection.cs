using System;
using GsmAgent.Models;
using Newtonsoft.Json;
using SocketIOClient;

namespace GsmAgent.Services;

/// <summary>
/// ServerConnection — Socket.IO client kết nối tới SMPP server GsmGateway.
/// Nhận sms:send, gửi sms:result, heartbeat.
/// </summary>
public class ServerConnection : IDisposable
{
    private SocketIOClient.SocketIO? _socket;
    private readonly AppSettings _settings;
    private readonly SerialPortManager _portManager;
    private System.Timers.Timer? _heartbeatTimer;
    private bool _disposed;

    public bool IsConnected => _socket?.Connected ?? false;
    public event Action<bool>? ConnectionChanged;
    public event Action<string>? LogMessage;
    public event Action<SmsTask>? SmsReceived;
    public event Action<string, string>? SmsDispatchFailed;

    public ServerConnection(AppSettings settings, SerialPortManager portManager)
    {
        _settings = settings;
        _portManager = portManager;
    }

    public async Task ConnectAsync()
    {
        try
        {
            var url = _settings.ServerUrl.TrimEnd('/');
            LogMessage?.Invoke($"🔗 Connecting to {url}/ws ...");

            _socket = new SocketIOClient.SocketIO(url + "/ws", new SocketIOOptions
            {
                Query = new List<KeyValuePair<string, string>>
                {
                    new("type", "gsm-agent"),
                    new("agentId", _settings.AgentId),
                },
                Reconnection = true,
                ReconnectionAttempts = 5,       // Giới hạn retry (không spam vô tận)
                ReconnectionDelay = 10000,       // 10s giữa mỗi lần thử
            });

            _socket.OnConnected += (s, e) =>
            {
                LogMessage?.Invoke("✅ Kết nối server thành công!");
                Logger.Info("✅ SocketIO Kết nối server thành công!");
                ConnectionChanged?.Invoke(true);

                // Report available SIMs
                ReportSims();
            };

            _socket.OnDisconnected += (s, e) =>
            {
                LogMessage?.Invoke("🔌 Mất kết nối server: " + e);
                Logger.Error($"Socket disconnected. Reason: {e}");
                ConnectionChanged?.Invoke(false);
            };

            _socket.OnError += (s, e) =>
            {
                Logger.Error($"Socket error: {e}");
            };

            _socket.OnReconnectAttempt += (s, e) =>
            {
                LogMessage?.Invoke($"🔄 Đang kết nối lại... (lần {e})");
            };

            // 🔥 Cơ chế "Ngắt mạch" (Circuit Breaker) & Tự động kết nối lại sau 5 phút ngủ đông
            _socket.OnReconnectFailed += async (s, e) =>
            {
                LogMessage?.Invoke("🚨 Đã hết 5 lần thử kết nối (ngắt mạch). App sẽ vào chế độ ngủ và tự động thử lại sau 5 phút...");
                
                // Ngủ đông 5 phút (300,000 ms)
                await Task.Delay(300_000); 

                if (!_disposed)
                {
                    LogMessage?.Invoke("🔄 Hết 5 phút nghỉ. Bắt đầu chu kỳ kết nối mới...");
                    
                    // Cleanup socket cũ trước khi retry tái sinh
                    try { await _socket.DisconnectAsync(); _socket.Dispose(); } catch { }
                    
                    // Gọi lại hàm ConnectAsync để khởi tạo và thử lại 5 lần nữa
                    _ = ConnectAsync(); 
                }
            };

            // Handle sms:send from server
            _socket.On("sms:send", response =>
            {
                try
                {
                    string? json = null;
                    try 
                    { 
                        json = response.GetValue<System.Text.Json.JsonElement>(0).GetRawText(); 
                    } 
                    catch 
                    { 
                        try { json = response.GetValue<string>(0); } catch { } 
                    }
                    
                    if (string.IsNullOrEmpty(json))
                    {
                        LogMessage?.Invoke($"⚠️ sms:send: không đọc được dữ liệu từ server. Raw: {response}");
                        return;
                    }

                    LogMessage?.Invoke($"📦 Raw Backend Payload: {json}");
                    
                    // Parse safely avoiding dynamic issues
                    var data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);

                    var task = new SmsTask
                    {
                        MessageId = data.TryGetProperty("messageId", out var mid) ? mid.GetString() ?? "" : "",
                        SourceAddr = data.TryGetProperty("sourceAddr", out var sa) ? sa.GetString() ?? "" : "",
                        DestAddr = data.TryGetProperty("destAddr", out var da) ? da.GetString() ?? "" : "",
                        Content = data.TryGetProperty("shortMessage", out var c) ? c.GetString() ?? "" : "",
                        AccountId = data.TryGetProperty("accountId", out var aid) && aid.TryGetInt32(out var accId) ? accId : 0,
                        SystemId = data.TryGetProperty("systemId", out var sid) ? sid.GetString() ?? "" : "",
                    };

                    LogMessage?.Invoke($"📨 Nhận SMS dispatch: {task.DestAddr} (from {task.SystemId}) [MsgID: {task.MessageId}]");
                    SmsReceived?.Invoke(task);

                    // Dispatch to modem
                    var dispatched = _portManager.DispatchSms(task);
                    if (!dispatched)
                    {
                        var errorMsg = "Không có modem khả dụng hoặc bị lỗi";
                        // No modem available — report failure immediately
                        LogMessage?.Invoke($"❌ Không có modem khả dụng cho SMS {task.MessageId}");
                        _ = SendSmsResult(task.MessageId, "FAILED", null, null, errorMsg);
                        SmsDispatchFailed?.Invoke(task.MessageId, errorMsg);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"❌ sms:send CATCH BÌNH THƯỜNG error: {ex.Message} \nStack: {ex.StackTrace}");
                    LogMessage?.Invoke($"❌ Parse sms:send error: {ex.Message}");
                }
            });

            await _socket.ConnectAsync();

            // Start heartbeat
            StartHeartbeat();
        }
        catch (Exception ex)
        {
            Logger.Error($"❌ Connection failed: {ex.Message} \nStack: {ex.StackTrace}");
            LogMessage?.Invoke($"❌ Connection failed: {ex.Message}");
            ConnectionChanged?.Invoke(false);
        }
    }

    public async Task SendSmsResult(string messageId, string status, string? simPhone, string? deviceName, string? error)
    {
        if (_socket == null) return;
        try
        {
            await _socket.EmitAsync("sms:result", new
            {
                messageId,
                status,
                simPhoneNumber = simPhone,
                deviceName = deviceName ?? _settings.AgentId,
                errorMessage = error,
            });
            Logger.Info($"=> Đã đẩy sms:result ({status}) của {messageId} lên server an toàn.");
        }
        catch (Exception ex)
        {
            Logger.Error($"❌ Lỗi EmitAsync sms:result cho {messageId}: {ex.Message}");
        }
    }

    /// <summary>
    /// 📨 Gửi incoming SMS lên server để lưu lịch sử + broadcast tới dashboard.
    /// Server sẽ lưu vào MongoDB collection incoming_sms.
    /// </summary>
    public void SendIncomingSms(string sender, string receiver, string content, string? comPort, DateTime? receivedAt)
    {
        if (_socket == null || !IsConnected)
        {
            LogMessage?.Invoke($"⚠️ Không thể gửi incoming SMS lên server (chưa kết nối)");
            return;
        }

        _socket.EmitAsync("sms:incoming", new
        {
            sender,
            receiver,
            content,
            comPort,
            deviceName = _settings.AgentId,
            receivedAt = (receivedAt ?? DateTime.Now).ToString("O"),
        });

        LogMessage?.Invoke($"📨 Đã gửi incoming SMS lên server: {sender} → {receiver}");
    }

    public void ReportSims()
    {
        var sims = _portManager.Sims.Values
            .Where(s => s.Status == SimStatus.Online)
            .Select(s => new
            {
                s.ComPort,
                s.PhoneNumber,
                s.Provider,
                s.SignalLevel,
                s.Ccid,
                DeviceName = _settings.AgentId, // hostname máy
            })
            .ToList();

        _socket?.EmitAsync("agent:sims", JsonConvert.SerializeObject(sims));
    }

    private void StartHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = new System.Timers.Timer(30000); // 30s
        _heartbeatTimer.Elapsed += (s, e) =>
        {
            if (IsConnected)
            {
                _socket?.EmitAsync("agent:heartbeat", new
                {
                    agentId = _settings.AgentId,
                    simCount = _portManager.Sims.Values.Count(s => s.Status == SimStatus.Online),
                });
            }
        };
        _heartbeatTimer.Start();
    }

    public async Task DisconnectAsync()
    {
        _heartbeatTimer?.Dispose();
        if (_socket != null)
        {
            await _socket.DisconnectAsync();
            _socket.Dispose();
            _socket = null;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _heartbeatTimer?.Dispose();
            _socket?.Dispose();
            _disposed = true;
        }
    }
}
