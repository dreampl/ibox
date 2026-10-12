using FluentAssertions;
using JackiVpn.Application.Connectivity;
using JackiVpn.Domain.Settings;

namespace JackiVpn.UnitTests.Application;

public class DnsChoiceTests
{
    private static readonly DnsServerEndpoint Best = new() { Transport = DnsTransport.Https, Server = "1.1.1.1", Path = "/dns-query" };

    [Fact]
    public void Default_is_smart_and_uses_the_networks_best_for_server_names_only()
    {
        var s = new AppSettings();
        DnsChoice.Selection(s).Should().Be(DnsSelection.Smart);

        var session = DnsChoice.ForSession(s, Best);
        session.Dns.Bootstrap.Should().Be(Best);
        session.Dns.Direct.Transport.Should().Be(DnsTransport.Local, "direct sites keep the network's DNS unless asked");
        DnsChoice.ForSession(s, null).Dns.Bootstrap.Transport.Should().Be(DnsTransport.Local, "no search result yet: the network's own");
    }

    [Fact]
    public void Network_choice_ignores_the_search()
    {
        var s = DnsChoice.Select(new AppSettings(), DnsSelection.System);
        DnsChoice.Selection(s).Should().Be(DnsSelection.System);
        DnsChoice.ForSession(s, Best).Dns.Bootstrap.Transport.Should().Be(DnsTransport.Local);
    }

    [Fact]
    public void Manual_pick_wins_over_the_search_and_can_cover_direct_sites()
    {
        var shecan = new DnsServerEndpoint { Transport = DnsTransport.Udp, Server = "178.22.122.100" };
        var s = DnsChoice.Use(new AppSettings(), shecan);
        DnsChoice.Selection(s).Should().Be(DnsSelection.Manual);
        DnsChoice.ForSession(s, Best).Dns.Bootstrap.Should().Be(shecan);

        s = s with { Dns = s.Dns with { UseChosenForDirect = true } };
        DnsChoice.ForSession(s, Best).Dns.Direct.Should().Be(shecan);
    }

    [Fact]
    public void Direct_resolver_set_by_hand_is_not_overridden()
    {
        var own = new DnsServerEndpoint { Transport = DnsTransport.Udp, Server = "192.168.1.1" };
        var s = new AppSettings();
        s = s with { Dns = s.Dns with { Direct = own, UseChosenForDirect = true } };
        DnsChoice.ForSession(s, Best).Dns.Direct.Should().Be(own);
    }

    [Theory]
    [InlineData("Office", "10.0.0.53", false, null, true)]
    [InlineData("Google v6", "2001:4860:4860::8888", false, null, true)]
    [InlineData("Google DoH", "8.8.8.8", true, "dns.google", true)]
    [InlineData("Bad", "dns.google", false, null, false)]
    [InlineData("Bad", "1", false, null, false)]
    [InlineData("", "1.1.1.1", false, null, false)]
    [InlineData("Bad host", "8.8.8.8", true, "not a host", false)]
    public void Own_resolvers_are_checked(string name, string address, bool doh, string? host, bool ok)
    {
        var r = new CustomDnsResolver { Name = name, Address = address, Transport = doh ? DnsTransport.Https : DnsTransport.Udp, Host = host };
        (r.Problem() is null).Should().Be(ok);
    }

    [Fact]
    public void Own_resolvers_are_added_by_name_and_removing_the_one_in_use_goes_back_to_smart()
    {
        var r = new CustomDnsResolver { Name = "Office", Address = "10.0.0.53" };
        var (s, problem) = DnsChoice.AddCustom(new AppSettings(), r);
        problem.Should().BeNull();
        (s, _) = DnsChoice.AddCustom(s, r with { Address = "10.0.0.54" });
        s.Dns.Custom.Should().ContainSingle().Which.Address.Should().Be("10.0.0.54");

        s = DnsChoice.Use(s, s.Dns.Custom[0].ToEndpoint());
        s = DnsChoice.RemoveCustom(s, "office");
        s.Dns.Custom.Should().BeEmpty();
        DnsChoice.Selection(s).Should().Be(DnsSelection.Smart);
    }

    [Fact]
    public void Own_doh_resolver_keeps_its_host_name_for_tls()
    {
        var c = DnsCandidate.From(new CustomDnsResolver { Name = "G", Address = "8.8.8.8", Transport = DnsTransport.Https, Host = "dns.google" });
        c.Custom.Should().BeTrue();
        c.ToEndpoint().Should().Be(new DnsServerEndpoint { Transport = DnsTransport.Https, Server = "8.8.8.8", Path = "/dns-query", TlsServerName = "dns.google" });
    }
}
