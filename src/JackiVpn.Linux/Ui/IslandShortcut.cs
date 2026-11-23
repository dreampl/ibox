using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace JackiVpn.Linux.Ui;

internal sealed class IslandShortcut(Action toggle) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    public void Start()
    {
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("DISPLAY") is not null)
        {
            _worker = Task.Run(Listen);
        }
    }

    private void Listen()
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) { return; }
        var root = XDefaultRootWindow(display);
        var key = XKeysymToKeycode(display, XStringToKeysym("i"));
        var failed = false;
        ErrorHandler? previous = null;
        ErrorHandler onError = (d, e) =>
        {
            if (d == display) { failed = true; return 0; }
            return previous?.Invoke(d, e) ?? 0;
        };
        var old = XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(onError));
        if (old != IntPtr.Zero) { previous = Marshal.GetDelegateForFunctionPointer<ErrorHandler>(old); }
        uint[] locks = [0, 2, 16, 18];
        try
        {
            foreach (var mask in locks) { XGrabKey(display, key, 12 | mask, root, false, 1, 1); }
            XSync(display, false);
        }
        finally
        {
            XSetErrorHandler(old);
            GC.KeepAlive(onError);
        }
        try
        {
            if (failed) { return; }
            var fd = new PollFd { Fd = XConnectionNumber(display), Events = 1 };
            while (!_stop.IsCancellationRequested)
            {
                if (XPending(display) == 0 && Poll(ref fd, 1, 500) <= 0) { continue; }
                while (XPending(display) > 0)
                {
                    XNextEvent(display, out var ev);
                    if (ev.Type == 2 && ev.KeyCode == key) { Dispatcher.UIThread.Post(toggle); }
                }
            }
        }
        finally
        {
            foreach (var mask in locks) { XUngrabKey(display, key, 12 | mask, root); }
            XCloseDisplay(display);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_worker is null) { _stop.Dispose(); }
        else { _ = _worker.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ErrorHandler(IntPtr display, IntPtr error);
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XEvent
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(84)] public uint KeyCode;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd { public int Fd; public short Events; public short Revents; }
    [DllImport("libc", EntryPoint = "poll")] private static extern int Poll(ref PollFd fds, nuint count, int timeout);
    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)] private static extern nuint XStringToKeysym(string name);
    [DllImport("libX11.so.6")] private static extern byte XKeysymToKeycode(IntPtr display, nuint keysym);
    [DllImport("libX11.so.6")] private static extern int XGrabKey(IntPtr display, int key, uint modifiers, IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool owner, int pointerMode, int keyboardMode);
    [DllImport("libX11.so.6")] private static extern int XUngrabKey(IntPtr display, int key, uint modifiers, IntPtr window);
    [DllImport("libX11.so.6")] private static extern int XSync(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool discard);
    [DllImport("libX11.so.6")] private static extern IntPtr XSetErrorHandler(IntPtr handler);
    [DllImport("libX11.so.6")] private static extern int XConnectionNumber(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XPending(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XNextEvent(IntPtr display, out XEvent ev);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
}
