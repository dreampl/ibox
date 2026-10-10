using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using JackiVpn.Desktop.ViewModels;
using JackiVpn.Domain.Connection;

namespace JackiVpn.Desktop.Views;

public partial class DashboardView : UserControl
{
    private DashboardViewModel? _vm;

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
        };
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
                break;
            case nameof(DashboardViewModel.LatencyLevel):
                UpdateLatency();
                break;
            case nameof(DashboardViewModel.IsSpeedTesting):
                UpdateSpeedButton();
                break;
        }
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

    private void UpdateSpeedButton() => SpeedButtonText.Text = _vm?.IsSpeedTesting == true ? "Stop" : "Run test";
}
