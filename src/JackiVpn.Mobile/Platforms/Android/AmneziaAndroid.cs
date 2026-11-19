using Android.Content;
using Android.Net;
using JackiVpn.Application.Amnezia;

namespace JackiVpn.Mobile.Platforms.Android;

/// <summary>
/// AmneziaVPN (org.amnezia.vpn) exports one entry point to other apps: its ImportConfigActivity, which takes
/// ACTION_VIEW on a <c>vpn://</c> URI (or ACTION_SEND text). It exports no connect/disconnect intent, so IBOX imports
/// and opens; the user taps Connect there. Switching back needs no help: when IBOX starts its VPN, Android revokes
/// AmneziaVPN's (one VPN at a time).
/// </summary>
public static class AmneziaAndroid
{
    public const string Package = "org.amnezia.vpn";
    public const string DownloadUrl = "https://amnezia.org/downloads";

    public static bool IsInstalled(Context context) => context.PackageManager?.GetLaunchIntentForPackage(Package) is not null;

    /// <summary>Hands the key to AmneziaVPN's import screen. False when no Amnezia activity takes it.</summary>
    public static bool Import(Context context, AmneziaShareKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var view = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(key.Key)).SetPackage(Package).AddFlags(ActivityFlags.NewTask);
        if (TryStart(context, view))
        {
            return true;
        }

        var send = new Intent(Intent.ActionSend).SetType("text/plain").PutExtra(Intent.ExtraText, key.Key).SetPackage(Package).AddFlags(ActivityFlags.NewTask);
        return TryStart(context, send);
    }

    public static bool Show(Context context) =>
        context.PackageManager?.GetLaunchIntentForPackage(Package) is { } launch && TryStart(context, launch.AddFlags(ActivityFlags.NewTask));

    /// <summary>A VPN network is up and AmneziaVPN is installed (Android does not tell other apps who owns a VPN).</summary>
    public static bool TunnelUp(Context context)
    {
        try
        {
            if (context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager cm || !IsInstalled(context))
            {
                return false;
            }

#pragma warning disable CA1422 // GetAllNetworks: the only way to see a VPN network that is not ours
            return cm.GetAllNetworks().Any(n => cm.GetNetworkCapabilities(n)?.HasTransport(TransportType.Vpn) == true);
#pragma warning restore CA1422
        }
        catch (Java.Lang.Exception)
        {
            return false;
        }
    }

    private static bool TryStart(Context context, Intent intent)
    {
        try
        {
            if (context.PackageManager is not { } pm || intent.ResolveActivity(pm) is null)
            {
                return false;
            }

            context.StartActivity(intent);
            return true;
        }
        catch (Java.Lang.Exception)
        {
            return false;
        }
    }
}
