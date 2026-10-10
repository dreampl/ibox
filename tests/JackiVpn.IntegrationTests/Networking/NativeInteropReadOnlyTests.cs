using System.Security.Principal;
using FluentAssertions;
using JackiVpn.Networking.Adapters;
using JackiVpn.Networking.KillSwitch;
using JackiVpn.Networking.SystemProxy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace JackiVpn.IntegrationTests.Networking;

/// <summary>
/// Exercises the real Win32 interop in <b>read-only</b> mode on the host. Nothing here changes system state
/// (the host itself runs through a system proxy). Mutating tests live in JackiVpn.E2E (VM/admin only).
/// </summary>
public sealed class NativeInteropReadOnlyTests
{
    [Fact]
    public void WinInet_query_matches_registry_view_of_current_user_settings()
    {
        var snapshot = new WinInetProxyApi().Query(null);
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        var regEnabled = key?.GetValue("ProxyEnable") is int e && e == 1;
        var regServer = key?.GetValue("ProxyServer") as string;

        snapshot.ProxyEnabled.Should().Be(regEnabled);
        if (regEnabled)
        {
            snapshot.ProxyServer.Should().Be(regServer);
        }
    }

    [Fact]
    public void Ras_enumeration_does_not_throw()
    {
        var names = new WinInetProxyApi().EnumerateRasConnections();

        names.Should().NotBeNull();
    }

    [Fact]
    public void Route_table_is_readable_and_consistent_with_adapters()
    {
        var routes = NetworkInspector.Routes();
        var adapters = NetworkInspector.Adapters();

        routes.Should().NotBeEmpty();
        routes.Should().Contain(r => r.Destination == "127.0.0.0" || r.Destination == "::1", "loopback routes always exist");
        adapters.Should().NotBeEmpty();
        var def = NetworkInspector.DefaultRouteV4();
        if (def is not null)
        {
            def.PrefixLength.Should().Be(0);
            def.InterfaceIndex.Should().BePositive();
        }
    }

    [Fact]
    public void Wfp_kill_switch_state_can_be_queried_when_elevated()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Assert.Skip("NOT RUN: querying WFP requires administrator. Covered by JackiVpn.E2E (scripts/test-e2e-admin.ps1).");
        }

        new WfpKillSwitch(NullLogger.Instance).IsEngaged().Should().BeFalse("no kill switch should be active on a test host");
    }
}
