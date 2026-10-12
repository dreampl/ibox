using System.Windows.Controls;
using JackiVpn.Desktop.ViewModels;

namespace JackiVpn.Desktop.Views;

public partial class DnsView : UserControl
{
    public DnsView()
    {
        InitializeComponent();

        // Adapters change outside the app (another Wi-Fi, a cable): read them each time the page opens.
        Loaded += async (_, _) =>
        {
            if (DataContext is DnsViewModel vm)
            {
                await vm.RefreshAdaptersAsync();
            }
        };
    }
}
