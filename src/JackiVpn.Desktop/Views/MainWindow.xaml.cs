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
    private readonly MainViewModel _vm;
    private readonly System.Collections.Generic.Dictionary<object, ContentPresenter> _pages = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        vm.PropertyChanged += OnVmChanged;
        ShowPage(vm.Selected?.Page);

        StateChanged += (_, _) => UpdateOnScreen();
        IsVisibleChanged += (_, _) => UpdateOnScreen();
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

        foreach (var (key, presenter) in _pages)
        {
            presenter.Visibility = ReferenceEquals(key, page) ? Visibility.Visible : Visibility.Collapsed;
        }
    }
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
}
