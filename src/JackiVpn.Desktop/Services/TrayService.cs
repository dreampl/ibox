using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using JackiVpn.Application.Notifications;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;

namespace JackiVpn.Desktop.Services;

/// <summary>
/// Notification-area icon with a state badge and a small menu. Doubles as the toast sink: on Windows 10/11
/// balloon notifications are rendered as native toasts in Action Center (no package identity required).
/// </summary>
public sealed partial class TrayService : INotificationSink, IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _base;
    private readonly Icon _idle;
    private readonly Icon _busy;
    private readonly Icon _connected;
    private readonly Icon _error;
    private readonly ToolStripMenuItem _connectItem;
    private readonly Func<bool> _windowVisible;

    public TrayService(Func<bool> windowVisible)
    {
        _windowVisible = windowVisible;
        using (var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/IBOX;component/Assets/jacki.ico")).Stream)
        {
            _base = new Icon(stream, SystemInformation.SmallIconSize);
        }

        _idle = Badge(Color.FromArgb(0x9C, 0xA3, 0xAF));
        _busy = Badge(Color.FromArgb(0xF5, 0x9E, 0x0B));
        _connected = Badge(Color.FromArgb(0x22, 0xC5, 0x5E));
        _error = Badge(Color.FromArgb(0xEF, 0x44, 0x44));

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("Open IBOX", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        _connectItem = new ToolStripMenuItem("Connect", null, (_, _) => ToggleRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(_connectItem);
        _notchItem = new ToolStripMenuItem("Show notch") { CheckOnClick = true };
        _notchItem.CheckedChanged += (_, _) =>
        {
            if (!_settingNotch)
            {
                NotchToggled?.Invoke(this, _notchItem.Checked);
            }
        };
        menu.Items.Add(_notchItem);
        menu.Items.Add(new ToolStripSeparator());
        // A separate process on purpose: it still works when this one is stuck (and it closes this one).
        menu.Items.Add("Emergency network restore", null, (_, _) =>
        {
            if (Environment.ProcessPath is { } exe)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, EmergencyRestore.Argument) { UseShellExecute = false })?.Dispose();
            }
        });
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _icon = new NotifyIcon
        {
            Icon = _idle,
            Text = Domain.ProductInfo.DisplayName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ToggleRequested;

    public event EventHandler? ExitRequested;

    /// <summary>The user turned the notch on or off from the tray menu.</summary>
    public event EventHandler<bool>? NotchToggled;

    private readonly ToolStripMenuItem _notchItem;
    private bool _settingNotch;

    /// <summary>Mirrors the setting into the menu tick (without raising <see cref="NotchToggled"/>).</summary>
    public void SetNotchChecked(bool on)
    {
        _settingNotch = true;
        _notchItem.Checked = on;
        _settingNotch = false;
    }

    private (ConnectionState, string?)? _shown;

    public void Update(ConnectionStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        // The status arrives every second (traffic counters); the tray icon changes only with the state or server
        // (each change is a call into Explorer).
        if (_shown == (status.State, status.ProfileName))
        {
            return;
        }

        _shown = (status.State, status.ProfileName);
        _icon.Icon = status.State switch
        {
            ConnectionState.Connected => _connected,
            ConnectionState.Faulted => _error,
            ConnectionState.Disconnected => _idle,
            _ => _busy,
        };
        var text = status.State == ConnectionState.Connected && status.ProfileName is { } name
            ? $"{Domain.ProductInfo.DisplayName}: {name}"
            : $"{Domain.ProductInfo.DisplayName}: {status.State}";
        _icon.Text = text.Length > 63 ? text[..62] + "…" : text;
        _connectItem.Text = status.State switch
        {
            ConnectionState.Disconnected or ConnectionState.Faulted => "Connect",
            ConnectionState.Connected => "Disconnect",
            ConnectionState.Disconnecting => "Stopping…",
            _ => "Cancel connecting",
        };
        _connectItem.Enabled = status.State != ConnectionState.Disconnecting;
    }

    public void Show(AppNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // While the window is in front the in-app toast is enough; otherwise use the system notification.
        if (_windowVisible())
        {
            return;
        }

        var icon = notification.Category switch
        {
            NotificationCategory.ConnectionFailed or NotificationCategory.CoreCrashed or NotificationCategory.KillSwitchActivated => ToolTipIcon.Warning,
            NotificationCategory.SubscriptionExpired or NotificationCategory.TrafficAlmostFinished => ToolTipIcon.Warning,
            _ => ToolTipIcon.Info,
        };
        _icon.ShowBalloonTip(5000, notification.Title, notification.Message, icon);
    }

    private Icon Badge(Color color)
    {
        var size = SystemInformation.SmallIconSize;
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawIcon(_base, new Rectangle(0, 0, size.Width, size.Height));
            var d = Math.Max(6, size.Width * 7 / 16);
            var rect = new Rectangle(size.Width - d, size.Height - d, d - 1, d - 1);
            using var ring = new SolidBrush(Color.FromArgb(0x10, 0x12, 0x16));
            using var fill = new SolidBrush(color);
            g.FillEllipse(ring, rect);
            rect.Inflate(-1, -1);
            g.FillEllipse(fill, rect);
        }

        var handle = bmp.GetHicon();
        using var temp = Icon.FromHandle(handle);
        var copy = (Icon)temp.Clone();
        DestroyIcon(handle);
        return copy;
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _base.Dispose();
        _idle.Dispose();
        _busy.Dispose();
        _connected.Dispose();
        _error.Dispose();
    }
}
