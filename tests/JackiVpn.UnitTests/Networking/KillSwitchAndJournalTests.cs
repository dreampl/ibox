using System.Net;
using FluentAssertions;
using JackiVpn.Networking.Journal;
using JackiVpn.Networking.KillSwitch;
using JackiVpn.Networking.Recovery;
using Microsoft.Extensions.Logging.Abstractions;

namespace JackiVpn.UnitTests.Networking;

public sealed class KillSwitchAndJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jvpn-ks-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void Plan_blocks_everything_except_explicit_permits()
    {
        var plan = KillSwitchPlan.Build(new KillSwitchOptions { AllowLan = false, AllowedApplications = [@"C:\Program Files\JackiVpn\cores\sing-box.exe"] });

        foreach (var layer in Enum.GetValues<WfpLayer>())
        {
            plan.Should().Contain(f => f.Layer == layer && f.Action == FilterAction.Block && f.Conditions.Count == 0, $"{layer} needs a catch-all block");
            plan.Should().Contain(f => f.Layer == layer && f.Conditions.OfType<LoopbackCondition>().Any() && f.Action == FilterAction.Permit);
            plan.Should().Contain(f => f.Layer == layer && f.Conditions.OfType<AppIdCondition>().Any());
        }

        plan.Where(f => f.Action == FilterAction.Block).Should().OnlyContain(b => plan.Where(p => p.Action == FilterAction.Permit).All(p => p.Weight > b.Weight));
        plan.Should().NotContain(f => f.Conditions.OfType<RemoteAddressCondition>().Any(), "no LAN permits when Allow LAN is off");
        plan.Should().NotContain(f => f.Conditions.OfType<RemotePortCondition>().Any(p => p.Port == 53), "DNS outside the tunnel is never permitted");
        plan.Select(f => f.Key).Should().OnlyHaveUniqueItems();
        plan.Count.Should().BeLessThanOrEqualTo(KillSwitchPlan.MaxFilters);
    }

    [Fact]
    public void Allow_lan_adds_private_ranges_for_both_families_with_matching_layers()
    {
        var plan = KillSwitchPlan.Build(new KillSwitchOptions { AllowLan = true });
        var lan = plan.Where(f => f.Conditions.OfType<RemoteAddressCondition>().Any()).ToList();

        lan.Should().Contain(f => f.Conditions.OfType<RemoteAddressCondition>().Single().Network.Equals(IPAddress.Parse("192.168.0.0")));
        lan.Should().Contain(f => f.Conditions.OfType<RemoteAddressCondition>().Single().Network.Equals(IPAddress.Parse("fe80::")));
        foreach (var f in plan)
        {
            foreach (var addr in f.Conditions.OfType<RemoteAddressCondition>().Select(c => c.Network)
                .Concat(f.Conditions.OfType<LocalAddressCondition>().Select(c => c.Network)))
            {
                KillSwitchPlan.MatchesFamily(addr, f.Layer).Should().BeTrue($"{f.Name} has a {addr.AddressFamily} address on {f.Layer}");
            }
        }
    }

    [Fact]
    public void Filter_keys_are_deterministic_for_journal_free_cleanup()
    {
        KillSwitchPlan.FilterKey(7).Should().Be(KillSwitchPlan.FilterKey(7));
        KillSwitchPlan.FilterKey(7).Should().NotBe(KillSwitchPlan.FilterKey(8));
        Enumerable.Range(0, KillSwitchPlan.MaxFilters).Select(KillSwitchPlan.FilterKey).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Wfp_struct_layouts_match_the_x64_windows_headers()
    {
        WfpKillSwitch.SizeOfFilter.Should().Be(200);
        WfpKillSwitch.SizeOfCondition.Should().Be(40);
        WfpKillSwitch.SizeOfSublayer.Should().Be(72);
        WfpKillSwitch.SizeOfSession.Should().Be(72);
        WfpKillSwitch.SizeOfProvider.Should().Be(64);
    }

    [Fact]
    public void Journal_survives_process_restart_and_handles_corruption()
    {
        var path = Path.Combine(_dir, "j.json");
        var j1 = new NetworkStateJournal(path);
        j1.IsDirty.Should().BeFalse();
        var id = j1.Record(JournalEntryKind.KillSwitch, new { allowLan = true });

        var j2 = new NetworkStateJournal(path);
        j2.IsDirty.Should().BeTrue();
        j2.OfKind(JournalEntryKind.KillSwitch).Should().ContainSingle();
        j2.Complete(id);
        File.Exists(path).Should().BeFalse();

        File.WriteAllText(path, "{ torn json");
        var j3 = new NetworkStateJournal(path);
        j3.IsDirty.Should().BeTrue("unknown prior state must trigger recovery");
        j3.WasCorrupt.Should().BeTrue();
        Directory.GetFiles(_dir, "j.json.corrupt-*").Should().ContainSingle("corrupt journals are preserved for diagnostics");
    }

    [Fact]
    public async Task Recovery_runs_handlers_in_safe_order_and_retries_failures()
    {
        var path = Path.Combine(_dir, "order.json");
        var journal = new NetworkStateJournal(path);
        journal.Record(JournalEntryKind.KillSwitch, new { });
        journal.Record(JournalEntryKind.WinInetProxy, new { });
        journal.Record(JournalEntryKind.CoreProcess, new { });
        var calls = new List<JournalEntryKind>();
        var failing = new RecordingHandler(JournalEntryKind.WinInetProxy, calls, succeed: false);
        var coordinator = new RecoveryCoordinator(journal,
            [new RecordingHandler(JournalEntryKind.KillSwitch, calls), failing, new RecordingHandler(JournalEntryKind.CoreProcess, calls)],
            [], NullLogger.Instance);

        var report = await coordinator.RecoverAsync(TestContext.Current.CancellationToken);

        calls.Should().Equal(JournalEntryKind.CoreProcess, JournalEntryKind.WinInetProxy, JournalEntryKind.KillSwitch);
        report.Success.Should().BeFalse();
        journal.OfKind(JournalEntryKind.WinInetProxy).Should().ContainSingle("a failed step stays journaled for the next attempt");
        journal.OfKind(JournalEntryKind.KillSwitch).Should().BeEmpty();
    }

    private sealed class RecordingHandler(JournalEntryKind kind, List<JournalEntryKind> calls, bool succeed = true) : IRecoveryHandler
    {
        public JournalEntryKind Kind => kind;

        public RecoveryAction Recover(JournalEntry entry)
        {
            calls.Add(kind);
            return new RecoveryAction(kind.ToString(), succeed, succeed ? "ok" : "failed");
        }
    }
}
