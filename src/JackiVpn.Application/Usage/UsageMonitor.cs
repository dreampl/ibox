using System.Globalization;
using System.Net;
using JackiVpn.Application.Notifications;
using JackiVpn.Cores.Generation;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Usage;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Usage;

/// <summary>Byte counters of one network adapter.</summary>
public readonly record struct NicCounter(string Name, long Received, long Sent);

/// <summary>Where device-wide totals come from (network adapters on Windows/Linux, TrafficStats on Android).</summary>
public interface INicCounterSource
{
    IReadOnlyList<NicCounter> Read();
}

/// <summary>.NET network interfaces: physical adapters only (loopback, VPN/TUN and virtual switches are skipped).</summary>
public sealed class DotNetNicCounters : INicCounterSource
{
    private static readonly string[] Virtual =
    [
        "loopback", "vethernet", "vmware", "virtualbox", "pseudo", "isatap", "teredo", "docker", "veth", "virbr", "br-", "lxc", "lxd",
        "vnet", "cni", "podman", "vbox", "ifb", "dummy", "hyper-v",
    ];

    private static readonly string[] Tunnels =
    [
        "tun", "tap", "wintun", "wireguard", "karing", "sing", "clash", "mihomo", "meta", "nekoray", "nekobox", "hiddify", "v2ray", "xray",
        "openvpn", "warp", "zerotier", "tailscale", "vpn", "proton", "outline", "jacki", "ibox", "wg", "ppp", "utun", "nordlynx",
    ];

    public static bool IsPhysical(string name, string description) =>
        !Virtual.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase) || description.Contains(v, StringComparison.OrdinalIgnoreCase))
        && !Tunnels.Any(v => name.StartsWith(v, StringComparison.OrdinalIgnoreCase) || description.Contains(v, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<NicCounter> Read()
    {
        var list = new List<NicCounter>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel
                    || !IsPhysical(nic.Name, nic.Description))
                {
                    continue;
                }

                try
                {
                    var s = nic.GetIPStatistics();
                    list.Add(new NicCounter(nic.Id, s.BytesReceived, s.BytesSent));
                }
                catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or PlatformNotSupportedException or IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or PlatformNotSupportedException or IOException)
        {
        }

        return list;
    }
}

/// <summary>What is using the network right now (bytes/s).</summary>
public sealed record LiveApp(string App, long DownPerSecond, long UpPerSecond, string? TopSite);

