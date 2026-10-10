using Android.App;
using Android.App.Usage;
using Android.Content;
using Android.Net;
using JackiVpn.Application.Usage;
using JackiVpn.Application.Workplace;
using JackiVpn.Domain.Usage;

namespace JackiVpn.Mobile.Platforms.Android;

/// <summary>
/// Device totals from TrafficStats: mobile data exactly; the rest (Wi-Fi, Ethernet) as total minus mobile minus
/// what went through the VPN's TUN (Android counts tunnelled traffic twice: on tun0 and on the real network).
/// </summary>
public sealed class AndroidNicCounters(Func<(long Down, long Up)> tunnelled) : INicCounterSource
{
    public IReadOnlyList<NicCounter> Read()
    {
        var totalRx = TrafficStats.TotalRxBytes;
        var totalTx = TrafficStats.TotalTxBytes;
        var mobileRx = TrafficStats.MobileRxBytes;
        var mobileTx = TrafficStats.MobileTxBytes;
        if (totalRx < 0 || totalTx < 0)
        {
            return [];
        }

        mobileRx = Math.Max(0, mobileRx);
        mobileTx = Math.Max(0, mobileTx);
        var (tunDown, tunUp) = tunnelled();
        return
        [
            new NicCounter("mobile", mobileRx, mobileTx),
            new NicCounter("other", Math.Max(0, totalRx - mobileRx - tunDown), Math.Max(0, totalTx - mobileTx - tunUp)),
        ];
    }
}

/// <summary>Network facts on Android: DNS search domains, resolvers, gateway and subnets (no Wi-Fi name: it needs location).</summary>
public sealed class AndroidNetworkFacts(Context context) : INetworkFactsProvider
{
    public NetworkFacts Read()
    {
        var cm = (ConnectivityManager?)context.GetSystemService(Context.ConnectivityService);
        if (cm is null)
        {
            return NetworkFacts.None;
        }

#pragma warning disable CA1422 // Deprecated on 31+ but still the only way to see the network under the VPN.
        foreach (var network in cm.GetAllNetworks())
#pragma warning restore CA1422
        {
            var caps = cm.GetNetworkCapabilities(network);
            if (caps is null || caps.HasTransport(TransportType.Vpn) || !caps.HasCapability(NetCapability.Internet))
            {
                continue;
            }

            var lp = cm.GetLinkProperties(network);
            if (lp is null)
            {
                continue;
            }

            var gateway = lp.Routes?.FirstOrDefault(r => r.IsDefaultRoute && r.Gateway is not null)?.Gateway?.HostAddress;
            var domains = string.IsNullOrWhiteSpace(lp.Domains) ? null : lp.Domains.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
            var wifi = caps.HasTransport(TransportType.Wifi) || caps.HasTransport(TransportType.Ethernet);
            if (!wifi)
            {
                // Mobile data is never a workplace network.
                return new NetworkFacts { Key = string.Empty };
            }

            return new NetworkFacts
            {
                Key = WorkplaceService.KeyOf(null, gateway, domains, null),
                DnsSuffix = domains,
                GatewayIp = gateway,
                DnsServers = lp.DnsServers?.Select(d => d.HostAddress).OfType<string>().ToList() ?? [],
                LocalCidrs = lp.LinkAddresses?.Where(a => a.Address is Java.Net.Inet4Address).Select(a => Cidr(a.Address!.HostAddress!, a.PrefixLength)).ToList() ?? [],
                IsWifi = true,
            };
        }

        return NetworkFacts.None;
    }

    private static string Cidr(string ip, int prefix)
    {
        var b = System.Net.IPAddress.Parse(ip).GetAddressBytes();
        var v = (((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3]) & (prefix == 0 ? 0u : uint.MaxValue << (32 - prefix));
        return $"{v >> 24}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}/{prefix}";
    }
}

/// <summary>One app's data use from Android's own statistics.</summary>
public sealed record AppUsage(string Package, string Label, long Down, long Up)
{
    public long Total => Down + Up;
}

/// <summary>
/// Per-app data use from NetworkStatsManager (all networks, VPN on or off) once the user granted "Usage access".
/// Queried only when the Usage page asks — nothing runs in the background.
/// </summary>
public static class AndroidAppUsage
{
    public static bool HasAccess(Context context)
    {
        var ops = (AppOpsManager?)context.GetSystemService(Context.AppOpsService);
        if (ops is null)
        {
            return false;
        }

#pragma warning disable CA1422, CS0618 // UnsafeCheckOpNoThrow needs API 29; CheckOpNoThrow is fine below that.
        var mode = OperatingSystem.IsAndroidVersionAtLeast(29)
            ? ops.UnsafeCheckOpNoThrow(AppOpsManager.OpstrGetUsageStats!, global::Android.OS.Process.MyUid(), context.PackageName!)
            : ops.CheckOpNoThrow(AppOpsManager.OpstrGetUsageStats!, global::Android.OS.Process.MyUid(), context.PackageName!);
#pragma warning restore CA1422, CS0618
        return mode == AppOpsManagerMode.Allowed;
    }

    /// <summary>Android's own per-app data usage screen (falls back to the network settings where a maker hides it).</summary>
    public static void OpenDataUsageSettings(Context context)
    {
        foreach (var action in new[] { "android.settings.DATA_USAGE_SETTINGS", global::Android.Provider.Settings.ActionWirelessSettings! })
        {
            try
            {
                context.StartActivity(new Intent(action).AddFlags(ActivityFlags.NewTask));
                return;
            }
            catch (ActivityNotFoundException)
            {
            }
        }
    }

    public static IReadOnlyList<AppUsage> Query(Context context, DateTimeOffset from, DateTimeOffset to)
    {
        if (!HasAccess(context) || context.GetSystemService(Context.NetworkStatsService) is not NetworkStatsManager stats)
        {
            return [];
        }

        var byUid = new Dictionary<int, (long Rx, long Tx)>();
        foreach (var type in new[] { ConnectivityType.Mobile, ConnectivityType.Wifi, ConnectivityType.Ethernet })
        {
            try
            {
                using var summary = stats.QuerySummary(type, null, from.ToUnixTimeMilliseconds(), to.ToUnixTimeMilliseconds());
                using var bucket = new NetworkStats.Bucket();
                while (summary?.HasNextBucket == true && summary.GetNextBucket(bucket))
                {
                    var (rx, tx) = byUid.TryGetValue(bucket.Uid, out var v) ? v : (0, 0);
                    byUid[bucket.Uid] = (rx + bucket.RxBytes, tx + bucket.TxBytes);
                }
            }
            catch (Exception ex) when (ex is Java.Lang.SecurityException or Java.Lang.RuntimeException)
            {
            }
        }

        var pm = context.PackageManager!;
        var self = global::Android.OS.Process.MyUid();
        return byUid.Where(kv => kv.Value.Rx + kv.Value.Tx > 0).Select(kv =>
        {
            var package = pm.GetPackagesForUid(kv.Key)?.FirstOrDefault() ?? (kv.Key == 1000 ? "android" : "uid " + kv.Key);
            string label;
            try
            {
                label = kv.Key == self ? "IBOX (VPN carrier)" : pm.GetApplicationLabel(pm.GetApplicationInfo(package, 0)) ?? package;
            }
            catch (global::Android.Content.PM.PackageManager.NameNotFoundException)
            {
                label = kv.Key switch { 1000 => "Android system", 1013 or 1019 => "Media", 0 => "Kernel", _ => package };
            }

            return new AppUsage(package, label, kv.Value.Rx, kv.Value.Tx);
        }).OrderByDescending(a => a.Total).ToList();
    }
}
