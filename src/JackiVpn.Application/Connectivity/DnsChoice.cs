using JackiVpn.Domain.Settings;

namespace JackiVpn.Application.Connectivity;

/// <summary>Which resolver IBOX uses outside the tunnel (server names, and optionally sites routed Direct).</summary>
public enum DnsSelection
{
    /// <summary>The fastest clean resolver found on each network (DNS search).</summary>
    Smart = 0,

    /// <summary>The resolvers the operating system has for this network.</summary>
    System,

    /// <summary>One resolver picked from the list or entered by hand.</summary>
    Manual,
}

/// <summary>
/// Smart, system or manual DNS on top of the stored settings (the bootstrap resolver and the auto-pick switch),
/// shared by the Windows, Linux and phone screens and applied to each session by the orchestrator.
/// </summary>
public static class DnsChoice
{
    public static DnsSelection Selection(AppSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Dns.Bootstrap.Transport != DnsTransport.Local ? DnsSelection.Manual : s.Connectivity.AutoPickDns ? DnsSelection.Smart : DnsSelection.System;
    }

    /// <summary>Smart or System; Manual needs a resolver (<see cref="Use"/>).</summary>
    public static AppSettings Select(AppSettings s, DnsSelection selection, DnsServerEndpoint? manual = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        return selection switch
        {
            DnsSelection.Manual => Use(s, manual ?? (s.Dns.Bootstrap.Transport != DnsTransport.Local ? s.Dns.Bootstrap : DnsServerEndpoint.CloudflareDoh)),
            _ => s with
            {
                Dns = s.Dns with { Bootstrap = DnsServerEndpoint.SystemLocal },
                Connectivity = s.Connectivity with { AutoPickDns = selection == DnsSelection.Smart },
            },
        };
    }

    /// <summary>Uses one resolver by hand (from the list, the search results or a custom entry).</summary>
    public static AppSettings Use(AppSettings s, DnsServerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(endpoint);
        return s with { Dns = s.Dns with { Bootstrap = endpoint }, Connectivity = s.Connectivity with { AutoPickDns = false } };
    }

    /// <summary>Adds (or replaces, by name) a resolver entered by hand. Returns the problem instead when it is not usable.</summary>
    public static (AppSettings Settings, string? Problem) AddCustom(AppSettings s, CustomDnsResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(resolver);
        resolver = resolver with { Name = resolver.Name?.Trim() ?? string.Empty, Address = resolver.Address?.Trim() ?? string.Empty, Host = resolver.Host?.Trim(), Path = resolver.Path?.Trim() };
        if (resolver.Problem() is { } problem)
        {
            return (s, problem);
        }

        var others = s.Dns.Custom.Where(c => !string.Equals(c.Name, resolver.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count >= CustomDnsResolver.MaxCount)
        {
            return (s, $"Up to {CustomDnsResolver.MaxCount} resolvers can be added.");
        }

        return (s with { Dns = s.Dns with { Custom = [.. others, resolver] } }, null);
    }

    /// <summary>Removes a resolver entered by hand; if it was in use, IBOX goes back to smart DNS.</summary>
    public static AppSettings RemoveCustom(AppSettings s, string name)
    {
        ArgumentNullException.ThrowIfNull(s);
        var removed = s.Dns.Custom.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).Select(c => c.ToEndpoint()).ToList();
        var next = s with { Dns = s.Dns with { Custom = s.Dns.Custom.Where(c => !string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).ToList() } };
        return removed.Contains(next.Dns.Bootstrap) ? Select(next, DnsSelection.Smart) : next;
    }

    /// <summary>The resolver used outside the tunnel for this session: manual, else the network's best (smart), else none (system).</summary>
    public static DnsServerEndpoint? Effective(AppSettings s, DnsServerEndpoint? best)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Selection(s) switch
        {
            DnsSelection.Manual => s.Dns.Bootstrap,
            DnsSelection.Smart => best,
            _ => null,
        };
    }

    /// <summary>Settings as one session uses them: the chosen resolver looks up server names (and Direct sites when asked).</summary>
    public static AppSettings ForSession(AppSettings s, DnsServerEndpoint? best)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (Effective(s, best) is not { } chosen)
        {
            return s;
        }

        var dns = s.Dns with { Bootstrap = chosen };
        if (s.Dns.UseChosenForDirect && s.Dns.Direct.Transport == DnsTransport.Local)
        {
            dns = dns with { Direct = chosen };
        }

        return s with { Dns = dns };
    }

    /// <summary>Short text for a resolver: the list's name when it is a known one, else its address.</summary>
    public static string Describe(DnsServerEndpoint endpoint, IEnumerable<DnsCandidate> known)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(known);
        if (endpoint.Transport == DnsTransport.Local)
        {
            return "The network's own DNS";
        }

        return known.FirstOrDefault(k => k.ToEndpoint() == endpoint)?.Label
            ?? $"{endpoint.Server} · {(endpoint.Transport == DnsTransport.Https ? "encrypted (DoH)" : endpoint.Transport.ToString())}";
    }
}
