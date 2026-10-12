using Android.Content;
using Android.Net;
using Android.OS;
using JackiVpn.Application.Background;

namespace JackiVpn.Mobile.Platforms.Android;

/// <summary>Android power and data-cost signals: battery saver, and whether the active network is metered.</summary>
public sealed class AndroidDeviceConditions(Context context) : IDeviceConditions
{
    public bool PowerSaver => context.GetSystemService(Context.PowerService) is PowerManager pm && pm.IsPowerSaveMode;

    public bool Metered => context.GetSystemService(Context.ConnectivityService) is ConnectivityManager cm && cm.IsActiveNetworkMetered;
}
