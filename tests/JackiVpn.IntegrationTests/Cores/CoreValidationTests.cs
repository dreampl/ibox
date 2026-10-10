using System.Security.Cryptography;
using FluentAssertions;
using JackiVpn.Cores;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;
using JackiVpn.IntegrationTests.Infrastructure;
using JackiVpn.Subscriptions.Parsing;
using JackiVpn.Subscriptions.Parsing.Uris;

namespace JackiVpn.IntegrationTests.Cores;

/// <summary>Every config the generators can emit must be accepted by the real installed core binaries.</summary>
public sealed class CoreValidationTests(CoreFixture cores) : IClassFixture<CoreFixture>
{
    private const string U = "11111111-2222-4333-8444-555555555555";
    private static readonly string Pbk = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static readonly string WgPriv = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string WgPub = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> SingBoxLinks => new()
    {
        { "vless-ws-tls-utls", $"vless://{U}@104.16.1.1:443?security=tls&sni=a.example.com&fp=firefox&alpn=http%2F1.1&type=ws&host=a.example.com&path=%2Fws%3Fed%3D2048&fragment=1#x" },
        { "vless-reality-vision", $"vless://{U}@r.example.com:443?security=reality&sni=www.microsoft.com&fp=chrome&pbk={Pbk}&sid=0123abcd&type=tcp&flow=xtls-rprx-vision#x" },
        { "vless-grpc", $"vless://{U}@g.example.com:443?security=tls&type=grpc&serviceName=svc#x" },
        { "vless-httpupgrade", $"vless://{U}@h.example.com:443?security=tls&type=httpupgrade&host=h.example.com&path=%2Fup#x" },
        { "vless-h2", $"vless://{U}@h2.example.com:443?security=tls&type=h2&host=h2.example.com&path=%2Fh2#x" },
        { "vmess-ws-tls", "vmess://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($$"""{"v":"2","ps":"vm","add":"vm.example.com","port":"443","id":"{{U}}","aid":"0","scy":"auto","net":"ws","host":"vm.example.com","path":"/vm","tls":"tls","sni":"vm.example.com"}""")) },
        { "trojan-grpc", "trojan://pass@t.example.com:443?sni=t.example.com&type=grpc&serviceName=tg#x" },
        { "ss-aead", "ss://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("aes-256-gcm:pw")) + "@s.example.com:8388#x" },
        { "ss-obfs-plugin", "ss://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("chacha20-ietf-poly1305:pw")) + "@s.example.com:8388/?plugin=obfs-local%3Bobfs%3Dhttp%3Bobfs-host%3Dbing.com#x" },
        { "hysteria2", "hysteria2://pw@hy.example.com:443?sni=hy.example.com&obfs=salamander&obfs-password=ob&mport=20000-21000#x" },
        { "tuic", $"tuic://{U}:pw@tu.example.com:443?congestion_control=bbr&udp_relay_mode=native&alpn=h3&sni=tu.example.com#x" },
        { "wireguard", $"wireguard://{Uri.EscapeDataString(WgPriv)}@162.159.192.1:2408?publickey={Uri.EscapeDataString(WgPub)}&address=172.16.0.2%2F32&reserved=1,2,3&mtu=1280#x" },
        { "socks", "socks5://user:pw@127.0.0.1:1080#x" },
        { "http", "http://user:pw@127.0.0.1:8080#x" },
    };

    public static TheoryData<string, string> XrayLinks => new()
    {
        { "vless-ws-tls-fragment", $"vless://{U}@104.16.1.1:443?security=tls&sni=a.example.com&fp=firefox&alpn=http%2F1.1&type=ws&host=a.example.com&path=%2Fws&fragment=tlshello,100-200,10-20#x" },
        { "vless-reality-vision", $"vless://{U}@r.example.com:443?security=reality&sni=www.microsoft.com&fp=chrome&pbk={Pbk}&sid=0123abcd&type=tcp&flow=xtls-rprx-vision#x" },
        { "vless-xhttp", $"vless://{U}@x.example.com:443?security=tls&type=xhttp&path=%2Fx&mode=auto&host=x.example.com#x" },
        { "vless-kcp", $"vless://{U}@k.example.com:443?type=kcp#x" },
        { "vless-grpc-tls", $"vless://{U}@p.example.com:443?security=tls&sni=p.example.com&type=grpc&serviceName=s#x" },
        { "vmess-tcp-http-header", $"vmess://{U}@v.example.com:80?type=tcp&headerType=http&host=www.example.com&path=%2F#x" },
        { "trojan-grpc", "trojan://pass@t.example.com:443?sni=t.example.com&type=grpc&serviceName=tg#x" },
        { "ss", "ss://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("aes-256-gcm:pw")) + "@s.example.com:8388#x" },
        { "socks", "socks5://user:pw@127.0.0.1:1080#x" },
        { "wireguard", $"wireguard://{Uri.EscapeDataString(WgPriv)}@162.159.192.1:2408?publickey={Uri.EscapeDataString(WgPub)}&address=172.16.0.2%2F32&mtu=1280#x" },
    };

    [Theory]
    [MemberData(nameof(SingBoxLinks))]
    public async Task SingBox_accepts_generated_proxy_config(string name, string link)
    {
        cores.RequireCores();
        var profile = ShareLinkParser.Parse(link, new ParseContext());
        var config = cores.SingBox.GenerateConfig(new CoreSessionPlan { Kind = SessionKind.SystemProxy, Profiles = [profile], ListenPort = 20801 }, cores.SingBoxCaps!);

        var result = await cores.SingBox.ValidateAsync(config, Ct);

        result.IsValid.Should().BeTrue($"{name}: {result.RedactedOutput}");
    }

    [Theory]
    [MemberData(nameof(XrayLinks))]
    public async Task Xray_accepts_generated_backend_config(string name, string link)
    {
        cores.RequireCores();
        var profile = ShareLinkParser.Parse(link, new ParseContext());
        var config = cores.Xray.GenerateConfig(new CoreSessionPlan { Kind = SessionKind.ChainBackend, Profiles = [profile], ListenPort = 20901 }, cores.XrayCaps!);

        var result = await cores.Xray.ValidateAsync(config, Ct);

        result.IsValid.Should().BeTrue($"{name}: {result.RedactedOutput}");
    }

    [Fact]
    public async Task SingBox_accepts_tun_config_with_rule_sets_dns_variants_and_chain()
    {
        cores.RequireCores();
        var ruleSetPath = Path.Combine(cores.WorkDir, "rs-ir.json");
        await File.WriteAllTextAsync(ruleSetPath, """{"version":2,"rules":[{"domain_suffix":["ir"]}]}""", Ct);
        var profile = CoreFixture.ReferenceProfile();
        var rules = new[]
        {
            new RoutingRule { Id = "ir", Name = "ir", Action = RuleAction.Direct, Conditions = [new RuleCondition(RuleConditionType.RuleSet, ["rs-ir"]), new RuleCondition(RuleConditionType.DomainSuffix, ["ir"])] },
            new RoutingRule { Id = "tg", Name = "tg", Action = RuleAction.Proxy, Conditions = [new RuleCondition(RuleConditionType.ProcessName, ["Telegram.exe"])] },
            new RoutingRule { Id = "bt", Name = "bt", Action = RuleAction.Direct, Conditions = [new RuleCondition(RuleConditionType.Protocol, ["bittorrent"]), new RuleCondition(RuleConditionType.PortRange, ["6881:6889"])] },
            new RoutingRule { Id = "ads", Name = "ads", Action = RuleAction.Block, Conditions = [new RuleCondition(RuleConditionType.DomainKeyword, ["doubleclick"])] },
        };

        foreach (var dns in new[]
        {
            new DnsSettings(),
            new DnsSettings { Mode = DnsMode.DoT, Remote = DnsServerEndpoint.CloudflareDot },
            new DnsSettings { Mode = DnsMode.DoH, Remote = new DnsServerEndpoint { Transport = DnsTransport.Https, Server = "dns.google", Path = "/dns-query" } },
            new DnsSettings { Mode = DnsMode.RemoteThroughVpn, Remote = new DnsServerEndpoint { Transport = DnsTransport.Udp, Server = "8.8.8.8" } },
            new DnsSettings { Mode = DnsMode.System },
            new DnsSettings { FakeIpEnabled = true },
        })
        {
            foreach (var ipv6 in new[] { true, false })
            {
                var plan = new CoreSessionPlan
                {
                    Kind = SessionKind.Tun,
                    Profiles = [profile],
                    Dns = dns,
                    Ipv6 = ipv6 ? Ipv6Mode.Enable : Ipv6Mode.Disable,
                    Routing = new RoutingSettings { Rules = rules, RuleSets = [] },
                    RuleSetPaths = new Dictionary<string, string> { ["rs-ir"] = ruleSetPath },
                    DirectProcessPaths = [cores.Catalog.PathOf(CoreKind.SingBox)],
                    ClashApi = new ClashApiOptions(20999, "secret"),
                };
                var result = await cores.SingBox.ValidateAsync(cores.SingBox.GenerateConfig(plan, cores.SingBoxCaps!), Ct);
                result.IsValid.Should().BeTrue($"dns={dns.Mode} fakeip={dns.FakeIpEnabled} ipv6={ipv6}: {result.RedactedOutput}");
            }
        }

        var chain = new CoreSessionPlan
        {
            Kind = SessionKind.Tun,
            Profiles = [profile],
            ChainBackend = new ChainBackendEndpoint(21001, cores.Catalog.PathOf(CoreKind.Xray)),
            Tun = new TunOptions { RouteExcludeAddresses = ["104.16.123.45/32"] },
        };
        var chainResult = await cores.SingBox.ValidateAsync(cores.SingBox.GenerateConfig(chain, cores.SingBoxCaps!), Ct);
        chainResult.IsValid.Should().BeTrue(chainResult.RedactedOutput);
    }

    [Fact]
    public async Task SingBox_accepts_every_performance_variant()
    {
        cores.RequireCores();
        var original = CoreFixture.ReferenceProfile();
        var profiles = new[]
        {
            original,
            Application.Health.ProfileTuner.ApplyFragment(original, Application.Health.FragmentVariant.RecordOnly),
            Application.Health.ProfileTuner.ApplyStatic(original with { Tls = original.Tls with { Fingerprint = null, Alpn = ["h2", "http/1.1"] } }, new PerformanceSettings()),
        };
        foreach (var profile in profiles)
        {
            foreach (var stack in Enum.GetValues<TunStack>())
            {
                foreach (var final in new[] { RuleActionType.Proxy, RuleActionType.Direct })
                {
                    var plan = new CoreSessionPlan
                    {
                        Kind = SessionKind.Tun,
                        Profiles = [profile],
                        TunStack = stack,
                        BlockQuic = true,
                        Dns = new DnsSettings(),
                        Routing = new RoutingSettings { Final = final },
                        DirectProcessPaths = [cores.Catalog.PathOf(CoreKind.SingBox)],
                        ClashApi = new ClashApiOptions(20998, "secret"),
                    };
                    var result = await cores.SingBox.ValidateAsync(cores.SingBox.GenerateConfig(plan, cores.SingBoxCaps!), Ct);
                    result.IsValid.Should().BeTrue($"stack={stack} final={final} fragment={profile.Tls.Fragment?.Enabled}: {result.RedactedOutput}");
                }
            }
        }
    }

    [Fact]
    public async Task Probe_batch_configs_are_accepted_by_both_cores()
    {
        cores.RequireCores();
        var profiles = SingBoxLinks.Select(r => ShareLinkParser.Parse(r.Data.Item2, new ParseContext())).ToList();
        var ports = Enumerable.Range(22000, profiles.Count).ToList();
        var sb = cores.SingBox.GenerateConfig(new CoreSessionPlan { Kind = SessionKind.ProbeBatch, Profiles = profiles, ProbePorts = ports }, cores.SingBoxCaps!);
        (await cores.SingBox.ValidateAsync(sb, Ct)).IsValid.Should().BeTrue();

        var xrayProfiles = XrayLinks.Select(r => ShareLinkParser.Parse(r.Data.Item2, new ParseContext())).ToList();
        var xr = cores.Xray.GenerateConfig(new CoreSessionPlan { Kind = SessionKind.ProbeBatch, Profiles = xrayProfiles, ProbePorts = Enumerable.Range(23000, xrayProfiles.Count).ToList() }, cores.XrayCaps!);
        var xrResult = await cores.Xray.ValidateAsync(xr, Ct);
        xrResult.IsValid.Should().BeTrue(xrResult.RedactedOutput);
    }

    [Fact]
    public async Task Invalid_configs_are_rejected_with_parsed_redacted_errors()
    {
        cores.RequireCores();
        const string secret = "super-secret-password-123";
        var bad = new GeneratedCoreConfig
        {
            Kind = CoreKind.SingBox,
            Session = SessionKind.SystemProxy,
            Json = $$"""{"outbounds":[{"type":"trojan","tag":"p","server":"a.example.com","server_port":"not-a-number","password":"{{secret}}"}]}""",
            Report = new Domain.Cores.CapabilityReport(CoreKind.SingBox, []),
            Secrets = [secret],
        };
        var sb = await cores.SingBox.ValidateAsync(bad, Ct);
        sb.IsValid.Should().BeFalse();
        sb.Errors.Should().NotBeEmpty();
        sb.RedactedOutput.Should().NotContain(secret);

        var badXray = bad with { Kind = CoreKind.Xray, Json = """{"outbounds":[{"protocol":"vless","settings":{"vnext":"broken"}}]}""" };
        var xr = await cores.Xray.ValidateAsync(badXray, Ct);
        xr.IsValid.Should().BeFalse();
        xr.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Version_and_capabilities_come_from_the_real_binaries()
    {
        cores.RequireCores();

        cores.SingBoxCaps!.Version.Version.Should().BeGreaterThanOrEqualTo(new Version(1, 12, 0));
        cores.SingBoxCaps.SupportsTun.Should().BeTrue();
        cores.SingBoxCaps.SupportsTlsFragment.Should().BeTrue();
        cores.SingBoxCaps.SupportsReality.Should().BeTrue();
        cores.SingBoxCaps.SupportsHysteria2.Should().BeTrue();
        cores.XrayCaps!.SupportsTun.Should().BeFalse();
        cores.XrayCaps.SupportsVless.Should().BeTrue();

        var entry = cores.Catalog.ManifestEntry(CoreKind.SingBox)!;
        entry.Version.Should().Be(cores.SingBoxCaps.Version.DisplayVersion);
        using var verified = cores.Catalog.Verify(CoreKind.SingBox);
        verified.Sha256.Should().HaveLength(64);
        await Task.CompletedTask;
    }
}
