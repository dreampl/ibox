using JackiVpn.Mobile.ViewModels;

namespace JackiVpn.Mobile.Views;

public partial class SettingsView : ContentView
{
    public SettingsView(SettingsViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
