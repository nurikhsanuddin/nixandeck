using System.Collections.Specialized;
using System.ComponentModel;
using ManagedShell;
using ManagedShell.Common.Enums;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;

namespace WinMacShell.Core.Services;

public sealed class ShellService : IDisposable
{
    private readonly ShellManager _shellManager;
    private bool _disposed;

    public Tasks Tasks => _shellManager.Tasks;
    public TasksService TasksService => _shellManager.TasksService;
    public NotificationArea NotificationArea => _shellManager.NotificationArea;

    public void SetExplorerTaskbarHidden(bool hidden)
    {
        try
        {
            _shellManager.ExplorerHelper.HideExplorerTaskbar = hidden;
        }
        catch
        {
            // Ignore if ExplorerHelper is unavailable
        }
    }

    public event EventHandler? TasksChanged;
    public event EventHandler<ApplicationWindow>? WindowAttentionRequested;

    public ShellService()
    {
        var config = ShellManager.DefaultShellConfig;
        config.TaskIconSize = IconSize.Large;
        config.EnableTasksService = true;
        config.AutoStartTasksService = true;
        config.EnableTrayService = true;
        config.AutoStartTrayService = true;

        _shellManager = new ShellManager(config);

        if (_shellManager.Tasks.GroupedWindows is INotifyCollectionChanged notifyCollection)
        {
            notifyCollection.CollectionChanged += OnGroupedWindowsCollectionChanged;
        }
    }

    private void OnGroupedWindowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (object item in e.OldItems)
            {
                if (item is INotifyPropertyChanged npc)
                {
                    npc.PropertyChanged -= OnWindowPropertyChanged;
                }
            }
        }

        if (e.NewItems != null)
        {
            foreach (object item in e.NewItems)
            {
                if (item is INotifyPropertyChanged npc)
                {
                    npc.PropertyChanged += OnWindowPropertyChanged;
                }
            }
        }

        TasksChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ApplicationWindow win &&
            e.PropertyName == nameof(ApplicationWindow.State) &&
            win.State == ApplicationWindow.WindowState.Flashing)
        {
            WindowAttentionRequested?.Invoke(this, win);
        }

        if (e.PropertyName is nameof(ApplicationWindow.Title)
            or nameof(ApplicationWindow.Icon)
            or nameof(ApplicationWindow.State)
            or nameof(ApplicationWindow.ShowInTaskbar)
            or nameof(ApplicationWindow.IsMinimized))
        {
            TasksChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<ApplicationWindow> GetVisibleWindows()
    {
        var list = new List<ApplicationWindow>();
        foreach (object item in _shellManager.Tasks.GroupedWindows)
        {
            if (item is ApplicationWindow win && win.ShowInTaskbar && win.CanAddToTaskbar)
            {
                list.Add(win);
            }
        }
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_shellManager.Tasks.GroupedWindows is INotifyCollectionChanged notifyCollection)
        {
            notifyCollection.CollectionChanged -= OnGroupedWindowsCollectionChanged;
        }

        _shellManager.Dispose();
    }
}
