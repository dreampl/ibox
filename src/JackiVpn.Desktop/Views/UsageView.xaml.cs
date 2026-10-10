using System.Windows.Controls;
using JackiVpn.Desktop.ViewModels;

namespace JackiVpn.Desktop.Views;

public partial class UsageView : UserControl
{
    public UsageView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is UsageViewModel vm)
            {
                vm.SyncUpdateGroups();
                vm.SetActive(true);
            }
        };
        Unloaded += (_, _) => (DataContext as UsageViewModel)?.SetActive(false);
    }
}