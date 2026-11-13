using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using WinMacShell.Core.Interop;
using WinMacShell.Models;

namespace WinMacShell.Core.Services;

public sealed class NotificationService : IDisposable
{
    private const int MaxHistoryCount = 15;
    private static readonly HttpClient AvatarHttpClient = new() { Timeout = TimeSpan.FromMilliseconds(1500) };

    private UserNotificationListener? _listener;
    private FileSystemWatcher? _wpnWatcher;
    private readonly HashSet<uint> _seenIds = new();
    private readonly object _historyLock = new();
    private readonly SemaphoreSlim _syncSemaphore = new(1, 1);
    private bool _disposed;
    private uint _syntheticIdCounter = 900_000;
    private long _lastSeenWpnOrder;
    private int _pendingDbSync;

    public ObservableCollection<NotificationItem> History { get; } = new();
    public int HistoryCount { get { lock (_historyLock) return History.Count; } }
    public bool IsListenerActive { get; private set; }
    public bool HasUnread { get; private set; }
    public bool IsDoNotDisturb { get; private set; }
    public string StatusMessage { get; private set; } = "Initializing notifications...";

    public void ToggleDoNotDisturb()
    {
        IsDoNotDisturb = !IsDoNotDisturb;
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler<NotificationItem>? NotificationReceived;
    public event EventHandler? HistoryChanged;

    public async Task InitializeAsync()
    {
        bool winrtListenerOk = false;

        try
        {
            _listener = UserNotificationListener.Current;
            UserNotificationListenerAccessStatus access = await _listener.RequestAccessAsync();

            if (_disposed) return;

            if (access == UserNotificationListenerAccessStatus.Allowed)
            {
                winrtListenerOk = true;
                IsListenerActive = true;
                StatusMessage = "Listening for Windows & WhatsApp notifications.";

                var existing = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                foreach (UserNotification n in existing)
                {
                    _seenIds.Add(n.Id);
                }

                foreach (UserNotification n in existing.OrderByDescending(x => x.CreationTime).Take(MaxHistoryCount))
                {
                    NotificationItem? parsed = await ParseNotificationAsync(n);
                    if (parsed != null)
                    {
                        lock (_historyLock)
                        {
                            History.Add(parsed);
                        }
                    }
                }

                _listener.NotificationChanged += OnNotificationChanged;
            }
            else
            {
                StatusMessage = "Using live WPN database capture for notifications.";
            }
        }
        catch
        {
            StatusMessage = "Using live WPN database capture for notifications.";
        }

        if (_disposed) return;

        await SyncFromWpnDatabaseAsync(initialLoad: true, populateHistory: !winrtListenerOk);
        StartNotificationFolderWatcher();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (_disposed) return;
        _ = SyncNotificationsAsync(args);
    }

    public async Task CheckForNewNotificationsAsync()
    {
        if (_disposed) return;
        if (_listener != null && IsListenerActive)
        {
            await SyncNotificationsAsync(null);
        }
        await SyncFromWpnDatabaseAsync(initialLoad: false, populateHistory: true);
    }

    private async Task SyncNotificationsAsync(UserNotificationChangedEventArgs? args)
    {
        if (_disposed || _listener == null) return;
        await _syncSemaphore.WaitAsync();
        try
        {
            if (_disposed || _listener == null) return;

            if (args?.ChangeKind == UserNotificationChangedKind.Removed)
            {
                bool removed = false;
                lock (_historyLock)
                {
                    NotificationItem? toRemove = History.FirstOrDefault(h => h.Id == args.UserNotificationId);
                    if (toRemove != null)
                    {
                        History.Remove(toRemove);
                        removed = true;
                    }
                }
                if (removed)
                {
                    HistoryChanged?.Invoke(this, EventArgs.Empty);
                }
                return;
            }

            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var newlyArrived = new List<UserNotification>();

            if (args is { ChangeKind: UserNotificationChangedKind.Added })
            {
                UserNotification? match = notifications.FirstOrDefault(n => n.Id == args.UserNotificationId);
                if (match != null && _seenIds.Add(match.Id))
                {
                    newlyArrived.Add(match);
                }
            }
            else
            {
                foreach (UserNotification n in notifications.OrderBy(x => x.CreationTime))
                {
                    if (_seenIds.Add(n.Id))
                    {
                        newlyArrived.Add(n);
                    }
                }
            }

            foreach (UserNotification n in newlyArrived)
            {
                NotificationItem? item = await ParseNotificationAsync(n);
                if (item == null || _disposed) continue;

                AddAndRaiseNotification(item);
            }
        }
        catch
        {
            // Ignore transient WinRT read failures
        }
        finally
        {
            _syncSemaphore.Release();
        }
    }

    public void TriggerTestNotification()
    {
        var item = new NotificationItem
        {
            Id = ++_syntheticIdCounter,
            AppName = "nixandeck",
            Title = "nixandeck Ready ✨🔥",
            Body = "Notifikasi emoji & Dynamic Island aktif! 🚀🎵",
            Timestamp = DateTimeOffset.Now
        };
        AddAndRaiseNotification(item);
    }

    private void AddAndRaiseNotification(NotificationItem item)
    {
        lock (_historyLock)
        {
            History.Insert(0, item);
            while (History.Count > MaxHistoryCount)
            {
                History.RemoveAt(History.Count - 1);
            }
        }

        HasUnread = true;
        if (!IsDoNotDisturb)
        {
            NotificationReceived?.Invoke(this, item);
        }
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MarkAllRead()
    {
        if (!HasUnread) return;
        HasUnread = false;
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public List<NotificationItem> GetRecentNotifications(int count)
    {
        lock (_historyLock)
        {
            return History.Take(count).ToList();
        }
    }

    public void ClearAll()
    {
        lock (_historyLock)
        {
            History.Clear();
        }
        HasUnread = false;

        if (_listener != null && IsListenerActive)
        {
            try { _listener.ClearNotifications(); } catch { }
        }

        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DismissNotification(uint id)
    {
        if (id == 0) return;

        if (_listener != null && IsListenerActive)
        {
            try { _listener.RemoveNotification(id); } catch { }
        }

        lock (_historyLock)
        {
            NotificationItem? match = History.FirstOrDefault(n => n.Id == id);
            if (match != null)
            {
                History.Remove(match);
            }
        }

        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string CleanNotificationText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string trimmed = text.Trim();

        if (trimmed.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmed.Contains("Alarm", StringComparison.OrdinalIgnoreCase))
            {
                return "⏰ Alarm";
            }
            if (trimmed.Contains("Timer", StringComparison.OrdinalIgnoreCase))
            {
                return "⏱️ Timer";
            }
            return string.Empty;
        }

        return trimmed;
    }

    private static async Task<NotificationItem?> ParseNotificationAsync(UserNotification notification)
    {
        try
        {
            string appName = notification.AppInfo?.DisplayInfo?.DisplayName ?? "Windows";
            NotificationBinding? toastBinding = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);

            string title = appName;
            string body = string.Empty;
            bool isAlarm = appName.Contains("Clock", StringComparison.OrdinalIgnoreCase)
                || appName.Contains("Alarm", StringComparison.OrdinalIgnoreCase);

            if (toastBinding != null)
            {
                var textElements = toastBinding.GetTextElements()
                    .Select(t => CleanNotificationText(t.Text))
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList();

                if (textElements.Any(t => t.Contains("Alarm", StringComparison.OrdinalIgnoreCase)))
                {
                    isAlarm = true;
                }

                if (textElements.Count == 1 && isAlarm)
                {
                    title = "⏰ Alarm";
                    body = textElements[0];
                }
                else
                {
                    if (textElements.Count > 0)
                    {
                        title = textElements[0];
                    }
                    if (textElements.Count > 1)
                    {
                        body = string.Join(" ", textElements.Skip(1));
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(title) && isAlarm)
            {
                title = "⏰ Alarm";
            }

            BitmapImage? logo = null;
            try
            {
                IRandomAccessStreamReference? logoRef = notification.AppInfo?.DisplayInfo?.GetLogo(new Size(64, 64));
                if (logoRef != null)
                {
                    logo = await LoadLogoAsync(logoRef);
                }
            }
            catch { }

            return new NotificationItem
            {
                Id = notification.Id,
                AppId = notification.AppInfo?.AppUserModelId ?? string.Empty,
                AppName = appName,
                Title = title,
                Body = body,
                Timestamp = notification.CreationTime,
                AppIcon = logo,
                IsAlarm = isAlarm
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<BitmapImage?> LoadLogoAsync(IRandomAccessStreamReference logoRef)
    {
        using var winrtStream = await logoRef.OpenReadAsync();
        using var netStream = winrtStream.AsStreamForRead();
        using var memStream = new MemoryStream();
        await netStream.CopyToAsync(memStream);
        memStream.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 64;
        bitmap.StreamSource = memStream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private async Task SyncFromWpnDatabaseAsync(bool initialLoad, bool populateHistory)
    {
        if (_disposed) return;

        string dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\Notifications\wpndatabase.db");

        if (!File.Exists(dbPath))
        {
            return;
        }

        await _syncSemaphore.WaitAsync();
        try
        {
            if (_disposed) return;

            var rows = ReadRecentWpnRows(dbPath);
            if (rows.Count == 0)
            {
                return;
            }

            if (initialLoad)
            {
                IsListenerActive = true;
                StatusMessage = "Listening for Windows & WhatsApp notifications.";
                _lastSeenWpnOrder = rows.Max(r => r.Order);

                foreach (var row in rows)
                {
                    _seenIds.Add((uint)row.Id);
                    if (populateHistory)
                    {
                        NotificationItem? item = await ParseWpnRowAsync(row);
                        if (item != null)
                        {
                            lock (_historyLock)
                            {
                                if (History.Count < MaxHistoryCount && !History.Any(h => h.Id == item.Id))
                                {
                                    History.Add(item);
                                }
                            }
                        }
                    }
                }

                HistoryChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            var newRows = rows
                .Where(r => r.Order > _lastSeenWpnOrder)
                .OrderBy(r => r.Order)
                .ToList();

            foreach (var row in newRows)
            {
                if (row.Order > _lastSeenWpnOrder)
                {
                    _lastSeenWpnOrder = row.Order;
                }

                if (!_seenIds.Add((uint)row.Id))
                {
                    continue;
                }

                NotificationItem? item = await ParseWpnRowAsync(row);
                if (item == null || _disposed) continue;

                AddAndRaiseNotification(item);
            }
        }
        catch
        {
            // Ignore transient SQLite read/lock issues
        }
        finally
        {
            _syncSemaphore.Release();
        }
    }

    private sealed record WpnRawRow(long Order, long Id, string PrimaryId, string PayloadXml, long ArrivalFileTime);

    private static List<WpnRawRow> ReadRecentWpnRows(string dbPath)
    {
        var results = new List<WpnRawRow>();
        IntPtr db = IntPtr.Zero;
        IntPtr stmt = IntPtr.Zero;

        try
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(dbPath + "\0");
            if (NativeMethods.sqlite3_open_v2(pathBytes, out db, NativeMethods.SQLITE_OPEN_READWRITE, IntPtr.Zero) != 0 || db == IntPtr.Zero)
            {
                return results;
            }

            const string query =
                "SELECT n.[Order], n.Id, h.PrimaryId, n.Payload, n.ArrivalTime " +
                "FROM Notification n LEFT JOIN NotificationHandler h ON n.HandlerId = h.RecordId " +
                "WHERE n.Type = 'toast' ORDER BY n.[Order] DESC LIMIT 15;\0";

            byte[] sqlBytes = Encoding.UTF8.GetBytes(query);
            if (NativeMethods.sqlite3_prepare_v2(db, sqlBytes, -1, out stmt, IntPtr.Zero) != 0 || stmt == IntPtr.Zero)
            {
                return results;
            }

            while (NativeMethods.sqlite3_step(stmt) == NativeMethods.SQLITE_ROW)
            {
                long order = NativeMethods.sqlite3_column_int64(stmt, 0);
                long id = NativeMethods.sqlite3_column_int64(stmt, 1);
                string primaryId = ReadUtf16Column(stmt, 2);
                string payloadXml = ReadUtf8Column(stmt, 3);
                long arrivalTime = NativeMethods.sqlite3_column_int64(stmt, 4);

                results.Add(new WpnRawRow(order, id, primaryId, payloadXml, arrivalTime));
            }
        }
        finally
        {
            if (stmt != IntPtr.Zero) NativeMethods.sqlite3_finalize(stmt);
            if (db != IntPtr.Zero) NativeMethods.sqlite3_close(db);
        }

        return results;
    }

    private static string ReadUtf16Column(IntPtr stmt, int col)
    {
        IntPtr ptr = NativeMethods.sqlite3_column_text16(stmt, col);
        return ptr != IntPtr.Zero ? (Marshal.PtrToStringUni(ptr) ?? string.Empty) : string.Empty;
    }

    private static string ReadUtf8Column(IntPtr stmt, int col)
    {
        int byteCount = NativeMethods.sqlite3_column_bytes(stmt, col);
        IntPtr blobPtr = NativeMethods.sqlite3_column_blob(stmt, col);
        if (blobPtr == IntPtr.Zero || byteCount <= 0)
        {
            return ReadUtf16Column(stmt, col);
        }

        byte[] buffer = new byte[byteCount];
        Marshal.Copy(blobPtr, buffer, 0, byteCount);
        return Encoding.UTF8.GetString(buffer).Trim('\0', '\uFEFF');
    }

    private static async Task<NotificationItem?> ParseWpnRowAsync(WpnRawRow row)
    {
        if (string.IsNullOrWhiteSpace(row.PayloadXml)) return null;

        try
        {
            string appName = MapFriendlyAppName(row.PrimaryId);
            string title = appName;
            string body = string.Empty;
            string? imageSrc = null;

            XDocument doc = XDocument.Parse(row.PayloadXml);
            bool isAlarm = row.PrimaryId.Contains("Alarm", StringComparison.OrdinalIgnoreCase)
                || row.PrimaryId.Contains("Clock", StringComparison.OrdinalIgnoreCase)
                || string.Equals((string?)doc.Root?.Attribute("scenario"), "alarm", StringComparison.OrdinalIgnoreCase)
                || row.PayloadXml.Contains("ms-resource:Alarm", StringComparison.OrdinalIgnoreCase);

            XElement? binding = doc.Descendants("binding").FirstOrDefault();
            if (binding != null)
            {
                var texts = binding.Descendants("text")
                    .Select(e => CleanNotificationText(System.Net.WebUtility.HtmlDecode(e.Value?.Trim())))
                    .Where(v => !string.IsNullOrEmpty(v))
                    .ToList();

                if (texts.Count == 1 && isAlarm)
                {
                    title = "⏰ Alarm";
                    body = texts[0];
                }
                else
                {
                    if (texts.Count > 0)
                    {
                        title = texts[0];
                    }
                    if (texts.Count > 1)
                    {
                        body = string.Join(" • ", texts.Skip(1));
                    }
                }

                XElement? imgEl = binding.Descendants("image")
                    .FirstOrDefault(e => string.Equals((string?)e.Attribute("placement"), "appLogoOverride", StringComparison.OrdinalIgnoreCase))
                    ?? binding.Descendants("image").FirstOrDefault();

                imageSrc = (string?)imgEl?.Attribute("src");
            }

            if (string.IsNullOrWhiteSpace(title) && isAlarm)
            {
                title = "⏰ Alarm";
            }

            BitmapImage? logo = await LoadNotificationImageAsync(imageSrc);

            DateTimeOffset timestamp;
            try
            {
                timestamp = row.ArrivalFileTime > 0
                    ? DateTimeOffset.FromFileTime(row.ArrivalFileTime)
                    : DateTimeOffset.Now;
            }
            catch
            {
                timestamp = DateTimeOffset.Now;
            }

            return new NotificationItem
            {
                Id = (uint)row.Id,
                AppId = row.PrimaryId ?? string.Empty,
                AppName = appName,
                Title = title,
                Body = body,
                Timestamp = timestamp,
                AppIcon = logo,
                IsAlarm = isAlarm
            };
        }
        catch
        {
            return null;
        }
    }

    private static string MapFriendlyAppName(string primaryId)
    {
        if (string.IsNullOrWhiteSpace(primaryId)) return "Windows";
        if (primaryId.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase)) return "WhatsApp";
        if (primaryId.Contains("Telegram", StringComparison.OrdinalIgnoreCase)) return "Telegram";
        if (primaryId.Contains("Discord", StringComparison.OrdinalIgnoreCase)) return "Discord";
        if (primaryId.Contains("Antigravity", StringComparison.OrdinalIgnoreCase)) return "Antigravity";
        if (primaryId.Contains("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase))
        {
            string tail = primaryId.Split('!')[0].Split('.').LastOrDefault() ?? "Windows";
            return tail;
        }

        string clean = primaryId.Split('!')[0];
        int underscore = clean.IndexOf('_');
        if (underscore > 0) clean = clean[..underscore];
        int lastDot = clean.LastIndexOf('.');
        return lastDot >= 0 && lastDot < clean.Length - 1 ? clean[(lastDot + 1)..] : clean;
    }

    private static async Task<BitmapImage?> LoadNotificationImageAsync(string? src)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;

        try
        {
            byte[]? bytes = null;
            if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                bytes = await AvatarHttpClient.GetByteArrayAsync(src);
            }
            else if (src.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
                     Uri.TryCreate(src, UriKind.Absolute, out Uri? fileUri) &&
                     File.Exists(fileUri.LocalPath))
            {
                bytes = await File.ReadAllBytesAsync(fileUri.LocalPath);
            }
            else if (File.Exists(src))
            {
                bytes = await File.ReadAllBytesAsync(src);
            }

            if (bytes == null || bytes.Length == 0) return null;

            using var memStream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 64;
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

    private void StartNotificationFolderWatcher()
    {
        if (_wpnWatcher != null) return;

        try
        {
            string wpnDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Windows\Notifications");

            if (!Directory.Exists(wpnDir)) return;

            _wpnWatcher = new FileSystemWatcher(wpnDir)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };
            _wpnWatcher.Changed += OnNotificationDatabaseChanged;
            _wpnWatcher.Created += OnNotificationDatabaseChanged;
        }
        catch
        {
            // Ignore watcher setup failure
        }
    }

    private void OnNotificationDatabaseChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _pendingDbSync, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(60);
                Interlocked.Exchange(ref _pendingDbSync, 0);
                await CheckForNewNotificationsAsync();
            }
            catch
            {
                Interlocked.Exchange(ref _pendingDbSync, 0);
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_wpnWatcher != null)
        {
            _wpnWatcher.EnableRaisingEvents = false;
            _wpnWatcher.Changed -= OnNotificationDatabaseChanged;
            _wpnWatcher.Created -= OnNotificationDatabaseChanged;
            _wpnWatcher.Dispose();
            _wpnWatcher = null;
        }

        if (_listener != null)
        {
            _listener.NotificationChanged -= OnNotificationChanged;
            _listener = null;
        }
        _syncSemaphore.Dispose();
    }
}
