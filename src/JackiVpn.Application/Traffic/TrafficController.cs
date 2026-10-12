using System.Text.Json;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Usage;
using JackiVpn.Application.Workplace;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Usage;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Traffic;

/// <summary>
/// Decides the traffic mode and keeps the running session in line with it:
/// <list type="bullet">
/// <item>Mode = the user's choice; otherwise Gaming/Streaming while one is detected, Work on a workplace network,
/// Eco when the data cap is used up (if the user asked for that), else Normal.</item>
/// <item>Mode changes only change relay limits (instant, no reconnect). Only a change of what lives in the core
/// configuration (blocks without Smart control, workplace routes) reconnects — and never because a game started.</item>
/// </list>
/// </summary>
public sealed class TrafficController
{
    private readonly ConnectionOrchestrator _orchestrator;
    private readonly Func<AppSettings> _settings;
    private readonly WorkplaceService? _workplace;
    private readonly UsageMonitor? _usage;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private ActivityProfile _activity;
    private string? _appliedPolicy;
    private TrafficLimits? _appliedLimits;
    private (string? Path, IReadOnlyList<string> Family) _game = (null, []);

    public TrafficController(ConnectionOrchestrator orchestrator, Func<AppSettings> settings, WorkplaceService? workplace, UsageMonitor? usage, ILogger logger)
    {
        _orchestrator = orchestrator;
        _settings = settings;
        _workplace = workplace;
        _usage = usage;
        _logger = logger;
        orchestrator.StatusChanged += (_, s) =>
        {
            if (s.Activity != _activity)
            {
                _activity = s.Activity;
                _ = ReevaluateAsync(userChange: false);
            }
        };
        if (workplace is not null)
        {
            workplace.Changed += (_, _) => _ = ReevaluateAsync(userChange: true);
        }

        if (usage is not null)
        {
            usage.Updated += (_, _) =>
            {
                if (usage.CapReached != _capEco && _settings().Usage.OnCap == CapAction.Eco)
                {
                    _ = ReevaluateAsync(userChange: true);
                }
            };
        }
    }

    private bool _capEco;

    /// <summary>The core sees which program owns a connection (Windows/Linux).</summary>
    public bool ProcessAware { get; init; } = true;

    public event EventHandler? Changed;

    public TrafficMode Mode { get; private set; }

    /// <summary>Why the mode is what it is ("manual", "game detected", "workplace: Acme", "data cap reached").</summary>
    public string Reason { get; private set; } = "manual";

    /// <summary>The game in front (desktop detection) — its folder and helpers get priority.</summary>
    public void ReportGame(string? path, IReadOnlyList<string> family)
    {
        lock (_gate)
        {
            if (_game.Path == path && _game.Family.SequenceEqual(family, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            _game = (path, family);
        }

        _ = ReevaluateAsync(userChange: false);
    }

    private TrafficContext Context()
    {
        var s = _settings();
        var t = s.Traffic;
        var work = _workplace?.Active;
        _capEco = _usage?.CapReached == true && s.Usage.OnCap == CapAction.Eco;
        TrafficMode mode;
        string reason;
        if (t.Mode != TrafficMode.Normal)
        {
            (mode, reason) = (t.Mode, "manual");
        }
        else if (t.AutoGaming && _activity == ActivityProfile.Gaming)
        {
            (mode, reason) = (TrafficMode.Gaming, "game detected");
        }
        else if (t.AutoGaming && _activity == ActivityProfile.Streaming)
        {
            (mode, reason) = (TrafficMode.Streaming, "stream detected");
        }
        else if (t.AutoWork && work is { WorkMode: true })
        {
            (mode, reason) = (TrafficMode.Work, "workplace: " + work.Name);
        }
        else if (_capEco)
        {
            (mode, reason) = (TrafficMode.Eco, "data cap reached");
        }
        else
        {
            (mode, reason) = (TrafficMode.Normal, "normal");
        }

        // Dual VPN: away from the office, Work mode brings the workplace along (its company traffic takes the work VPN).
        var away = false;
        if (work is null && mode == TrafficMode.Work && s.Workplace.Profiles.FirstOrDefault(p => p.DualVpn.IsOn) is { } remote)
        {
            (work, away) = (remote, true);
            Reason = reason + " · dual VPN: " + remote.Name;
        }
        else
        {
            Reason = reason;
        }

        Mode = mode;
        lock (_gate)
        {
            return new TrafficContext { Mode = mode, Workplace = work, Away = away, ProcessAware = ProcessAware, GamePath = _game.Path, GameFamily = _game.Family };
        }
    }

    /// <summary>For the orchestrator: the policy a new session starts with (remembered to detect later changes).</summary>
    public TrafficPolicy CurrentPolicy()
    {
        var policy = TrafficPolicyBuilder.Build(_settings(), Context());
        _appliedPolicy = JsonSerializer.Serialize(policy, JsonDefaults.Storage);
        return policy;
    }

    public TrafficLimits CurrentLimits()
    {
        var limits = TrafficPolicyBuilder.Limits(_settings(), Context());
        _appliedLimits = limits;
        return limits;
    }

    /// <summary>Call after the user changed traffic settings or the mode.</summary>
    public Task SettingsChangedAsync() => ReevaluateAsync(userChange: true);

    private async Task ReevaluateAsync(bool userChange)
    {
        try
        {
            var s = _settings();
            var ctx = Context();
            var limits = TrafficPolicyBuilder.Limits(s, ctx);
            var policy = JsonSerializer.Serialize(TrafficPolicyBuilder.Build(s, ctx), JsonDefaults.Storage);
            Changed?.Invoke(this, EventArgs.Empty);
            if (_orchestrator.State != ConnectionState.Connected)
            {
                return;
            }

            if (_appliedPolicy is not null && policy != _appliedPolicy)
            {
                if (userChange)
                {
                    // Needs a new core configuration: reconnect on the same server.
                    await _orchestrator.ReconnectAsync("traffic settings changed", CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                _logger.LogInformation("Traffic mode {Mode} takes full effect on the next connection ({Reason}).", ctx.Mode, Reason);
            }

            if (!LimitsEqual(limits, _appliedLimits))
            {
                _appliedLimits = limits;
                await _orchestrator.ApplyTrafficLimitsAsync(limits, CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("Traffic mode {Mode} ({Reason}).", ctx.Mode, Reason);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("Traffic mode change failed: {Error}", ex.Message);
        }
    }

    private static bool LimitsEqual(TrafficLimits a, TrafficLimits? b) =>
        b is not null
        && a.Classes.Count == b.Classes.Count && a.Classes.All(kv => b.Classes.TryGetValue(kv.Key, out var v) && v == kv.Value)
        && a.PriorityProcesses.SequenceEqual(b.PriorityProcesses) && a.PriorityFolders.SequenceEqual(b.PriorityFolders);
}
