using System.Windows;
using GsmAgent.ViewModels;

namespace GsmAgent;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 🔥 Đảm bảo COM port luôn được release — kể cả khi kill process
        AppDomain.CurrentDomain.ProcessExit += (s, args) =>
        {
            ForceReleaseAllPorts();
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            ForceReleaseAllPorts();
        };

        DispatcherUnhandledException += (s, args) =>
        {
            ForceReleaseAllPorts();
        };
    }

    /// <summary>
    /// Force close tất cả COM port — gọi khi process bị kill.
    /// Không cần graceful shutdown, chỉ cần release port.
    /// </summary>
    public static void ForceReleaseAllPorts()
    {
        try
        {
            if (Current?.MainWindow?.DataContext is MainViewModel vm)
            {
                vm.Cleanup();
            }
        }
        catch { /* Best effort — process đang die */ }

        // Fallback: force close tất cả SerialPort instances
        try
        {
            foreach (var portName in System.IO.Ports.SerialPort.GetPortNames())
            {
                try
                {
                    using var port = new System.IO.Ports.SerialPort(portName);
                    if (port.IsOpen) port.Close();
                }
                catch { /* Port có thể đang bị lock bởi process khác */ }
            }
        }
        catch { }
    }
}
