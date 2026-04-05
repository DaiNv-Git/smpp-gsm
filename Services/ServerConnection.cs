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
                ConnectionChanged?.Invoke(true);

                // Report available SIMs
                ReportSims();
            };

            _socket.OnDisconnected += (s, e) =>
            {
                LogMessage?.Invoke("🔌 Mất kết nối server");
                ConnectionChanged?.Invoke(false);
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
                    // SocketIOClient 3.x: server emit(obj) → GetValue<string> trả về JSON string
                    string? json = null;
                    try { json = response.GetValue<string>(0); } catch { }
                    
                    // Fallback: nếu server emit(JSON.stringify(obj)), GetValue trả về double-encoded
                    if (string.IsNullOrEmpty(json))
                    {
                        LogMessage?.Invoke("⚠️ sms:send: không đọc được dữ liệu từ server");
                        return;
                    }

                    var data = JsonConvert.DeserializeObject<dynamic>(json);
                    if (data == null)
                    {
                        LogMessage?.Invoke("⚠️ sms:send: dữ liệu rỗng sau parse");
                        return;
                    }

                    var task = new SmsTask
                    {
                        MessageId = data.messageId?.ToString() ?? "",
                        SourceAddr = data.sourceAddr?.ToString() ?? "",
                        DestAddr = data.destAddr?.ToString() ?? "",
                        Content = data.shortMessage?.ToString() ?? "",
                        AccountId = (int)(data.accountId ?? 0),
                        SystemId = data.systemId?.ToString() ?? "",
                    };

                    LogMessage?.Invoke($"📨 Nhận SMS dispatch: {task.DestAddr} (from {task.SystemId}) [MsgID: {task.MessageId}]");
                    SmsReceived?.Invoke(task);

                    // Dispatch to modem
                    var dispatched = _portManager.DispatchSms(task);
                    if (!dispatched)
                    {
                        // No modem available — report failure immediately
                        LogMessage?.Invoke($"❌ Không có modem khả dụng cho SMS {task.MessageId}");
                        SendSmsResult(task.MessageId, "FAILED", null, null, "No available modem");
                    }
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"❌ Parse sms:send error: {ex.Message}");
                }
            });

            await _socket.ConnectAsync();

            // Start heartbeat
            StartHeartbeat();
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"❌ Connection failed: {ex.Message}");
            ConnectionChanged?.Invoke(false);
        }
    }

    public void SendSmsResult(string messageId, string status, string? simPhone, string? deviceName, string? error)
    {
        _socket?.EmitAsync("sms:result", new
        {
            messageId,
            status,
            simPhoneNumber = simPhone,
            deviceName = deviceName ?? _settings.AgentId,
            errorMessage = error,
        });
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
