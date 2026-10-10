using JackiVpn.Mobile.Controls;
using JackiVpn.Mobile.ViewModels;

namespace JackiVpn.Mobile.Pages;

public partial class AppsPage : AnimatedPage
{
    private readonly AppsViewModel _vm;

    public AppsPage(AppsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await _vm.LoadAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _vm.Header = "Could not list apps: " + ex.Message;
        }
    }
}
