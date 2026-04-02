using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GsmAgent.Models;
using GsmAgent.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GsmAgent.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SerialPortManager _portManager;
    private ServerConnection? _serverConnection;
    private MongoDbService? _mongoDb;
    private SimSyncService? _simSyncService;
    private CancellationTokenSource? _notificationCts;

    [ObservableProperty] private string _serverStatus = "Chưa kết nối";
    [ObservableProperty] private SolidColorBrush _serverStatusColor = Brushes.Gray;
    [ObservableProperty] private int _onlineSimCount;
    [ObservableProperty] private int _totalQueueSize;
    [ObservableProperty] private int _totalSentCount;
    [ObservableProperty] private int _rateLimitedCount;
    [ObservableProperty] private int _missingPhoneCount;
    [ObservableProperty] private string _scanStatus = "";
    [ObservableProperty] private string _statusMessage = "Sẵn sàng";
    [ObservableProperty] private AppSettings _settings;
    [ObservableProperty] private string _selectedFilter = "ALL";
    [ObservableProperty] private int _incomingCount;
    [ObservableProperty] private int _outgoingCount;
    [ObservableProperty] private int _failedCount;
    private DateTime _lastMessageResetDate = DateTime.Today;
    [ObservableProperty] private string _mongoStatus = "Chưa kết nối";
    [ObservableProperty] private SolidColorBrush _mongoStatusColor = Brushes.Gray;
    [ObservableProperty] private bool _isNotificationVisible;
    [ObservableProperty] private string _notificationTitle = "";
    [ObservableProperty] private string _notificationMessage = "";

    // 📤 Send SMS properties
    [ObservableProperty] private SimCard? _selectedSim;
    [ObservableProperty] private string _smsDestNumber = "";
    [ObservableProperty] private string _smsContent = "";
    [ObservableProperty] private string _smsResult = "";
    [ObservableProperty] private bool _isSending;

    // 📞 Voice Call properties
    [ObservableProperty] private SimCard? _selectedCallSim;
    [ObservableProperty] private string _callDestNumber = "";
    [ObservableProperty] private bool _enableRecording = true;
    [ObservableProperty] private int _callDurationSeconds = 30;
    [ObservableProperty] private string _callStatus = "Sẵn sàng";
    [ObservableProperty] private string _callStatusIcon = "☎️";
    [ObservableProperty] private bool _isCalling;
    [ObservableProperty] private int _callElapsedSeconds;
    [ObservableProperty] private string _recordingStatus = "";
    [ObservableProperty] private Uri? _mediaSource;
    private CancellationTokenSource? _callCts;
    private AtCommandHelper? _activeCallHelper;

    public ObservableCollection<SimCard> SimList { get; } = new();
    public ObservableCollection<SmsMessage> MessageList { get; } = new();
    public ObservableCollection<CallRecord> CallHistory { get; } = new();
    public ICollectionView FilteredMessages { get; private set; }

    public MainViewModel()
    {
        // Load saved settings
        _settings = SettingsManager.Load();
        _portManager = new SerialPortManager(_settings);

        // Setup filtered view for messages
        FilteredMessages = CollectionViewSource.GetDefaultView(MessageList);
        FilteredMessages.Filter = FilterMessagesPredicate;

        Logger.Info("GSM Agent khởi động");

        // 🔥 Progressive loading: hiển thị từng SIM ngay khi scan được
        _portManager.SimScanned += sim =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // Check duplicate by ComPort
                var existing = SimList.FirstOrDefault(s => s.ComPort == sim.ComPort);
                if (existing != null)
                {
                    var idx = SimList.IndexOf(existing);
                    SimList[idx] = sim;
                }
                else
                {
                    // Insert sorted by ComPort
                    int insertIdx = 0;
                    for (int i = 0; i < SimList.Count; i++)
                    {
                        if (string.Compare(SimList[i].ComPort, sim.ComPort, StringComparison.Ordinal) > 0)
                            break;
                        insertIdx = i + 1;
                    }
                    SimList.Insert(insertIdx, sim);
                }
                UpdateStats();
                ScanStatus = $"🔍 Đang scan... ({SimList.Count} SIM)";
            });
        };

        _portManager.SimUpdated += sim =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var existing = SimList.FirstOrDefault(s => s.ComPort == sim.ComPort);
                if (existing != null)
                {
                    var idx = SimList.IndexOf(existing);
                    SimList[idx] = sim;
                }
                UpdateStats();
            });
        };

        _portManager.ScanCompleted += sims =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // Final reconciliation: remove SIMs not in final list
                var finalPorts = new HashSet<string>(sims.Select(s => s.ComPort));
                for (int i = SimList.Count - 1; i >= 0; i--)
                {
                    if (!finalPorts.Contains(SimList[i].ComPort))
                        SimList.RemoveAt(i);
                }

                // Update any SIMs that changed during scan
                foreach (var sim in sims)
                {
                    var existing = SimList.FirstOrDefault(s => s.ComPort == sim.ComPort);
                    if (existing != null)
                    {
                        var idx = SimList.IndexOf(existing);
                        SimList[idx] = sim;
                    }
                }

                UpdateStats();
                ScanStatus = $"✅ Scan xong: {sims.Count} SIM";
                Logger.Info($"Scan hoàn tất: {sims.Count} SIM(s)");

                // 🔗 Report SIM + Device info lên server
                _serverConnection?.ReportSims();

                // 🔥 Trigger LƯU DB NGAY LẬP TỨC (không đợi 5 phút)
                if (_simSyncService != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await _simSyncService.SyncSimsToMongo(); }
                        catch (Exception ex) { Logger.Error($"Immediate DB Sync error: {ex.Message}"); }
                    });
                }
            });
        };

        // 📨 Incoming SMS notification
        _portManager.IncomingSms += (sender, content, time) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageList.Insert(0, new SmsMessage
                {
                    MessageId = $"IN-{DateTime.Now.Ticks}",
                    SourceAddr = sender,
                    DestAddr = "SIM",
                    Content = content,
                    Direction = "IN",
                    Status = "RECEIVED",
                    CreatedAt = time,
                });

                while (MessageList.Count > 500)
                    MessageList.RemoveAt(MessageList.Count - 1);

                StatusMessage = $"📨 SMS mới từ {sender}: {content[..Math.Min(30, content.Length)]}...";
                Logger.Info($"📨 Incoming SMS from {sender}: {content}");

                ShowNotification("📨 Tin nhắn mới", $"Từ: {sender}\n{content}");
            });
        };
        // 📞 Discovery progress — hiện trên giao diện
        _portManager.DiscoveryLog += (comPort, message, isSuccess) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageList.Insert(0, new SmsMessage
                {
                    MessageId = $"DISC-{DateTime.Now.Ticks}",
                    SourceAddr = comPort,
                    DestAddr = "DISCOVERY",
                    Content = message,
                    Direction = "OUT",
                    Status = isSuccess ? "SENT" : "FAILED",
                    CreatedAt = DateTime.Now,
                });

                while (MessageList.Count > 500)
                    MessageList.RemoveAt(MessageList.Count - 1);

                StatusMessage = $"📞 [{comPort}] {message}";
                UpdateStats();
            });
        };

        // 🚀 Auto-scan on startup (always — SIM list should be populated immediately)
        _ = AutoStartAsync();
    }

    private async Task AutoStartAsync()
    {
        await Task.Delay(1000); // Wait for UI to load
        Logger.Info("Auto-scan starting...");
        await ScanAsync();

        if (_settings.AutoConnect)
        {
            StartWorkers();
        }

        // 🔗 Khởi động MongoDB sync (mỗi 5 phút)
        InitMongoSync();
    }

    /// <summary>
    /// 🔗 Khởi tạo MongoDB connection + SimSyncService.
    /// Tương tự cơ chế trong simsmart-gsm (Java).
    /// </summary>
    private void InitMongoSync()
    {
        if (!_settings.EnableMongoSync)
        {
            Logger.Info("⏭️ MongoDB sync disabled trong settings");
            return;
        }

        try
        {
            _mongoDb = new MongoDbService(_settings.MongoDbUri);

            // 🔗 Inject MongoDB vào SerialPortManager để lookup phone trực tiếp
            _portManager.SetMongoDb(_mongoDb);

            Application.Current.Dispatcher.Invoke(() =>
            {
                MongoStatus = "Đã kết nối";
                MongoStatusColor = new SolidColorBrush(Color.FromRgb(6, 214, 160));
            });

            _simSyncService = new SimSyncService(_mongoDb, _portManager, _settings);

            _simSyncService.LogMessage += msg =>
            {
                Application.Current.Dispatcher.Invoke(() => StatusMessage = msg);
            };

            _simSyncService.SyncCompleted += (synced, total) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ScanStatus = $"💾 MongoDB: {synced} SIM synced ({total} scanned)";
                });
            };

            // 🔄 Bắt đầu scheduled sync (mỗi 5 phút)
            _simSyncService.StartScheduledSync();

            Logger.Info("✅ MongoDB sync initialized successfully");
        }
        catch (Exception ex)
        {
            Logger.Error($"❌ MongoDB init failed: {ex.Message}");
            Application.Current.Dispatcher.Invoke(() =>
            {
                MongoStatus = "Lỗi kết nối";
                MongoStatusColor = Brushes.Red;
                StatusMessage = $"❌ MongoDB: {ex.Message}";
            });
        }
    }

    private void ShowNotification(string title, string message)
    {
        _notificationCts?.Cancel();
        var cts = new CancellationTokenSource();
        _notificationCts = cts;

        NotificationTitle = title;
        NotificationMessage = message;
        IsNotificationVisible = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(5000, cts.Token);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (_notificationCts == cts)
                        IsNotificationVisible = false;
                });
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (_notificationCts == cts)
                    _notificationCts = null;
            }
        });
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        ScanStatus = "🔍 Đang scan COM ports...";
        StatusMessage = "Scanning...";
        SimList.Clear(); // Clear để progressive loading thêm từng SIM
        UpdateStats();
        Logger.Info("Manual scan started");

        try
        {
            await _portManager.ScanAllAsync();
            ScanStatus = _portManager.LastScanStats;
            StatusMessage = $"Scan hoàn tất: {SimList.Count} SIM(s)";

            // 🔗 Sync ngay vào MongoDB sau khi scan thủ công
            if (_simSyncService != null)
            {
                _ = Task.Run(() => _simSyncService.SyncSimsToMongo());
            }
        }
        catch (Exception ex)
        {
            ScanStatus = $"❌ Lỗi: {ex.Message}";
            StatusMessage = $"Scan error: {ex.Message}";
            Logger.Error($"Scan error: {ex.Message}");
        }
    }

    [RelayCommand]
    private void StartWorkers()
    {
        _portManager.StartAllWorkers();
        var onlineCount = SimList.Count(s => s.Status == SimStatus.Online);
        StatusMessage = $"Workers started for {onlineCount} SIMs";
        Logger.Info($"Workers started: {onlineCount} SIMs");

        // Connect to server
        if (_serverConnection == null || !_serverConnection.IsConnected)
        {
            _ = ConnectToServerAsync();
        }

        // 🔥 Auto Self-SMS Discovery cho SIM thiếu số (chạy sau 10s để workers ổn định)
        var missingPhone = SimList.Count(s => string.IsNullOrWhiteSpace(s.PhoneNumber));
        if (missingPhone > 0)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(10_000); // Đợi workers khởi động xong
                Logger.Info($"📞 Auto Self-SMS Discovery: {missingPhone} SIM thiếu số...");
                await _portManager.DiscoverPhoneBySelfSmsAsync();
            });
        }
    }

    [RelayCommand]
    private void StopWorkers()
    {
        _portManager.StopAllWorkers();
        SimList.ToList().ForEach(s => { s.Status = SimStatus.Offline; });
        UpdateStats();
        StatusMessage = "All workers stopped";
        Logger.Info("All workers stopped");
    }

    [RelayCommand]
    private void SaveSettings()
    {
        SettingsManager.Save(Settings);
        StatusMessage = "✅ Cài đặt đã lưu";
        Logger.Info("Settings saved");
    }

    /// <summary>🔗 Manual trigger: Sync SIM → MongoDB ngay.</summary>
    [RelayCommand]
    private async Task SyncMongoAsync()
    {
        if (_simSyncService == null)
        {
            InitMongoSync();
            return;
        }

        StatusMessage = "🔄 Đang sync MongoDB...";
        await _simSyncService.SyncSimsToMongo();
    }

    /// <summary>📤 Gửi SMS thủ công qua SIM đã chọn.</summary>
    [RelayCommand]
    private async Task SendSmsAsync()
    {
        if (SelectedSim == null)
        {
            SmsResult = "⚠️ Chưa chọn SIM";
            return;
        }
        if (string.IsNullOrWhiteSpace(SmsDestNumber))
        {
            SmsResult = "⚠️ Chưa nhập số đích";
            return;
        }
        if (string.IsNullOrWhiteSpace(SmsContent))
        {
            SmsResult = "⚠️ Chưa nhập nội dung";
            return;
        }

        IsSending = true;
        var sim = SelectedSim; // Capture trước await (SelectedSim có thể bị null do SimList update async)
        SmsResult = $"📤 Đang gửi qua {sim.ComPort}...";
        StatusMessage = $"📤 Gửi SMS → {SmsDestNumber} qua {sim.ComPort}";

        try
        {
            var (success, message) = await _portManager.SendSmsViaPort(
                sim.ComPort, SmsDestNumber, SmsContent);

            SmsResult = message;
            StatusMessage = message;
            Logger.Info($"SMS Manual: {sim.ComPort} → {SmsDestNumber}: {(success ? "OK" : "FAIL")}");

            // Thêm vào message list (cả thành công lẫn thất bại)
            MessageList.Insert(0, new SmsMessage
            {
                MessageId = $"MANUAL-{DateTime.Now.Ticks}",
                SourceAddr = sim.PhoneNumber ?? sim.ComPort,
                DestAddr = SmsDestNumber,
                Content = SmsContent,
                Direction = "OUT",
                Status = success ? "SENT" : "FAILED",
                CreatedAt = DateTime.Now,
            });

            // 🔥 Fix bộ đếm: update CẢ header counter + message counter
            UpdateStats();

            if (success)
            {
                SmsContent = "";
            }
        }
        catch (Exception ex)
        {
            SmsResult = $"❌ Lỗi: {ex.Message}";
            Logger.Error($"SendSMS error: {ex.Message}");
        }
        finally
        {
            IsSending = false;
        }
    }

    private async Task ConnectToServerAsync()
    {
        _serverConnection?.Dispose();
        _serverConnection = new ServerConnection(Settings, _portManager);

        // 🔗 Wire SMS result: ModemWorker → SerialPortManager.SmsResult → ServerConnection → BE → DLR
        _portManager.SmsResult += (messageId, status, success, error) =>
        {
            _serverConnection?.SendSmsResult(messageId, status, null, null, error);
            Logger.Info($"📋 SMS {messageId} → {status}");

            // Update message status in UI
            Application.Current.Dispatcher.Invoke(() =>
            {
                var msg = MessageList.FirstOrDefault(m => m.MessageId == messageId);
                if (msg != null)
                {
                    var idx = MessageList.IndexOf(msg);
                    msg.Status = status;
                    msg.ErrorMessage = error;
                    MessageList[idx] = msg;
                }
            });
        };

        _serverConnection.ConnectionChanged += connected =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                ServerStatus = connected ? "Đã kết nối" : "Mất kết nối";
                ServerStatusColor = connected
                    ? new SolidColorBrush(Color.FromRgb(6, 214, 160))
                    : Brushes.Red;
                StatusMessage = connected ? "Server connected" : "Server disconnected";
                Logger.Info($"Server {(connected ? "connected" : "disconnected")}");
            });
        };

        _serverConnection.LogMessage += msg =>
        {
            Application.Current.Dispatcher.Invoke(() => StatusMessage = msg);
        };

        _serverConnection.SmsReceived += task =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageList.Insert(0, new SmsMessage
                {
                    MessageId = task.MessageId,
                    DestAddr = task.DestAddr,
                    SourceAddr = task.SourceAddr,
                    Content = task.Content,
                    SystemId = task.SystemId,
                    Status = "PENDING",
                    CreatedAt = DateTime.Now,
                });

                while (MessageList.Count > 500)
                    MessageList.RemoveAt(MessageList.Count - 1);

                UpdateStats();
                Logger.Info($"SMS dispatch received: {task.MessageId} → {task.DestAddr}");
            });
        };

        await _serverConnection.ConnectAsync();
    }

    private void UpdateStats()
    {
        // 🔥 Auto-reset tin nhắn cuối ngày (0:00)
        if (_lastMessageResetDate.Date < DateTime.Today)
        {
            _lastMessageResetDate = DateTime.Today;
            MessageList.Clear();
            Logger.Info("🔄 Auto-reset MessageList cuối ngày");
        }

        OnlineSimCount = SimList.Count(s => s.Status == SimStatus.Online || s.Status == SimStatus.Busy);
        TotalQueueSize = SimList.Sum(s => s.QueueSize);
        TotalSentCount = MessageList.Count(m =>
            m.Direction == "OUT" && (m.Status == "SENT" || m.Status == "DELIVERED"));
        RateLimitedCount = SimList.Count(s => s.IsRateLimited);
        // 🔥 FIX: Đếm TẤT CẢ SIM thiếu số điện thoại (kể cả chưa kết nối worker)
        MissingPhoneCount = SimList.Count(s => string.IsNullOrWhiteSpace(s.PhoneNumber));
        UpdateMessageCounts();
    }

    private void UpdateMessageCounts()
    {
        IncomingCount = MessageList.Count(m => m.Direction == "IN");
        OutgoingCount = MessageList.Count(m => m.Direction == "OUT");
        FailedCount = MessageList.Count(m => m.Status == "FAILED");
    }

    [RelayCommand]
    private void ClearMessages()
    {
        MessageList.Clear();
        UpdateStats();
        StatusMessage = "🧹 Đã xóa tất cả tin nhắn";
    }

    [RelayCommand]
    private void FilterMessages(string filter)
    {
        SelectedFilter = filter;
        FilteredMessages.Refresh();
    }

    private bool FilterMessagesPredicate(object obj)
    {
        if (obj is not SmsMessage msg) return false;
        return SelectedFilter switch
        {
            "IN" => msg.Direction == "IN",
            "OUT" => msg.Direction == "OUT",
            "FAILED" => msg.Status == "FAILED",
            _ => true,
        };
    }

    // ═══════════════ VOICE CALL ═══════════════

    private static string RecordingsFolder
    {
        get
        {
            var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recordings");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return folder;
        }
    }

    [RelayCommand]
    private async Task MakeCallAsync()
    {
        if (SelectedCallSim == null)
        {
            CallStatus = "⚠️ Chưa chọn SIM";
            return;
        }
        if (string.IsNullOrWhiteSpace(CallDestNumber))
        {
            CallStatus = "⚠️ Chưa nhập số gọi đến";
            return;
        }
        if (CallDurationSeconds <= 0) CallDurationSeconds = 30;

        IsCalling = true;
        CallElapsedSeconds = 0;
        RecordingStatus = "";
        _callCts = new CancellationTokenSource();
        var ct = _callCts.Token;
        var sim = SelectedCallSim;
        var dest = CallDestNumber;
        var duration = CallDurationSeconds;
        var recordEnabled = EnableRecording;

        var callRecord = new CallRecord
        {
            ComPort = sim.ComPort,
            PhoneNumber = sim.PhoneNumber,
            DestNumber = dest,
            TargetDurationSec = duration,
            RecordingEnabled = recordEnabled,
            State = CallState.Dialing,
        };

        Application.Current.Dispatcher.Invoke(() =>
        {
            CallHistory.Insert(0, callRecord);
            while (CallHistory.Count > 100) CallHistory.RemoveAt(CallHistory.Count - 1);
        });

        try
        {
            await Task.Run(async () =>
            {
                // 1. Tạm dừng SMS worker trên port này
                UpdateCallUI(callRecord, CallState.Dialing, $"⏸️ Tạm dừng SMS worker trên {sim.ComPort}...");
                _portManager.StopWorkerForPort(sim.ComPort);
                await Task.Delay(300);

                // 2. Mở COM port
                var helper = new AtCommandHelper(sim.ComPort, Settings.BaudRate);
                _activeCallHelper = helper;

                if (!helper.Open())
                {
                    UpdateCallUI(callRecord, CallState.Failed, "❌ Không thể mở " + sim.ComPort);
                    _portManager.RestartWorkerForPort(sim.ComPort);
                    return;
                }

                if (!helper.IsAlive())
                {
                    UpdateCallUI(callRecord, CallState.Failed, "❌ Modem không phản hồi");
                    helper.Dispose();
                    _portManager.RestartWorkerForPort(sim.ComPort);
                    return;
                }

                // 3. Gọi điện
                UpdateCallUI(callRecord, CallState.Dialing, $"📞 Đang gọi {dest}...");

                if (!helper.MakeVoiceCall(dest))
                {
                    UpdateCallUI(callRecord, CallState.Failed, "❌ Gọi thất bại");
                    helper.Dispose();
                    _portManager.RestartWorkerForPort(sim.ComPort);
                    return;
                }

                    // 4. Poll trạng thái — đợi bắt máy hoặc timeout 45s
                UpdateCallUI(callRecord, CallState.Ringing, "🔔 Đang đổ chuông...");
                var ringStart = DateTime.Now;
                bool answered = false;

                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(1000, ct);
                    var status = helper.GetCallStatus();

                    if (status == 0) // Active = bắt máy
                    {
                        answered = true;
                        break;
                    }
                    else if (status == -1) // No call = đã kết thúc
                    {
                        UpdateCallUI(callRecord, CallState.NoAnswer, "❌ Không ai bắt máy");
                        helper.Dispose();
                        _portManager.RestartWorkerForPort(sim.ComPort);
                        return;
                    }

                    // Timeout 45s không bắt máy
                    if ((DateTime.Now - ringStart).TotalSeconds >= 45)
                    {
                        UpdateCallUI(callRecord, CallState.NoAnswer, "❌ 45s không bắt máy - tự động tắt");
                        helper.HangUp();
                        helper.Dispose();
                        _portManager.RestartWorkerForPort(sim.ComPort);
                        return;
                    }
                }

                if (ct.IsCancellationRequested)
                {
                    helper.HangUp();
                    helper.Dispose();
                    _portManager.RestartWorkerForPort(sim.ComPort);
                    return;
                }

                // 4. Đã bắt máy!
                callRecord.AnsweredAt = DateTime.Now;
                UpdateCallUI(callRecord, CallState.Active, "🟢 Đã bắt máy!");

                // 5. Bắt đầu ghi âm (nếu enabled)
                bool recordingStarted = false;
                string modemRecFile = "call_rec.amr";
                if (recordEnabled)
                {
                    Application.Current.Dispatcher.Invoke(() => RecordingStatus = "🔴 Đang ghi âm...");
                    recordingStarted = helper.StartCallRecording(modemRecFile, duration + 5);
                    if (!recordingStarted)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                            RecordingStatus = "⚠️ Modem không hỗ trợ ghi âm");
                    }
                }

                // 6. Đếm thời gian cuộc gọi (từ lúc bắt máy)
                var callStart = DateTime.Now;
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(1000, ct);
                    var elapsed = (int)(DateTime.Now - callStart).TotalSeconds;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        CallElapsedSeconds = elapsed;
                        CallStatus = $"🟢 Đang gọi... {elapsed}/{duration}s";
                    });

                    // Check cuộc gọi còn active không
                    var cStatus = helper.GetCallStatus();
                    if (cStatus == -1) // Đối phương cúp
                    {
                        callRecord.ActualDurationSec = elapsed;
                        break;
                    }

                    // Hết thời gian
                    if (elapsed >= duration)
                    {
                        callRecord.ActualDurationSec = elapsed;
                        break;
                    }
                }

                // 7. Cúp máy
                helper.HangUp();
                UpdateCallUI(callRecord, CallState.Ended, "✅ Đã kết thúc cuộc gọi");
                callRecord.EndedAt = DateTime.Now;
                if (callRecord.ActualDurationSec == 0)
                    callRecord.ActualDurationSec = (int)(DateTime.Now - callStart).TotalSeconds;

                // 8. Dừng ghi âm + tải file
                if (recordingStarted)
                {
                    Application.Current.Dispatcher.Invoke(() => RecordingStatus = "⏹️ Đang dừng ghi âm...");
                    helper.StopCallRecording();
                    await Task.Delay(1000); // Đợi modem lưu file

                    // Download file
                    Application.Current.Dispatcher.Invoke(() =>
                        RecordingStatus = "📥 Đang tải file ghi âm...");

                    var data = helper.DownloadRecordingFile(modemRecFile, 30000);
                    if (data != null && data.Length > 0)
                    {
                        var localFile = Path.Combine(RecordingsFolder,
                            $"call_{sim.ComPort}_{DateTime.Now:yyyyMMdd_HHmmss}.amr");
                        await File.WriteAllBytesAsync(localFile, data);
                        callRecord.RecordingPath = localFile;

                        Application.Current.Dispatcher.Invoke(() =>
                            RecordingStatus = $"✅ Ghi âm đã lưu ({data.Length / 1024}KB)");

                        // Cleanup modem
                        helper.DeleteModemFile(modemRecFile);
                    }
                    else
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                            RecordingStatus = "⚠️ Không tải được file ghi âm");
                    }
                }

                helper.Dispose();
                _activeCallHelper = null;

                // 9. Khởi động lại SMS worker
                UpdateCallUI(callRecord, CallState.Ended, "▶️ Đang khởi động lại SMS worker...");
                _portManager.RestartWorkerForPort(sim.ComPort);

                // Update call record in history
                Application.Current.Dispatcher.Invoke(() =>
                {
                    CallStatus = "✅ Hoàn thành — SMS worker đã resume";
                    var idx = CallHistory.IndexOf(callRecord);
                    if (idx >= 0)
                    {
                        CallHistory[idx] = callRecord; // Trigger UI update
                    }
                });

            }, ct);
        }
        catch (OperationCanceledException)
        {
            UpdateCallUI(callRecord, CallState.Ended, "⏹️ Cuộc gọi đã hủy");
            _portManager.RestartWorkerForPort(sim.ComPort);
        }
        catch (Exception ex)
        {
            UpdateCallUI(callRecord, CallState.Failed, $"❌ Lỗi: {ex.Message}");
            Logger.Error($"MakeCall error: {ex.Message}");
            _portManager.RestartWorkerForPort(sim.ComPort);
        }
        finally
        {
            IsCalling = false;
            _callCts?.Dispose();
            _callCts = null;
        }
    }

    private void UpdateCallUI(CallRecord record, CallState state, string statusText)
    {
        record.State = state;
        Application.Current.Dispatcher.Invoke(() =>
        {
            CallStatus = statusText;
            CallStatusIcon = record.StateIcon;

            // Refresh record in history
            var idx = CallHistory.IndexOf(record);
            if (idx >= 0)
            {
                CallHistory[idx] = record;
            }
        });
    }

    [RelayCommand]
    private void HangUpCall()
    {
        _callCts?.Cancel();
        try
        {
            _activeCallHelper?.HangUp();
            _activeCallHelper?.Dispose();
            _activeCallHelper = null;
        }
        catch { }
        CallStatus = "⏹️ Đã cúp máy";
        CallStatusIcon = "☎️";
        IsCalling = false;
    }

    [RelayCommand]
    private void OpenRecording(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Không mở được file: {ex.Message}";
        }
    }

    [RelayCommand]
    private void PlayRecording(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        // Set MediaSource để WPF MediaElement play
        MediaSource = new Uri(path, UriKind.Absolute);
    }

    public void Cleanup()
    {
        Logger.Info("GSM Agent shutting down...");

        // Hang up any active call
        try
        {
            _callCts?.Cancel();
            _activeCallHelper?.HangUp();
            _activeCallHelper?.Dispose();
        }
        catch { }

        // 🛑 Shutdown handler: set INACTIVE tất cả SIM trong MongoDB
        _simSyncService?.OnShutdown();

        _simSyncService?.Dispose();
        _serverConnection?.Dispose();
        _notificationCts?.Cancel();
        _notificationCts?.Dispose();
        _portManager.Dispose();
    }
}
