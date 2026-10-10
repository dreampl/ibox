using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using JackiVpn.Desktop.ViewModels;

namespace JackiVpn.Desktop.Views;

/// <summary>"Sync device" dialog content: scan, pick a device, send. The radar runs while a scan is in progress.</summary>
public partial class EcosystemView : UserControl
{
    private EcosystemViewModel? _vm;

    public EcosystemView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnChanged;
            }

            _vm = e.NewValue as EcosystemViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnChanged;
            }
        };
        Unloaded += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnChanged;
                _vm.CancelScan();
            }
        };
    }

    /// <summary>Opens the dialog and starts a scan right away.</summary>
    public static void ShowDialog(EcosystemViewModel vm)
    {
        var window = DialogService.CreateWindow("Sync device", 520);
        var view = new EcosystemView { DataContext = vm };
        window.Content = new ScrollViewer { Content = view, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        window.Loaded += async (_, _) =>
        {
            await vm.LoadAsync();
            await vm.ScanCommand.ExecuteAsync(null);
        };
        window.ShowDialog();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EcosystemViewModel.IsScanning) || Resources["Radar"] is not Storyboard radar)
        {
            return;
        }

        if (_vm?.IsScanning == true && SystemParameters.ClientAreaAnimation)
        {
            radar.Begin(this, true);
        }
        else
        {
            radar.Stop(this);
        }
    }
}
