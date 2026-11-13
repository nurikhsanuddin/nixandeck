using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NixanDeck.Watchdog;

internal static class Program
{
    private const string PipeName = "NixanDeck_WindowControlPipe";
    private const int SW_SHOW = 5;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const uint ABM_SETSTATE = 0x0000000A;
    private const int ABS_ALWAYSONTOP = 0x00000002;
    private const uint SPI_SETWORKAREA = 0x002F;
    private const uint SPIF_SENDCHANGE = 0x0002;
    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [STAThread]
    private static int Main(string[] args)
    {
        int targetPid = ParseTargetPid(args);
        if (targetPid <= 0)
        {
            targetPid = ReadTargetPidFromFile();
        }

        if (targetPid <= 0)
        {
            return 1;
        }

        using var cts = new CancellationTokenSource();
        _ = RunWindowCommandPipeServerAsync(cts.Token);

        try
        {
            using Process mainProcess = Process.GetProcessById(targetPid);
            mainProcess.WaitForExit();
        }
        catch
        {
            // Process already exited or inaccessible — proceed directly to restore taskbar
        }

        cts.Cancel();
        RestoreWindowsShellState();
        return 0;
    }

    private static async Task RunWindowCommandPipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipeSecurity = new PipeSecurity();
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                    AccessControlType.Allow));

                using NamedPipeServerStream server = NamedPipeServerStreamAcl.Create(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 256,
                    outBufferSize: 0,
                    pipeSecurity);

                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server);
                string? line = await reader.ReadLineAsync(ct);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    ExecuteWindowCommand(line.Trim());
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                try { await Task.Delay(100, ct); } catch { break; }
            }
        }
    }

    private static void ExecuteWindowCommand(string commandLine)
    {
        string[] parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !long.TryParse(parts[1], out long rawHwnd) || rawHwnd == 0)
        {
            return;
        }

        IntPtr hwnd = new(rawHwnd);
        string verb = parts[0].ToUpperInvariant();

        switch (verb)
        {
            case "MINIMIZE":
                if (!ShowWindow(hwnd, SW_MINIMIZE))
                {
                    ShowWindowAsync(hwnd, SW_MINIMIZE);
                }
                break;

            case "RESTORE":
                if (IsIconic(hwnd))
                {
                    if (!ShowWindow(hwnd, SW_RESTORE))
                    {
                        ShowWindowAsync(hwnd, SW_RESTORE);
                    }
                }
                SetForegroundWindow(hwnd);
                SwitchToThisWindow(hwnd, true);
                break;

            case "ACTIVATE":
                if (IsIconic(hwnd))
                {
                    if (!ShowWindow(hwnd, SW_RESTORE))
                    {
                        ShowWindowAsync(hwnd, SW_RESTORE);
                    }
                }
                if (!SetForegroundWindow(hwnd))
                {
                    SwitchToThisWindow(hwnd, true);
                }
                break;
        }
    }

    private static int ParseTargetPid(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--pid", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length &&
                int.TryParse(args[i + 1], out int pid))
            {
                return pid;
            }

            if (args[i].StartsWith("--pid=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i]["--pid=".Length..], out int inlinePid))
            {
                return inlinePid;
            }
        }

        return -1;
    }

    private static int ReadTargetPidFromFile()
    {
        try
        {
            string pidFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinMacShell",
                "watchdog.pid");
            if (File.Exists(pidFile) &&
                int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid) &&
                pid > 0)
            {
                return pid;
            }
        }
        catch { }

        try
        {
            Process[] procs = Process.GetProcessesByName("WinMacShell");
            if (procs.Length > 0)
            {
                int found = procs[0].Id;
                foreach (var p in procs) p.Dispose();
                return found;
            }
        }
        catch { }

        return -1;
    }

    private static void RestoreWindowsShellState()
    {
        try
        {
            IntPtr primary = FindWindow("Shell_TrayWnd", null);
            if (primary != IntPtr.Zero)
            {
                var abd = new APPBARDATA
                {
                    cbSize = Marshal.SizeOf<APPBARDATA>(),
                    hWnd = primary,
                    lParam = new IntPtr(ABS_ALWAYSONTOP)
                };
                SHAppBarMessage(ABM_SETSTATE, ref abd);
                ShowWindow(primary, SW_SHOW);
            }

            IntPtr secondary = IntPtr.Zero;
            while ((secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            {
                ShowWindow(secondary, SW_SHOW);
            }

            // Restore primary monitor work area if top bar reservation was left active
            IntPtr monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref mi) && mi.rcWork.Top > mi.rcMonitor.Top)
            {
                RECT restoredWork = mi.rcMonitor;
                SystemParametersInfo(SPI_SETWORKAREA, 0, ref restoredWork, SPIF_SENDCHANGE);
            }
        }
        catch
        {
            // Never throw from watchdog exit path
        }
    }
}
