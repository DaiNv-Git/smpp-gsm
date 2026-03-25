using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GsmAgent.Models;
using GsmAgent.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace GsmAgent.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SerialPortManager _portManager;
    private ServerConnection? _serverConnection;
    private System.Timers.Timer? _periodicScanTimer;

    [ObservableProperty] private string _serverStatus = "Chưa kết nối";
    [ObservableProperty] private SolidColorBrush _serverStatusColor = Brushes.Gray;
    [ObservableProperty] private int _onlineSimCount;
    [ObservableProperty] private int _totalQueueSize;
    [ObservableProperty] private int _totalSentCount;
    [ObservableProperty] private int _rateLimitedCount;
    [ObservableProperty] private string _scanStatus = "";
    [ObservableProperty] private string _statusMessage = "Sẵn sàng";
    [ObservableProperty] private AppSettings _settings;

    public ObservableCollection<SimCard> SimList { get; } = new();
    public ObservableCollection<SmsMessage> MessageList { get; } = new();

    public MainViewModel()
    {
        // Load saved settings
        _settings = SettingsManager.Load();
        _portManager = new SerialPortManager(_settings);

        Logger.Info("GSM Agent khởi động");

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
                SimList.Clear();
                foreach (var sim in sims.OrderBy(s => s.ComPort))
                    SimList.Add(sim);
                UpdateStats();
                ScanStatus = $"✅ Scan xong: {sims.Count} SIM";
                Logger.Info($"Scan hoàn tất: {sims.Count} SIM(s)");

                // 🔥 Report SIM + Device info lên server → lưu DB
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

        // Auto-scan on startup
        if (_settings.AutoScan)
        {
            _ = AutoStartAsync();
        }
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

        // Start periodic re-scan (5 min)
        StartPeriodicScan();
    }

    private void StartPeriodicScan()
    {
        _periodicScanTimer?.Dispose();
        _periodicScanTimer = new System.Timers.Timer(_settings.ScanIntervalSec * 1000);
        _periodicScanTimer.Elapsed += async (s, e) =>
        {
            Logger.Info("Periodic re-scan...");
            // Only refresh signal levels, don't full re-scan
            foreach (var worker in _portManager.Workers.Values)
            {
                worker.RefreshSimInfo();
            }
            Application.Current.Dispatcher.Invoke(UpdateStats);
        };
        _periodicScanTimer.Start();
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
        Logger.Info("Manual scan started");

        try
        {
            await _portManager.ScanAllAsync();
            StatusMessage = $"Scan hoàn tất: {SimList.Count} SIM(s)";
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
        TotalSentCount = SimList.Sum(s => s.TotalSent);
        RateLimitedCount = SimList.Count(s => s.IsRateLimited);
    }

    public void Cleanup()
    {
        Logger.Info("GSM Agent shutting down...");
        _periodicScanTimer?.Dispose();
        _serverConnection?.Dispose();
        _portManager.Dispose();
    }
}
