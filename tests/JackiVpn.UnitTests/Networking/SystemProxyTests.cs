using FluentAssertions;
using JackiVpn.Networking.Journal;
using JackiVpn.Networking.Recovery;
using JackiVpn.Networking.SystemProxy;
using Microsoft.Extensions.Logging.Abstractions;

namespace JackiVpn.UnitTests.Networking;

/// <summary>In-memory WinINET: the real API is never touched by unit tests.</summary>
internal sealed class FakeWinInet : IWinInetProxyApi
{
    public Dictionary<string, ProxySnapshot> State { get; } = new(StringComparer.Ordinal);
    public List<string> Ras { get; } = [];
    public int Notifications { get; private set; }
    public Func<ProxySnapshot, bool>? FailApply { get; set; }

    private static string Key(string? c) => c ?? "<LAN>";

    public ProxySnapshot Query(string? connectionName) =>
        State.TryGetValue(Key(connectionName), out var s) ? s with { ConnectionName = connectionName } : new ProxySnapshot { ConnectionName = connectionName };

    public void Apply(ProxySnapshot snapshot)
    {
        if (FailApply?.Invoke(snapshot) == true)
        {
            throw new InvalidOperationException("simulated failure");
        }

        State[Key(snapshot.ConnectionName)] = snapshot;
    }

    public void NotifyChanged() => Notifications++;

    public IReadOnlyList<string> EnumerateRasConnections() => Ras;
}

public sealed class SystemProxyTests : IDisposable
{
    private const string Bypass = "localhost;127.*;<local>";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jvpn-proxy-" + Guid.NewGuid().ToString("N"));

