using System;
using System.ComponentModel;
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

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        vm.PropertyChanged += OnVmChanged;

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
        else if (e.PropertyName == nameof(MainViewModel.Selected) && !Motion.Reduced)
        {
            // Page transition: fade + rise (280 ms); pages that opt in also stagger their cards.
            var fade = new DoubleAnimation(0, 1, Motion.Normal) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var slide = new DoubleAnimation(12, 0, Motion.Slow) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var t = new TranslateTransform();
            PageHost.RenderTransform = t;
            PageHost.BeginAnimation(OpacityProperty, fade);
            t.BeginAnimation(TranslateTransform.YProperty, slide);
        }
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
