using FluentAssertions;
using JackiVpn.Application.Sessions;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Connection;
using JackiVpn.UnitTests.Cores;
using static JackiVpn.UnitTests.TestSupport;

namespace JackiVpn.UnitTests.Application;

public class SessionPlanTests
{
    private static readonly SessionPlanFactory Plans = new(new CoreBinaryCatalog(Path.Combine(Path.GetTempPath(), "jackivpn-no-cores")));

    private static SessionRequest Request(bool externalTun) => new()
    {
        Profile = Parse(Fixture("jacki-vless-ws-tls.singbox.json")).Profiles[0],
        Mode = ConnectionMode.SystemProxy,
        Core = CorePreference.SingBox,
        ExternalTun = externalTun,
    };

    [Fact]
    public async Task Android_frontend_gets_fresh_random_credentials_per_session()
    {
        var a = await Plans.CreateAsync(Request(externalTun: true), GoldenConfigTests.SingBox114, null, CancellationToken.None);
        var b = await Plans.CreateAsync(Request(externalTun: true), GoldenConfigTests.SingBox114, null, CancellationToken.None);

        a.Frontend.InboundAuth.Should().NotBeNull("on Android every app can reach loopback ports");
        a.Frontend.InboundAuth!.Password.Should().HaveLength(48);
        b.Frontend.InboundAuth!.Password.Should().NotBe(a.Frontend.InboundAuth.Password);
        a.Frontend.InboundAuth.ToString().Should().NotContain(a.Frontend.InboundAuth.Password);
        a.Frontend.ToString().Should().NotContain(a.Frontend.InboundAuth.Password, "a plan printed into diagnostics must not reveal it");
    }

    [Fact]
    public async Task Windows_system_proxy_has_no_inbound_credentials()
    {
        var plan = await Plans.CreateAsync(Request(externalTun: false), GoldenConfigTests.SingBox114, null, CancellationToken.None);
        plan.Frontend.InboundAuth.Should().BeNull("WinINet cannot hand proxy credentials to applications");
    }
}
