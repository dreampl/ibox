using System.Net;
using FluentAssertions;
using JackiVpn.Application.Ecosystem;
using JackiVpn.Domain.Settings;

namespace JackiVpn.UnitTests.Application;

/// <summary>"Sync device" scans only the local LAN around this device, never large or public ranges.</summary>
public class SyncDeviceScanTests
{
    [Fact]
    public void Home_network_scans_every_other_host_of_the_24()
    {
        var targets = EcosystemService.ScanTargets([new LocalNetwork(IPAddress.Parse("192.168.1.37"), 24, true)]);

        targets.Should().HaveCount(253, "254 hosts minus this device");
        targets.Should().NotContain(IPAddress.Parse("192.168.1.37"));
        targets.Should().Contain(IPAddress.Parse("192.168.1.1")).And.Contain(IPAddress.Parse("192.168.1.254"));
        targets.Should().NotContain(IPAddress.Parse("192.168.1.0")).And.NotContain(IPAddress.Parse("192.168.1.255"));
    }

    [Fact]
    public void Big_ranges_are_limited_to_the_24_around_the_device_and_public_addresses_are_never_scanned()
    {
        var corporate = EcosystemService.ScanTargets([new LocalNetwork(IPAddress.Parse("10.20.30.40"), 8)]);
        corporate.Should().HaveCount(253);
        corporate.Should().OnlyContain(a => a.ToString().StartsWith("10.20.30.", StringComparison.Ordinal));

        EcosystemService.ScanTargets([new LocalNetwork(IPAddress.Parse("8.8.8.8"), 24)]).Should().BeEmpty();
        EcosystemService.ScanTargets([new LocalNetwork(IPAddress.Loopback, 8)]).Should().BeEmpty();
    }

    [Fact]
    public void Slash_22_is_scanned_whole_and_duplicates_once()
    {
        var net = new LocalNetwork(IPAddress.Parse("192.168.4.10"), 22);

        EcosystemService.ScanTargets([net, net]).Should().HaveCount(1021);
    }

    [Fact]
    public void Being_findable_is_on_after_upgrade()
    {
        var upgraded = SettingsMigrations.Upgrade(new AppSettings { SchemaVersion = 4, Ecosystem = new EcosystemSettings { Enabled = false } });

        upgraded.Ecosystem.Enabled.Should().BeTrue();
        new AppSettings().Ecosystem.Enabled.Should().BeTrue();
    }
}
