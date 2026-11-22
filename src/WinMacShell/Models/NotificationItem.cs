using System.Windows.Media;

namespace WinMacShell.Models;

public sealed class NotificationItem
{
    public uint Id { get; init; }
    public string AppId { get; init; } = string.Empty;
    public string AppName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
    public string TimeText => Timestamp.LocalDateTime.ToString("HH:mm");
    public ImageSource? AppIcon { get; init; }
    public bool IsAlarm { get; init; }
    public string LaunchUri { get; set; } = string.Empty;
}
