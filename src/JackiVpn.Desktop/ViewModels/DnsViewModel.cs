using System;
using System.Collections.Generic;
using System.Linq;
using JackiVpn.Desktop.Services;
using JackiVpn.Domain.Settings;
using JackiVpn.Application.Settings;

namespace JackiVpn.Desktop.ViewModels;

/// <summary>One editable resolver (remote / direct / bootstrap).</summary>
public sealed class DnsEndpointEditor(Func<DnsServerEndpoint> get, Action<DnsServerEndpoint> set)
{
    public DnsTransport Transport
    {
        get => get().Transport;
        set => set(get() with { Transport = value });
    }

    public string Server
    {
        get => get().Server ?? string.Empty;
        set => set(get() with { Server = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
    }

    public string Port
    {
        get => get().Port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        set => set(get() with { Port = int.TryParse(value, out var p) && p is > 0 and < 65536 ? p : null });
    }

    public string Path
    {
        get => get().Path ?? string.Empty;
        set => set(get() with { Path = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
    }

    public string TlsServerName
    {
        get => get().TlsServerName ?? string.Empty;
        set => set(get() with { TlsServerName = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
    }
}

/// <summary>One row of the DNS search results.</summary>
public sealed record DnsResultRow(string Name, string Kind, string Address, string Speed, string Verdict, bool Good, Application.Connectivity.DnsResult Result);

public sealed class DnsViewModel : SettingsPageViewModel
{
    private readonly Application.Connectivity.DnsFinder? _finder;
    private readonly Func<bool> _canProbe;
    private bool _isSearching;
    private string? _searchStatus;

    public DnsViewModel(SettingsStore store, Application.Connectivity.DnsFinder? finder = null, Func<bool>? canProbe = null)
        : base(store)
    {
        _finder = finder;
        _canProbe = canProbe ?? (() => true);
        Remote = new DnsEndpointEditor(() => S.Dns.Remote, v => Set(s => s with { Dns = s.Dns with { Remote = v } }));
        Direct = new DnsEndpointEditor(() => S.Dns.Direct, v => Set(s => s with { Dns = s.Dns with { Direct = v } }));
        Bootstrap = new DnsEndpointEditor(() => S.Dns.Bootstrap, v => Set(s => s with { Dns = s.Dns with { Bootstrap = v } }));
        SearchCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(SearchAsync, () => !IsSearching);
        UseForServersCommand = new CommunityToolkit.Mvvm.Input.RelayCommand<DnsResultRow>(r =>
        {
            if (r is not null)
            {
                Set(s => s with { Dns = s.Dns with { Bootstrap = r.Result.Candidate.ToEndpoint() } });
                SearchStatus = $"Server names are now looked up with {r.Result.Candidate.Label}.";
            }
        });
        if (_finder?.Last is { Count: > 0 } last)
        {
            Fill(last);
        }
    }

    // ------------------------------------------------------------------ DNS search

    public System.Collections.ObjectModel.ObservableCollection<DnsResultRow> Results { get; } = [];

    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand SearchCommand { get; }

    public CommunityToolkit.Mvvm.Input.IRelayCommand<DnsResultRow> UseForServersCommand { get; }

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetProperty(ref _isSearching, value))
            {
                SearchCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string? SearchStatus
    {
        get => _searchStatus;
        private set => SetProperty(ref _searchStatus, value);
    }

    public string BestText => _finder?.BestLabel() is { } best ? "Best on this network: " + best : "Not searched on this network yet.";

    /// <summary>Use the best clean resolver of each network to look up server addresses (unless one is set by hand below).</summary>
    public bool AutoPickDns
    {
        get => S.Connectivity.AutoPickDns;
        set => Set(s => s with { Connectivity = s.Connectivity with { AutoPickDns = value } });
    }

    private async System.Threading.Tasks.Task SearchAsync()
    {
        if (_finder is null)
        {
            return;
        }

        if (!_canProbe())
        {
            SearchStatus = "Disconnect first: in TUN mode the test would go through the tunnel and measure the VPN, not your network.";
            return;
        }

        IsSearching = true;
        SearchStatus = "Asking every resolver (a few seconds)…";
        try
        {
            var results = await System.Threading.Tasks.Task.Run(() => _finder.SearchAsync(System.Threading.CancellationToken.None));
            Fill(results);
            var best = results.FirstOrDefault(r => r.Reachable && r.Clean);
            SearchStatus = best is null
                ? "No resolver answered honestly here: the VPN's own encrypted DNS and the rescue are used."
                : $"Suggested: {best.Candidate.Label} ({best.LatencyMs:0} ms)." + (AutoPickDns && S.Dns.Bootstrap.Transport == DnsTransport.Local ? " Used automatically from the next connection." : string.Empty);
        }
        finally
        {
            IsSearching = false;
            OnPropertyChanged(nameof(BestText));
        }
    }

    private void Fill(IReadOnlyList<Application.Connectivity.DnsResult> results)
    {
        Results.Clear();
        foreach (var r in results)
        {
            var c = r.Candidate;
            Results.Add(new DnsResultRow(
                c.Name + (c.Iranian ? " (Iran)" : string.Empty),
                c.Transport == DnsTransport.Https ? "Encrypted" : "Plain",
                c.Address,
                r.LatencyMs is { } ms ? $"{ms:0} ms" : "—",
                !r.Reachable ? "No answer" : r.Clean ? "Clean" : r.Note?.StartsWith("intercepted", StringComparison.Ordinal) == true ? "Intercepted" : "Filtered",
                r.Reachable && r.Clean,
                r));
        }
    }

    public IReadOnlyList<DnsMode> Modes { get; } = Choices.Of<DnsMode>();

    public IReadOnlyList<DnsTransport> Transports { get; } = Choices.Of<DnsTransport>();

    public DnsEndpointEditor Remote { get; }

    public DnsEndpointEditor Direct { get; }

    public DnsEndpointEditor Bootstrap { get; }

    public DnsMode Mode
    {
        get => S.Dns.Mode;
        set => Set(s => s with { Dns = s.Dns with { Mode = value } });
    }

    public string ModeHint => S.Dns.Mode switch
    {
        DnsMode.AutoSecure => "Recommended. Queries go encrypted (DoH) through the tunnel; only VPN server names use the bootstrap resolver; port 53 is captured so apps cannot leak.",
        DnsMode.System => "Uses Windows' own resolvers directly. NOT leak-protected: your ISP can see the names you look up.",
        DnsMode.Local => "A resolver on this machine or LAN (e.g. Pi-hole), queried directly. Not leak-protected outside your network.",
        DnsMode.RemoteThroughVpn => "Plain DNS to the remote server, sent through the tunnel.",
        _ => "The remote resolver below is used for tunneled names; direct names use the direct resolver.",
    };

    public bool IsLeakRisk => S.Dns.Mode is DnsMode.System or DnsMode.Local;

    public bool FakeIp
    {
        get => S.Dns.FakeIpEnabled;
        set => Set(s => s with { Dns = s.Dns with { FakeIpEnabled = value } });
    }

    public string FakeIpV4
    {
        get => S.Dns.FakeIpV4Range;
        set => Set(s => s with { Dns = s.Dns with { FakeIpV4Range = value.Trim() } });
    }

    public string FakeIpV6
    {
        get => S.Dns.FakeIpV6Range;
        set => Set(s => s with { Dns = s.Dns with { FakeIpV6Range = value.Trim() } });
    }

    public bool Cache
    {
        get => S.Dns.CacheEnabled;
        set => Set(s => s with { Dns = s.Dns with { CacheEnabled = value } });
    }

    public void ResetDefaults() => Set(s => s with { Dns = new DnsSettings() });
}
