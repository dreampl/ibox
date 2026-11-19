using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using FluentAssertions;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Health;
using JackiVpn.Application.Privacy;
using JackiVpn.Cores;
using JackiVpn.Cores.Generation;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using JackiVpn.Networking.Journal;
using JackiVpn.Networking.Privacy;
using JackiVpn.Networking.Recovery;
using JackiVpn.UnitTests.Cores;
using Microsoft.Extensions.Logging.Abstractions;

namespace JackiVpn.UnitTests.Privacy;

/// <summary>In-memory Windows time zone: unit tests never touch the real clock settings.</summary>
internal sealed class FakeTimeZoneApi(string current) : ITimeZoneApi
{
    public string Current { get; private set; } = current;
    public List<string> Sets { get; } = [];

    public string GetCurrentId() => Current;

    public void Set(string windowsId)
    {
        Sets.Add(windowsId);
        Current = windowsId;
    }

    public bool Exists(string windowsId) => windowsId.EndsWith("Standard Time", StringComparison.Ordinal);
}

internal sealed class FakeTimeZoneControl : ITimeZoneControl
{
    public List<string> Calls { get; } = [];
    public bool Fail { get; set; }

    public Task ApplyAsync(string ianaId, CancellationToken ct)
    {
        if (Fail)
        {
            throw new InvalidOperationException("simulated");
        }

        Calls.Add("apply " + ianaId);
        return Task.CompletedTask;
    }

    public Task RestoreAsync(CancellationToken ct)
    {
        Calls.Add("restore");
        return Task.CompletedTask;
    }
}

public sealed class TimeZoneMappingTests
{
    [Theory]
    [InlineData("Europe/Berlin", "DE", "Europe/Berlin")]
    [InlineData(null, "DE", "Europe/Berlin")]
    [InlineData(null, "nl", "Europe/Amsterdam")]
    [InlineData("America/Argentina/Buenos_Aires", null, "America/Argentina/Buenos_Aires")]
    [InlineData("../../etc/passwd", "FI", "Europe/Helsinki")]
    [InlineData("Bad Zone", null, null)]
    [InlineData(null, "XX", null)]
    [InlineData(null, null, null)]
    public void Exit_zone_prefers_the_lookup_and_falls_back_to_the_country(string? lookup, string? country, string? expected) =>
        TimeZoneMap.ForExit(lookup, country).Should().Be(expected);

    [Theory]
    [InlineData("Europe/Berlin", "W. Europe Standard Time")]
    [InlineData("Asia/Tehran", "Iran Standard Time")]
    [InlineData("Europe/London", "GMT Standard Time")]
    [InlineData("America/New_York", "Eastern Standard Time")]
    public void Iana_zones_convert_to_windows_ids(string iana, string windows) =>
        TimeZoneMap.ToWindowsId(iana).Should().Be(windows);

    [Theory]
    [InlineData(null)]
    [InlineData("Not/AZone")]
    [InlineData("W. Europe Standard Time")]
    [InlineData("C:\\Windows")]
    public void Unknown_or_malformed_zones_have_no_windows_id(string? iana) => TimeZoneMap.ToWindowsId(iana).Should().BeNull();

    [Fact]
    public void Geo_lookup_zone_is_read_and_the_hosting_flag_is_ignored()
    {
        const string json = """
            {"success":true,"ip":"203.0.113.7","country_code":"DE","city":"Frankfurt","latitude":50.1,"longitude":8.6,
             "connection":{"isp":"Example GmbH","org":"Example"},"security":{"hosting":true,"proxy":true,"vpn":true},"hosting":true,
             "timezone":{"id":"Europe/Berlin","abbr":"CET","utc":"+01:00"}}
            """;
        HttpLiveProbe.ParseGeoTimeZone(json).Should().Be("Europe/Berlin");
        HttpLiveProbe.ParseGeo(json).Should().Be(("Frankfurt", "Example GmbH", "DE"));
        typeof(ExitInfo).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Hosting", StringComparison.OrdinalIgnoreCase) || n.Contains("Datacenter", StringComparison.OrdinalIgnoreCase));
        typeof(ConnectionStatus).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Hosting", StringComparison.OrdinalIgnoreCase));
        HttpLiveProbe.ParseGeoTimeZone("""{"timezone":{"id":"../evil"}}""").Should().BeNull();
        HttpLiveProbe.ParseGeoTimeZone("""{"timezone":"Europe/Berlin"}""").Should().BeNull();
    }
}

