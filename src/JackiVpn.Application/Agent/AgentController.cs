using System.Text.Json;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Connectivity;
using JackiVpn.Application.Settings;
using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Agent;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Health;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;
using JackiVpn.Routing;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Agent;

public sealed record AgentDependencies
{
    public required SettingsStore Settings { get; init; }
    public required ConnectionOrchestrator Orchestrator { get; init; }
    public required IProfileRepository Profiles { get; init; }
    public required Func<string, NodeHealthSnapshot> Health { get; init; }

    /// <summary>Last exit country seen through a server (by fingerprint).</summary>
    public required Func<string, string?> CountryOf { get; init; }

    public DnsFinder? DnsFinder { get; init; }
    public SpeedTestService? SpeedTest { get; init; }

    /// <summary>Ecosystem extras (traffic mode, usage, Dual VPN, network); null where the platform has none.</summary>
    public Func<AgentEco>? Eco { get; init; }

    public Func<string, string?>? SetTraffic { get; init; }

    public Func<bool, CancellationToken, Task<string?>>? SetDualVpn { get; init; }
    public required ILogger Logger { get; init; }
}

/// <summary>
/// Answers the control channel's requests (see <see cref="AgentProtocol"/>) with the app's own services: the same
/// rules as the UI (validated rules, DNS choices), never a credential in an answer. The pipe server checks whether
/// the channel is on and whether changes are allowed before a request gets here.
/// </summary>
public sealed class AgentController(AgentDependencies d)
{
    public async Task<AgentResponse> HandleAsync(AgentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            object? result = request.Method switch
            {
                AgentMethods.Status => Status(),
                AgentMethods.ListServers => await ServersAsync(Param<string>(request, "filter"), ct).ConfigureAwait(false),
                AgentMethods.Connect => await ConnectAsync(Parse<AgentConnectRequest>(request) ?? new AgentConnectRequest(), ct).ConfigureAwait(false),
                AgentMethods.Disconnect => await DisconnectAsync().ConfigureAwait(false),
                AgentMethods.SetMode => SetMode(Param<string>(request, "mode")),
                AgentMethods.ListRules => Rules(),
                AgentMethods.AddRule => AddRule(Parse<AgentRuleRequest>(request) ?? throw new AgentException("rules.add needs name, action and at least one of domains, ips, apps.")),
                AgentMethods.RemoveRule => RemoveRule(Param<string>(request, "id")),
                AgentMethods.GetDns => Dns(),
                AgentMethods.SetDns => SetDns(Parse<AgentDnsRequest>(request) ?? throw new AgentException("dns.set needs a selection (smart, system or manual).")),
                AgentMethods.SpeedTest => await SpeedTestAsync(ct).ConfigureAwait(false),
                AgentMethods.EcoStatus or AgentMethods.Watch or AgentMethods.NotchYield => Eco(),
                AgentMethods.SetTraffic => (d.SetTraffic ?? throw new AgentException("Not available here."))(Param<string>(request, "mode") ?? "normal") is { } problem ? throw new AgentException(problem) : Eco(),
                AgentMethods.SetDualVpn => await (d.SetDualVpn ?? throw new AgentException("Not available here."))(Param<bool>(request, "on"), ct).ConfigureAwait(false) is { } dualProblem ? throw new AgentException(dualProblem) : Eco(),
                _ => throw new AgentException("Unknown method."),
            };
            return new AgentResponse(request.Id, true, JsonSerializer.SerializeToElement(result, AgentProtocol.Json));
        }
        catch (AgentException ex)
        {
            return new AgentResponse(request.Id, false, null, ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or ConnectionException)
        {
            return new AgentResponse(request.Id, false, null, Security.SecretRedactor.Redact(ex.Message));
        }
    }

    /// <summary>The status plus the platform's extras (IBAR shows them all).</summary>
    public AgentEco Eco() => d.Eco?.Invoke() is { } eco ? eco with { Status = Status() } : new AgentEco { Status = Status() };

    private AgentStatus Status()
    {
        var st = d.Orchestrator.Status;
        return new AgentStatus
        {
            State = st.State.ToString(),
            Server = st.ProfileName,
            Mode = st.Mode switch { ConnectionMode.Tun => "tun", ConnectionMode.SystemProxy => "proxy", _ => null },
            Country = st.ExitCountryName ?? st.ExitCountryCode,
            City = st.ExitCity,
            PublicIp = st.PublicIp,
            LatencyMs = st.LatencyMs,
            DownloadBytesPerSecond = st.Traffic?.DownloadBytesPerSecond,
            UploadBytesPerSecond = st.Traffic?.UploadBytesPerSecond,
            ConnectedSinceUtc = st.ConnectedSinceUtc,
            LastError = st.State == ConnectionState.Connected ? null : st.LastError,
            SmartConnect = d.Settings.Current.Connection.SmartMode,
        };
    }

