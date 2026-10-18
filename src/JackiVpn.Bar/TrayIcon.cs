using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace JackiVpn.Bar;

/// <summary>
/// IBAR's icon in the notification area, made directly with Shell_NotifyIcon (Windows Forms would load several
/// megabytes for one icon). Left click opens the control panel, right click the menu; comes back after Explorer restarts.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 41;
    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly HwndSource _window;
    private readonly nint _icon;
    private bool _added;

    public TrayIcon(string tip)
    {
        _window = new HwndSource(new HwndSourceParameters("IBAR tray") { ParentWindow = -3 /* HWND_MESSAGE */, WindowStyle = 0 });
        _window.AddHook(Hook);
        _icon = ExtractIcon(0, Environment.ProcessPath ?? string.Empty, 0);
        Tip = tip;
        Add();
    }

    public event EventHandler? Clicked;

    public event EventHandler? MenuRequested;

    public string Tip { get; }

    private void Add()
    {
        var data = Data();
        _added = ShellNotifyIcon(0 /* NIM_ADD */, ref data);
    }

    private NotifyIconData Data() => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        Window = _window.Handle,
        Id = 1,
        Flags = 0x1 | 0x2 | 0x4, // NIF_MESSAGE | NIF_ICON | NIF_TIP
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = Tip,
    };

    private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)lParam & 0xFFFF)
            {
                case 0x0202: // WM_LBUTTONUP
                    Clicked?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
                case 0x0205: // WM_RBUTTONUP
                    MenuRequested?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
            }
        }
        else if (msg == TaskbarCreated)
        {
            Add();
        }

        return 0;
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data();
            ShellNotifyIcon(2 /* NIM_DELETE */, ref data);
            _added = false;
        }

        if (_icon != 0)
        {
            DestroyIcon(_icon);
        }

        _window.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public nint Window;
        public int Id;
        public int Flags;
        public int CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public int State;
        public int StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public int TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public int InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

#pragma warning disable SYSLIB1054 // struct with by-value strings: classic marshalling
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(int message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ExtractIcon(nint instance, string path, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);
#pragma warning restore SYSLIB1054

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
}
