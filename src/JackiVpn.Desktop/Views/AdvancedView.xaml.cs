using System.Windows.Controls;
using JackiVpn.Desktop.ViewModels;

namespace JackiVpn.Desktop.Views;

public partial class AdvancedView : UserControl
{
    public AdvancedView()
    {
        InitializeComponent();
        SectionTabs.Apply(this);
        Loaded += async (_, _) =>
        {
            if (DataContext is AdvancedViewModel vm && vm.Cores.Count == 0)
            {
                await vm.LoadCoresAsync();
            }
        };
    }
}