    private async Task<IReadOnlyList<AgentServer>> ServersAsync(string? filter, CancellationToken ct)
    {
        var st = d.Orchestrator.Status;
        var selected = d.Settings.Current.Connection.SelectedProfileId;
        var all = await d.Profiles.GetAllAsync(ct).ConfigureAwait(false);
        return all
            .Where(p => string.IsNullOrWhiteSpace(filter) || p.DisplayName.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase))
            .Take(500)
            .Select(p =>
            {
                var h = d.Health(p.Id);
                return new AgentServer(
                    p.Id,
                    p.DisplayName,
                    p.Protocol.ToString(),
                    d.CountryOf(p.Fingerprint) ?? Domain.Visuals.CountryGuess.FromName(p.DisplayName),
                    h.MedianLatencyMs,
                    h.Label.ToString(),
                    p.Id == selected,
                    st.State == ConnectionState.Connected && st.ProfileId == p.Id);
            })
            .ToList();
    }

    private async Task<AgentStatus> ConnectAsync(AgentConnectRequest req, CancellationToken ct)
    {
        string? id = null;
        if (!string.IsNullOrWhiteSpace(req.Server))
        {
            var all = await d.Profiles.GetAllAsync(ct).ConfigureAwait(false);
            var exact = all.FirstOrDefault(p => p.Id == req.Server || string.Equals(p.DisplayName, req.Server, StringComparison.OrdinalIgnoreCase));
            var matches = exact is not null ? [exact] : all.Where(p => p.DisplayName.Contains(req.Server.Trim(), StringComparison.OrdinalIgnoreCase)).Take(6).ToList();
            if (matches.Count == 0)
            {
                throw new AgentException($"No server matches '{req.Server}'. Use servers.list.");
            }

            if (matches.Count > 1)
            {
                throw new AgentException("Several servers match: " + string.Join(", ", matches.Select(m => m.DisplayName)) + ". Be more specific or use the id.");
            }

            id = matches[0].Id;
            d.Settings.Update(s => s with { Connection = s.Connection with { SelectedProfileId = id, SmartMode = false } });
        }
        else if (req.Smart == true)
        {
            d.Settings.Update(s => s with { Connection = s.Connection with { SmartMode = true } });
        }

        var state = d.Orchestrator.State;
        if (state == ConnectionState.Connected && id is not null)
        {
            await d.Orchestrator.SwitchToAsync(id, ct).ConfigureAwait(false);
        }
        else if (ConnectionStateRules.IsIdle(state))
        {
            var c = d.Settings.Current.Connection;
            if (!c.SmartMode && c.SelectedProfileId is null)
            {
                throw new AgentException("No server selected and smart connect is off: name a server or ask for smart.");
            }

            await d.Orchestrator.ConnectAsync(new ConnectRequest(), ct).ConfigureAwait(false);
        }

        return Status();
    }

    private async Task<AgentStatus> DisconnectAsync()
    {
        // While connecting this is the force cancel.
        await d.Orchestrator.DisconnectAsync().ConfigureAwait(false);
        return Status();
    }

    private object SetMode(string? mode)
    {
        var m = mode?.Trim().ToLowerInvariant() switch
        {
            "tun" => ConnectionMode.Tun,
            "proxy" or "systemproxy" or "system-proxy" => ConnectionMode.SystemProxy,
            _ => throw new AgentException("mode must be tun or proxy."),
        };
        d.Settings.Update(s => s with { Connection = s.Connection with { Mode = m } });
        return new { mode = m == ConnectionMode.Tun ? "tun" : "proxy", appliesFrom = d.Orchestrator.State == ConnectionState.Connected ? "the next connection" : "now" };
    }

    private IReadOnlyList<AgentRule> Rules() => d.Settings.Current.Routing.Rules.Select(Describe).ToList();

    private static AgentRule Describe(RoutingRule r) => new(
        r.Id,
        r.Name,
        r.Action.Type switch { RuleActionType.Direct => "direct", RuleActionType.Block => "block", RuleActionType.Proxy => "proxy", _ => r.Action.Type.ToString().ToLowerInvariant() },
        r.Enabled,
        string.Join("; ", r.Conditions.Select(c => $"{c.Type}: {string.Join(", ", c.Values.Take(6))}{(c.Values.Count > 6 ? $" (+{c.Values.Count - 6})" : string.Empty)}")));

    private AgentRule AddRule(AgentRuleRequest req)
    {
        var action = req.Action?.Trim().ToLowerInvariant() switch
        {
            "direct" => RuleAction.Direct,
            "proxy" or "vpn" => RuleAction.Proxy,
            "block" => RuleAction.Block,
            _ => throw new AgentException("action must be direct, proxy or block."),
        };
        var conditions = new List<RuleCondition>();
        if (req.Domains is { Count: > 0 } domains)
        {
            conditions.Add(new RuleCondition(RuleConditionType.DomainSuffix, domains.Select(x => x.Trim().TrimStart('*', '.')).ToList()));
        }

        if (req.Ips is { Count: > 0 } ips)
        {
            conditions.Add(new RuleCondition(RuleConditionType.IpCidr, ips.Select(x => x.Trim()).ToList()));
        }

        if (req.Apps is { Count: > 0 } apps)
        {
            conditions.Add(new RuleCondition(RuleConditionType.ProcessName, apps.Select(x => x.Trim()).ToList()));
        }

        var rule = new RoutingRule { Id = Guid.NewGuid().ToString("N"), Name = string.IsNullOrWhiteSpace(req.Name) ? "Agent rule" : req.Name.Trim(), Action = action, Conditions = conditions };
        var validation = RuleValidator.Validate(rule, d.Settings.Current.Routing.RuleSets.Select(s => s.Tag).ToList());
        if (!validation.IsValid)
        {
            throw new AgentException(string.Join(" ", validation.Errors.Select(e => e.Message)));
        }

        // First in the list (first match wins), like rules added in the app; applied to a running connection.
        d.Settings.Update(s => s with { Routing = s.Routing with { Rules = [rule, .. s.Routing.Rules], Preset = RoutingPreset.Custom } });
        d.Logger.LogInformation("Agent added routing rule {Name}.", rule.Name);
        return Describe(rule);
    }

    private object RemoveRule(string? id)
    {
        var rule = d.Settings.Current.Routing.Rules.FirstOrDefault(r => r.Id == id) ?? throw new AgentException("No rule with that id. Use rules.list.");
        d.Settings.Update(s => s with { Routing = s.Routing with { Rules = s.Routing.Rules.Where(r => r.Id != rule.Id).ToList(), Preset = RoutingPreset.Custom } });
        d.Logger.LogInformation("Agent removed routing rule {Name}.", rule.Name);
        return new { removed = rule.Name };
    }

    private AgentDns Dns()
    {
        var s = d.Settings.Current;
        var known = d.DnsFinder?.AllCandidates() ?? DnsFinder.Candidates;
        var inUse = DnsChoice.Selection(s) switch
        {
            DnsSelection.Smart => d.DnsFinder?.BestLabel() ?? "the network's own (no search result yet)",
            DnsSelection.System => "the network's own",
            _ => DnsChoice.Describe(s.Dns.Bootstrap, known),
        };
        return new AgentDns(DnsChoice.Selection(s).ToString().ToLowerInvariant(), inUse, s.Dns.UseChosenForDirect);
    }

    private AgentDns SetDns(AgentDnsRequest req)
    {
        switch (req.Selection?.Trim().ToLowerInvariant())
        {
            case "smart":
                d.Settings.Update(s => DnsChoice.Select(s, DnsSelection.Smart));
                break;
            case "system":
                d.Settings.Update(s => DnsChoice.Select(s, DnsSelection.System));
                break;
            case "manual":
                var resolver = new CustomDnsResolver
                {
                    Name = req.Address?.Trim() ?? string.Empty,
                    Address = req.Address?.Trim() ?? string.Empty,
                    Transport = req.Doh ? DnsTransport.Https : DnsTransport.Udp,
                    Host = req.Doh ? req.Host : null,
                };
                if (resolver.Problem() is { } problem)
                {
                    throw new AgentException(problem);
                }

                d.Settings.Update(s => DnsChoice.Use(s, resolver.ToEndpoint()));
                break;
            default:
                throw new AgentException("selection must be smart, system or manual.");
        }

        return Dns();
    }

    private async Task<SpeedTestResult> SpeedTestAsync(CancellationToken ct)
    {
        var st = d.Orchestrator.Status;
        if (d.SpeedTest is null || st.State != ConnectionState.Connected || st.Mode is not { } mode)
        {
            throw new AgentException("Connect first: the speed test measures the VPN.");
        }

        return await d.SpeedTest.RunAsync(mode, st.LocalProxyPort, null, ct).ConfigureAwait(false);
    }

    private static T? Param<T>(AgentRequest r, string name) =>
        r.Params is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) ? v.Deserialize<T>(AgentProtocol.Json) : default;

    private static T? Parse<T>(AgentRequest r)
        where T : class =>
        r.Params is { ValueKind: JsonValueKind.Object } p ? p.Deserialize<T>(AgentProtocol.Json) : null;
}

/// <summary>A request the controller refuses with a message for the caller.</summary>
public sealed class AgentException(string message) : Exception(message);
