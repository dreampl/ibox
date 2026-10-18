using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace JackiVpn.Desktop.Services;

/// <summary>
/// Says when Windows shows a notification, so the notification list is read only then (Windows offers apps like
/// IBAR no change event, and reading the list every second or two kept the CPU awake all day). A window event hook
/// limited to the process that draws notifications (ShellExperienceHost): no events from anything else reach us.
/// Must be started on a thread with a message loop (the UI thread).
/// </summary>
public static partial class ToastSignal
{
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectNameChange = 0x800C;
    private static readonly WinEventProc Callback = OnShow;
    private static nint _hook;
    private static nint _nameHook;
    private static Action? _shown;

    /// <summary>Raised with every notification window (tests and diagnostics).</summary>
    public static event Action? Seen;

    /// <summary>Starts watching; <paramref name="shown"/> runs on the UI thread when a notification window appears.</summary>
    public static bool Start(Action shown)
    {
        Stop();
        var host = Process.GetProcessesByName("ShellExperienceHost").FirstOrDefault();
        if (host is null)
        {
            return false;
        }

        _shown = shown;
        _hook = SetWinEventHook(EventObjectShow, EventObjectShow, 0, Callback, (uint)host.Id, 0, 0);

        // The notification host's window also announces a new notification by changing its name — measured on
        // Windows 11 even when the banner is not shown as a new window.
        _nameHook = SetWinEventHook(EventObjectNameChange, EventObjectNameChange, 0, Callback, (uint)host.Id, 0, 0);
        host.Dispose();
        return _hook != 0;
    }

    public static void Stop()
    {
        if (_hook != 0)
        {
            UnhookWinEvent(_hook);
            _hook = 0;
        }

        if (_nameHook != 0)
        {
            UnhookWinEvent(_nameHook);
            _nameHook = 0;
        }

        _shown = null;
    }

    private static void OnShow(nint hook, uint type, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject == 0 && hwnd != 0)
        {
            _shown?.Invoke();
            Seen?.Invoke();
        }
    }

    private delegate void WinEventProc(nint hook, uint type, nint hwnd, int idObject, int idChild, uint thread, uint time);

#pragma warning disable SYSLIB1054 // delegate marshalling: classic interop
    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc proc, uint process, uint thread, uint flags);
#pragma warning restore SYSLIB1054

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hook);
}
