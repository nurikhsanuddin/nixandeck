using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using ManagedShell.Common.Enums;
using ManagedShell.ShellFolders;
using ManagedShell.UWPInterop;
using Microsoft.Win32;
using WinMacShell.Models;

namespace WinMacShell.Core.Interop;

internal static partial class TaskbarPinHelper
{
    private static readonly HashSet<string> IgnoredShortcuts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shows Desktop.lnk",
        "Show Desktop.lnk",
        "Window Switcher.lnk"
    };

    [GeneratedRegex(@"[A-Za-z0-9\._-]+_[a-z0-9]{13}![A-Za-z0-9\._-]+", RegexOptions.Compiled)]
    private static partial Regex AumidRegex();

    private static bool _storeAppsInitialized;

    public static List<PinnedAppItem> DiscoverTaskbarPinnedApps()
    {
        var discovered = new List<PinnedAppItem>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Standard Windows Taskbar pinned shortcuts (.lnk)
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string userPinnedDir = Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        ScanShortcutDirectory(userPinnedDir, discovered, seenKeys);

        // 2. UWP / Packaged pinned apps stored in HKCU\...\Explorer\Taskband Favorites
        ScanTaskbandRegistryAumids(discovered, seenKeys);

        // 3. Quick Launch shortcuts if any (Chrome, Edge, etc.)
        string quickLaunchDir = Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch");
        ScanShortcutDirectory(quickLaunchDir, discovered, seenKeys);

        return discovered;
    }

    private static void ScanShortcutDirectory(string dir, List<PinnedAppItem> output, HashSet<string> seenKeys)
    {
        if (!Directory.Exists(dir)) return;

        foreach (string lnkPath in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(lnkPath);
            if (IgnoredShortcuts.Contains(fileName)) continue;

            try
            {
                string target = ShellLinkHelper.GetLinkTarget(IntPtr.Zero, lnkPath);
                if (string.IsNullOrWhiteSpace(target)) continue;

                string key = Path.GetFileName(target).ToLowerInvariant();
                if (!seenKeys.Add($"exe:{key}")) continue;

                output.Add(new PinnedAppItem
                {
                    Name = Path.GetFileNameWithoutExtension(lnkPath),
                    ExecutablePath = target,
                    ShortcutPath = lnkPath
                });
            }
            catch
            {
                // Ignore broken shortcut
            }
        }
    }

    private static void ScanTaskbandRegistryAumids(List<PinnedAppItem> output, HashSet<string> seenKeys)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
            if (key?.GetValue("Favorites") is not byte[] favoritesBytes || favoritesBytes.Length == 0)
            {
                return;
            }

            EnsureStoreAppsLoaded();
            string unicodeBlob = Encoding.Unicode.GetString(favoritesBytes);

            foreach (Match match in AumidRegex().Matches(unicodeBlob))
            {
                string aumid = match.Value;
                if (!seenKeys.Add($"aumid:{aumid}")) continue;

                StoreApp? storeApp = StoreAppHelper.AppList.GetAppByAumid(aumid);
                string displayName = storeApp != null && !string.IsNullOrWhiteSpace(storeApp.DisplayName)
                    ? storeApp.DisplayName
                    : ExtractFriendlyNameFromAumid(aumid);

                output.Add(new PinnedAppItem
                {
                    Name = displayName,
                    AppUserModelId = aumid
                });
            }
        }
        catch
        {
            // Ignore registry read failures
        }
    }

    public static ImageSource? GetPinnedAppIcon(PinnedAppItem pinned)
    {
        if (!string.IsNullOrWhiteSpace(pinned.AppUserModelId))
        {
            try
            {
                EnsureStoreAppsLoaded();
                StoreApp? app = StoreAppHelper.AppList.GetAppByAumid(pinned.AppUserModelId);
                ImageSource? icon = app?.GetIconImageSource(IconSize.Large);
                if (icon != null)
                {
                    return icon;
                }
            }
            catch
            {
                // Fallback below
            }
        }

        if (!string.IsNullOrWhiteSpace(pinned.ShortcutPath) && File.Exists(pinned.ShortcutPath))
        {
            try
            {
                using var shellItem = new ShellItem(pinned.ShortcutPath);
                if (shellItem.LargeIcon != null)
                {
                    return shellItem.LargeIcon;
                }
            }
            catch
            {
                // Fallback below
            }
        }

        return IconHelper.ExtractIconFromPath(pinned.ExecutablePath);
    }

    private static void EnsureStoreAppsLoaded()
    {
        if (_storeAppsInitialized) return;
        _storeAppsInitialized = true;
        try
        {
            StoreAppHelper.AppList.FetchApps();
        }
        catch
        {
            // Ignore StoreAppList fetch issues
        }
    }

    private static string ExtractFriendlyNameFromAumid(string aumid)
    {
        int underscore = aumid.IndexOf('_');
        string prefix = underscore > 0 ? aumid[..underscore] : aumid;
        int dot = prefix.LastIndexOf('.');
        return dot >= 0 && dot < prefix.Length - 1 ? prefix[(dot + 1)..] : prefix;
    }
}
