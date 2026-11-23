using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using JackiVpn.Domain.Visuals;

namespace JackiVpn.Desktop.Views;

public sealed partial class NotchWindow
{
    private const int ShortcutId = 0x4942;
    private HwndSource? _shortcutSource;
    private bool _shortcutVisible;
    private bool _shortcutRegistered;
    private readonly DispatcherTimer _shortcutTimeout = new() { Interval = TimeSpan.FromSeconds(15) };

    private void RegisterShortcut()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _shortcutSource = HwndSource.FromHwnd(hwnd);
        _shortcutRegistered = RegisterHotKey(hwnd, ShortcutId, 0x4003, 0x49); // Ctrl+Alt+I, no key repeats.
        _shortcutSource?.AddHook(OnShortcutMessage);
        _shortcutTimeout.Tick += (_, _) =>
        {
            if (!_pill.IsMouseOver) { DismissShortcut(); }
        };
        Closed += (_, _) =>
        {
            _shortcutTimeout.Stop();
            if (_shortcutRegistered) { UnregisterHotKey(hwnd, ShortcutId); }
            _shortcutSource?.RemoveHook(OnShortcutMessage);
        };
        _pill.ToolTip = _shortcutRegistered ? "Ctrl+Alt+I: show or hide the island, including over full-screen apps"
            : "Ctrl+Alt+I is already in use by another app";
    }

    private IntPtr OnShortcutMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == ShortcutId)
        {
            handled = true;
            if (_shortcutVisible) { DismissShortcut(); }
            else
            {
                _shortcutVisible = true;
                EndSeeThrough();
                CheckFullScreen();
                SetFaded(false);
                SetExpanded(true);
                Topmost = false;
                Topmost = true;
                _shortcutTimeout.Start();
            }
        }
        return IntPtr.Zero;
    }

    private void DismissShortcut()
    {
        _shortcutTimeout.Stop();
        _shortcutVisible = false;
        SetExpanded(false);
        CheckFullScreen();
        RestartFade();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
