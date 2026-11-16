using System.Text.Json.Nodes;
using FluentAssertions;
using JackiVpn.Application.Sessions;
using JackiVpn.Cores;
using JackiVpn.Cores.Generation;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using JackiVpn.Subscriptions.Parsing;
using JackiVpn.Subscriptions.Parsing.Uris;
using JackiVpn.UnitTests.Cores;
using static JackiVpn.UnitTests.TestSupport;

namespace JackiVpn.UnitTests.Parsing;

public class DnsTunnelTests
{
    private const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string LinkFor(string resolver = "178.22.122.100") =>
        ShareLinkParser.FormatDnstt("t.example.test", Key.ToUpperInvariant(), resolver, "alice", "p@ss:w/rd", "Test NL");

    [Fact]
    public void Dnstt_link_becomes_a_tcp_only_dns_tunnel_profile()
    {
        var p = Link($"dnstt://alice:secret@t.example.test?pubkey={Key}&resolver=178.22.122.100:53#Test%20NL");

        p.Protocol.Should().Be(ProxyProtocol.Dnstt);
        p.Server.Should().Be("t.example.test");
        p.Credentials.Username.Should().Be("alice");
        p.Credentials.Password.Should().Be("secret");
        p.Dnstt!.PublicKey.Should().Be(Key);
        p.Dnstt.Resolver.Should().Be("178.22.122.100:53");
        p.Udp.Should().BeFalse();
        p.Name.Should().Be("Test NL");
        ProfileValidator.Validate(p).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Formatted_link_round_trips_special_characters_and_doh_resolvers()
    {
        var p = Link(LinkFor("https://dns.example.test/dns-query?x=1"));

        p.Credentials.Password.Should().Be("p@ss:w/rd");
        p.Dnstt!.PublicKey.Should().Be(Key, "keys are stored lower-case");
        p.Dnstt.Resolver.Should().Be("https://dns.example.test/dns-query?x=1");
        p.Name.Should().Be("Test NL");
        ProfileValidator.Validate(p).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Link_without_key_or_resolver_is_rejected()
    {
        var noKey = () => Link("dnstt://t.example.test?resolver=1.1.1.1");
        var noResolver = () => Link($"dnstt://t.example.test?pubkey={Key}");
        noKey.Should().Throw<ProfileParseException>();
        noResolver.Should().Throw<ProfileParseException>();
    }

    [Theory]
    [InlineData("1.1.1.1", DnsttResolverKind.Udp, "1.1.1.1:53")]
    [InlineData("udp://8.8.8.8:5353", DnsttResolverKind.Udp, "8.8.8.8:5353")]
    [InlineData("[2001:db8::1]", DnsttResolverKind.Udp, "[2001:db8::1]:53")]
    [InlineData("tls://dns.example.test", DnsttResolverKind.Dot, "dns.example.test:853")]
    [InlineData("https://dns.example.test/dns-query", DnsttResolverKind.Doh, "https://dns.example.test/dns-query")]
    public void Resolver_forms(string text, DnsttResolverKind kind, string value)
    {
        var r = DnsttResolver.TryParse(text);
        r.Should().NotBeNull();
        r!.Kind.Should().Be(kind);
        r.Value.Should().Be(value);
    }

    [Theory]
    [InlineData("dns.example.test")]
    [InlineData("1.1.1.1:99999")]
    [InlineData("")]
    [InlineData("1.1.1.1\" -evil")]
    public void Bad_resolvers_are_refused(string text) => DnsttResolver.TryParse(text).Should().BeNull();

    [Fact]
    public void Auto_tries_the_carrier_dns_first_then_public_resolvers_then_doh()
    {
        var p = Link(LinkFor("auto"));
        ProfileValidator.Validate(p).IsValid.Should().BeTrue();

        DnsTunnel.Candidates(p, () => ["10.20.30.1"]).Should().Equal(
            "10.20.30.1", "8.8.8.8", "1.1.1.1", "178.22.122.100", "https://dns.google/dns-query", "https://cloudflare-dns.com/dns-query");
        DnsTunnel.Candidates(p, () => []).Should().StartWith("8.8.8.8", "a network without DNS servers just skips that entry");
    }

    [Fact]
    public void A_resolver_list_keeps_its_order_and_rejects_any_bad_entry()
    {
        DnsttResolver.ParseList("system, 1.1.1.1 ,tls://dns.example.test").Should().Equal("system", "1.1.1.1", "tls://dns.example.test");
        DnsttResolver.ParseList("1.1.1.1, 1.1.1.1").Should().Equal("1.1.1.1");
        DnsttResolver.ParseList("1.1.1.1, resolver.example.test").Should().BeNull();
        DnsttResolver.ParseList(" ").Should().BeNull();
    }

    [Fact]
    public void Validator_wants_a_domain_and_a_64_hex_key()
    {
        var good = Link(LinkFor());
        ProfileValidator.Validate(good with { Server = "193.151.151.14" }).IsValid.Should().BeFalse();
        ProfileValidator.Validate(good with { Dnstt = good.Dnstt! with { PublicKey = "abcd" } }).IsValid.Should().BeFalse();
        ProfileValidator.Validate(good with { Dnstt = good.Dnstt! with { Resolver = "resolver.example.test" } }).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("178.22.122.100", "-udp", "178.22.122.100:53", "178.22.122.100")]
    [InlineData("https://dns.example.test/dns-query", "-doh", "https://dns.example.test/dns-query", null)]
    [InlineData("tls://9.9.9.9", "-dot", "9.9.9.9:853", "9.9.9.9")]
    public void Client_arguments(string resolver, string mode, string value, string? resolverAddress)
    {
        var catalog = new CoreBinaryCatalog(Path.Combine(Path.GetTempPath(), "jackivpn-no-cores"));
        var e = DnsTunnel.Plan(catalog, Link(LinkFor(resolver)), 40123);

        e.Arguments.Should().Equal(mode, value, "-pubkey", Key, "t.example.test", "127.0.0.1:40123");
        e.ResolverAddress.Should().Be(resolverAddress);
        e.ExecutablePath.Should().EndWith(CoreBinaryCatalog.ExecutableName(CoreKind.Dnstt));
        string.Join(' ', e.Arguments).Should().NotContain("p@ss", "the SOCKS password never goes on a command line");
    }

    [Fact]
    public async Task Session_runs_the_client_and_hands_sing_box_a_tcp_only_socks_server()
    {
        using var cores = new FakeCores();
        var plans = new SessionPlanFactory(cores.Catalog);
        var request = new SessionRequest
        {
            Profile = Link(LinkFor()),
            Mode = ConnectionMode.Tun,
            Core = CorePreference.Auto,
            Dns = new DnsSettings { Remote = new DnsServerEndpoint { Transport = DnsTransport.Udp, Server = "1.1.1.1" } },
        };

        var plan = await plans.CreateAsync(request, GoldenConfigTests.SingBox114, null, CancellationToken.None);

        plan.DnsTunnel.Should().NotBeNull();
        plan.Chained.Should().BeFalse();
        var front = plan.Frontend.Profiles[0];
        front.Protocol.Should().Be(ProxyProtocol.Socks);
        front.Server.Should().Be("127.0.0.1");
        front.Port.Should().Be(plan.DnsTunnel!.Port);
        front.Credentials.Password.Should().Be("p@ss:w/rd");
        plan.Frontend.DirectProcessPaths.Should().Contain(plan.DnsTunnel.ExecutablePath, "the client's queries must not loop into the tunnel");
        plan.Frontend.Tun.RouteExcludeAddresses.Should().Contain("178.22.122.100/32");
        plan.Frontend.Dns.Remote.Transport.Should().Be(DnsTransport.Tcp, "the tunnel carries no UDP");

        var config = SingBoxConfigBuilder.Build(plan.Frontend, GoldenConfigTests.SingBox114).Config;
        var proxy = config["outbounds"]!.AsArray().OfType<JsonObject>().Single(o => (string?)o["tag"] == SingBoxConfigBuilder.ProxyTag);
        ((string?)proxy["type"]).Should().Be("socks");
        ((string?)proxy["network"]).Should().Be("tcp");
    }

    [Fact]
    public async Task Without_the_client_the_session_says_so()
    {
        var plans = new SessionPlanFactory(new CoreBinaryCatalog(Path.Combine(Path.GetTempPath(), "jackivpn-no-cores")));
        var request = new SessionRequest { Profile = Link(LinkFor()), Mode = ConnectionMode.SystemProxy };

        var act = () => plans.CreateAsync(request, GoldenConfigTests.SingBox114, null, CancellationToken.None);

        await act.Should().ThrowAsync<SessionPlanException>().WithMessage("*dnstt client*");
    }

    /// <summary>A cores folder whose manifest lists the dnstt client (an empty file; nothing is run).</summary>
    private sealed class FakeCores : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "jackivpn-dnstt-" + Guid.NewGuid().ToString("N"));

        public FakeCores()
        {
            Directory.CreateDirectory(_dir);
            var exe = CoreBinaryCatalog.ExecutableName(CoreKind.Dnstt);
            File.WriteAllBytes(Path.Combine(_dir, exe), []);
            File.WriteAllText(Path.Combine(_dir, "manifest.json"),
                new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["cores"] = new JsonObject
                    {
                        ["dnstt"] = new JsonObject
                        {
                            ["version"] = "1.20260501.0",
                            ["files"] = new JsonArray(new JsonObject
                            {
                                ["path"] = exe,
                                ["sha256"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                                ["size"] = 0,
                            }),
                        },
                    },
                }.ToJsonString());
            Catalog = new CoreBinaryCatalog(_dir);
        }

        public CoreBinaryCatalog Catalog { get; }

        public void Dispose() => Directory.Delete(_dir, recursive: true);
    }
}
