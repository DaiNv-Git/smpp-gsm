using System.IO;

namespace GsmAgent.Services;

/// <summary>
/// Simple file logger — ghi log ra file gsm-agent.log.
/// </summary>
public static class Logger
{
    private static readonly string LogPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "gsm-agent.log");

    private static readonly object _lock = new();
    private static readonly long MAX_LOG_SIZE = 10 * 1024 * 1024; // 10MB

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        lock (_lock)
        {
            try
            {
                // Rotate log if too large
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MAX_LOG_SIZE)
                {
                    var backup = LogPath + ".old";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(LogPath, backup);
                }

                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
                File.AppendAllText(LogPath, line + Environment.NewLine);
                System.Diagnostics.Debug.WriteLine(line);
            }
            catch { }
        }
    }
}
