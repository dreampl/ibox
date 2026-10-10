using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JackiVpn.Desktop.Composition;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;

namespace JackiVpn.Desktop.ViewModels;

public sealed record CoreRow(string Name, string Version, string Status);

public sealed partial class AdvancedViewModel : SettingsPageViewModel
{
    private readonly AppServices _s;

    [ObservableProperty]
    private string? _muxMessage;

    [ObservableProperty]
    private bool _isBenchmarking;

    public AdvancedViewModel(AppServices services)
        : base(services.Settings)
    {
        _s = services;
    }

    public IReadOnlyList<CorePreference> CorePreferences { get; } = Choices.Of<CorePreference>();

    public IReadOnlyList<MuxMode> MuxModes { get; } = Choices.Of<MuxMode>();

    public ObservableCollection<CoreRow> Cores { get; } = [];

    // ---------------------------------------------------------------- speed & handshake
    public IReadOnlyList<TunStack> TunStacks { get; } = Choices.Of<TunStack>();

    public IReadOnlyList<QuicPolicy> QuicPolicies { get; } = Choices.Of<QuicPolicy>();

    public IReadOnlyList<string> Fingerprints { get; } = ["chrome", "firefox", "edge", "safari", "ios", "android", "(none)"];

    public TunStack TunStack
    {
        get => S.Performance.TunStack;
        set => Set(s => s with { Performance = s.Performance with { TunStack = value } });
    }

    public QuicPolicy Quic
    {
        get => S.Performance.Quic;
        set => Set(s => s with { Performance = s.Performance with { Quic = value } });
    }

    public string DefaultFingerprint
    {
        get => string.IsNullOrEmpty(S.Performance.DefaultFingerprint) ? "(none)" : S.Performance.DefaultFingerprint;
        set => Set(s => s with { Performance = s.Performance with { DefaultFingerprint = value == "(none)" ? string.Empty : value } });
    }

    public IReadOnlyList<Domain.Connection.ActivityOptimization> ActivityOptions { get; } = Choices.Of<Domain.Connection.ActivityOptimization>();

    public Domain.Connection.ActivityOptimization Activity
    {
        get => S.Performance.Activity;
        set => Set(s => s with { Performance = s.Performance with { Activity = value } });
    }

    public bool AutoTune
    {
        get => S.Performance.AutoTune;
        set => Set(s => s with { Performance = s.Performance with { AutoTune = value } });
    }

    public bool FastDns
    {
        get => S.Dns.FakeIpEnabled;
        set => Set(s => s with { Dns = s.Dns with { FakeIpEnabled = value } });
    }

    // ---------------------------------------------------------------- cores & mux
    public CorePreference Core
    {
        get => S.Connection.Core;
        set => Set(s => s with { Connection = s.Connection with { Core = value } });
    }

    public MuxMode Mux
    {
        get => S.Connection.Mux;
        set => Set(s => s with { Connection = s.Connection with { Mux = value } });
    }

    public bool MuxBenchmark
    {
        get => S.Smart.MuxBenchmark;
        set => Set(s => s with { Smart = s.Smart with { MuxBenchmark = value } });
    }

    // ---------------------------------------------------------------- smart & failover
    public bool ProactiveSwitching
    {
        get => S.Smart.ProactiveSwitching;
        set => Set(s => s with { Smart = s.Smart with { ProactiveSwitching = value } });
    }

    public int HealthCheckSeconds
    {
        get => (int)S.Smart.Failover.HealthCheckInterval.TotalSeconds;
        set => Set(s => s with { Smart = s.Smart with { Failover = s.Smart.Failover with { HealthCheckInterval = TimeSpan.FromSeconds(Clamp(value, 10, 600)) } } });
    }

    public int FailuresToFailover
    {
        get => S.Smart.Failover.ConsecutiveFailuresToFailover;
        set => Set(s => s with { Smart = s.Smart with { Failover = s.Smart.Failover with { ConsecutiveFailuresToFailover = Clamp(value, 1, 10) } } });
    }

