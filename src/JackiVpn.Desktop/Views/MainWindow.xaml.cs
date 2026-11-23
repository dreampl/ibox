using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using JackiVpn.Desktop.Services;
using JackiVpn.Desktop.ViewModels;
using JackiVpn.Domain.Connection;

namespace JackiVpn.Desktop.Views;

public partial class MainWindow : Window
{
    public static MainWindow? Current { get; private set; }
    private Action? _drawerClosed;
    private readonly System.Collections.Generic.Dictionary<object, ContentControl> _retainedDrawers = new();
    private FrameworkElement? _profile;
    private readonly MainViewModel _vm;
    private readonly System.Collections.Generic.Dictionary<object, ContentPresenter> _pages = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Current = this;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && DrawerLayer.IsVisible)
            {
                CloseDrawer();
                e.Handled = true;
            }
        };
        Closed += (_, _) => { CloseDrawer(); if (Current == this) { Current = null; } };
        DataContext = vm;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        vm.PropertyChanged += OnVmChanged;
        ShowPage(vm.Selected?.Page);
        IsVisibleChanged += OnVisibilityChanged;

        StateChanged += (_, _) =>
        {
            UpdateOnScreen();
            ScheduleRelease();
        };
        IsVisibleChanged += (_, _) => UpdateOnScreen();
    }

    public void OpenDrawer(string title, object content, Action? closed = null, bool retain = false, bool edgeToEdge = false, double width = 560)
    {
        CloseDrawer();
        DrawerTitle.Text = title;
        DrawerFrame.Margin = edgeToEdge ? new Thickness(0) : new Thickness(20);
        DrawerPanel.Width = width;
        DrawerHeading.Margin = edgeToEdge ? new Thickness(20,16,20,12) : new Thickness(0,0,0,16);
        if (retain)
        {
            if (!_retainedDrawers.TryGetValue(content, out var host))
            {
                host = new ContentControl { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
                _retainedDrawers.Add(content, host);
                DrawerHosts.Children.Add(host);
            }
            host.Visibility = Visibility.Visible;
        }
        else { DrawerContent.Content = content; }
        _drawerClosed = closed;
        DrawerLayer.Visibility = Visibility.Visible;
        DrawerLayer.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
    }

    public void CloseDrawer()
    {
        DrawerLayer.Visibility = Visibility.Collapsed;
        DrawerContent.Content = null;
        foreach (var host in _retainedDrawers.Values) { host.Visibility = Visibility.Collapsed; }
        var closed = _drawerClosed;
        _drawerClosed = null;
        closed?.Invoke();
        PageHost.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
    }

    public void OpenDialog(string title, object content, Action closed)
    {
        var previousTitle = DrawerTitle.Text;
        var previousWidth = DrawerPanel.Width;
        var previousContent = DrawerContent.Content;
        var previousRetained = System.Linq.Enumerable.FirstOrDefault(_retainedDrawers, pair => pair.Value.Visibility == Visibility.Visible).Key;
        var previousCallback = _drawerClosed;
        var wasOpen = DrawerLayer.Visibility == Visibility.Visible;
        var edgeToEdge = DrawerFrame.Margin.Left == 0;
        _drawerClosed = null;
        OpenDrawer(title, content, () =>
        {
            if (wasOpen && (previousContent ?? previousRetained) is { } previous)
            {
                OpenDrawer(previousTitle, previous, previousCallback, previousRetained is not null, edgeToEdge, previousWidth);
            }
            closed();
        });
    }

    public void ReleaseDrawer(object content)
    {
        if (_retainedDrawers.Remove(content, out var host)) { host.Content = null; DrawerHosts.Children.Remove(host); }
    }

    private void OnCloseDrawer(object sender, RoutedEventArgs e) => CloseDrawer();

    public void ShowProfile(FrameworkElement content)
    {
        CloseDrawer();
        _vm.Selected = null;
        if (_profile is not null) { PageHost.Children.Remove(_profile); }
        foreach (var presenter in _pages.Values) { presenter.Visibility = Visibility.Collapsed; }
        _profile = content;
        PageHost.Children.Add(content);
    }

    /// <summary>The window is on screen (not minimized, not hidden in the tray).</summary>
    public bool OnScreen { get; private set; }

    /// <summary>Raised when <see cref="OnScreen"/> changes: decorations, live polling and optional work follow it.</summary>
    public event EventHandler<bool>? OnScreenChanged;

    private void UpdateOnScreen()
    {
        var shown = IsVisible && WindowState != WindowState.Minimized;
        if (shown == OnScreen)
        {
            return;
        }

        OnScreen = shown;
        Motion.SetMainWindowShown(shown);
        OnScreenChanged?.Invoke(this, shown);
    }

    /// <summary>Set by the app: when true, closing hides to the tray instead of exiting.</summary>
    public Func<bool> CloseToTray { get; set; } = () => false;

    public bool AllowClose { get; set; }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.State))
        {
            var key = _vm.State switch
            {
                ConnectionState.Connected => "B.Success",
                ConnectionState.Faulted => "B.Danger",
                ConnectionState.Disconnected => "B.TextMuted",
                _ => "B.Warning",
            };
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
            StatusHalo.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
            TaglineDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
            if (_vm.State == ConnectionState.Disconnected)
            {
                Motion.FadeTo(StatusHalo, 0);
            }
            else
            {
                Motion.Breathe(StatusHalo, 0.15, 0.9, TimeSpan.FromSeconds(_vm.State == ConnectionState.Connected ? 1.8 : 0.7));
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.Selected))
        {
            ShowPage(_vm.Selected?.Page);
            if (Motion.Reduced)
            {
                return;
            }

            // Page transition: fade + rise (280 ms); pages that opt in also stagger their cards.
            var fade = new DoubleAnimation(0, 1, Motion.Normal) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var slide = new DoubleAnimation(12, 0, Motion.Slow) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var t = new TranslateTransform();
            PageHost.RenderTransform = t;
            PageHost.BeginAnimation(OpacityProperty, fade);
            t.BeginAnimation(TranslateTransform.YProperty, slide);
        }
    }

    /// <summary>
    /// Shows a page, building its view only on the first visit; the others stay alive but collapsed (no layout or
    /// drawing, and their clocks stop), so going back to a page is instant.
    /// </summary>
    private void ShowPage(object? page)
    {
        if (_profile is not null)
        {
            PageHost.Children.Remove(_profile);
            _profile = null;
        }
        CloseDrawer();
        if (page is null)
        {
            return;
        }

        if (!_pages.TryGetValue(page, out var view))
        {
            view = new ContentPresenter { Content = page };
            _pages[page] = view;
            PageHost.Children.Add(view);
        }

        // Only the last few pages stay built: going back is instant, memory stays small.
        _recent.Remove(page);
        _recent.Add(page);
        while (_recent.Count > MaxCachedPages)
        {
            var old = _recent[0];
            _recent.RemoveAt(0);
            if (_pages.Remove(old, out var gone))
            {
                PageHost.Children.Remove(gone);
                gone.Content = null;
            }
        }

        foreach (var (key, presenter) in _pages)
        {
            presenter.Visibility = ReferenceEquals(key, page) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private const int MaxCachedPages = 3;
    private readonly System.Collections.Generic.List<object> _recent = [];

    private System.Windows.Threading.DispatcherTimer? _release;

    /// <summary>
    /// Closed to the tray: after a few seconds the cached pages (except the current one) and the shadow pictures
    /// are let go and the memory handed back to Windows — IBOX mostly lives in the tray. They are rebuilt on the
    /// next visit. A one-shot timer started by hiding, not a repeating one.
    /// </summary>
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => ScheduleRelease();

    /// <summary>
    /// Off screen (tray or minimized): after a few seconds the other pages are let go and the memory handed back to
    /// Windows; while it stays off screen the unused memory is handed back again every two minutes (timers touch a
    /// little of it), so IBOX in the tray stays at a few tens of megabytes.
    /// </summary>
    private void ScheduleRelease()
    {
        _release ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        _release.Tick -= ReleasePages;
        _release.Tick += ReleasePages;
        _release.Stop();
        if (OffScreen)
        {
            _releaseRounds = 0;
            _release.Interval = TimeSpan.FromSeconds(5);
            _release.Start();
        }
    }

    private bool OffScreen => !IsVisible || WindowState == WindowState.Minimized;

    private int _releaseRounds;

    private void ReleasePages(object? sender, EventArgs e)
    {
        if (!OffScreen)
        {
            _release?.Stop();
            return;
        }

        if (_releaseRounds++ == 0)
        {
            var current = _vm.Selected?.Page;
            foreach (var key in System.Linq.Enumerable.ToList(_pages.Keys))
            {
                if (!ReferenceEquals(key, current))
                {
                    PageHost.Children.Remove(_pages[key]);
                    _pages[key].Content = null;
                    _pages.Remove(key);
                    _recent.Remove(key);
                }
            }

            SoftShadow.ClearCache();
        }

        // A full compacting collection now and then; in between only the untouched pages go back to Windows.
        if (_releaseRounds % 5 == 1)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }

        using var self = System.Diagnostics.Process.GetCurrentProcess();
        _ = EmptyWorkingSet(self.Handle);
        _release!.Interval = TimeSpan.FromMinutes(2);
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(nint process);
    /// <summary>
    /// After start-up, builds the other pages one at a time while the app is idle (laid out hidden, then collapsed),
    /// so even the first visit of a page shows at once instead of building it then.
    /// </summary>
    public async Task WarmUpPagesAsync(System.Collections.Generic.IEnumerable<object> pages)
    {
        foreach (var page in pages)
        {
            // Only while nobody is using the mouse or keyboard: building a page takes the UI thread for a moment.
            while (IsVisible && UserIdle() < TimeSpan.FromSeconds(1.5))
            {
                await Task.Delay(500);
            }

            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (_pages.ContainsKey(page) || !IsVisible)
            {
                continue;
            }

            var view = new ContentPresenter { Content = page, Visibility = Visibility.Hidden };
            _pages[page] = view;
            PageHost.Children.Add(view);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); // laid out by now
            if (!ReferenceEquals(_vm.Selected?.Page, page))
            {
                view.Visibility = Visibility.Collapsed;
            }

            await Task.Delay(400);
        }
    }


    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>How long since the last mouse or keyboard input anywhere (zero when unknown).</summary>
    private static TimeSpan UserIdle()
    {
        var info = new LastInputInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!AllowClose && CloseToTray())
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>Closed for good (exit, or the switch to the Mini view): let go of the shared view models and pages.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _vm.PropertyChanged -= OnVmChanged;
        _release?.Stop();
        foreach (var presenter in _pages.Values)
        {
            presenter.Content = null;
        }

        PageHost.Children.Clear();
        _pages.Clear();
        _recent.Clear();
        DataContext = null;
        base.OnClosed(e);
    }
}
