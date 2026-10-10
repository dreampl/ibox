using JackiVpn.Mobile.Localization;
using JackiVpn.Mobile.Services;

namespace JackiVpn.Mobile;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services, MobileServices app)
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Dark;
        _services = services;
        L.Instance.Apply("auto");
        _ = StartAsync(app);
    }

    private static async Task StartAsync(MobileServices app)
    {
        await app.InitializeAsync();
        MainThread.BeginInvokeOnMainThread(() => L.Instance.Apply(app.Settings.Current.Ui.Language));
    }

    /// <summary>A new Shell for every window: Android creates a new activity (and window) after Back + reopen.</summary>
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(_services.GetRequiredService<AppShell>()) { Title = "IBOX" };
}