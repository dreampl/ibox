using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using JackiVpn.Desktop.Services;
using JackiVpn.Desktop.ViewModels;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// The compact window (Mini view): the big toggle with the state, the current server with a small picker, and one
/// speed / latency line. It shares the app's view models with the full window; the full window is closed meanwhile.
/// </summary>
public partial class MiniWindow : Window
{
    private const double Gap = 12;

    private readonly MainViewModel _vm;
    private ListCollectionView? _servers;

    public MiniWindow(MainViewModel vm, WindowBounds? bounds, bool onTop)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        BorderThickness = new Thickness(1);
        SetResourceReference(BorderBrushProperty, "B.Border");
        Place(bounds);
        Topmost = onTop;
        PinButton.IsChecked = onTop;

        SourceInitialized += (_, _) =>
        {
            ThemeManager.ApplyTitleBar(this);
            var round = 2; // DWMWCP_ROUND: Windows 11 rounds the frameless window like any other
            _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int));
        };
        vm.Dashboard.PropertyChanged += OnDashboardChanged;
        UpdateStatusDot();
        IsVisibleChanged += (_, _) =>
        {
            UpdateOnScreen();
            if (IsVisible)
            {
                PlayEntrance();
            }
        };
        StateChanged += (_, _) => UpdateOnScreen();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Picker.Visibility == Visibility.Visible)
            {
                ClosePicker();
                e.Handled = true;
            }
        };
    }

    /// <summary>The window is on screen (not minimized, not hidden in the tray).</summary>
    public bool OnScreen { get; private set; }

    /// <summary>Raised when <see cref="OnScreen"/> changes.</summary>
    public event EventHandler<bool>? OnScreenChanged;

    /// <summary>Raised when the user pins the window on top of others, or unpins it.</summary>
    public event EventHandler<bool>? PinnedChanged;

    /// <summary>Raised when the window leaves the screen (closed or hidden to the tray), with where it was.</summary>
    public event EventHandler<WindowBounds>? BoundsCommitted;

    /// <summary>Set by the app: when true, closing hides to the tray instead of exiting.</summary>
    public Func<bool> CloseToTray { get; set; } = () => false;

    public bool AllowClose { get; set; }

    public WindowBounds Bounds => WindowState == WindowState.Normal
        ? new WindowBounds(Left, Top, Width, Height)
        : new WindowBounds(RestoreBounds.Left, RestoreBounds.Top, RestoreBounds.Width, RestoreBounds.Height);

    /// <summary>The last place if it is still on a screen, else just above the tray (bottom right of the work area).</summary>
    private void Place(WindowBounds? b)
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (b is not null && double.IsFinite(b.Left) && double.IsFinite(b.Top) && b.Width > 0 && b.Height > 0)
        {
            var w = Math.Clamp(b.Width, MinWidth, MaxWidth);
            var h = Math.Clamp(b.Height, MinHeight, MaxHeight);
            var r = new Rect(b.Left, b.Top, w, h);
            r.Intersect(screen);
            if (!r.IsEmpty && r.Width >= 80 && r.Height >= 40)
            {
                Width = w;
                Height = h;
                Left = b.Left;
                Top = b.Top;
                return;
            }
        }

        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - Gap;
        Top = work.Bottom - Height - Gap;
    }

    private void UpdateOnScreen()
    {
        var shown = IsVisible && WindowState != WindowState.Minimized;
        if (shown == OnScreen)
        {
            return;
        }

        OnScreen = shown;
        OnScreenChanged?.Invoke(this, shown);
    }

    /// <summary>Fade and a small zoom in (skipped with reduced motion).</summary>
    private void PlayEntrance()
    {
        if (Motion.Reduced || Root.RenderTransform is not ScaleTransform scale)
        {
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        var grow = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void OnDashboardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DashboardViewModel.ButtonState) or nameof(DashboardViewModel.State))
        {
            UpdateStatusDot();
        }
    }

    private void UpdateStatusDot() => StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, _vm.Dashboard.ButtonState switch
    {
        ConnectionState.Connected => "B.Success",
        ConnectionState.Faulted => "B.Danger",
        ConnectionState.Disconnected => "B.TextMuted",
        _ => "B.Warning",
    });

    private void OnPinChanged(object sender, RoutedEventArgs e)
    {
        var on = PinButton.IsChecked == true;
        if (Topmost != on)
        {
            Topmost = on;
            PinnedChanged?.Invoke(this, on);
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ---- Server picker -------------------------------------------------------------------------------------

    private void OnOpenPicker(object sender, RoutedEventArgs e)
    {
        // Its own view over the shared list (best first), made only while the picker is open.
        _servers ??= new ListCollectionView(_vm.Servers.Items) { Filter = Matches };
        _servers.SortDescriptions.Clear();
        _servers.SortDescriptions.Add(new SortDescription(nameof(ServerItem.Score), ListSortDirection.Descending));
        _servers.SortDescriptions.Add(new SortDescription(nameof(ServerItem.Name), ListSortDirection.Ascending));
        List.ItemsSource = _servers;
        Search.Text = string.Empty;
        UpdatePickerState();
        Picker.Visibility = Visibility.Visible;
        if (!Motion.Reduced && Picker.RenderTransform is TranslateTransform t)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            Picker.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }

        Search.Focus();
    }

    private void OnClosePicker(object sender, RoutedEventArgs e) => ClosePicker();

    private void ClosePicker()
    {
        Picker.Visibility = Visibility.Collapsed;
        List.ItemsSource = null;
        _servers?.DetachFromSourceCollection();
        _servers = null;
    }

    private bool Matches(object o)
    {
        var q = Search.Text.Trim();
        return q.Length == 0 || o is ServerItem s
            && (s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.SubscriptionName.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _servers?.Refresh();
        UpdatePickerState();
    }

    private void UpdatePickerState()
    {
        SmartCheck.Visibility = _vm.Dashboard.SmartMode ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = _servers is { IsEmpty: true } ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = Search.Text.Trim().Length == 0 ? "No servers yet. Add a subscription in Pro view." : "No servers match.";
    }

    private void OnPickSmart(object sender, RoutedEventArgs e)
    {
        ClosePicker();
        _vm.Dashboard.UseSmartCommand.Execute(null);
    }

    private void OnPickServer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ServerItem item)
        {
            ClosePicker();
            _vm.Servers.ConnectCommand.Execute(item);
        }
    }

    // ---- Closing -------------------------------------------------------------------------------------------

    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        BoundsCommitted?.Invoke(this, Bounds);
        if (!AllowClose && CloseToTray())
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Nothing of this window stays reachable from the long-lived view models.
        _vm.Dashboard.PropertyChanged -= OnDashboardChanged;
        ClosePicker();
        DataContext = null;
        base.OnClosed(e);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
