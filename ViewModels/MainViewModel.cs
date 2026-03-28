using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GsmAgent.Models;
using GsmAgent.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
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

    [ObservableProperty] private string _serverStatus = "Chưa kết nối";
    [ObservableProperty] private SolidColorBrush _serverStatusColor = Brushes.Gray;
    [ObservableProperty] private int _onlineSimCount;
    [ObservableProperty] private int _totalQueueSize;
    [ObservableProperty] private int _totalSentCount;
    [ObservableProperty] private int _rateLimitedCount;
    [ObservableProperty] private string _scanStatus = "";
    [ObservableProperty] private string _statusMessage = "Sẵn sàng";
    [ObservableProperty] private AppSettings _settings;
    [ObservableProperty] private string _selectedFilter = "ALL";
    [ObservableProperty] private int _incomingCount;
    [ObservableProperty] private int _outgoingCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private string _mongoStatus = "Chưa kết nối";
    [ObservableProperty] private SolidColorBrush _mongoStatusColor = Brushes.Gray;

    // 📤 Send SMS properties
    [ObservableProperty] private SimCard? _selectedSim;
    [ObservableProperty] private string _smsDestNumber = "";
    [ObservableProperty] private string _smsContent = "";
    [ObservableProperty] private string _smsResult = "";
    [ObservableProperty] private bool _isSending;

    public ObservableCollection<SimCard> SimList { get; } = new();
    public ObservableCollection<SmsMessage> MessageList { get; } = new();
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

                // 🔗 Report SIM + Device info lên server → lưu DB
                _serverConnection?.ReportSims();
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
        try
        {
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch { }
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
        SmsResult = $"📤 Đang gửi qua {SelectedSim.ComPort}...";
        StatusMessage = $"📤 Gửi SMS → {SmsDestNumber} qua {SelectedSim.ComPort}";

        try
        {
            var (success, message) = await _portManager.SendSmsViaPort(
                SelectedSim.ComPort, SmsDestNumber, SmsContent);

            SmsResult = message;
            StatusMessage = message;
            Logger.Info($"SMS Manual: {SelectedSim.ComPort} → {SmsDestNumber}: {(success ? "OK" : "FAIL")}");

            // Thêm vào message list (cả thành công lẫn thất bại)
            MessageList.Insert(0, new SmsMessage
            {
                MessageId = $"MANUAL-{DateTime.Now.Ticks}",
                SourceAddr = SelectedSim.PhoneNumber ?? SelectedSim.ComPort,
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
        OnlineSimCount = SimList.Count(s => s.Status == SimStatus.Online || s.Status == SimStatus.Busy);
        TotalQueueSize = SimList.Sum(s => s.QueueSize);
        // 🔥 FIX: Đếm từ MessageList (chính xác) — bao gồm cả SMS manual + worker
        TotalSentCount = MessageList.Count(m =>
            m.Direction == "OUT" && (m.Status == "SENT" || m.Status == "DELIVERED"));
        RateLimitedCount = SimList.Count(s => s.IsRateLimited);
        UpdateMessageCounts();
    }

    private void UpdateMessageCounts()
    {
        IncomingCount = MessageList.Count(m => m.Direction == "IN");
        OutgoingCount = MessageList.Count(m => m.Direction == "OUT");
        FailedCount = MessageList.Count(m => m.Status == "FAILED");
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

    public void Cleanup()
    {
        Logger.Info("GSM Agent shutting down...");

        // 🛑 Shutdown handler: set INACTIVE tất cả SIM trong MongoDB
        // Giống SimShutdownHandler.java
        _simSyncService?.OnShutdown();

        _simSyncService?.Dispose();
        _serverConnection?.Dispose();
        _portManager.Dispose();
    }
}