    public int MinDwellMinutes
    {
        get => (int)S.Smart.Failover.MinDwell.TotalMinutes;
        set => Set(s => s with { Smart = s.Smart with { Failover = s.Smart.Failover with { MinDwell = TimeSpan.FromMinutes(Clamp(value, 1, 120)) } } });
    }

    public int CooldownMinutes
    {
        get => (int)S.Smart.Failover.FailedNodeCooldown.TotalMinutes;
        set => Set(s => s with { Smart = s.Smart with { Failover = s.Smart.Failover with { FailedNodeCooldown = TimeSpan.FromMinutes(Clamp(value, 1, 240)) } } });
    }

    public int MaxSwitchesPerHour
    {
        get => S.Smart.Failover.MaxSwitchesPerHour;
        set => Set(s => s with { Smart = s.Smart with { Failover = s.Smart.Failover with { MaxSwitchesPerHour = Clamp(value, 1, 60) } } });
    }

    public int SwitchMarginPercent
    {
        get => (int)Math.Round(S.Smart.Failover.SwitchMarginRatio * 100);
        set => Set(s => s with { Smart = s.Smart with { Failover = s.Smart.Failover with { SwitchMarginRatio = Clamp(value, 0, 200) / 100.0 } } });
    }

    // ---------------------------------------------------------------- probes
    public int ProbeTimeoutSeconds
    {
        get => (int)S.Probe.Timeout.TotalSeconds;
        set => Set(s => s with { Probe = s.Probe with { Timeout = TimeSpan.FromSeconds(Clamp(value, 2, 60)) } });
    }

    public int SamplesPerNode
    {
        get => S.Probe.SamplesPerNode;
        set => Set(s => s with { Probe = s.Probe with { SamplesPerNode = Clamp(value, 1, 10) } });
    }

    public int Concurrency
    {
        get => S.Probe.MaxConcurrency;
        set => Set(s => s with { Probe = s.Probe with { MaxConcurrency = Clamp(value, 1, 6) } });
    }

    public int CacheMinutes
    {
        get => (int)S.Probe.CacheTtl.TotalMinutes;
        set => Set(s => s with { Probe = s.Probe with { CacheTtl = TimeSpan.FromMinutes(Clamp(value, 0, 1440)) } });
    }

    public string ProbeUrls
    {
        get => Format.Lines(S.Probe.ProbeUrls);
        set
        {
            var urls = Format.SplitLines(value).Where(u => Uri.TryCreate(u, UriKind.Absolute, out var x) && x.Scheme is "http" or "https").ToArray();
            if (urls.Length > 0)
            {
                Set(s => s with { Probe = s.Probe with { ProbeUrls = urls } });
            }
        }
    }

    public bool ThroughputTest
    {
        get => S.Probe.ThroughputTest;
        set => Set(s => s with { Probe = s.Probe with { ThroughputTest = value } });
    }

    public bool CheckPublicIp
    {
        get => S.Probe.CheckPublicIp;
        set => Set(s => s with { Probe = s.Probe with { CheckPublicIp = value } });
    }

    // ---------------------------------------------------------------- score weights
    public string WeightLatency { get => W(S.Smart.Weights.Latency); set => SetWeight(value, (w, v) => w with { Latency = v }); }

    public string WeightJitter { get => W(S.Smart.Weights.Jitter); set => SetWeight(value, (w, v) => w with { Jitter = v }); }

    public string WeightSuccess { get => W(S.Smart.Weights.SuccessRate); set => SetWeight(value, (w, v) => w with { SuccessRate = v }); }

    public string WeightHandshake { get => W(S.Smart.Weights.Handshake); set => SetWeight(value, (w, v) => w with { Handshake = v }); }

    public string WeightStability { get => W(S.Smart.Weights.Stability); set => SetWeight(value, (w, v) => w with { Stability = v }); }

    public string WeightThroughput { get => W(S.Smart.Weights.Throughput); set => SetWeight(value, (w, v) => w with { Throughput = v }); }