    private string JournalPath => Path.Combine(_dir, "journal.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private SystemProxyManager Manager(FakeWinInet api) => new(api, new NetworkStateJournal(JournalPath), NullLogger.Instance);

    public static TheoryData<string, ProxySnapshot> Scenarios => new()
    {
        { "no proxy", new ProxySnapshot { Flags = ProxySnapshot.FlagDirect } },
        { "auto-detect only", new ProxySnapshot { Flags = ProxySnapshot.FlagDirect | ProxySnapshot.FlagAutoDetect } },
        { "manual proxy", new ProxySnapshot { Flags = ProxySnapshot.FlagDirect | ProxySnapshot.FlagProxy, ProxyServer = "10.0.0.5:8080" } },
        { "PAC URL", new ProxySnapshot { Flags = ProxySnapshot.FlagDirect | ProxySnapshot.FlagAutoProxyUrl, AutoConfigUrl = "http://wpad.corp.example/proxy.pac" } },
        { "manual + existing bypass list", new ProxySnapshot { Flags = ProxySnapshot.FlagDirect | ProxySnapshot.FlagProxy, ProxyServer = "http=1.2.3.4:3128;https=1.2.3.4:3128", ProxyBypass = "*.corp.example;10.*;<local>" } },
        { "another app's loopback proxy", new ProxySnapshot { Flags = ProxySnapshot.FlagDirect | ProxySnapshot.FlagProxy, ProxyServer = "127.0.0.1:3067", ProxyBypass = "localhost" } },
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Apply_then_restore_returns_exact_previous_settings(string name, ProxySnapshot before)
    {
        var api = new FakeWinInet();
        api.State["<LAN>"] = before;
        var m = Manager(api);

        m.Apply(20811, Bypass);

        var during = api.Query(null);
        during.ProxyEnabled.Should().BeTrue(name);
        during.ProxyServer.Should().Be("127.0.0.1:20811");
        during.ProxyBypass.Should().Be(Bypass);
        during.PacEnabled.Should().BeFalse("PAC would override the manual proxy");
        m.IsApplied.Should().BeTrue();

        var report = m.Restore();

        report.Restored.Should().Be(1);
        api.Query(null).Should().Be(before with { ConnectionName = null }, name);
        m.IsApplied.Should().BeFalse();
        File.Exists(JournalPath).Should().BeFalse("journal is removed after a clean restore");
        api.Notifications.Should().Be(2);
    }

    [Fact]
    public void Ras_vpn_connections_are_handled_and_restored()
    {
        var api = new FakeWinInet();
        api.Ras.Add("Office VPN");
        api.State["Office VPN"] = new ProxySnapshot { ConnectionName = "Office VPN", Flags = 1 | 4, AutoConfigUrl = "http://pac/x.pac" };
        var m = Manager(api);

        m.Apply(20811, Bypass);
        api.Query("Office VPN").ProxyServer.Should().Be("127.0.0.1:20811");
        m.Restore();

        api.Query("Office VPN").AutoConfigUrl.Should().Be("http://pac/x.pac");
        api.Query("Office VPN").ProxyEnabled.Should().BeFalse();
    }

    [Fact]
    public void Reapply_with_new_port_keeps_the_original_backup()
    {
        var api = new FakeWinInet();
        var original = new ProxySnapshot { Flags = 3, ProxyServer = "10.0.0.5:8080" };
        api.State["<LAN>"] = original;
        var m = Manager(api);

        m.Apply(20811, Bypass);
        m.Apply(20822, Bypass);
        m.Restore();

        api.Query(null).ProxyServer.Should().Be("10.0.0.5:8080");
    }

    [Fact]
    public async Task Crash_recovery_restores_from_journal_in_a_new_process()
    {
        var api = new FakeWinInet();
        var original = new ProxySnapshot { Flags = 1 | 4, AutoConfigUrl = "http://pac/p.pac" };
        api.State["<LAN>"] = original;
        Manager(api).Apply(20811, Bypass);

        // "Process crashed": a brand new manager/journal instance reads the same file.
        var journal = new NetworkStateJournal(JournalPath);
        journal.IsDirty.Should().BeTrue();
        var coordinator = new RecoveryCoordinator(journal, [new WinInetRecoveryHandler(new SystemProxyManager(api, journal, NullLogger.Instance))], [], NullLogger.Instance);

        var report = await coordinator.RecoverAsync(TestContext.Current.CancellationToken);

        report.WasDirty.Should().BeTrue();
        report.Success.Should().BeTrue();
        api.Query(null).AutoConfigUrl.Should().Be("http://pac/p.pac");
        journal.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Recovery_never_clobbers_a_proxy_changed_by_someone_else_after_the_crash()
    {
        var api = new FakeWinInet();
        api.State["<LAN>"] = new ProxySnapshot { Flags = 1 };
        Manager(api).Apply(20811, Bypass);
        var userChoice = new ProxySnapshot { Flags = 3, ProxyServer = "127.0.0.1:3067" };
        api.State["<LAN>"] = userChoice;

        var report = Manager(api).Recover();

        report.SkippedChangedByOthers.Should().Be(1);
        api.Query(null).Should().Be(userChoice with { ConnectionName = null });
    }

    [Fact]
    public void Journal_is_written_before_the_change_so_a_failed_apply_is_still_recoverable()
    {
        var api = new FakeWinInet();
        var original = new ProxySnapshot { Flags = 3, ProxyServer = "10.9.9.9:1" };
        api.State["<LAN>"] = original;
        api.FailApply = s => s.ProxyServer == "127.0.0.1:20811";
        var m = Manager(api);

        var act = () => m.Apply(20811, Bypass);

        act.Should().Throw<InvalidOperationException>();
        new NetworkStateJournal(JournalPath).IsDirty.Should().BeTrue();
        api.FailApply = null;
        Manager(api).Restore();
        api.Query(null).Should().Be(original with { ConnectionName = null });
    }

    [Fact]
    public void Suspicious_leftover_proxy_is_reported_but_not_changed()
    {
        var api = new FakeWinInet();
        api.State["<LAN>"] = new ProxySnapshot { Flags = 3, ProxyServer = "127.0.0.1:20811", ProxyBypass = Bypass };

        var action = new SuspiciousProxyReport(api, Bypass).Run();

        action.Detail.Should().Contain("NOT changed");
        api.Query(null).ProxyServer.Should().Be("127.0.0.1:20811");
    }
}
