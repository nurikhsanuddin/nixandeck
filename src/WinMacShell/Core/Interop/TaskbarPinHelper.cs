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

    [GeneratedRegex(@"[A-Za-z0-9\._-]+_[a-z0-9]{13}", RegexOptions.Compiled)]
    private static partial Regex PackageFamilyRegex();

    private static bool _storeAppsInitialized;

    public static List<PinnedAppItem> DiscoverTaskbarPinnedApps()
    {
        var discovered = new List<PinnedAppItem>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Standard Windows Taskbar pinned shortcuts (.lnk)
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string userPinnedDir = Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        ScanShortcutDirectory(userPinnedDir, discovered, seenKeys);

        // 2. UWP / Packaged pinned apps stored in HKCU\...\Explorer\Taskband Favorites & FavoritesResolve
        ScanTaskbandRegistryAumids(discovered, seenKeys);

        return discovered;
    }

    public static bool UnpinFromTaskbar(string? shortcutPath, string? executablePath, string? appUserModelId)
    {
        bool unpinned = false;

        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return false;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return false;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string userPinnedDir = Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

            // 1. Try matching shortcut in User Pinned\TaskBar
            string? matchedLnk = null;
            if (!string.IsNullOrWhiteSpace(shortcutPath) && File.Exists(shortcutPath))
            {
                matchedLnk = shortcutPath;
            }
            else if (!string.IsNullOrWhiteSpace(executablePath) && Directory.Exists(userPinnedDir))
            {
                string exeName = Path.GetFileName(executablePath);
                foreach (string lnk in Directory.EnumerateFiles(userPinnedDir, "*.lnk", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        string target = ShellLinkHelper.GetLinkTarget(IntPtr.Zero, lnk);
                        if (!string.IsNullOrWhiteSpace(target) &&
                            string.Equals(Path.GetFileName(target), exeName, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedLnk = lnk;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (!string.IsNullOrWhiteSpace(matchedLnk) && File.Exists(matchedLnk))
            {
                string? dir = Path.GetDirectoryName(matchedLnk);
                string file = Path.GetFileName(matchedLnk);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    dynamic? ns = shell.NameSpace(dir);
                    dynamic? folderItem = ns?.ParseName(file);
                    if (TryInvokeUnpinVerb(folderItem))
                    {
                        unpinned = true;
                    }
                }

                if (!unpinned && matchedLnk.StartsWith(userPinnedDir, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        File.Delete(matchedLnk);
                        unpinned = true;
                    }
                    catch { }
                }
            }

            // 2. Try matching UWP/Store or desktop app via shell:AppsFolder
            if (!unpinned && (!string.IsNullOrWhiteSpace(appUserModelId) || !string.IsNullOrWhiteSpace(executablePath)))
            {
                dynamic? appsNs = shell.NameSpace("shell:AppsFolder");
                if (appsNs != null)
                {
                    foreach (dynamic item in appsNs.Items())
                    {
                        string? itemPath = item.Path as string;
                        if (string.IsNullOrWhiteSpace(itemPath)) continue;

                        bool isMatch = false;
                        if (!string.IsNullOrWhiteSpace(appUserModelId) &&
                            string.Equals(itemPath, appUserModelId, StringComparison.OrdinalIgnoreCase))
                        {
                            isMatch = true;
                        }
                        else if (!string.IsNullOrWhiteSpace(executablePath) &&
                                 string.Equals(itemPath, executablePath, StringComparison.OrdinalIgnoreCase))
                        {
                            isMatch = true;
                        }

                        if (isMatch)
                        {
                            if (TryInvokeUnpinVerb(item))
                            {
                                unpinned = true;
                            }
                            break;
                        }
                    }
                }
            }
        }
        catch { }

        return unpinned;
    }

    private static bool TryInvokeUnpinVerb(dynamic? folderItem)
    {
        if (folderItem == null) return false;
        try
        {
            dynamic? verbs = folderItem.Verbs();
            if (verbs == null) return false;

            foreach (dynamic verb in verbs)
            {
                string? name = verb.Name as string;
                if (string.IsNullOrWhiteSpace(name)) continue;

                string normalized = name.Replace("&", string.Empty);
                if (normalized.Contains("Unpin from taskbar", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Contains("taskbarunpin", StringComparison.OrdinalIgnoreCase) ||
                    (normalized.Contains("Lepas sematan", StringComparison.OrdinalIgnoreCase) &&
                     normalized.Contains("taskbar", StringComparison.OrdinalIgnoreCase)))
                {
                    verb.DoIt();
                    return true;
                }
            }
        }
        catch { }

        return false;
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
            if (key == null) return;

            EnsureStoreAppsLoaded();

            var blobs = new List<byte[]>();
            if (key.GetValue("Favorites") is byte[] fav && fav.Length > 0) blobs.Add(fav);
            if (key.GetValue("FavoritesResolve") is byte[] favRes && favRes.Length > 0) blobs.Add(favRes);

            foreach (byte[] blob in blobs)
            {
                string unicodeBlob = Encoding.Unicode.GetString(blob);

                // First match explicit full AUMIDs (PackageFamily!AppId)
                foreach (Match match in AumidRegex().Matches(unicodeBlob))
                {
                    AddStorePinByAumid(match.Value, output, seenKeys);
                }

                // Then match PackageFamilyNames (e.g. Lenovo Vantage, WhatsApp) against StoreAppHelper.AppList
                foreach (Match match in PackageFamilyRegex().Matches(unicodeBlob))
                {
                    string pfn = match.Value;
                    StoreApp? app = FindStoreAppByPackageFamily(pfn);
                    string resolvedAumid = app?.AppUserModelId ?? $"{pfn}!App";
                    AddStorePinByAumid(resolvedAumid, output, seenKeys, app);
                }
            }
        }
        catch
        {
            // Ignore registry read failures
        }
    }

    private static StoreApp? FindStoreAppByPackageFamily(string packageFamilyName)
    {
        try
        {
            foreach (StoreApp app in StoreAppHelper.AppList)
            {
                if (!string.IsNullOrWhiteSpace(app.AppUserModelId) &&
                    app.AppUserModelId.StartsWith(packageFamilyName + "!", StringComparison.OrdinalIgnoreCase))
                {
                    return app;
                }
            }
        }
        catch { }

        return null;
    }

    private static void AddStorePinByAumid(string aumid, List<PinnedAppItem> output, HashSet<string> seenKeys, StoreApp? storeApp = null)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return;
        // Reject PackageFullName strings (e.g. Name_1.0.0.0_x64__PublisherId!App) that appear in FavoritesResolve
        if (aumid.Contains("__", StringComparison.Ordinal)) return;

        storeApp ??= StoreAppHelper.AppList.GetAppByAumid(aumid);
        // Only add Store apps that actually exist in shell:AppsFolder so phantom/uninstalled entries are skipped
        if (storeApp == null) return;

        if (!seenKeys.Add($"aumid:{aumid}")) return;

        string displayName = !string.IsNullOrWhiteSpace(storeApp.DisplayName)
            ? storeApp.DisplayName
            : ExtractFriendlyNameFromAumid(aumid);

        output.Add(new PinnedAppItem
        {
            Name = displayName,
            AppUserModelId = aumid
        });
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

            ImageSource? shellAumidIcon = IconHelper.ExtractIconFromAumid(pinned.AppUserModelId, 64);
            if (shellAumidIcon != null)
            {
                return shellAumidIcon;
            }
        }

        if (!string.IsNullOrWhiteSpace(pinned.ShortcutPath) && File.Exists(pinned.ShortcutPath))
        {
            ImageSource? shellShortcutIcon = IconHelper.ExtractShellImage(pinned.ShortcutPath, 64);
            if (shellShortcutIcon != null)
            {
                return shellShortcutIcon;
            }

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
