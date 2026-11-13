using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ManagedShell.WindowsTasks;

namespace WinMacShell.Models;

public sealed class DockItem : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private ImageSource? _icon;
    private bool _isPinned;
    private bool _isRunning;
    private bool _isActive;
    private double _scale = 1.0;

    public string Id { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public string Arguments { get; init; } = string.Empty;
    public string AppUserModelId { get; init; } = string.Empty;
    public string ShortcutPath { get; init; } = string.Empty;
    public bool IsSeparator { get; init; }

    public List<ApplicationWindow> Windows { get; } = new();

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public ImageSource? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }

    public bool IsPinned
    {
        get => _isPinned;
        set => SetField(ref _isPinned, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set => SetField(ref _isRunning, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetField(ref _isActive, value);
    }

    public double Scale
    {
        get => _scale;
        set => SetField(ref _scale, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
