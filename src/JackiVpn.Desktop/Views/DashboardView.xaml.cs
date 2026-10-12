using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using JackiVpn.Desktop.ViewModels;
using JackiVpn.Domain.Connection;

namespace JackiVpn.Desktop.Views;

public partial class DashboardView : UserControl
{
    private DashboardViewModel? _vm;
    private int _layout = -1;

    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as DashboardViewModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) =>
        {
            Attach(DataContext as DashboardViewModel);
            UpdateGlow();
            UpdateLatency();
            UpdateSpeedButton();
            UpdateServerState();
            Arrange(ActualWidth);
        };
        SizeChanged += (_, e) => Arrange(e.NewSize.Width);
    }

    private void Attach(DashboardViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
        }

        _vm = vm;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DashboardViewModel.State):
                UpdateGlow();
                UpdateServerState();
                break;
            case nameof(DashboardViewModel.LatencyLevel):
                UpdateLatency();
                break;
            case nameof(DashboardViewModel.IsSpeedTesting):
                UpdateSpeedButton();
                break;
            case nameof(DashboardViewModel.PlanOk):
                PlanDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, _vm?.PlanOk == false ? "B.Danger" : "B.Success");
                break;
        }
    }

    /// <summary>
    /// Responsive: three cards side by side on wide windows; traffic on its own row with speed test and server below
    /// on medium ones; everything stacked on narrow ones. The map shrinks and finally steps aside.
    /// </summary>
    private void Arrange(double width)
    {
        var layout = width >= 1200 ? 0 : width >= 900 ? 1 : 2;
        if (layout == _layout)
        {
            return;
        }

        _layout = layout;
        void Place(FrameworkElement e, int row, int column, int span)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, column);
            Grid.SetColumnSpan(e, span);
        }

        switch (layout)
        {
            case 0:
                Place(TrafficCard, 0, 0, 1);
                Place(SpeedCard, 0, 2, 1);
                Place(ServerCard, 0, 4, 1);
                break;
            case 1:
                Place(TrafficCard, 0, 0, 5);
                Place(SpeedCard, 1, 0, 1);
                Place(ServerCard, 1, 2, 3);
                break;
            default:
                Place(TrafficCard, 0, 0, 5);
                Place(SpeedCard, 1, 0, 5);
                Place(ServerCard, 2, 0, 5);
                break;
        }

        Map.Width = layout == 0 ? 330 : 250;
        Map.Height = layout == 0 ? 138 : 105;
        Map.Visibility = layout == 2 ? Visibility.Collapsed : Visibility.Visible;
        StatusBlock.Margin = new Thickness(2, 10, layout == 0 ? 360 : layout == 1 ? 270 : 0, 0);
    }

    /// <summary>The hero card's ambient glow follows the connection state with a soft cross-fade.</summary>
    private void UpdateGlow()
    {
        if (_vm is null)
        {
            return;
        }

        var target = _vm.State switch
        {
            ConnectionState.Connected => GlowOn,
            ConnectionState.Faulted => GlowFail,
            ConnectionState.Disconnected => GlowIdle,
            _ => GlowBusy,
        };
        foreach (var layer in new UIElement[] { GlowIdle, GlowBusy, GlowOn, GlowFail })
        {
            Motion.FadeTo(layer, ReferenceEquals(layer, target) ? 1 : 0, Motion.Slow);
        }
    }

    private void UpdateServerState()
    {
        if (_vm is null)
        {
            return;
        }

        var (text, key) = _vm.State switch
        {
            ConnectionState.Connected => ("Connected", "B.Success"),
            ConnectionState.Faulted => ("Failed", "B.Danger"),
            ConnectionState.Disconnected => ("Not connected", "B.TextMuted"),
            ConnectionState.Disconnecting => ("Stopping…", "B.Warning"),
            _ => ("Connecting…", "B.Warning"),
        };
        ServerState.Text = text;
        ServerDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
    }

    private void UpdateLatency()
    {
        if (_vm is null)
        {
            return;
        }

        LatencyDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, _vm.LatencyLevel switch
        {
            0 => "B.Success",
            1 => "B.Warning",
            2 => "B.Danger",
            _ => "B.TextMuted",
        });
    }

    private void UpdateSpeedButton()
    {
        var testing = _vm?.IsSpeedTesting == true;
        SpeedButtonText.Text = testing ? "Stop" : "Run Test";
        SpeedButtonGlyph.Text = testing ? ((char)0xE71A).ToString() : ((char)0xE768).ToString();
    }
}
