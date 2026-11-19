using Android.Content;
using Android.Net;
using Android.Net.Wifi;
using JackiVpn.Application.Automations;
using NetworkInfo = JackiVpn.Application.Automations.NetworkInfo;

namespace JackiVpn.Mobile.Platforms.Android;

/// <summary>
/// The network the phone is on, for the trusted Wi-Fi automation. Android only reveals the Wi-Fi name to apps
/// with location access; without it the name is unknown and the automation does nothing on Wi-Fi.
/// </summary>
internal static class AndroidNetworkInfo
{
    public static NetworkInfo Current()
    {
        try
        {
            var context = global::Android.App.Application.Context;
            if (context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager cm
                || cm.GetNetworkCapabilities(cm.ActiveNetwork) is not { } caps)
            {
                return new NetworkInfo(NetworkKind.None, null);
            }

            if (caps.HasTransport(TransportType.Cellular))
            {
                return new NetworkInfo(NetworkKind.Cellular, null);
            }

            if (!caps.HasTransport(TransportType.Wifi))
            {
                return new NetworkInfo(caps.HasTransport(TransportType.Ethernet) ? NetworkKind.Ethernet : NetworkKind.Unknown, null);
            }

#pragma warning disable CA1422 // ConnectionInfo is the one API that still returns the SSID to a foreground app.
            var ssid = (context.GetSystemService(Context.WifiService) as WifiManager)?.ConnectionInfo?.SSID?.Trim('"');
#pragma warning restore CA1422
            return new NetworkInfo(NetworkKind.Wifi, string.IsNullOrEmpty(ssid) || ssid == "<unknown ssid>" ? null : ssid);
        }
        catch (Java.Lang.SecurityException)
        {
            return NetworkInfo.Unknown;
        }
    }
}
