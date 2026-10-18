using System;
using System.Runtime.InteropServices;
using System.Text;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// Tells each notch whether a full-screen window (game, video, slides) covers its own monitor. Driven by window
/// events — the foreground changing, and the foreground window moving or resizing (F11, a game switching mode) —
/// never by a polling timer. The move/resize hook is scoped to the foreground window's process only.
/// </summary>
public static partial class FullScreenWatcher
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WinEventOutOfContext = 0x0000;
    private static readonly WinEventProc Callback = OnEvent; // kept alive for the native side
    private static nint _foregroundHook;
    private static nint _locationHook;
    private static uint _hookedProcess;
    private static int _subscribers;
    private static EventHandler? _changed;

    /// <summary>Something that may change full-screen state happened (raised on the UI thread).</summary>
    public static event EventHandler? Changed
    {
        add
        {
            _changed += value;
            if (++_subscribers == 1)
            {
                _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, Callback, 0, 0, WinEventOutOfContext);
                FollowForeground();
            }
        }

        remove
        {
            _changed -= value;
            if (--_subscribers == 0)
            {
                Unhook(ref _foregroundHook);
                Unhook(ref _locationHook);
                _hookedProcess = 0;
            }
        }
    }

    /// <summary>Whether the window in front fills the monitor <paramref name="notch"/> is on.</summary>
    public static bool Covers(nint notch)
    {
        var front = GetForegroundWindow();
        if (front == 0 || notch == 0 || IsShell(front))
        {
            return false;
        }

        var monitor = MonitorFromWindow(notch, 2);
        if (MonitorFromWindow(front, 2) != monitor)
        {
            return false;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info) && GetWindowRect(front, out var r)
            && r.Left <= info.Monitor.Left && r.Top <= info.Monitor.Top && r.Right >= info.Monitor.Right && r.Bottom >= info.Monitor.Bottom;
    }

    private static bool IsShell(nint hwnd)
    {
        var name = new StringBuilder(64);
        _ = GetClassName(hwnd, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private static void OnEvent(nint hook, uint type, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (type == EventSystemForeground)
        {
            FollowForeground();
            _changed?.Invoke(null, EventArgs.Empty);
        }
        else if (idObject == 0 && hwnd == GetForegroundWindow())
        {
            _changed?.Invoke(null, EventArgs.Empty);
        }
    }

    private static void FollowForeground()
    {
        var front = GetForegroundWindow();
        _ = GetWindowThreadProcessId(front, out var pid);
        if (pid == _hookedProcess)
        {
            return;
        }

        Unhook(ref _locationHook);
        _hookedProcess = pid;
        if (pid != 0)
        {
            _locationHook = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, 0, Callback, pid, 0, WinEventOutOfContext);
        }
    }

    private static void Unhook(ref nint hook)
    {
        if (hook != 0)
        {
            UnhookWinEvent(hook);
            hook = 0;
        }
    }

    private delegate void WinEventProc(nint hook, uint type, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

#pragma warning disable SYSLIB1054 // delegate marshalling: classic interop
    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc proc, uint process, uint thread, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder name, int max);
#pragma warning restore SYSLIB1054

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out NativeRect rect);
}
