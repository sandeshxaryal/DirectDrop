using System.IO;
using System.Text.Json;

namespace DirectDrop.App.Services;

public sealed class DirectDropSettings
{
    public string UploadDestination { get; set; } = DirectDropSettingsService.DefaultUploadDestination;
    public int PreferredPort { get; set; } = 8765;
    public bool HasCompletedFirstRunSetup { get; set; }
    public bool HasGrantedNetworkAccess { get; set; }
    public bool FastModePreferenceSet { get; set; }
    public bool PreferFastMode { get; set; }
}

public sealed class DirectDropSettingsService
{
    public static string DefaultUploadDestination =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DirectDrop");

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DirectDrop", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DirectDropSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new DirectDropSettings();

            var settings = JsonSerializer.Deserialize<DirectDropSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                           ?? new DirectDropSettings();

            settings.UploadDestination = Normalize(settings.UploadDestination, DefaultUploadDestination);
            settings.PreferredPort = settings.PreferredPort is >= 1024 and <= 65535 ? settings.PreferredPort : 8765;
            return settings;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Could not load settings; using defaults.", ex);
            return new DirectDropSettings();
        }
    }

    public bool Save(DirectDropSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            settings.UploadDestination = Normalize(settings.UploadDestination, DefaultUploadDestination);
            settings.PreferredPort = settings.PreferredPort is >= 1024 and <= 65535 ? settings.PreferredPort : 8765;
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Could not save settings.", ex);
            return false;
        }
    }

    private static string Normalize(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try { return Path.GetFullPath(value.Trim()); }
        catch { return fallback; }
    }
}
