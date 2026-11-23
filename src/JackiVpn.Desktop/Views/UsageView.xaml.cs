using System.Windows.Controls;
using JackiVpn.Desktop.ViewModels;

namespace JackiVpn.Desktop.Views;

public partial class UsageView : UserControl
{
    public UsageView() : this(false) { }

    public UsageView(bool workplaceOnly)
    {
        InitializeComponent();
        var sections = (StackPanel)WorkplaceSection.Parent;
        sections.Children.Remove(WorkplaceSection);
        if (workplaceOnly)
        {
            Content = new ScrollViewer { Content = WorkplaceSection, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new System.Windows.Thickness(20, 0, 30, 24) };
        }
        else
        {
            SectionTabs.Apply(this);
        }
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
