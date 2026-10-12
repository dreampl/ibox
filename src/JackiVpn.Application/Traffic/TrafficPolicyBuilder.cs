using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Usage;

namespace JackiVpn.Application.Traffic;

/// <summary>What the policy depends on besides the settings.</summary>
public sealed record TrafficContext
{
    public TrafficMode Mode { get; init; } = TrafficMode.Normal;
    public WorkplaceProfile? Workplace { get; init; }

    /// <summary>The workplace applies from elsewhere (Work mode with Dual VPN), not from its own network.</summary>
    public bool Away { get; init; }

    /// <summary>The core can tell which program owns a connection (Windows/Linux; not Android).</summary>
    public bool ProcessAware { get; init; } = true;

    /// <summary>The game in front right now (full path), for Gaming priority.</summary>
    public string? GamePath { get; init; }

    /// <summary>Processes started by or starting the game (launcher, crash handler, anti-cheat helper).</summary>
    public IReadOnlyList<string> GameFamily { get; init; } = [];
}

/// <summary>
/// Turns settings + mode + workplace into the core's static <see cref="TrafficPolicy"/> and the relay's run-time
/// <see cref="TrafficLimits"/>. With Smart control the static part does not depend on the mode, so switching
/// Normal / Eco / Gaming / Streaming / Work only changes limits — no reconnect, no dropped game session.
/// </summary>
public static class TrafficPolicyBuilder
{
    public static IReadOnlyList<string> DomainsOf(IEnumerable<UpdateGroup> groups) =>
        groups.Distinct().SelectMany(TrafficCatalog.UpdateDomains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Apps that get their own relay class (limits, Eco saver list, company blocks).</summary>
    public static IReadOnlyList<string> LimitedApps(AppSettings s, WorkplaceProfile? workplace)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Traffic.Apps.Where(a => a.DownKbps > 0 || a.UpKbps > 0 || a.Blocked).Select(a => a.App)
            .Concat(s.Traffic.SaverApps)
            .Concat(workplace?.Rules.BlockApps ?? [])
            .Select(a => a.Trim())
            .Where(a => a.Length > 0 && !TrafficCatalog.Uncuttable.Contains(TrafficCatalog.AppName(a)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();
    }

    public static TrafficPolicy Build(AppSettings s, TrafficContext ctx)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(ctx);
        var t = s.Traffic;
        var work = ctx.Workplace;
        var smart = t.SmartControl;
        var blockedDomains = t.BlockedDomains.Concat(work?.Rules.BlockDomains ?? []).ToList();
        var blockedApps = new List<string>();
        var classes = new List<DomainClass>();
        IReadOnlyList<string> limited = [];

        if (smart)
        {
            // Every family / app has its own class; modes only change the classes' limits.
            classes.AddRange(Enum.GetValues<UpdateGroup>().Select(g => new DomainClass(ShapeClasses.Update(g), TrafficCatalog.UpdateDomains(g))));
            classes.Add(new DomainClass(ShapeClasses.Telemetry, TrafficCatalog.TelemetryDomains));
            if (ctx.ProcessAware)
            {
                limited = LimitedApps(s, work);
            }
        }
        else
        {
            // Without the relay, the current mode is written into the core (a mode change reconnects).
            blockedDomains.AddRange(DomainsOf(BlockedGroups(s, ctx.Mode, work)));
            if (ctx.Mode is TrafficMode.Eco or TrafficMode.Work)
            {
                blockedDomains.AddRange(TrafficCatalog.TelemetryDomains);
            }

            if (ctx.ProcessAware)
            {
                blockedApps.AddRange(t.Apps.Where(a => a.Blocked).Select(a => a.App));
                if (ctx.Mode == TrafficMode.Eco)
                {
                    blockedApps.AddRange(t.SaverApps);
                }

                if (ctx.Mode == TrafficMode.Work)
                {
                    blockedApps.AddRange(work?.Rules.BlockApps ?? []);
                }
            }
        }

        if (!smart || !ctx.ProcessAware)
        {
            // Always-blocked update families hold even without the relay.
            if (!smart)
            {
                blockedDomains.AddRange(DomainsOf(t.BlockedUpdates));
            }
        }

        return new TrafficPolicy
        {
            BlockedApps = blockedApps.Where(a => !TrafficCatalog.Uncuttable.Contains(TrafficCatalog.AppName(a))).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            BlockedDomains = blockedDomains.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            BlockedCidrs = t.BlockedIps.Where(Domain.Common.NetText.IsCidr).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            InternalDomains = work?.InternalDomains ?? [],
            InternalCidrs = (work?.InternalCidrs ?? []).Where(Domain.Common.NetText.IsCidr).ToList(),
            InternalDns = (work?.InternalDns ?? []).Where(Domain.Common.NetText.IsIpLiteral).ToList(),
            // On site the company network is right there; the second VPN is only for the way from elsewhere.
            // Android runs one VPN app at a time, so only a work server there.
            WorkRoute = WorkRouteOf(work, ctx),
            WorkServerId = WorkRouteOf(work, ctx) == WorkRoute.Server ? work!.DualVpn.ServerId : null,
            WorkInterface = WorkRouteOf(work, ctx) == WorkRoute.Adapter ? work!.DualVpn.Interface!.Trim() : null,
            Shaping = smart,
            DomainClasses = classes,
            ShapeBackground = smart && ctx.ProcessAware,
            // Calls rank right after the game: never routed through the relay, so never slowed.
            PriorityApps = smart && ctx.ProcessAware
                ? t.PriorityApps.Concat(TrafficCatalog.GameCompanions).Concat(TrafficCatalog.GameExecutables).Concat(TrafficCatalog.CallApps).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [],
            LimitedApps = limited,
            MediaApps = smart && ctx.ProcessAware ? Category(TrafficCatalog.MediaPlayers, t, limited) : [],
            DownloadApps = smart && ctx.ProcessAware ? Category(TrafficCatalog.DownloadApps, t, limited) : [],
            TrackProcesses = ctx.ProcessAware && s.Usage.Enabled,
        };
    }

    private static WorkRoute WorkRouteOf(WorkplaceProfile? work, TrafficContext ctx) => work is { DualVpn.IsOn: true } && ctx.Away
        ? work.DualVpn.Route == WorkRoute.Adapter && !ctx.ProcessAware ? WorkRoute.Off : work.DualVpn.Route
        : WorkRoute.Off;

    /// <summary>A built-in category, minus apps the user placed themselves (their own limit or priority wins).</summary>
    private static List<string> Category(IEnumerable<string> apps, TrafficControlSettings t, IReadOnlyList<string> limited) =>
        apps.Where(a => !t.PriorityApps.Contains(a, StringComparer.OrdinalIgnoreCase) && !limited.Contains(a, StringComparer.OrdinalIgnoreCase))
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Update families held back in this mode.</summary>
    public static IReadOnlyList<UpdateGroup> BlockedGroups(AppSettings s, TrafficMode mode, WorkplaceProfile? workplace)
    {
        ArgumentNullException.ThrowIfNull(s);
        var groups = new HashSet<UpdateGroup>(s.Traffic.BlockedUpdates);
        switch (mode)
        {
            case TrafficMode.Eco:
                groups.UnionWith(s.Traffic.EcoUpdates);
                break;
            case TrafficMode.Work:
                groups.UnionWith(s.Traffic.EcoUpdates);
                groups.UnionWith(workplace?.Rules.BlockUpdates ?? []);
                break;
            case TrafficMode.Gaming:
            case TrafficMode.Streaming:
                // Pause everything except game patches (a game may need its patch to start); those are slowed instead.
                groups.UnionWith(Enum.GetValues<UpdateGroup>().Where(g => g != UpdateGroup.Games));
                break;
        }

        return groups.ToList();
    }

    public static TrafficLimits Limits(AppSettings s, TrafficContext ctx)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(ctx);
        var t = s.Traffic;
        var mode = ctx.Mode;
        var work = ctx.Workplace;
        var classes = new Dictionary<string, ShapeRate>(StringComparer.Ordinal);

        var background = mode switch
        {
            TrafficMode.Gaming => Rate(t.BackgroundKbpsWhileGaming),
            TrafficMode.Streaming => Rate(t.BackgroundKbpsWhileStreaming),
            TrafficMode.Work when work?.Rules.BackgroundKbps is > 0 and var kbps => Rate(kbps),
            _ => ShapeRate.Unlimited,
        };
        classes[ShapeClasses.Background] = background;

        // The order under contention: game > calls (never relayed) > browsing ("bg") > media players > downloads and
        // updates. Normal and Eco leave everything at full speed; Gaming, Streaming and a company's Work limit give
        // each lower class less than the one above it.
        var shaping = mode is TrafficMode.Gaming or TrafficMode.Streaming || !background.IsUnlimited;
        classes[ShapeClasses.Media] = mode switch
        {
            TrafficMode.Gaming => Fraction(background, 2),
            TrafficMode.Streaming => ShapeRate.Unlimited, // the stream is what the user is watching
            _ => background,
        };
        var lowest = shaping ? Fraction(background, 4) : ShapeRate.Unlimited;
        classes[ShapeClasses.Downloads] = lowest;

        var blocked = BlockedGroups(s, mode, work);
        foreach (var g in Enum.GetValues<UpdateGroup>())
        {
            classes[ShapeClasses.Update(g)] = blocked.Contains(g) ? ShapeRate.Blocked : lowest;
        }

        classes[ShapeClasses.Telemetry] = mode is TrafficMode.Eco or TrafficMode.Work ? ShapeRate.Blocked : ShapeRate.Unlimited;

        var limited = ctx.ProcessAware ? LimitedApps(s, work) : [];
        for (var i = 0; i < limited.Count; i++)
        {
            var app = limited[i];
            var rule = t.Apps.FirstOrDefault(a => string.Equals(a.App, app, StringComparison.OrdinalIgnoreCase));
            var cut = rule?.Blocked == true
                || (mode == TrafficMode.Eco && t.SaverApps.Contains(app, StringComparer.OrdinalIgnoreCase))
                || (mode == TrafficMode.Work && (work?.Rules.BlockApps ?? []).Contains(app, StringComparer.OrdinalIgnoreCase));
            classes[ShapeClasses.App(i)] = cut ? ShapeRate.Blocked : rule is null ? ShapeRate.Unlimited : ShapeRate.FromKbps(rule.DownKbps, rule.UpKbps);
        }

        var priority = new List<string>(t.PriorityApps);
        var folders = new List<string>();
        if (mode == TrafficMode.Gaming)
        {
            priority.AddRange(TrafficCatalog.GameCompanions);
            priority.AddRange(ctx.GameFamily);
            if (ctx.GamePath is { Length: > 0 } game)
            {
                priority.Add(TrafficCatalog.AppName(game));
                if (GameFolder(game) is { } folder)
                {
                    folders.Add(folder);
                }
            }
        }
        else if (mode == TrafficMode.Streaming)
        {
            priority.AddRange(TrafficCatalog.StreamingApps);
        }

        return new TrafficLimits
        {
            Classes = classes,
            PriorityProcesses = priority.Select(TrafficCatalog.AppName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            PriorityFolders = folders,
        };
    }

    private static ShapeRate Rate(int downKbps) =>
        downKbps <= 0 ? ShapeRate.Unlimited : ShapeRate.FromKbps(downKbps, Math.Max(512, downKbps / 3));

    /// <summary>A share of a limit (never below 256/128 kbit/s, so a lower class still trickles).</summary>
    private static ShapeRate Fraction(ShapeRate rate, int divisor) =>
        rate.IsUnlimited || rate.Block
            ? rate
            : new ShapeRate(Math.Max(32 * 1024, rate.DownBytesPerSecond / divisor), Math.Max(16 * 1024, rate.UpBytesPerSecond / divisor));

    /// <summary>
    /// The install folder of a game: inside a game library the game's own folder (so its launcher, anti-cheat and
    /// crash reporter count as the game), otherwise the executable's folder.
    /// </summary>
    public static string? GameFolder(string gamePath)
    {
        ArgumentNullException.ThrowIfNull(gamePath);
        foreach (var lib in TrafficCatalog.GameLibraries)
        {
            var at = gamePath.IndexOf(lib, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                var start = at + lib.Length;
                var next = gamePath.IndexOfAny(['\\', '/'], start);
                return next > start ? gamePath[..(next + 1)] : null;
            }
        }

        var dir = Path.GetDirectoryName(gamePath);
        return dir is { Length: > 3 } ? dir + Path.DirectorySeparatorChar : null;
    }
}
