using JackiVpn.Mobile.Controls;
using JackiVpn.Mobile.Pages;
using JackiVpn.Mobile.Services;
using JackiVpn.Mobile.ViewModels;

namespace JackiVpn.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            })
            .ConfigureMauiHandlers(handlers => handlers.AddHandler<BlurBackdrop, BlurBackdropHandler>());

        // Inputs sit inside rounded fields: no Material underline.
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoUnderline", (h, _) =>
            h.PlatformView.BackgroundTintList = global::Android.Content.Res.ColorStateList.ValueOf(global::Android.Graphics.Color.Transparent));
        Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping("SoftUnderline", (h, _) =>
            h.PlatformView.BackgroundTintList = global::Android.Content.Res.ColorStateList.ValueOf(global::Android.Graphics.Color.Argb(0x40, 0xFF, 0xFF, 0xFF)));

        builder.Services.AddSingleton(_ => new MobileServices(global::Android.App.Application.Context));
        builder.Services.AddSingleton<HomeViewModel>();
        builder.Services.AddSingleton<ServersViewModel>();
        builder.Services.AddSingleton<SubscriptionsViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        // Views are per window: Android recreates the activity (e.g. after Back), and a Shell or page still parented
        // to the old window cannot be reused. State lives in the singleton view models.
        builder.Services.AddTransient<MainPage>();
        builder.Services.AddTransient<AppShell>();
        builder.Services.AddTransient<AppsViewModel>();
        builder.Services.AddTransient<AppsPage>();
        builder.Services.AddSingleton<EcosystemViewModel>();
        builder.Services.AddTransient<EcosystemPage>();
        builder.Services.AddTransient<UsagePage>();
        builder.Services.AddTransient<IslandPage>();
        return builder.Build();
    }
}
