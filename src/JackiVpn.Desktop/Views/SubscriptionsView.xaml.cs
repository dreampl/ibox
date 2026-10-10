using System.Windows;
using System.Windows.Controls;
using JackiVpn.Desktop.ViewModels;

namespace JackiVpn.Desktop.Views;

public partial class SubscriptionsView : UserControl
{
    public SubscriptionsView() => InitializeComponent();

    private void OnSyncDeviceClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current.MainWindow?.DataContext is MainViewModel main)
        {
            EcosystemView.ShowDialog(main.Ecosystem);
        }
    }
}
