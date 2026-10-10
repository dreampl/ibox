using JackiVpn.Mobile.Controls;
using JackiVpn.Mobile.ViewModels;

namespace JackiVpn.Mobile.Views;

public partial class SubscriptionsView : ContentView
{
    private readonly SubscriptionsViewModel _vm;

    public SubscriptionsView(SubscriptionsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    public async Task AppearAsync()
    {
        try
        {
            await _vm.LoadAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _vm.Message = Security.SecretRedactor.Redact(ex.Message);
        }
    }

    private async void OnSyncTapped(object? sender, TappedEventArgs e)
    {
        _ = Motion.StretchAsync(SyncButton);
        try
        {
            await Shell.Current.GoToAsync("ecosystem");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _vm.Message = ex.Message;
        }
    }

    private async void OnAddTapped(object? sender, TappedEventArgs e)
    {
        _ = Motion.StretchAsync(AddButton);
        await _vm.AddCommand.ExecuteAsync(null);
    }
}
