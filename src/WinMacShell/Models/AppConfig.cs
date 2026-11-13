namespace WinMacShell.Models;

public sealed class PinnedAppItem
{
    public string Name { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string AppUserModelId { get; set; } = string.Empty;
    public string ShortcutPath { get; set; } = string.Empty;
}

public sealed class AppConfig
{
    public List<PinnedAppItem> PinnedApps { get; set; } = new();
    public bool AutoHideDock { get; set; } = true;
    public bool HasImportedTaskbarPins { get; set; }
}