/// <summary>
/// Measures data usage: device totals from the adapters every tick; per app and per site from the tunnel core's
/// connection list (TUN: the whole device). Raises alerts (cap, daily cap, app limits, spikes, new apps, heavy
/// uploads, automatic updates) once each, as events and notifications. Light by design: one small JSON file,
/// a few dictionaries, and polling that slows down when the app is in the background.
/// </summary>
public sealed class UsageMonitor : IAsyncDisposable
{
    private readonly UsageBook _book;
    private readonly INicCounterSource? _nics;
    private readonly Func<CancellationToken, Task<CoreConnections?>> _connections;
    private readonly Func<AppSettings> _settings;
    private readonly NotificationService? _notifications;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, (long Up, long Down)> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long[]> _minutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Down, long Up, string? Site)> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<UpdateGroup, long> _updateWindow = [];
    private long _minuteIndex;
    private DateTimeOffset _lastSave;
    private DateTimeOffset _lastPrune;
    private DateTimeOffset _updateWindowStart;
    private bool _nicPrimed;

    public UsageMonitor(UsageBook book, INicCounterSource? nics, Func<CancellationToken, Task<CoreConnections?>> connections, Func<AppSettings> settings, NotificationService? notifications, ILogger logger, TimeProvider? time = null)
    {
        _book = book;
        _nics = nics;
        _connections = connections;
        _settings = settings;
        _notifications = notifications;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public UsageBook Book => _book;

    /// <summary>Tick interval; the platform raises it while the app is in the background (battery).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Alerts and texts in Persian.</summary>
    public Func<bool> Persian { get; init; } = static () => false;

    /// <summary>Workplace daily cap in MB (from the active company policy; 0 = none).</summary>
    public Func<int> WorkplaceDailyCapMb { get; init; } = static () => 0;

    /// <summary>Internal names of the active workplace (counted as local network).</summary>
    public Func<IReadOnlyList<string>> InternalDomains { get; init; } = static () => [];

    /// <summary>Raised after each tick (UI refresh).</summary>
    public event EventHandler? Updated;

    public event EventHandler<UsageEvent>? EventRaised;

    /// <summary>The period cap is used up (Eco can switch on automatically).</summary>
    public bool CapReached { get; private set; }

    /// <summary>Bytes carried through the core since start (Android subtracts them: the TUN is counted twice there).</summary>
    public long CoreDownTotal { get; private set; }

    public long CoreUpTotal { get; private set; }

    public long DownPerSecond { get; private set; }
    public long UpPerSecond { get; private set; }

    public IReadOnlyList<LiveApp> Live { get; private set; } = [];

    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var interval = Interval;
            try
            {
                await Task.Delay(interval, _time, ct).ConfigureAwait(false);
                if (!_settings().Usage.Enabled)
                {
                    continue;
                }

                await TickAsync(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug("Usage tick failed: {Error}", ex.Message);
            }
        }

        _book.Save(force: true);
    }

    /// <summary>One measurement step (public for tests).</summary>
    public async Task TickAsync(TimeSpan elapsed, CancellationToken ct)
    {
        var settings = _settings();
        var local = _time.GetLocalNow().DateTime;
        var seconds = Math.Max(0.5, elapsed.TotalSeconds);
        long devDown = 0, devUp = 0;
        if (_nics is not null)
        {
            (devDown, devUp) = ReadNics();
        }

        CoreConnections? snapshot = null;
        try
        {
            snapshot = await _connections(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
        }

        lock (_book.Gate)
        {
            if (devDown + devUp > 0)
            {
                _book.AddDevice(local, devDown, devUp);
            }

            if (snapshot is not null)
            {
                Attribute(snapshot, local, seconds, settings);
            }
            else
            {
                _seen.Clear();
                Live = [];
            }
        }

        DownPerSecond = (long)(devDown / seconds);
        UpPerSecond = (long)(devUp / seconds);
        if (DownPerSecond > _book.Data.Peak)
        {
            _book.Data.Peak = DownPerSecond;
        }

        CheckAlerts(settings, local);
        var now = _time.GetUtcNow();
        if (now - _lastSave > TimeSpan.FromSeconds(30))
        {
            _lastSave = now;
            _book.Save();
        }

        if (now - _lastPrune > TimeSpan.FromHours(6))
        {
            _lastPrune = now;
            lock (_book.Gate)
            {
                _book.Prune(settings.Usage.KeepDays);
            }
        }

        Updated?.Invoke(this, EventArgs.Empty);
    }

    private (long Down, long Up) ReadNics()
    {
        long down = 0, up = 0;
        var last = _book.Data.Last;
        foreach (var n in _nics!.Read())
        {
            if (last.TryGetValue(n.Name, out var prev) && prev.Length == 2)
            {
                // A counter that went backwards was reset (reboot, adapter re-created): count what it shows now.
                down += n.Received >= prev[0] ? n.Received - prev[0] : (_nicPrimed ? n.Received : 0);
                up += n.Sent >= prev[1] ? n.Sent - prev[1] : (_nicPrimed ? n.Sent : 0);
            }

            last[n.Name] = [n.Received, n.Sent];
        }

        _nicPrimed = true;
        // Sanity: a single read never adds more than 10 GB (bogus counters).
        return (Math.Min(down, 10L << 30), Math.Min(up, 10L << 30));
    }

    private void Attribute(CoreConnections snapshot, DateTime local, double seconds, AppSettings settings)
    {
        var internalNames = InternalDomains();
        var backRoutes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in snapshot.Connections.Where(c => c.Inbound == SingBoxConfigBuilder.BackInboundTag))
        {
            backRoutes[(c.Host ?? c.DestinationIp) + ":" + c.DestinationPort] = c.Outbound;
        }

        var alive = new HashSet<string>(StringComparer.Ordinal);
        _live.Clear();
        long tunnelDown = 0, tunnelUp = 0, lanDown = 0, lanUp = 0;
        var minute = _time.GetUtcNow().ToUnixTimeSeconds() / 60;
        foreach (var c in snapshot.Connections)
        {
            alive.Add(c.Id);
            if (c.Inbound == SingBoxConfigBuilder.BackInboundTag)
            {
                continue; // the relay's second leg of a connection already counted
            }

            var (pu, pd) = _seen.TryGetValue(c.Id, out var prev) ? prev : (0L, 0L);
            _seen[c.Id] = (c.Upload, c.Download);
            var up = Math.Max(0, c.Upload - pu);
            var down = Math.Max(0, c.Download - pd);
            if (up + down == 0)
            {
                continue;
            }

            var app = c.ProcessPath is { Length: > 0 } ? TrafficCatalog.AppName(c.ProcessPath) : "?";
            var host = c.Host ?? c.DestinationIp;
            var site = UsageBook.SiteOf(host);
            var outbound = c.Outbound.StartsWith("jv-shape-", StringComparison.Ordinal)
                && backRoutes.TryGetValue(host + ":" + c.DestinationPort, out var real) ? real : c.Outbound;
            var tunnel = outbound is SingBoxConfigBuilder.ProxyTag or "" || outbound.StartsWith("jv-shape-", StringComparison.Ordinal);
            var lan = IsLan(c, internalNames);
            if (lan)
            {
                tunnel = false;
                lanDown += down;
                lanUp += up;
            }
            else if (tunnel)
            {
                tunnelDown += down;
                tunnelUp += up;
            }

            _book.AddApp(local, app, c.ProcessPath, site, down, up, tunnel);
            CoreDownTotal += down;
            CoreUpTotal += up;
            var l = _live.TryGetValue(app, out var cur) ? cur : (0, 0, null);
            _live[app] = (l.Down + down, l.Up + up, l.Site is null || down > 0 ? site : l.Site);
            Minutes(app, minute)[minute % 60] += down + up;
            if (TrafficCatalog.UpdateGroupOf(c.Host) is { } group)
            {
                _updateWindow[group] = (_updateWindow.TryGetValue(group, out var g) ? g : 0) + down;
            }
        }

        _book.AddTunnel(local, tunnelDown, tunnelUp);
        _book.AddLan(local, lanDown, lanUp);
        foreach (var id in _seen.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _seen.Remove(id);
        }

        Live = _live.Select(kv => new LiveApp(kv.Key, (long)(kv.Value.Down / seconds), (long)(kv.Value.Up / seconds), kv.Value.Site))
            .OrderByDescending(x => x.DownPerSecond + x.UpPerSecond)
            .Take(12)
            .ToList();
        _minuteIndex = minute;
    }

    private static bool IsLan(CoreConnection c, IReadOnlyList<string> internalNames)
    {
        if (c.Host is { } h && internalNames.Count > 0 && NetText.IsHostInDomains(h, internalNames))
        {
            return true;
        }

        return IPAddress.TryParse(c.DestinationIp ?? c.Host, out var ip) && NetText.IsPrivateOrLocal(ip)
            && !SessionsFakeIp(ip);
    }

    private static bool SessionsFakeIp(IPAddress ip) => Sessions.SessionPlanFactory.IsFakeIp(ip);

    /// <summary>Per-minute bytes of an app for the last hour (ring buffer, cleared lazily).</summary>
    private long[] Minutes(string app, long minute)
    {
        if (!_minutes.TryGetValue(app, out var ring))
        {
            ring = new long[61];
            _minutes[app] = ring;
        }

        // ring[60] remembers which minute was written last; clear the slots skipped since then.
        var lastMinute = ring[60];
        if (lastMinute != minute)
        {
            var gap = Math.Min(60, minute - lastMinute);
            for (var i = 1; i <= gap; i++)
            {
                ring[(lastMinute + i) % 60] = 0;
            }

            ring[60] = minute;
        }

        return ring;
    }

    private long RecentBytes(string app, int minutes)
    {
        if (!_minutes.TryGetValue(app, out var ring) || _minuteIndex - ring[60] >= minutes)
        {
            return 0;
        }

        long sum = 0;
        for (var i = (int)(_minuteIndex - ring[60]); i < Math.Min(60, minutes); i++)
        {
            sum += ring[(int)((_minuteIndex - i + 600) % 60)];
        }

        return sum;
    }

    // ------------------------------------------------------------------ alerts

    private string T(string en, string fa) => Persian() ? fa : en;

    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return v.ToString(u == 0 ? "0" : v < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[u];
    }

    private void CheckAlerts(AppSettings settings, DateTime local)
    {
        var us = settings.Usage;
        var budget = _book.Budget(us);
        CapReached = budget.Cap is not null && budget.Used >= budget.Cap;
        if (!us.Alerts)
        {
            return;
        }

        var day = UsageBook.Day(local);
        var period = budget.PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (budget.Cap is { } cap)
        {
            foreach (var pct in us.AlertPercents.Where(p => p is > 0 and <= 200).OrderByDescending(p => p))
            {
                if (budget.Percent >= pct)
                {
                    Alert($"cap{pct}:{period}", "cap", pct >= 100 ? "crit" : "warn",
                        T($"{pct}% of your data used", $"{pct}٪ حجم مصرف شد"),
                        T($"{Size(budget.Used)} of {Size(cap)} used this period; {Size(budget.Remaining)} left.",
                          $"{Size(budget.Used)} از {Size(cap)} در این دوره مصرف شده؛ {Size(budget.Remaining)} مانده."));
                    break;
                }
            }
        }

        if (budget.DailyCap is { } dailyCap && budget.Today >= dailyCap)
        {
            Alert($"daily:{day}", "daily", "warn", T("Daily limit reached", "سقف روزانه پر شد"),
                T($"{Size(budget.Today)} used today (limit {Size(dailyCap)}).", $"امروز {Size(budget.Today)} مصرف شد (سقف {Size(dailyCap)})."));
        }

        if (WorkplaceDailyCapMb() is > 0 and var wcap && budget.Today >= wcap * 1024L * 1024)
        {
            Alert($"workcap:{day}", "daily", "warn", T("Company daily limit reached", "سقف روزانهٔ شرکت پر شد"),
                T($"{Size(budget.Today)} used today; the company allows {wcap} MB.", $"امروز {Size(budget.Today)} مصرف شد؛ سقف شرکت {wcap} مگابایت است."));
        }

        var todayApps = _book.Apps(1, us);
        foreach (var row in todayApps.Take(40))
        {
            if (us.AppDailyLimitsMb.TryGetValue(row.Key, out var mb) && mb > 0 && row.Total >= mb * 1024L * 1024)
            {
                Alert($"applimit:{row.Key}:{day}", "app-limit", "warn", T($"{row.Key} reached its daily limit", $"{row.Key} به سقف روزانه‌اش رسید"),
                    T($"{Size(row.Total)} today (limit {mb} MB).", $"امروز {Size(row.Total)} (سقف {mb} مگابایت)."), row.Key);
            }

            var recent = RecentBytes(row.Key, Math.Clamp(us.SpikeMinutes, 1, 60));
            if (us.SpikeMb > 0 && recent >= us.SpikeMb * 1024L * 1024)
            {
                Alert($"spike:{row.Key}:{day}:{local.Hour}", "spike", "warn", T($"{row.Key} is using a lot of data", $"{row.Key} حجم زیادی مصرف می‌کند"),
                    T($"{Size(recent)} in the last {us.SpikeMinutes} minutes.", $"{Size(recent)} در {us.SpikeMinutes} دقیقهٔ اخیر."), row.Key);
            }

            if (us.AlertNewApp && row.Total >= 5L * 1024 * 1024 && _book.IsNewApp(row.Key, TimeSpan.FromDays(1)))
            {
                Alert($"newapp:{row.Key}", "new-app", "info", T($"New app using the internet: {row.Key}", $"برنامهٔ تازه از اینترنت استفاده می‌کند: {row.Key}"),
                    T($"{Size(row.Total)} today. If you don't know it, cut it from the Usage page.", $"امروز {Size(row.Total)}. اگر آن را نمی‌شناسید از صفحهٔ مصرف قطعش کنید."), row.Key);
            }

            var uploadHour = RecentBytes(row.Key, 60);
            if (us.AlertHeavyUpload && row.Up >= 200L * 1024 * 1024 && row.Up > row.Down * 3 && uploadHour > 50L * 1024 * 1024 && !TrafficCatalog.StreamingApps.Contains(row.Key))
            {
                Alert($"upload:{row.Key}:{day}", "upload", "warn", T($"{row.Key} is uploading a lot", $"{row.Key} آپلود سنگین دارد"),
                    T($"{Size(row.Up)} sent today, much more than received (sync, backup — or something unwanted).",
                      $"امروز {Size(row.Up)} فرستاده، خیلی بیشتر از دریافت (همگام‌سازی، پشتیبان‌گیری یا چیزی ناخواسته)."), row.Key);
            }
        }

        var now = _time.GetUtcNow();
        if (now - _updateWindowStart > TimeSpan.FromMinutes(10))
        {
            if (us.AlertUpdates)
            {
                foreach (var (group, bytes) in _updateWindow.Where(kv => kv.Value >= 50L * 1024 * 1024))
                {
                    Alert($"updates:{group}:{day}", "updates", "warn", T($"{group} updates are downloading", $"آپدیت‌های {group} در حال دانلود است"),
                        T($"{Size(bytes)} in the last 10 minutes. Eco mode or the update switches on the Usage page hold them back.",
                          $"{Size(bytes)} در ۱۰ دقیقهٔ اخیر. حالت Eco یا کلیدهای آپدیت در صفحهٔ مصرف جلویش را می‌گیرد."));
                }
            }

            _updateWindow.Clear();
            _updateWindowStart = now;
        }
    }

    private void Alert(string key, string kind, string level, string title, string message, string? app = null)
    {
        UsageEvent e;
        lock (_book.Gate)
        {
            if (_book.Data.Alerted.ContainsKey(key))
            {
                return;
            }

            _book.Data.Alerted[key] = UsageBook.Day(_time.GetLocalNow().DateTime);
            e = _book.AddEvent(kind, level, title, message, app);
        }

        _notifications?.Notify(NotificationCategory.UsageAlert, title, message);
        EventRaised?.Invoke(this, e);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _book.Save(force: true);
        _cts.Dispose();
    }
}
