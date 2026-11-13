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
        if (File.Exists(resolved) || Directory.Exists(resolved))
        {
            ImageSource? shellImage = ExtractShellImage(resolved, 64);
            if (shellImage != null)
            {
                return shellImage;
            }
        }

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

    public static ImageSource? ExtractIconFromAumid(string? aumid, int size = 64)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return null;
        return ExtractShellImage($"shell:AppsFolder\\{aumid}", size);
    }

    public static ImageSource? ExtractShellImage(string parsingName, int size = 64)
    {
        if (string.IsNullOrWhiteSpace(parsingName)) return null;
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            Guid iid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            int hr = NativeMethods.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out NativeMethods.IShellItemImageFactory factory);
            if (hr != 0 || factory == null)
            {
                return null;
            }

            try
            {
                hr = factory.GetImage(
                    new NativeMethods.SIZE { cx = size, cy = size },
                    NativeMethods.SIIGBF.SIIGBF_ICONONLY | NativeMethods.SIIGBF.SIIGBF_BIGGERSIZEOK,
                    out hBitmap);

                if (hr != 0 || hBitmap == IntPtr.Zero)
                {
                    hr = factory.GetImage(
                        new NativeMethods.SIZE { cx = size, cy = size },
                        NativeMethods.SIIGBF.SIIGBF_BIGGERSIZEOK,
                        out hBitmap);
                }

                if (hr == 0 && hBitmap != IntPtr.Zero)
                {
                    var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch
        {
            // Fallback to SHGetFileInfo
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(hBitmap);
            }
        }

        return null;
    }

    public static ImageSource? ExtractIconFromWindowHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            IntPtr hIcon = IntPtr.Zero;
            NativeMethods.SendMessageTimeout(hwnd, NativeMethods.WM_GETICON, new IntPtr(NativeMethods.ICON_BIG), IntPtr.Zero, 2, 80, out hIcon);
            if (hIcon == IntPtr.Zero)
            {
                NativeMethods.SendMessageTimeout(hwnd, NativeMethods.WM_GETICON, new IntPtr(NativeMethods.ICON_SMALL2), IntPtr.Zero, 2, 80, out hIcon);
            }
            if (hIcon == IntPtr.Zero)
            {
                hIcon = NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCLP_HICON);
            }
            if (hIcon == IntPtr.Zero)
            {
                hIcon = NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCLP_HICONSM);
            }
            if (hIcon == IntPtr.Zero) return null;

            // Do not call DestroyIcon on window/class-owned HICONs
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
