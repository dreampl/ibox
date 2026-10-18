using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace JackiVpn.Bar.Sources;

/// <summary>
/// Window events from Windows itself (no polling): one out-of-context hook for top-level windows appearing and going
/// away, and per-process title-change hooks only for the programs a source asked for (a download manager's
/// progress window, Explorer's copy dialog, an Adobe app). Callbacks arrive on the UI thread's message loop.
/// </summary>
public static partial class WindowEvents
{
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectNameChange = 0x800C;
    private const uint EventObjectValueChange = 0x800E;
    private static readonly WinEventProc Callback = OnEvent;
    private static readonly Dictionary<uint, (nint Hook, int Count)> Watched = [];
    private static readonly Dictionary<uint, string> Names = [];
    private static nint _showHook;
    private static int _users;

    /// <summary>A top-level window appeared (hwnd, process id).</summary>
    public static event Action<nint, uint>? Shown;

    /// <summary>A top-level window was hidden or destroyed.</summary>
    public static event Action<nint, uint>? Gone;

    /// <summary>A window's title (or a control's value) changed in a watched process.</summary>
    public static event Action<nint, uint>? Changed;

    public static void Start()
    {
        if (_users++ == 0)
        {
            _showHook = SetWinEventHook(EventObjectDestroy, EventObjectHide, 0, Callback, 0, 0, 0 /* OUTOFCONTEXT */);
        }
    }

    public static void Stop()
    {
        if (_users > 0 && --_users == 0)
        {
            Unhook(ref _showHook);
            foreach (var (hook, _) in Watched.Values)
            {
                var h = hook;
                Unhook(ref h);
            }

            Watched.Clear();
        }
    }

    /// <summary>Title and value changes of one process (counted: the last <see cref="Unwatch"/> removes the hook).</summary>
    public static void Watch(uint pid)
    {
        if (Watched.TryGetValue(pid, out var w))
        {
            Watched[pid] = (w.Hook, w.Count + 1);
            return;
        }

        var hook = SetWinEventHook(EventObjectNameChange, EventObjectValueChange, 0, Callback, pid, 0, 0);
        Watched[pid] = (hook, 1);
    }

    public static void Unwatch(uint pid)
    {
        if (!Watched.TryGetValue(pid, out var w))
        {
            return;
        }

        if (w.Count > 1)
        {
            Watched[pid] = (w.Hook, w.Count - 1);
            return;
        }

        var hook = w.Hook;
        Unhook(ref hook);
        Watched.Remove(pid);
    }

    private static void OnEvent(nint hook, uint type, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == 0)
        {
            return;
        }

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (type == EventObjectNameChange || type == EventObjectValueChange)
        {
            Changed?.Invoke(hwnd, pid);
            return;
        }

        if (idObject != 0 || idChild != 0 || GetAncestor(hwnd, 2 /* GA_ROOT */) != hwnd)
        {
            return;
        }

        if (type == EventObjectShow)
        {
            Shown?.Invoke(hwnd, pid);
        }
        else
        {
            Gone?.Invoke(hwnd, pid);
        }
    }

    /// <summary>The program's file name without extension, lower case (cached per process id).</summary>
    public static string ProcessName(uint pid)
    {
        if (Names.TryGetValue(pid, out var name))
        {
            return name;
        }

        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName.ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            name = string.Empty;
        }

        if (Names.Count > 512)
        {
            Names.Clear();
        }

        Names[pid] = name;
        return name;
    }

    public static string? ProcessPath(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public static string Title(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(Math.Min(length + 1, 1024));
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassName(nint hwnd)
    {
        var sb = new StringBuilder(128);
        _ = GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static bool Alive(nint hwnd) => IsWindow(hwnd);

    /// <summary>The top-level window a control belongs to.</summary>
    public static nint Root(nint hwnd) => GetAncestor(hwnd, 2 /* GA_ROOT */);

    public static bool Visible(nint hwnd) => IsWindowVisible(hwnd);

    /// <summary>Brings a program's main window forward (a click on its circle).</summary>
    public static void Focus(nint hwnd)
    {
        if (hwnd != 0)
        {
            _ = ShowWindow(hwnd, 9 /* SW_RESTORE */);
            _ = SetForegroundWindow(hwnd);
        }
    }

    private static void Unhook(ref nint hook)
    {
        if (hook != 0)
        {
            _ = UnhookWinEvent(hook);
            hook = 0;
        }
    }

    private delegate void WinEventProc(nint hook, uint type, nint hwnd, int idObject, int idChild, uint thread, uint time);

#pragma warning disable SYSLIB1054 // delegate and StringBuilder marshalling: classic interop
    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc proc, uint process, uint thread, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder text, int max);
#pragma warning restore SYSLIB1054

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);
}
