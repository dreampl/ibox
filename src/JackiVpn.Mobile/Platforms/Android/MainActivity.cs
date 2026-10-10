using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// Long-pressing the quick-settings tile opens the app.
[IntentFilter(["android.service.quicksettings.action.QS_TILE_PREFERENCES"])]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (!OperatingSystem.IsAndroidVersionAtLeast(35))
        {
            // Android 15+ is edge-to-edge (bar colors are ignored there); older versions get the app's dark bars.
            Window?.SetStatusBarColor(Android.Graphics.Color.ParseColor("#08090D"));
            Window?.SetNavigationBarColor(Android.Graphics.Color.ParseColor("#0B0C10"));
        }

        OpenRoute(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        OpenRoute(intent);
    }

    /// <summary>The widget's server button opens the Servers tab.</summary>
    private static void OpenRoute(Intent? intent)
    {
        if (intent?.GetStringExtra(Platforms.Android.QuickToggle.RouteExtra) is not "servers")
        {
            return;
        }

        intent.RemoveExtra(Platforms.Android.QuickToggle.RouteExtra);
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 40 && Shell.Current is null; i++)
            {
                await Task.Delay(100);
            }

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try
                {
                    if (Shell.Current is { } shell)
                    {
                        await shell.GoToAsync("//servers");
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Services.MobileServices.Instance?.Logger.LogDebug("Open route failed: {Error}", ex.Message);
                }
            });
        });
    }

    protected override void OnResume()
    {
        base.OnResume();
        Services.MobileServices.Instance?.SetForeground(true);
        Sync()?.SetForeground(true);
    }

    protected override void OnPause()
    {
        Services.MobileServices.Instance?.SetForeground(false);
        Sync()?.SetForeground(false);
        base.OnPause();
    }

    /// <summary>"Sync device" answers scans from the user's other devices only while the app is on screen.</summary>
    private static ViewModels.EcosystemViewModel? Sync()
    {
        try
        {
            return IPlatformApplication.Current?.Services.GetService(typeof(ViewModels.EcosystemViewModel)) as ViewModels.EcosystemViewModel;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        Platforms.Android.VpnPermission.OnResult(requestCode, resultCode);
    }
}
