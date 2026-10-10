using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using JackiVpn.Application.Connectivity;
using JackiVpn.Application.Health;
using JackiVpn.Application.Traffic;
using JackiVpn.Application.Usage;
using JackiVpn.Application.Workplace;
using JackiVpn.Cores;
using JackiVpn.Cores.Generation;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Usage;
using JackiVpn.Subscriptions.Parsing;
using JackiVpn.Subscriptions.Parsing.Uris;
using Microsoft.Extensions.Logging.Abstractions;


namespace JackiVpn.UnitTests.Application;

public sealed class TrafficUsageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ policy & limits

    [Fact]
    public void Gaming_slows_background_pauses_updates_and_keeps_the_game_and_its_helpers_fast()
    {
        var settings = new AppSettings();
        var game = @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe";
        var limits = TrafficPolicyBuilder.Limits(settings, new TrafficContext { Mode = TrafficMode.Gaming, GamePath = game, GameFamily = ["crashhandler64.exe"] });

        limits.For(ShapeClasses.Background).DownBytesPerSecond.Should().Be(3000 * 125);
        limits.For(ShapeClasses.Update(UpdateGroup.Windows)).Block.Should().BeTrue();
        limits.For(ShapeClasses.Update(UpdateGroup.Games)).Block.Should().BeFalse("a game may need its patch to start");
        limits.For(ShapeClasses.Update(UpdateGroup.Games)).DownBytesPerSecond.Should().BeGreaterThan(0);
        limits.PriorityProcesses.Should().Contain(["cs2.exe", "steam.exe", "discord.exe", "vgc.exe", "crashhandler64.exe"]);
        limits.PriorityFolders.Should().ContainSingle().Which.Should().EndWith(@"\Counter-Strike Global Offensive\");
        TrafficShaper.IsPriority(limits, @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\steamerrorreporter64.exe").Should().BeTrue();
        TrafficShaper.IsPriority(limits, @"C:\Program Files\Google\Chrome\Application\chrome.exe").Should().BeFalse();
    }

    [Fact]
    public void Eco_blocks_telemetry_updates_and_saver_apps_normal_is_unlimited()
    {
        var settings = new AppSettings { Traffic = new TrafficControlSettings { SaverApps = ["onedrive.exe"], Apps = [new AppRule { App = "qbittorrent.exe", DownKbps = 800 }] } };
        var eco = TrafficPolicyBuilder.Limits(settings, new TrafficContext { Mode = TrafficMode.Eco });
        var normal = TrafficPolicyBuilder.Limits(settings, new TrafficContext());
        var limited = TrafficPolicyBuilder.LimitedApps(settings, null);

        eco.For(ShapeClasses.Telemetry).Block.Should().BeTrue();
        eco.For(ShapeClasses.Update(UpdateGroup.Windows)).Block.Should().BeTrue();
        eco.For(ShapeClasses.App(limited.ToList().IndexOf("onedrive.exe"))).Block.Should().BeTrue();
        normal.For(ShapeClasses.App(limited.ToList().IndexOf("onedrive.exe"))).IsUnlimited.Should().BeTrue();
        normal.For(ShapeClasses.App(limited.ToList().IndexOf("qbittorrent.exe"))).DownBytesPerSecond.Should().Be(100_000);
        normal.Classes.Where(kv => !kv.Key.StartsWith("app", StringComparison.Ordinal)).Should().OnlyContain(kv => kv.Value.IsUnlimited);
    }

    [Fact]
    public void Smart_policy_does_not_depend_on_the_mode_so_switching_never_reconnects()
    {
        var settings = new AppSettings { Traffic = new TrafficControlSettings { SaverApps = ["onedrive.exe"] } };
        var a = TrafficPolicyBuilder.Build(settings, new TrafficContext { Mode = TrafficMode.Normal });
        var b = TrafficPolicyBuilder.Build(settings, new TrafficContext { Mode = TrafficMode.Gaming, GamePath = @"C:\Games\x.exe" });
        var c = TrafficPolicyBuilder.Build(settings, new TrafficContext { Mode = TrafficMode.Eco });

        System.Text.Json.JsonSerializer.Serialize(a).Should().Be(System.Text.Json.JsonSerializer.Serialize(b)).And.Be(System.Text.Json.JsonSerializer.Serialize(c));
        a.Shaping.Should().BeTrue();
        a.ShapeBackground.Should().BeTrue();
        a.DomainClasses.Select(d => d.Class).Should().Contain([ShapeClasses.Telemetry, ShapeClasses.Update(UpdateGroup.Windows)]);
    }

    [Fact]
    public void Without_smart_control_eco_is_written_into_the_core_and_android_never_shapes_by_app()
    {
        var settings = new AppSettings { Traffic = new TrafficControlSettings { SmartControl = false, SaverApps = ["onedrive.exe"] } };
        var eco = TrafficPolicyBuilder.Build(settings, new TrafficContext { Mode = TrafficMode.Eco });
        var android = TrafficPolicyBuilder.Build(new AppSettings(), new TrafficContext { ProcessAware = false });

        eco.Shaping.Should().BeFalse();
        eco.BlockedApps.Should().Contain("onedrive.exe");
        eco.BlockedDomains.Should().Contain(["windowsupdate.com", "vortex.data.microsoft.com"]);
        android.ShapeBackground.Should().BeFalse();
        android.LimitedApps.Should().BeEmpty();
        android.TrackProcesses.Should().BeFalse();
    }

    [Fact]
    public void System_programs_are_never_cut()
    {
        var settings = new AppSettings { Traffic = new TrafficControlSettings { SaverApps = ["svchost.exe", "sing-box.exe", "x.exe"] } };
        TrafficPolicyBuilder.LimitedApps(settings, null).Should().Equal("x.exe");
    }

    // ------------------------------------------------------------------ config generation

    [Fact]
    public void Tun_config_routes_classes_to_the_relay_keeps_workplace_local_and_adds_the_system_proxy_inbound()
    {
        var profile = ShareLinkParser.Parse("vless://11111111-2222-4333-8444-555555555555@r.example.com:443?security=tls&sni=r.example.com&type=ws&path=%2Fws#x", new ParseContext());
        var settings = new AppSettings { Traffic = new TrafficControlSettings { Apps = [new AppRule { App = "OneDrive.exe", Blocked = true }] } };
        var work = new WorkplaceProfile { Name = "Acme", InternalDomains = ["acme.local"], InternalCidrs = ["10.20.0.0/16"], InternalDns = ["10.20.0.53"] };
        var policy = TrafficPolicyBuilder.Build(settings, new TrafficContext { Workplace = work });
        var plan = new CoreSessionPlan
        {
            Kind = SessionKind.Tun,
            Profiles = [profile],
            Traffic = policy,
            Shaper = new ShaperEndpoint(30001, "relay-secret", 30002, new InboundCredentials("back", "back-secret")),
            TunProxyPort = 10808,
        };

        var (config, secrets, _, ports) = SingBoxConfigBuilder.Build(plan, CapabilityMatrix());
        var json = config.ToJsonString();

        json.Should().Contain("\"tag\":\"jv-back\"").And.Contain("\"tag\":\"jv-shape-bg\"").And.Contain("\"tag\":\"jv-shape-u-windows\"").And.Contain("\"tag\":\"jv-shape-app0\"");
        json.Should().Contain("\"type\":\"logical\"").And.Contain("\"find_process\":true");
        json.Should().Contain("\"tag\":\"mixed-in\"").And.Contain("\"listen_port\":10808");
        json.Should().Contain("10.20.0.0/16").And.Contain("\"tag\":\"dns-internal\"").And.Contain("acme.local");
        json.Should().Contain("\"inbound\":[\"tun-in\",\"mixed-in\"]", "both entry points are shaped, the relay's return path is not");
        secrets.Should().Contain(["relay-secret", "back-secret"]);
        ports.Should().Contain([30002, 10808]);
    }

    [Fact]
    public void App_pattern_matches_name_case_insensitively_and_escapes_regex()
    {
        SingBoxConfigBuilder.AppPattern("OneDrive.exe").Should().Be(@"(?i)(^|[\\/])OneDrive\.exe$");
        SingBoxConfigBuilder.AppPattern(@"C:\Tools\a+b.exe").Should().Be(@"(?i)^C:\\Tools\\a\+b\.exe$");
        SingBoxConfigBuilder.AppPattern("bad\nname").Should().BeNull();
    }

    private static JackiVpn.Domain.Cores.CoreCapabilities CapabilityMatrix() => JackiVpn.UnitTests.Cores.GoldenConfigTests.SingBox114;

    /// <summary>A clock fixed at a UTC instant, local time = UTC.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    // ------------------------------------------------------------------ relay

    [Fact]
    public async Task Relay_carries_data_limits_speed_and_refuses_blocked_classes()
    {
        using var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var s = await echo.AcceptSocketAsync(Ct);
                _ = Task.Run(async () =>
                {
                    var buf = new byte[8192];
                    int n;
                    while ((n = await s.ReceiveAsync(buf, SocketFlags.None, Ct)) > 0)
                    {
                        await s.SendAsync(buf.AsMemory(0, n), SocketFlags.None, Ct);
                    }

                    s.Dispose();
                }, Ct);
            }
        }, Ct);

        // A minimal stand-in for the core's back inbound: SOCKS5 user/pass, CONNECT to the echo server.
        using var back = new TcpListener(IPAddress.Loopback, 0);
        back.Start();
        var backPort = ((IPEndPoint)back.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var s = await back.AcceptSocketAsync(Ct);
                _ = Task.Run(() => FakeSocksAsync(s, echoPort), Ct);
            }
        }, Ct);

        var relayPort = PortAllocator.GetFreeLoopbackPorts(1)[0];
        var endpoint = new ShaperEndpoint(relayPort, "pw", backPort, new InboundCredentials("b", "bp"));
        await using var shaper = new TrafficShaper(endpoint, NullLogger.Instance);
        shaper.SetLimits(new TrafficLimits
        {
            Classes = new Dictionary<string, ShapeRate> { ["slow"] = new ShapeRate(64 * 1024, 0), ["cut"] = ShapeRate.Blocked },
        });
        shaper.Start();

        (await RoundTripAsync(relayPort, "fast", "pw", 64 * 1024)).Should().BeLessThan(TimeSpan.FromSeconds(2));
        (await RoundTripAsync(relayPort, "slow", "pw", 160 * 1024)).Should().BeGreaterThan(TimeSpan.FromSeconds(1.3), "160 KB at 64 KB/s with a 16 KB burst");
        var refused = async () => await RoundTripAsync(relayPort, "cut", "pw", 1024);
        await refused.Should().ThrowAsync<IOException>();
        var wrongPassword = async () => await RoundTripAsync(relayPort, "fast", "nope", 1024);
        await wrongPassword.Should().ThrowAsync<IOException>();
    }

    private static async Task<TimeSpan> RoundTripAsync(int relayPort, string cls, string password, int bytes)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await s.ConnectAsync(new IPEndPoint(IPAddress.Loopback, relayPort), Ct);
        await s.SendAsync(new byte[] { 5, 1, 2 }, SocketFlags.None, Ct);
        await ExpectAsync(s, 2);
        var u = Encoding.ASCII.GetBytes(cls);
        var p = Encoding.ASCII.GetBytes(password);
        await s.SendAsync((byte[])[1, (byte)u.Length, .. u, (byte)p.Length, .. p], SocketFlags.None, Ct);
        var auth = await ExpectAsync(s, 2);
        if (auth[1] != 0)
        {
            throw new IOException("auth refused");
        }

        await s.SendAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, 0, 80 }, SocketFlags.None, Ct);
        var rep = await ExpectAsync(s, 10);
        if (rep[1] != 0)
        {
            throw new IOException("connect refused " + rep[1]);
        }

        var sw = Stopwatch.StartNew();
        var payload = RandomNumberGenerator.GetBytes(bytes);
        var send = s.SendAsync(payload, SocketFlags.None, Ct).AsTask();
        var got = await ExpectAsync(s, bytes);
        await send;
        got.Should().Equal(payload);
        return sw.Elapsed;
    }

    private static async Task FakeSocksAsync(Socket s, int echoPort)
    {
        using var owned = s;
        await ExpectAsync(s, 3);
        await s.SendAsync(new byte[] { 5, 2 }, SocketFlags.None, Ct);
        var ver = await ExpectAsync(s, 2);
        await ExpectAsync(s, ver[1]);
        var plen = await ExpectAsync(s, 1);
        await ExpectAsync(s, plen[0]);
        await s.SendAsync(new byte[] { 1, 0 }, SocketFlags.None, Ct);
        await ExpectAsync(s, 10);
        using var target = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await target.ConnectAsync(new IPEndPoint(IPAddress.Loopback, echoPort), Ct);
        await s.SendAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 1 }, SocketFlags.None, Ct);
        var a = Pipe(s, target);
        var b = Pipe(target, s);
        await Task.WhenAny(a, b);
    }

    private static async Task Pipe(Socket from, Socket to)
    {
        var buf = new byte[8192];
        try
        {
            int n;
            while ((n = await from.ReceiveAsync(buf, SocketFlags.None, Ct)) > 0)
            {
                await to.SendAsync(buf.AsMemory(0, n), SocketFlags.None, Ct);
            }
        }
        catch (SocketException)
        {
        }
    }

    private static async Task<byte[]> ExpectAsync(Socket s, int count)
    {
        var buf = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await s.ReceiveAsync(buf.AsMemory(read), SocketFlags.None, Ct);
            if (n == 0)
            {
                throw new IOException("closed");
            }

            read += n;
        }

        return buf;
    }

    // ------------------------------------------------------------------ usage

    [Fact]
    public void Jalali_period_and_budget_levels()
    {
        UsageBook.Period(new UsageSettings(), new DateOnly(2026, 10, 9)).Should().Be((new DateOnly(2026, 9, 23), new DateOnly(2026, 10, 22)), "Mehr 1405");
        UsageBook.Period(new UsageSettings { Cycle = UsageCycle.Gregorian }, new DateOnly(2026, 2, 10)).Should().Be((new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)));
        UsageBook.Period(new UsageSettings { Cycle = UsageCycle.Custom, CycleStart = new DateOnly(2026, 9, 15), CycleLength = 30 }, new DateOnly(2026, 10, 20))
            .Should().Be((new DateOnly(2026, 10, 15), new DateOnly(2026, 11, 13)));

        var time = new FixedTime(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        var book = new UsageBook(Path.Combine(Path.GetTempPath(), "jv-usage-" + Guid.NewGuid().ToString("N") + ".json"), time);
        lock (book.Gate)
        {
            book.AddDevice(new DateTime(2026, 10, 9, 12, 0, 0), 9L << 30, 0);
        }

        var budget = book.Budget(new UsageSettings { CapGb = 10 });
        budget.Percent.Should().BeApproximately(90, 0.01);
        budget.Level.Should().Be(UsageLevel.Serious);
        budget.Remaining.Should().Be(1L << 30);
        book.Budget(new UsageSettings { CapGb = 10, FreeFromHour = 12, FreeToHour = 13 }).Used.Should().Be(0, "free hours do not count");
    }

    [Theory]
    [InlineData("www.google.com", "google.com")]
    [InlineData("a.b.co.uk", "b.co.uk")]
    [InlineData("cdn.digikala.ac.ir", "digikala.ac.ir")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData(null, "?")]
    public void Site_names(string? host, string site) => UsageBook.SiteOf(host).Should().Be(site);

    [Fact]
    public async Task Monitor_attributes_deltas_to_apps_and_sites_and_skips_the_relay_return_leg()
    {
        var time = new FixedTime(new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero));
        var book = new UsageBook(Path.Combine(Path.GetTempPath(), "jv-usage-" + Guid.NewGuid().ToString("N") + ".json"), time);
        var round = 0;
        CoreConnections Snapshot() => new(0, 0,
        [
            new CoreConnection { Id = "a", Host = "rr1.googlevideo.com", DestinationPort = 443, ProcessPath = @"C:\X\Chrome.exe", Download = 1000 * (round + 1), Upload = 10, Outbound = "proxy", Inbound = "tun-in" },
            new CoreConnection { Id = "b", Host = "nas.local", DestinationIp = "192.168.1.5", DestinationPort = 445, ProcessPath = @"C:\X\explorer.exe", Download = 500, Outbound = "direct", Inbound = "tun-in" },
            new CoreConnection { Id = "c", Host = "dl.example.com", DestinationPort = 443, ProcessPath = @"C:\X\idm.exe", Download = 4000, Outbound = "jv-shape-bg", Inbound = "tun-in" },
            new CoreConnection { Id = "d", Host = "dl.example.com", DestinationPort = 443, Download = 4000, Outbound = "direct", Inbound = "jv-back" },
        ]);
        var monitor = new UsageMonitor(book, null, _ => Task.FromResult<CoreConnections?>(Snapshot()), () => new AppSettings(), null, NullLogger.Instance, time);

        await monitor.TickAsync(TimeSpan.FromSeconds(2), Ct);
        round = 1;
        await monitor.TickAsync(TimeSpan.FromSeconds(2), Ct);

        var apps = book.Apps(1, new UsageSettings()).ToDictionary(r => r.Key);
        apps["chrome.exe"].Down.Should().Be(2000);
        apps["chrome.exe"].TunnelDown.Should().Be(2000);
        apps["idm.exe"].Down.Should().Be(4000);
        apps["idm.exe"].TunnelDown.Should().Be(0, "its relay leg went direct");
        apps.Should().NotContainKey("?", "the relay's return leg is not a second app");
        book.Sites(1, new UsageSettings()).Select(r => r.Key).Should().Contain(["googlevideo.com", "example.com"]);
        book.DayTotals(new DateOnly(2026, 10, 9))[2].Should().Be(500, "LAN");
        monitor.Live.First().App.Should().Be("chrome.exe");
    }

    // ------------------------------------------------------------------ connectivity

    [Fact]
    public void Dns_wire_format_round_trip_drops_block_page_answers()
    {
        var q = SecureDns.BuildQuery("example.com", 1);
        var answer = new List<byte>(q);
        answer[2] = 0x81;
        answer[3] = 0x80;
        answer[7] = 2; // two answers
        void Add(byte[] ip) => answer.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, .. ip]);
        Add([10, 10, 34, 35]);
        Add([93, 184, 216, 34]);

        SecureDns.ParseAnswer(answer.ToArray(), q).Select(a => a.ToString()).Should().Equal("93.184.216.34");
        var otherId = answer.ToArray();
        otherId[0] ^= 0xFF;
        SecureDns.ParseAnswer(otherId, q).Should().BeEmpty("an answer to another query is ignored");
    }

    [Fact]
    public void Clean_ip_samples_are_cloudflare_addresses_one_per_slash24()
    {
        var sample = new CleanIpScanner().Sample(200, new Random(7));
        sample.Should().HaveCount(200);
        sample.Should().OnlyContain(ip => CleanIpScanner.IsCloudflare(ip));
        sample.Select(ip => string.Join('.', ip.GetAddressBytes().Take(3))).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Cdn_detection_and_fix_keep_the_name_for_sni_and_host()
    {
        var ws = ShareLinkParser.Parse("vless://11111111-2222-4333-8444-555555555555@cdn.example.com:443?security=tls&type=ws&path=%2Fws#x", new ParseContext());
        var reality = ShareLinkParser.Parse("vless://11111111-2222-4333-8444-555555555555@r.example.com:443?security=reality&sni=www.microsoft.com&fp=chrome&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&sid=01&type=tcp#x", new ParseContext());

        CleanIpScanner.IsCdnTransport(ws).Should().BeTrue();
        CleanIpScanner.IsCdnTransport(reality).Should().BeFalse();
        var fixedWs = ConnectionTuner.ApplyFix(ws, new EndpointFix("104.17.1.2", null, "clean", DateTimeOffset.UtcNow));
        fixedWs.Server.Should().Be("104.17.1.2");
        fixedWs.Tls.ServerName.Should().Be("cdn.example.com");
        fixedWs.Transport.Host.Should().Be("cdn.example.com");
        ConnectionTuner.ApplyFix(reality, new EndpointFix(null, "firefox", "fp", DateTimeOffset.UtcNow)).Tls.Fingerprint.Should().Be("firefox");
    }

    // ------------------------------------------------------------------ workplace

    [Fact]
    public void Company_policy_is_verified_and_pinned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var policy = """
            {"name":"Acme","expires":"2099-01-01T00:00:00Z","match":{"dnsSuffixes":["acme.local"]},
             "internal":{"domains":["acme.local"],"cidrs":["10.20.0.0/16"],"dns":["10.20.0.53"]},
             "rules":{"blockApps":["utorrent.exe"],"blockUpdates":["Windows","Games"],"dailyCapMb":2048}}
            """;
        var envelope = CompanyPolicyReader.Sign(policy, key);

        var read = CompanyPolicyReader.Read(envelope, null, DateTimeOffset.UtcNow);
        read.Profile.Name.Should().Be("Acme");
        read.Profile.InternalCidrs.Should().Equal("10.20.0.0/16");
        read.Profile.Rules.BlockUpdates.Should().Equal(UpdateGroup.Windows, UpdateGroup.Games);
        read.Profile.Rules.DailyCapMb.Should().Be(2048);

        var tampered = envelope.Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes(policy)), Convert.ToBase64String(Encoding.UTF8.GetBytes(policy.Replace("2048", "999999", StringComparison.Ordinal))), StringComparison.Ordinal);
        var bad = () => CompanyPolicyReader.Read(tampered, null, DateTimeOffset.UtcNow);
        bad.Should().Throw<CompanyPolicyException>();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var foreign = () => CompanyPolicyReader.Read(CompanyPolicyReader.Sign(policy, other), read.KeySha256, DateTimeOffset.UtcNow);
        foreign.Should().Throw<CompanyPolicyException>().WithMessage("*different key*");
    }

    [Fact]
    public void Workplace_matching_and_suggestions()
    {
        var facts = new NetworkFacts { Key = WorkplaceService.KeyOf("aa:bb", "10.0.0.1", "corp.acme.com", null), DnsSuffix = "corp.acme.com", DnsServers = ["10.0.0.53", "8.8.8.8"], LocalCidrs = ["10.0.0.0/24"] };
        WorkplaceService.LooksCorporate(facts).Should().BeTrue();
        WorkplaceService.LooksCorporate(facts with { DnsSuffix = "home" }).Should().BeFalse();
        WorkplaceService.SuggestName(facts).Should().Be("Acme");

        var profile = WorkplaceService.FromFacts(facts, "");
        profile.InternalDns.Should().Equal("10.0.0.53");
        profile.InternalDomains.Should().Equal("corp.acme.com");
        WorkplaceService.Match([profile], facts with { Key = "other" }).Should().Be(profile, "same DNS suffix");
        WorkplaceService.Match([profile], new NetworkFacts { Key = "x", DnsSuffix = "home" }).Should().BeNull();
    }
}
