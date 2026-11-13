using System.IO;
using System.Text.Json;
using WinMacShell.Core.Interop;
using WinMacShell.Models;

namespace WinMacShell.Persistence;

public sealed class ConfigManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _configDirectory;
    private readonly string _configPath;

    public AppConfig Config { get; private set; } = new();

    public ConfigManager()
    {
        _configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinMacShell");
        _configPath = Path.Combine(_configDirectory, "config.json");
    }

    public AppConfig Load()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                string json = File.ReadAllText(_configPath);
                AppConfig? loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (loaded != null)
                {
                    loaded.PinnedApps ??= new List<PinnedAppItem>();
                    Config = loaded;
                    if (!Config.HasImportedTaskbarPins)
                    {
                        ImportTaskbarPins();
                        Save();
                    }
                    return Config;
                }
            }
        }
        catch
        {
            // Fall back to default config if file is unreadable or malformed
        }

        Config = CreateDefaultConfig();
        ImportTaskbarPins();
        Save();
        return Config;
    }

    private void ImportTaskbarPins()
    {
        var taskbarPins = TaskbarPinHelper.DiscoverTaskbarPinnedApps();
        foreach (PinnedAppItem pin in taskbarPins)
        {
            bool exists = Config.PinnedApps.Any(existing =>
                (!string.IsNullOrWhiteSpace(pin.AppUserModelId) &&
                 string.Equals(existing.AppUserModelId, pin.AppUserModelId, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(pin.ExecutablePath) &&
                 string.Equals(Path.GetFileName(existing.ExecutablePath), Path.GetFileName(pin.ExecutablePath), StringComparison.OrdinalIgnoreCase)));

            if (!exists)
            {
                Config.PinnedApps.Add(pin);
            }
        }

        Config.HasImportedTaskbarPins = true;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(_configDirectory);
            string json = JsonSerializer.Serialize(Config, JsonOptions);
            File.WriteAllText(_configPath, json);
        }
        catch
        {
            // Ignore IO errors on save to avoid crashing shell
        }
    }

    private static AppConfig CreateDefaultConfig() => new()
    {
        AutoHideDock = true,
        PinnedApps = new List<PinnedAppItem>
        {
            new() { Name = "File Explorer", ExecutablePath = "explorer.exe" },
            new() { Name = "Windows Terminal", ExecutablePath = "wt.exe" },
            new() { Name = "Notepad", ExecutablePath = "notepad.exe" }
        }
    };
}
