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
        Nav.SelectionChanged += (_, _) => MoveNavHighlight(animate: true);
        Nav.SizeChanged += (_, _) => MoveNavHighlight(animate: false);
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => MoveNavHighlight(animate: false), DispatcherPriority.Loaded);
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

    /// <summary>The highlight glides to the selected item with a slight overshoot.</summary>
    private void MoveNavHighlight(bool animate)
    {
        if (Nav.SelectedItem is null || Nav.ItemContainerGenerator.ContainerFromItem(Nav.SelectedItem) is not ListBoxItem item || !item.IsLoaded)
        {
            return;
        }

        var y = item.TranslatePoint(new Point(0, 0), Nav).Y;
        NavHighlight.Width = Math.Max(0, Nav.ActualWidth);
        NavHighlight.Height = item.ActualHeight;
        if (NavHighlight.Opacity == 0 || !animate || Motion.Reduced)
        {
            NavHighlightY.BeginAnimation(TranslateTransform.YProperty, null);
            NavHighlightY.Y = y;
            NavHighlight.Opacity = 1;
            return;
        }

        NavHighlightY.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut },
        });
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
