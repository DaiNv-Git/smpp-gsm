using System.IO;
using GsmAgent.Models;
using System.Text.Json;

namespace GsmAgent.Services;

/// <summary>
/// Lưu/đọc settings từ file JSON (settings.json cùng thư mục app).
/// </summary>
public static class SettingsManager
{
    private static readonly string SettingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    System.Diagnostics.Debug.WriteLine($"✅ Settings loaded from {SettingsPath}");
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"⚠️ Load settings error: {ex.Message}");
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(SettingsPath, json);
            System.Diagnostics.Debug.WriteLine($"💾 Settings saved to {SettingsPath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Save settings error: {ex.Message}");
        }
    }
}