public sealed class TimeZoneRestoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jvpn-tz-" + Guid.NewGuid().ToString("N"));

    public TimeZoneRestoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string JournalPath => Path.Combine(_dir, "journal.json");

    [Fact]
    public void Apply_journals_the_original_once_and_restore_puts_it_back()
    {
        var api = new FakeTimeZoneApi("Iran Standard Time");
        var journal = new NetworkStateJournal(JournalPath);
        var tz = new TimeZoneManager(api, journal, NullLogger.Instance);

        tz.Apply("W. Europe Standard Time").Should().BeTrue();
        tz.Apply("GMT Standard Time").Should().BeTrue(); // server switch: the original must stay the user's own
        journal.OfKind(JournalEntryKind.TimeZone).Single().As<TimeZoneJournalData>().OriginalId.Should().Be("Iran Standard Time");
        api.Current.Should().Be("GMT Standard Time");

        tz.Restore().Should().BeTrue();
        api.Current.Should().Be("Iran Standard Time");
        journal.IsDirty.Should().BeFalse();
        tz.Restore().Should().BeFalse("nothing is left to restore");
    }

    [Fact]
    public void Unknown_zone_changes_nothing()
    {
        var api = new FakeTimeZoneApi("Iran Standard Time");
        var journal = new NetworkStateJournal(JournalPath);
        new TimeZoneManager(api, journal, NullLogger.Instance).Apply("Nowhere").Should().BeFalse();
        api.Sets.Should().BeEmpty();
        journal.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task A_crash_is_undone_by_the_next_start_from_the_journal_file()
    {
        var api = new FakeTimeZoneApi("Iran Standard Time");
        new TimeZoneManager(api, new NetworkStateJournal(JournalPath), NullLogger.Instance).Apply("W. Europe Standard Time");

        // The process dies here; a new one reads the journal from disk.
        var journal = new NetworkStateJournal(JournalPath);
        journal.IsDirty.Should().BeTrue();
        var manager = new TimeZoneManager(api, journal, NullLogger.Instance);
        var report = await new RecoveryCoordinator(journal, [new TimeZoneRecoveryHandler(manager)], [], NullLogger.Instance).RecoverAsync(CancellationToken.None);
        report.WasDirty.Should().BeTrue();
        api.Current.Should().Be("Iran Standard Time");
        journal.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task Sync_follows_the_session_and_keeps_the_zone_across_reconnects()
    {
        var control = new FakeTimeZoneControl();
        var settings = new AppSettings();
        var sync = new TimeZoneSync(control, () => settings, NullLogger.Instance);

        await sync.RecoverAsync(CancellationToken.None);
        await sync.OnStatusAsync(new ConnectionStatus { State = ConnectionState.Connecting }, CancellationToken.None);
        await sync.OnStatusAsync(new ConnectionStatus { State = ConnectionState.Connected }, CancellationToken.None); // lookup pending
        await sync.OnStatusAsync(new ConnectionStatus { State = ConnectionState.Connected, ExitTimeZone = "Europe/Berlin" }, CancellationToken.None);
        await sync.OnStatusAsync(new ConnectionStatus { State = ConnectionState.Connected, ExitTimeZone = "Europe/Berlin" }, CancellationToken.None);
        await sync.OnStatusAsync(new ConnectionStatus { State = ConnectionState.Reconnecting }, CancellationToken.None);
        await sync.OnStatusAsync(new ConnectionStatus { State = ConnectionState.Connected, ExitTimeZone = "Europe/Amsterdam" }, CancellationToken.None);
        await sync.OnStatusAsync(new ConnectionStatus { State = ConnectionState.Disconnected }, CancellationToken.None);
        await sync.OnStatusAsync(new ConnectionStatus { State = ConnectionState.Disconnected }, CancellationToken.None);

        control.Calls.Should().Equal("restore", "apply Europe/Berlin", "apply Europe/Amsterdam", "restore");
        sync.Applied.Should().BeNull();
    }

    [Fact]
    public async Task Turning_the_setting_off_restores_at_once_and_failures_never_throw()
    {
        var control = new FakeTimeZoneControl();
        var settings = new AppSettings();
        var sync = new TimeZoneSync(control, () => settings, NullLogger.Instance);
        var connected = new ConnectionStatus { State = ConnectionState.Connected, ExitTimeZone = "Europe/Berlin" };
        await sync.OnStatusAsync(connected, CancellationToken.None);
        settings = settings with { Privacy = settings.Privacy with { MatchTimeZone = false } };
        await sync.OnStatusAsync(connected, CancellationToken.None);
        control.Calls.Should().Equal("apply Europe/Berlin", "restore");

        control.Fail = true;
        settings = new AppSettings();
        await FluentActions.Awaiting(() => sync.OnStatusAsync(connected, CancellationToken.None)).Should().NotThrowAsync();
        sync.Applied.Should().BeNull();
    }

    [Fact]
    public void Matching_is_on_for_new_installs_only_and_the_jumbo_mtu_is_migrated()
    {
        new AppSettings().Privacy.MatchTimeZone.Should().BeTrue();
        new AppSettings().Connection.TunMtu.Should().Be(1500);

        var old = SettingsMigrations.Upgrade(new AppSettings { SchemaVersion = 5, Connection = new ConnectionSettings { TunMtu = 9000 } });
        old.Privacy.MatchTimeZone.Should().BeFalse();
        old.Connection.TunMtu.Should().Be(ConnectionSettings.DefaultTunMtu);
        old.SchemaVersion.Should().Be(6);

        SettingsMigrations.Upgrade(new AppSettings { SchemaVersion = 5, Connection = new ConnectionSettings { TunMtu = 1400 } }).Connection.TunMtu.Should().Be(1400, "a user's own choice stays");
        var current = SettingsMigrations.Upgrade(new AppSettings());
        current.Privacy.MatchTimeZone.Should().BeTrue();
    }
}

public sealed class VpnTellTests
{
    [Fact]
    public void Adapter_name_is_generic_on_windows_and_only_wintun_devices_are_swept_by_it()
    {
        JackiVpn.Domain.ProductInfo.TunInterfaceName.Should().Be(OperatingSystem.IsWindows() ? "LAN Connection" : "IBOX");
        new CoreSessionPlan { Kind = SessionKind.Tun, Profiles = [], ListenPort = 0 }.Tun.InterfaceName.Should().NotContainAny("sing-box", "wintun", "IBOX", "Jacki");

        StaleAdapterCleaner.MayRemove("LAN Connection", @"SWD\Wintun\{6BA2E8C5-0000-4E5D-8D11-111111111111}").Should().BeTrue();
        StaleAdapterCleaner.MayRemove("LAN Connection", @"PCI\VEN_8086&DEV_15B8\3&11583659&0&FE").Should().BeFalse("a real adapter with the same name is never removed");
        StaleAdapterCleaner.MayRemove("IBOX", @"ROOT\NET\0001").Should().BeTrue("the brand names are ours alone");
    }

    [Fact]
    public void Stun_goes_through_the_tunnel_before_direct_rules_and_ipv6_cannot_leak()
    {
        var plan = new DnsAndTunPolicyTests.CoreSessionPlanBuilder().Plan with { Kind = SessionKind.Tun, Ipv6 = Ipv6Mode.Disable };
        var config = SingBoxConfigBuilder.Build(plan, GoldenConfigTests.SingBox114).Config;
        var rules = config["route"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();
        var stun = rules.FindIndex(r => r["network"]?.GetValue<string>() == "udp" && r["port"] is JsonArray p && p.Select(v => v!.GetValue<int>()).SequenceEqual([3478, 19302]));
        stun.Should().BeGreaterThan(-1);
        rules[stun]["outbound"]!.GetValue<string>().Should().Be("proxy");
        rules.FindIndex(r => r["ip_version"]?.GetValue<int>() == 6 && r["action"]?.GetValue<string>() == "reject").Should().BeGreaterThan(-1);
        rules.Should().Contain(r => r["action"] != null && r["action"]!.GetValue<string>() == "hijack-dns");

        var tun = config["inbounds"]!.AsArray().OfType<JsonObject>().Single(i => i["type"]!.GetValue<string>() == "tun");
        tun["strict_route"]!.GetValue<bool>().Should().BeTrue("no DNS or v6 leak around the tunnel");
        tun["mtu"]!.GetValue<int>().Should().Be(1500);
    }
}

public sealed class OurServerDetectorTests
{
    private static OurServerDetector Detector(string? publicIp, bool foreign, Func<AppSettings>? settings = null, FakeTime? time = null, Action? onLookup = null) =>
        new(
            _ =>
            {
                onLookup?.Invoke();
                return Task.FromResult(publicIp);
            },
            _ => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(["198.51.100.4", "2001:db8::1"])),
            () => foreign,
            settings ?? (() => new AppSettings()),
            NullLogger.Instance,
            time);

    [Fact]
    public async Task Our_exit_ip_with_no_local_vpn_means_the_router()
    {
        var finding = await Detector("198.51.100.4", foreign: false).CheckAsync(false, CancellationToken.None);
        finding.Should().Be(new OurServerFinding("198.51.100.4", OurServerPath.Router));
    }

    [Fact]
    public async Task Our_exit_ip_with_a_local_vpn_or_proxy_means_another_app()
    {
        var finding = await Detector("2001:0db8:0000::1", foreign: true).CheckAsync(false, CancellationToken.None);
        finding!.Via.Should().Be(OurServerPath.OtherApp);
    }

    [Fact]
    public async Task Other_addresses_connected_or_disabled_show_nothing()
    {
        (await Detector("203.0.113.9", false).CheckAsync(false, CancellationToken.None)).Should().BeNull();
        (await Detector(null, false).CheckAsync(false, CancellationToken.None)).Should().BeNull();
        var lookups = 0;
        (await Detector("198.51.100.4", false, onLookup: () => lookups++).CheckAsync(true, CancellationToken.None)).Should().BeNull();
        var off = new AppSettings { Privacy = new PrivacySettings { DetectOtherApps = false } };
        (await Detector("198.51.100.4", false, () => off, onLookup: () => lookups++).CheckAsync(false, CancellationToken.None)).Should().BeNull();
        lookups.Should().Be(0, "no request while connected or turned off");
    }

    [Fact]
    public async Task Checks_are_rate_limited_never_a_loop()
    {
        var time = new FakeTime();
        var lookups = 0;
        var d = Detector("198.51.100.4", false, time: time, onLookup: () => lookups++);
        var changes = 0;
        d.Changed += (_, _) => changes++;
        for (var i = 0; i < 5; i++)
        {
            await d.CheckAsync(false, CancellationToken.None);
        }

        lookups.Should().Be(1);
        time.Advance(OurServerDetector.MinInterval);
        await d.CheckAsync(false, CancellationToken.None);
        lookups.Should().Be(2);
        changes.Should().Be(1);
        d.Clear();
        d.Current.Should().BeNull();
    }

    [Fact]
    public void Known_ips_come_from_learned_exits_and_ip_literal_servers_only()
    {
        var path = Path.Combine(Path.GetTempPath(), "jvpn-ins-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var insights = new InsightStore(path);
            insights.Update("fp1", i => i with { Exit = new ExitInfo("192.0.2.10", "DE", null, null, null, DateTimeOffset.UtcNow) });
            ProxyProfile P(string server) => new() { Id = server, Name = server, Protocol = ProxyProtocol.Vless, Server = server, Port = 443 };
            var ips = OurServerDetector.KnownIps(insights, [P("192.0.2.20"), P("[2001:db8::5]"), P("cdn.example.com")]);
            ips.Should().BeEquivalentTo(["192.0.2.10", "192.0.2.20", "2001:db8::5"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("tun0", null, NetworkInterfaceType.Unknown, true)]
    [InlineData("wg0", null, NetworkInterfaceType.Unknown, true)]
    [InlineData("Local Area Connection", "Wintun Userspace Tunnel", NetworkInterfaceType.Unknown, true)]
    [InlineData("Ethernet", "Intel(R) Ethernet Connection", NetworkInterfaceType.Ethernet, false)]
    [InlineData("Teredo Tunneling Pseudo-Interface", "Microsoft Teredo Tunneling Adapter", NetworkInterfaceType.Tunnel, false)]
    [InlineData("LAN Connection", "Wintun Userspace Tunnel", NetworkInterfaceType.Unknown, false)]
    [InlineData("IBOX", "Wintun Userspace Tunnel", NetworkInterfaceType.Unknown, false)]
    public void Foreign_tunnels_are_recognised_but_ours_are_not(string name, string? description, NetworkInterfaceType type, bool expected) =>
        OurServerDetector.IsForeignTunnel(name, description, type).Should().Be(expected);

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