    private static string W(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private void SetWeight(string text, Func<ScoreWeights, double, ScoreWeights> apply)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 1)
        {
            Set(s => s with { Smart = s.Smart with { Weights = apply(s.Smart.Weights, v) } });
        }
    }

    // ---------------------------------------------------------------- ownership (Jacki Smart Connect)
    public string AllowedDomains
    {
        get => Format.Lines(S.Ownership.AllowedProviderDomains);
        set => Set(s => s with { Ownership = s.Ownership with { AllowedProviderDomains = Format.SplitLines(value).Select(d => d.ToLowerInvariant()).ToArray() } });
    }

    public string SingleConfigHosts
    {
        get => Format.Lines(S.Ownership.SingleConfigAllowedHosts);
        set => Set(s => s with { Ownership = s.Ownership with { SingleConfigAllowedHosts = Format.SplitLines(value).Select(d => d.ToLowerInvariant()).ToArray() } });
    }

    public string MarkerHeader
    {
        get => S.Ownership.MarkerHeaderName;
        set => Set(s => s with { Ownership = s.Ownership with { MarkerHeaderName = value.Trim() } });
    }

    public string MarkerValue
    {
        get => S.Ownership.MarkerHeaderValue;
        set => Set(s => s with { Ownership = s.Ownership with { MarkerHeaderValue = value.Trim() } });
    }

    public bool HeaderRequiresDomain
    {
        get => S.Ownership.HeaderRequiresAllowedDomain;
        set => Set(s => s with { Ownership = s.Ownership with { HeaderRequiresAllowedDomain = value } });
    }

    public string ManifestPublicKey
    {
        get => S.Ownership.ManifestPublicKeyPem ?? string.Empty;
        set => Set(s => s with { Ownership = s.Ownership with { ManifestPublicKeyPem = string.IsNullOrWhiteSpace(value) ? null : value.Trim() } });
    }

    // ---------------------------------------------------------------- subscriptions
    public string UserAgent
    {
        get => S.Subscriptions.UserAgent;
        set => Set(s => s with { Subscriptions = s.Subscriptions with { UserAgent = string.IsNullOrWhiteSpace(value) ? new SubscriptionSettings().UserAgent : value.Trim() } });
    }

    public int FetchTimeoutSeconds
    {
        get => (int)S.Subscriptions.Timeout.TotalSeconds;
        set => Set(s => s with { Subscriptions = s.Subscriptions with { Timeout = TimeSpan.FromSeconds(Clamp(value, 5, 120)) } });
    }

    public async Task LoadCoresAsync()
    {
        Cores.Clear();
        foreach (var (kind, engine) in new (CoreKind, global::JackiVpn.Cores.ICoreEngine)[] { (CoreKind.SingBox, _s.SingBox), (CoreKind.Xray, _s.Xray) })
        {
            var name = kind == CoreKind.SingBox ? "sing-box" : "Xray";
            try
            {
                using (var binary = _s.Catalog.Verify(kind))
                {
                    var caps = await engine.GetCapabilitiesAsync(_s.Lifetime);
                    Cores.Add(new CoreRow(name, caps.Version.DisplayVersion, $"Verified · sha256 {binary.Sha256[..12]}…"));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                Cores.Add(new CoreRow(name, "—", "Unavailable: " + ex.Message));
            }
        }
    }

    [RelayCommand]
    private Task RefreshCoresAsync() => LoadCoresAsync();

    [RelayCommand]
    private async Task BenchmarkMuxAsync()
    {
        var id = _s.Orchestrator.Status.ProfileId ?? S.Connection.SelectedProfileId;
        var profile = id is null ? null : await _s.Profiles.GetAsync(id, _s.Lifetime);
        if (profile is null)
        {
            MuxMessage = "Select or connect to a server first.";
            return;
        }

        IsBenchmarking = true;
        MuxMessage = $"Benchmarking MUX on {profile.DisplayName}…";
        try
        {
            var r = await Task.Run(() => _s.Tuner.BenchmarkMuxAsync(profile, _s.Lifetime));
            MuxMessage = !r.MuxWorks
                ? "MUX is not usable with this server (unsupported or failing). It stays off."
                : $"With MUX {Format.Ms(r.MedianWithMuxMs)}, without {Format.Ms(r.MedianWithoutMuxMs)} → {(r.Recommended ? "MUX will be used (Auto)" : "MUX stays off (Auto)")}.";
        }
        finally
        {
            IsBenchmarking = false;
        }
    }
}
