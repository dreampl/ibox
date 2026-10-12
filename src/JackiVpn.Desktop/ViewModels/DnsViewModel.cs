using System;
using System.Collections.Generic;
using System.Linq;
using JackiVpn.Desktop.Services;
using JackiVpn.Domain.Settings;
using JackiVpn.Application.Settings;
using JackiVpn.Application.Connectivity;

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

/// <summary>One resolver the user can pick by hand (built-in or entered).</summary>
public sealed record DnsChoiceItem(string Label, DnsServerEndpoint Endpoint, bool IsCustom, string Name)
{
    public override string ToString() => Label;
}

/// <summary>A ready-made set of servers for an adapter.</summary>
public sealed record AdapterDnsPreset(string Name, IReadOnlyList<string> Servers)
{
    public override string ToString() => Name;
}

/// <summary>One network adapter and its DNS, editable.</summary>
public sealed class AdapterDnsRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private string _servers;
    private string? _message;
    private bool _canRestore;
    private AdapterDnsPreset? _preset;

    public AdapterDnsRow(Domain.Network.AdapterDns adapter, bool canRestore)
    {
        Adapter = adapter;
        _canRestore = canRestore;
        _servers = adapter.Automatic ? string.Empty : string.Join(", ", adapter.Servers);
    }

    public Domain.Network.AdapterDns Adapter { get; }

    public string Title => $"{Adapter.Name} · {Adapter.Kind}" + (Adapter.Up ? string.Empty : " · not connected");

    public string? Description => Adapter.Description;

    public string CurrentText => (Adapter.Automatic ? "Automatic (from the network)" : "Set by hand")
        + (Adapter.Servers.Count > 0 ? ": " + string.Join(", ", Adapter.Servers) : string.Empty);

    /// <summary>Servers to set, comma separated (empty = automatic).</summary>
    public string Servers
    {
        get => _servers;
        set => SetProperty(ref _servers, value);
    }

    public AdapterDnsPreset? Preset
    {
        get => _preset;
        set
        {
            if (SetProperty(ref _preset, value) && value is not null)
            {
                Servers = string.Join(", ", value.Servers);
            }
        }
    }

    public string? Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public bool CanRestore
    {
        get => _canRestore;
        set => SetProperty(ref _canRestore, value);
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

    private readonly AdapterDnsService? _adapters;
    private string? _adaptersStatus;
    private readonly Func<System.Threading.Tasks.Task<bool>>? _applyNow;
    private readonly Func<bool> _isConnected;
    private string _newName = string.Empty;
    private string _newAddress = string.Empty;
    private bool _newIsDoh;
    private string _newHost = string.Empty;
    private string? _entryError;
    private string _customKey = string.Empty;

    public DnsViewModel(
        SettingsStore store,
        Application.Connectivity.DnsFinder? finder = null,
        Func<bool>? canProbe = null,
        Func<System.Threading.Tasks.Task<bool>>? applyNow = null,
        Func<bool>? isConnected = null,
        AdapterDnsService? adapters = null)
        : base(store)
    {
        _adapters = adapters;
        RefreshAdaptersCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(RefreshAdaptersAsync);
        SetAdapterCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<AdapterDnsRow>(r => ChangeAdapterAsync(r, automatic: false));
        AutomaticAdapterCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<AdapterDnsRow>(r => ChangeAdapterAsync(r, automatic: true));
        RestoreAdapterCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<AdapterDnsRow>(RestoreAdapterAsync);
        _finder = finder;
        _canProbe = canProbe ?? (() => true);
        _applyNow = applyNow;
        _isConnected = isConnected ?? (() => false);
        RebuildChoices();
        store.Changed += (_, _) => Ui.Post(RebuildChoices);
        AddCustomCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(AddCustom);
        RemoveCustomCommand = new CommunityToolkit.Mvvm.Input.RelayCommand<DnsChoiceItem>(i =>
        {
            if (i is { IsCustom: true })
            {
                Set(s => DnsChoice.RemoveCustom(s, i.Name));
            }
        });
        ApplyNowCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(async () =>
        {
            if (_applyNow is null)
            {
                return;
            }

            SearchStatus = "Applying to the running connection…";
            SearchStatus = await _applyNow() ? "Applied to the running connection." : "Could not apply now; used from the next connection.";
        });
        Remote = new DnsEndpointEditor(() => S.Dns.Remote, v => Set(s => s with { Dns = s.Dns with { Remote = v } }));
        Direct = new DnsEndpointEditor(() => S.Dns.Direct, v => Set(s => s with { Dns = s.Dns with { Direct = v } }));
        Bootstrap = new DnsEndpointEditor(() => S.Dns.Bootstrap, v => Set(s => s with { Dns = s.Dns with { Bootstrap = v } }));
        SearchCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(SearchAsync, () => !IsSearching);
        UseForServersCommand = new CommunityToolkit.Mvvm.Input.RelayCommand<DnsResultRow>(r =>
        {
            if (r is not null)
            {
                Set(s => DnsChoice.Use(s, r.Result.Candidate.ToEndpoint()));
                SearchStatus = $"Now using {r.Result.Candidate.Label}." + AppliesHint;
            }
        });
        if (_finder?.Last is { Count: > 0 } last)
        {
            Fill(last);
        }
    }

    // ------------------------------------------------------------------ which DNS (smart / system / manual)

    private string AppliesHint => _isConnected() ? " Press Apply now to use it on this connection." : string.Empty;

    public bool IsSmart
    {
        get => DnsChoice.Selection(S) == DnsSelection.Smart;
        set
        {
            if (value)
            {
                Set(s => DnsChoice.Select(s, DnsSelection.Smart));
            }
        }
    }

    public bool IsSystem
    {
        get => DnsChoice.Selection(S) == DnsSelection.System;
        set
        {
            if (value)
            {
                Set(s => DnsChoice.Select(s, DnsSelection.System));
            }
        }
    }

    public bool IsManual
    {
        get => DnsChoice.Selection(S) == DnsSelection.Manual;
        set
        {
            if (value && !IsManual)
            {
                // Start from the network's best clean resolver when one is known.
                Set(s => DnsChoice.Select(s, DnsSelection.Manual, _finder?.BestForNetwork()));
            }
        }
    }

    /// <summary>Built-in and entered resolvers, for the manual pick.</summary>
    public System.Collections.ObjectModel.ObservableCollection<DnsChoiceItem> ResolverChoices { get; } = [];

    public DnsChoiceItem? Chosen
    {
        get => IsManual ? ResolverChoices.FirstOrDefault(c => c.Endpoint == S.Dns.Bootstrap) : null;
        set
        {
            if (value is not null && value.Endpoint != S.Dns.Bootstrap)
            {
                Set(s => DnsChoice.Use(s, value.Endpoint));
            }
        }
    }

    public System.Collections.Generic.IEnumerable<DnsChoiceItem> CustomChoices => ResolverChoices.Where(c => c.IsCustom);

    public bool HasCustom => S.Dns.Custom.Count > 0;

    public string InUseText
    {
        get
        {
            var known = _finder?.AllCandidates() ?? Application.Connectivity.DnsFinder.Candidates;
            return DnsChoice.Selection(S) switch
            {
                DnsSelection.Smart => _finder?.BestLabel() is { } best ? "In use: " + best + " (best on this network)" : "In use: the network's own DNS until a search finds a better one.",
                DnsSelection.System => "In use: the DNS servers of your network adapter.",
                _ => "In use: " + DnsChoice.Describe(S.Dns.Bootstrap, known),
            };
        }
    }

    public bool UseChosenForDirect
    {
        get => S.Dns.UseChosenForDirect;
        set => Set(s => s with { Dns = s.Dns with { UseChosenForDirect = value } });
    }

    public bool CanApplyNow => _applyNow is not null && _isConnected();

    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand ApplyNowCommand { get; }

    public CommunityToolkit.Mvvm.Input.IRelayCommand AddCustomCommand { get; }

    public CommunityToolkit.Mvvm.Input.IRelayCommand<DnsChoiceItem> RemoveCustomCommand { get; }

    public string NewName
    {
        get => _newName;
        set => SetProperty(ref _newName, value);
    }

    public string NewAddress
    {
        get => _newAddress;
        set => SetProperty(ref _newAddress, value);
    }

    public bool NewIsDoh
    {
        get => _newIsDoh;
        set => SetProperty(ref _newIsDoh, value);
    }

    public string NewHost
    {
        get => _newHost;
        set => SetProperty(ref _newHost, value);
    }

    public string? EntryError
    {
        get => _entryError;
        private set => SetProperty(ref _entryError, value);
    }

    private void AddCustom()
    {
        var resolver = new CustomDnsResolver
        {
            Name = string.IsNullOrWhiteSpace(NewName) ? NewAddress.Trim() : NewName.Trim(),
            Address = NewAddress.Trim(),
            Transport = NewIsDoh ? DnsTransport.Https : DnsTransport.Udp,
            Host = NewIsDoh && !string.IsNullOrWhiteSpace(NewHost) ? NewHost.Trim() : null,
        };
        var (next, problem) = DnsChoice.AddCustom(S, resolver);
        if (problem is not null)
        {
            EntryError = problem;
            return;
        }

        EntryError = null;
        Set(_ => DnsChoice.Use(next, resolver.ToEndpoint()));
        NewName = NewAddress = NewHost = string.Empty;
        NewIsDoh = false;
        SearchStatus = $"Added and now using {resolver.Name}." + AppliesHint;
    }

    private void RebuildChoices()
    {
        var key = System.Text.Json.JsonSerializer.Serialize(S.Dns.Custom);
        if (key == _customKey && ResolverChoices.Count > 0)
        {
            OnPropertyChanged(nameof(Chosen));
            return;
        }

        _customKey = key;
        ResolverChoices.Clear();
        foreach (var c in Application.Connectivity.DnsFinder.Candidates)
        {
            ResolverChoices.Add(new DnsChoiceItem(c.Label + (c.Iranian ? " (Iran)" : string.Empty), c.ToEndpoint(), false, c.Name));
        }

        foreach (var r in S.Dns.Custom)
        {
            var c = Application.Connectivity.DnsCandidate.From(r);
            ResolverChoices.Add(new DnsChoiceItem(c.Label + " (yours)", c.ToEndpoint(), true, r.Name));
        }

        OnPropertyChanged(nameof(CustomChoices));
        OnPropertyChanged(nameof(Chosen));
    }

    // ------------------------------------------------------------------ network adapters

    public bool HasAdapters => _adapters is not null;

    public System.Collections.ObjectModel.ObservableCollection<AdapterDnsRow> Adapters { get; } = [];

    public IReadOnlyList<AdapterDnsPreset> AdapterPresets => AdapterDnsPresets.All(S.Dns.Custom).Select(p => new AdapterDnsPreset(p.Name, p.Servers)).ToList();

    public string? AdaptersStatus
    {
        get => _adaptersStatus;
        private set => SetProperty(ref _adaptersStatus, value);
    }

    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand RefreshAdaptersCommand { get; }

    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<AdapterDnsRow> SetAdapterCommand { get; }

    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<AdapterDnsRow> AutomaticAdapterCommand { get; }

    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<AdapterDnsRow> RestoreAdapterCommand { get; }

    /// <summary>Reads the adapters (called when the page is shown and by Refresh).</summary>
    public async System.Threading.Tasks.Task RefreshAdaptersAsync()
    {
        if (_adapters is null)
        {
            return;
        }

        try
        {
            var list = await _adapters.ListAsync(System.Threading.CancellationToken.None);
            Adapters.Clear();
            foreach (var a in list)
            {
                Adapters.Add(new AdapterDnsRow(a, _adapters.CanRestore(a.Id)));
            }

            AdaptersStatus = list.Count == 0 ? "No network adapters found." : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AdaptersStatus = ex.Message;
        }
    }

    private async System.Threading.Tasks.Task ChangeAdapterAsync(AdapterDnsRow? row, bool automatic)
    {
        if (_adapters is null || row is null)
        {
            return;
        }

        var servers = automatic ? [] : Domain.Network.AdapterDnsChange.Parse(row.Servers);
        if (!automatic && servers.Count == 0)
        {
            row.Message = "Enter at least one server, or choose Automatic.";
            return;
        }

        row.Message = "Applying…";
        try
        {
            row.Message = await _adapters.SetAsync(row.Adapter, servers, System.Threading.CancellationToken.None);
            if (row.Message is null)
            {
                await RefreshAdaptersAsync();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            row.Message = ex.Message;
        }
    }

    private async System.Threading.Tasks.Task RestoreAdapterAsync(AdapterDnsRow? row)
    {
        if (_adapters is null || row is null)
        {
            return;
        }

        row.Message = "Restoring…";
        try
        {
            await _adapters.RestoreAsync(row.Adapter, System.Threading.CancellationToken.None);
            await RefreshAdaptersAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            row.Message = ex.Message;
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
