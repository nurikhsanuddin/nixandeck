using System.IO;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WinMacShell.Core.Interop;

namespace WinMacShell.Core.Services;

public sealed record MediaSnapshot(
    bool HasMedia,
    bool IsPlaying,
    string Title,
    string Artist,
    BitmapImage? Thumbnail);

public sealed class MediaService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private bool _disposed;
    private int _updateVersion;

    public MediaSnapshot CurrentState { get; private set; } = new(false, false, "No Media Playing", "Ready for SMTC", null);

    public event EventHandler<MediaSnapshot>? MediaStateChanged;

    public async Task InitializeAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed) return;

            _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
            AttachSession(_sessionManager.GetCurrentSession());
            await RefreshMediaStateAsync();
        }
        catch
        {
            // Keep safe default state if SMTC is unavailable
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        if (_disposed) return;
        AttachSession(sender.GetCurrentSession());
        _ = RefreshMediaStateAsync();
    }

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_currentSession != null)
        {
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        }

        _currentSession = session;

        if (_currentSession != null)
        {
            _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
        }
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        if (_disposed) return;
        _ = RefreshMediaStateAsync();
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        if (_disposed) return;
        _ = RefreshMediaStateAsync();
    }

    private async Task RefreshMediaStateAsync()
    {
        int version = Interlocked.Increment(ref _updateVersion);
        var session = _currentSession;

        if (session == null)
        {
            UpdateState(version, new MediaSnapshot(false, false, "No Media Playing", "Ready for SMTC", null));
            return;
        }

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var props = await session.TryGetMediaPropertiesAsync();
            if (_disposed || version != Volatile.Read(ref _updateVersion)) return;

            string title = string.IsNullOrWhiteSpace(props?.Title) ? "Unknown Track" : props.Title;
            string artist = string.IsNullOrWhiteSpace(props?.Artist) ? "Unknown Artist" : props.Artist;
            BitmapImage? thumb = await LoadThumbnailAsync(props?.Thumbnail);

            UpdateState(version, new MediaSnapshot(true, isPlaying, title, artist, thumb));
        }
        catch
        {
            UpdateState(version, new MediaSnapshot(false, false, "No Media Playing", "Ready for SMTC", null));
        }
    }

    private void UpdateState(int version, MediaSnapshot snapshot)
    {
        if (_disposed || version != Volatile.Read(ref _updateVersion)) return;
        CurrentState = snapshot;
        MediaStateChanged?.Invoke(this, snapshot);
    }

    // AGENT.md §3.3: Safe WinRT thumbnail stream -> frozen BitmapImage
    public static async Task<BitmapImage?> LoadThumbnailAsync(IRandomAccessStreamReference? thumbRef)
    {
        if (thumbRef == null) return null;
        try
        {
            using var winrtStream = await thumbRef.OpenReadAsync();
            using var netStream = winrtStream.AsStreamForRead();
            using var memStream = new MemoryStream();
            await netStream.CopyToAsync(memStream);
            memStream.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 112;
            bitmap.StreamSource = memStream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private GlobalSystemMediaTransportControlsSession? GetActiveSession()
    {
        try
        {
            if (_sessionManager != null)
            {
                var sessions = _sessionManager.GetSessions();
                foreach (var s in sessions)
                {
                    if (s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        return s;
                    }
                }
                return _sessionManager.GetCurrentSession() ?? _currentSession;
            }
        }
        catch { }

        return _currentSession;
    }

    private static void SendMediaKey(byte vk)
    {
        NativeMethods.keybd_event(vk, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(vk, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public async Task PlayPauseAsync()
    {
        var session = GetActiveSession();
        bool ok = false;
        if (session != null)
        {
            try { ok = await session.TryTogglePlayPauseAsync(); } catch { }
        }
        if (!ok)
        {
            SendMediaKey(NativeMethods.VK_MEDIA_PLAY_PAUSE);
        }
        await Task.Delay(120);
        await RefreshMediaStateAsync();
    }

    public async Task SkipNextAsync()
    {
        var session = GetActiveSession();
        bool ok = false;
        if (session != null)
        {
            try
            {
                var controls = session.GetPlaybackInfo()?.Controls;
                if (controls?.IsNextEnabled == true)
                {
                    ok = await session.TrySkipNextAsync();
                }
                if (!ok)
                {
                    ok = await session.TrySkipNextAsync();
                }
                if (!ok && controls?.IsFastForwardEnabled == true)
                {
                    ok = await session.TryFastForwardAsync();
                }
                if (!ok && controls?.IsPlaybackPositionEnabled == true)
                {
                    var tl = session.GetTimelineProperties();
                    if (tl != null)
                    {
                        ok = await session.TryChangePlaybackPositionAsync((tl.Position + TimeSpan.FromSeconds(10)).Ticks);
                    }
                }
            }
            catch { }
        }
        if (!ok)
        {
            SendMediaKey(NativeMethods.VK_MEDIA_NEXT_TRACK);
        }
        await Task.Delay(150);
        await RefreshMediaStateAsync();
    }

    public async Task SkipPreviousAsync()
    {
        var session = GetActiveSession();
        bool ok = false;
        if (session != null)
        {
            try
            {
                var controls = session.GetPlaybackInfo()?.Controls;
                if (controls?.IsPreviousEnabled == true)
                {
                    ok = await session.TrySkipPreviousAsync();
                }
                if (!ok)
                {
                    ok = await session.TrySkipPreviousAsync();
                }
                if (!ok && controls?.IsRewindEnabled == true)
                {
                    ok = await session.TryRewindAsync();
                }
                if (!ok && controls?.IsPlaybackPositionEnabled == true)
                {
                    var tl = session.GetTimelineProperties();
                    if (tl != null)
                    {
                        TimeSpan targetPos = tl.Position > TimeSpan.FromSeconds(12)
                            ? tl.Position - TimeSpan.FromSeconds(10)
                            : TimeSpan.Zero;
                        ok = await session.TryChangePlaybackPositionAsync(targetPos.Ticks);
                    }
                }
            }
            catch { }
        }
        if (!ok)
        {
            SendMediaKey(NativeMethods.VK_MEDIA_PREV_TRACK);
        }
        await Task.Delay(150);
        await RefreshMediaStateAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        AttachSession(null);
        if (_sessionManager != null)
        {
            _sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _sessionManager = null;
        }
    }
}
