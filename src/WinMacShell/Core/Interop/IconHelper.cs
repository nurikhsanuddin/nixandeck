using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinMacShell.Core.Interop;

internal static class IconHelper
{
    public static ImageSource? CreateFrozenBitmapFromHIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    public static ImageSource? ExtractIconFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string resolved = ResolveExecutablePath(path);
        var shfi = new NativeMethods.SHFILEINFO();
        uint flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON;

        if (!File.Exists(resolved) && !Directory.Exists(resolved))
        {
            flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;
        }

        IntPtr result = NativeMethods.SHGetFileInfo(
            resolved,
            NativeMethods.FILE_ATTRIBUTE_NORMAL,
            ref shfi,
            (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
            flags);

        if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
        {
            if (shfi.hIcon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(shfi.hIcon);
            }
            return null;
        }

        return CreateFrozenBitmapFromHIcon(shfi.hIcon);
    }

    public static string ResolveExecutablePath(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return string.Empty;

        string expanded = Environment.ExpandEnvironmentVariables(executable.Trim('"'));
        if (File.Exists(expanded) || Directory.Exists(expanded))
        {
            return expanded;
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(dir.Trim(), expanded);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        // Check WindowsApps alias directory (e.g., wt.exe)
        string windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            expanded);
        if (File.Exists(windowsApps))
        {
            return windowsApps;
        }

        return expanded;
    }
}
