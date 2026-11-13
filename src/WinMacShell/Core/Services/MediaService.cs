using System.IO;
using System.Runtime.InteropServices;
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
    private readonly List<GlobalSystemMediaTransportControlsSession> _trackedSessions = new();
    private readonly object _sessionLock = new();
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
            _sessionManager.SessionsChanged += OnSessionsChanged;
            RefreshTrackedSessions();
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
        RefreshTrackedSessions();
        _ = RefreshMediaStateAsync();
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        if (_disposed) return;
        RefreshTrackedSessions();
        _ = RefreshMediaStateAsync();
    }

    private void RefreshTrackedSessions()
    {
        lock (_sessionLock)
        {
            foreach (var old in _trackedSessions)
            {
                try
                {
                    old.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                    old.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                }
                catch { }
            }
            _trackedSessions.Clear();

            if (_sessionManager == null) return;

            try
            {
                var sessions = _sessionManager.GetSessions();
                if (sessions != null)
                {
                    foreach (var s in sessions)
                    {
                        if (s == null) continue;
                        _trackedSessions.Add(s);
                        s.PlaybackInfoChanged += OnPlaybackInfoChanged;
                        s.MediaPropertiesChanged += OnMediaPropertiesChanged;
                    }
                }
            }
            catch { }
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
        var session = GetActiveSession();

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
                if (sessions != null)
                {
                    foreach (var s in sessions)
                    {
                        try
                        {
                            if (s?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                            {
                                return s;
                            }
                        }
                        catch { }
                    }

                    var current = _sessionManager.GetCurrentSession();
                    if (current != null) return current;

                    return sessions.FirstOrDefault();
                }

                return _sessionManager.GetCurrentSession();
            }
        }
        catch { }

        lock (_sessionLock)
        {
            return _trackedSessions.FirstOrDefault();
        }
    }

    private static TimeSpan GetLivePosition(GlobalSystemMediaTransportControlsSession session, GlobalSystemMediaTransportControlsSessionTimelineProperties tl)
    {
        TimeSpan pos = tl.Position;
        try
        {
            var status = session.GetPlaybackInfo()?.PlaybackStatus;
            if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                tl.LastUpdatedTime != default)
            {
                TimeSpan elapsed = DateTimeOffset.UtcNow - tl.LastUpdatedTime;
                if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(12))
                {
                    pos += elapsed;
                }
            }
        }
        catch { }

        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        if (tl.EndTime > TimeSpan.Zero && pos > tl.EndTime) pos = tl.EndTime;
        return pos;
    }

    private static void SendMediaKey(byte vk)
    {
        try
        {
            var inputs = new NativeMethods.INPUT[2];
            inputs[0].type = NativeMethods.INPUT_KEYBOARD;
            inputs[0].ki.wVk = vk;
            inputs[1].type = NativeMethods.INPUT_KEYBOARD;
            inputs[1].ki.wVk = vk;
            inputs[1].ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
            uint sent = NativeMethods.SendInput(2, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
            if (sent == 0)
            {
                NativeMethods.keybd_event(vk, 0, 0, UIntPtr.Zero);
                NativeMethods.keybd_event(vk, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
        }
        catch
        {
            NativeMethods.keybd_event(vk, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(vk, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
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

                // 1. Only trust TrySkipNextAsync first when the session explicitly enables Next Track
                if (controls?.IsNextEnabled == true)
                {
                    ok = await session.TrySkipNextAsync();
                }

                // 2. For single YouTube videos / streams where IsNextEnabled is false, seek forward +15s using live interpolated timeline
                if (!ok && controls?.IsPlaybackPositionEnabled == true)
                {
                    var tl = session.GetTimelineProperties();
                    if (tl != null && tl.EndTime > TimeSpan.Zero)
                    {
                        TimeSpan livePos = GetLivePosition(session, tl);
                        TimeSpan targetPos = livePos + TimeSpan.FromSeconds(15);
                        if (targetPos > tl.EndTime) targetPos = tl.EndTime;
                        ok = await session.TryChangePlaybackPositionAsync(targetPos.Ticks);
                    }
                }

                if (!ok && controls?.IsFastForwardEnabled == true)
                {
                    ok = await session.TryFastForwardAsync();
                }

                if (!ok)
                {
                    SendMediaKey(NativeMethods.VK_MEDIA_NEXT_TRACK);
                    ok = true;
                }
            }
            catch
            {
                SendMediaKey(NativeMethods.VK_MEDIA_NEXT_TRACK);
            }
        }
        else
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

                // 1. Only trust TrySkipPreviousAsync first when the session explicitly enables Previous Track
                if (controls?.IsPreviousEnabled == true)
                {
                    string? beforeTitle = null;
                    try { beforeTitle = (await session.TryGetMediaPropertiesAsync())?.Title; } catch { }

                    ok = await session.TrySkipPreviousAsync();

                    // Many players (Spotify / YouTube playlists) only rewind to 0:00 on the first Previous click if >3s into the track;
                    // send a second Previous pulse if the track title did not change so a single click goes to the previous track.
                    await Task.Delay(90);
                    try
                    {
                        string? afterTitle = (await session.TryGetMediaPropertiesAsync())?.Title;
                        if (!string.IsNullOrEmpty(beforeTitle) &&
                            string.Equals(beforeTitle, afterTitle, StringComparison.Ordinal))
                        {
                            await session.TrySkipPreviousAsync();
                        }
                    }
                    catch { }
                }

                // 2. For single YouTube videos / streams where IsPreviousEnabled is false, seek backward -15s using live interpolated timeline
                if (!ok && controls?.IsPlaybackPositionEnabled == true)
                {
                    var tl = session.GetTimelineProperties();
                    if (tl != null)
                    {
                        TimeSpan livePos = GetLivePosition(session, tl);
                        TimeSpan targetPos = livePos > TimeSpan.FromSeconds(15)
                            ? livePos - TimeSpan.FromSeconds(15)
                            : TimeSpan.Zero;
                        ok = await session.TryChangePlaybackPositionAsync(targetPos.Ticks);
                    }
                }

                if (!ok && controls?.IsRewindEnabled == true)
                {
                    ok = await session.TryRewindAsync();
                }

                if (!ok)
                {
                    SendMediaKey(NativeMethods.VK_MEDIA_PREV_TRACK);
                    ok = true;
                }
            }
            catch
            {
                SendMediaKey(NativeMethods.VK_MEDIA_PREV_TRACK);
            }
        }
        else
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

        lock (_sessionLock)
        {
            foreach (var old in _trackedSessions)
            {
                try
                {
                    old.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                    old.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                }
                catch { }
            }
            _trackedSessions.Clear();
        }

        if (_sessionManager != null)
        {
            _sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _sessionManager.SessionsChanged -= OnSessionsChanged;
            _sessionManager = null;
        }
    }
}
