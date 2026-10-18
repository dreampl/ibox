using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using JackiVpn.Agent;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Bar;
using JackiVpn.Domain.Visuals;

namespace JackiVpn.UnitTests.Agent;

/// <summary>IBAR (squircle corners, plugin rules, ibar-ctl) and the Linux privileged helper (rules, protocol).</summary>
public class IbarAndHelperTests
{
    // ------------------------------------------------------------------ squircle

    [Theory]
    [InlineData(14)]
    [InlineData(30)]
    public void A_continuous_corner_meets_both_edges_and_bends_gradually(double r)
    {
        var p = Squircle.Reach(r, Squircle.AppleSmoothing);
        var pts = Squircle.Corner(r);

        p.Should().BeApproximately(1.6 * r, 1e-9);
        pts[0].X.Should().BeApproximately(0, 1e-6);
        pts[0].Y.Should().BeApproximately(-p, 1e-6);
        pts[^1].X.Should().BeApproximately(-p, 1e-6);
        pts[^1].Y.Should().BeApproximately(0, 1e-6);

        // It runs monotonically from the side to the bottom, and bulges outwards past a plain quarter circle's chord.
        for (var i = 1; i < pts.Count; i++)
        {
            pts[i].X.Should().BeLessThanOrEqualTo(pts[i - 1].X + 1e-6);
            pts[i].Y.Should().BeGreaterThanOrEqualTo(pts[i - 1].Y - 1e-6);
        }

        var mid = pts[pts.Count / 2];
        (mid.X + mid.Y).Should().BeGreaterThan(-p, "the curve is convex towards the corner");
    }

    [Fact]
    public void No_smoothing_is_a_plain_quarter_circle()
    {
        var pts = Squircle.Corner(10, 0, 16);
        foreach (var (x, y) in pts)
        {
            Math.Sqrt(((x + 10) * (x + 10)) + ((y + 10) * (y + 10))).Should().BeApproximately(10, 0.05);
        }
    }

    // ------------------------------------------------------------------ plugins

    private static PluginManifest Alita => new()
    {
        Id = "alita",
        Name = "Alita",
        Actions =
        [
            new PluginAction { Id = "open", Title = "Open", Command = @"C:\Alita\alita.exe", Args = ["--show"] },
            new PluginAction { Id = "docs", Title = "Docs", Url = "https://example.com/alita" },
        ],
    };

    [Fact]
    public void A_good_manifest_passes_and_risky_ones_do_not()
    {
        BarPlugins.Problem(Alita).Should().BeNull();
        BarPlugins.Problem(Alita with { Id = "Alita!" }).Should().NotBeNull();
        BarPlugins.Problem(Alita with { Schema = 2 }).Should().Contain("schema");
        BarPlugins.Problem(Alita with { Actions = [new PluginAction { Id = "x1", Title = "X", Command = "cmd /c del" }] }).Should().Contain("full path");
        BarPlugins.Problem(Alita with { Actions = [new PluginAction { Id = "x1", Title = "X", Command = @"C:\a\run.bat" }] }).Should().Contain(".exe");
        BarPlugins.Problem(Alita with { Actions = [new PluginAction { Id = "x1", Title = "X", Url = "file:///C:/secret" }] }).Should().Contain("http");
        BarPlugins.Problem(Alita with { Actions = [new PluginAction { Id = "x1", Title = "X", Url = "https://a.b", Command = @"C:\a.exe" }] }).Should().Contain("either");
        BarPlugins.Problem(Alita with { Icon = "https://evil/icon.png" }).Should().Contain("icon");
    }

    [Fact]
    public void Any_change_to_the_manifest_needs_a_new_approval()
    {
        var changed = Alita with { Actions = [.. Alita.Actions, new PluginAction { Id = "more", Title = "More", Url = "https://example.com" }] };
        BarPlugins.Hash(Alita).Should().Be(BarPlugins.Hash(Alita));
        BarPlugins.Hash(changed).Should().NotBe(BarPlugins.Hash(Alita));
    }

    [Fact]
    public void Only_the_approved_plugin_with_its_token_is_heard_and_only_with_data_it_may_send()
    {
        var approved = new ApprovedPlugin(Alita, "SECRET-TOKEN", BarPlugins.Hash(Alita));
        PluginRules.Authenticate([approved], new PluginRequest { Plugin = "alita", Token = "SECRET-TOKEN" }).Should().Be(approved);
        PluginRules.Authenticate([approved], new PluginRequest { Plugin = "alita", Token = "SECRET-TOKEN-" }).Should().BeNull();
        PluginRules.Authenticate([approved], new PluginRequest { Plugin = "other", Token = "SECRET-TOKEN" }).Should().BeNull();

        PluginRules.Problem(approved, new PluginRequest { Op = "notify", Title = "Done", Link = "docs" }).Should().BeNull();
        PluginRules.Problem(approved, new PluginRequest { Op = "notify", Title = "Done", Link = "rm-rf" }).Should().Contain("declared");
        PluginRules.Problem(approved, new PluginRequest { Op = "chip", Color = "#22C55E", Value = "3" }).Should().BeNull();
        PluginRules.Problem(approved, new PluginRequest { Op = "chip", Color = "red", Value = "3" }).Should().NotBeNull();
        PluginRules.Problem(approved, new PluginRequest { Op = "panel", Widgets = [new PluginWidget { Kind = "html", Value = "<script>" }] }).Should().NotBeNull();
        PluginRules.Problem(approved, new PluginRequest { Op = "panel", Widgets = [new PluginWidget { Kind = "spark", Values = [1, 2, 3] }], Buttons = ["open"] }).Should().BeNull();
        PluginRules.Problem(approved, new PluginRequest { Op = "exec" }).Should().Contain("Unknown op");
    }

    [Fact]
    public void A_chatty_plugin_is_slowed_down()
    {
        var now = 0.0;
        var limiter = new RateLimiter(perSecond: 1, burst: 3, clock: () => now);
        Enumerable.Range(0, 3).Select(_ => limiter.Allow("a")).ToList().Should().AllBeEquivalentTo(true);
        limiter.Allow("a").Should().BeFalse();
        limiter.Allow("b").Should().BeTrue("each plugin has its own budget");
        now = 1.1;
        limiter.Allow("a").Should().BeTrue();
    }

    [Fact]
    public void Ibar_ctl_builds_the_requests_the_sdks_send()
    {
        var r = BarCtl.Build(["notify", "--plugin", "alita", "--token", "T", "--title", "Hi", "--priority", "high", "--duration", "9"], new StringReader(string.Empty))!;
        r.Op.Should().Be("notify");
        r.Plugin.Should().Be("alita");
        r.Token.Should().Be("T");
        r.Duration.Should().Be(9);
        BarCtl.Build(["chip", "--plugin", "alita", "--color", "#FFFFFF", "--value", "1"], new StringReader(string.Empty))!.Color.Should().Be("#FFFFFF");
        BarCtl.Build(["send", "-"], new StringReader("""{"v":1,"op":"clear","plugin":"alita","token":"T"}"""))!.Op.Should().Be("clear");
        BarCtl.Build(["nonsense"], new StringReader(string.Empty)).Should().BeNull();
    }

    // ------------------------------------------------------------------ Linux privileged helper

    [Fact]
    public void Root_runs_sing_box_only_with_its_own_folders_and_loopback_control()
    {
        var config = """
            {"log":{"level":"info","output":"/etc/passwd"},
             "experimental":{"cache_file":{"enabled":true,"path":"/etc/shadow"},"clash_api":{"external_controller":"127.0.0.1:9090","external_ui":"/"}},
             "route":{"rule_set":[{"tag":"ads","type":"local","path":"/home/u/.local/share/jackivpn/rules/ads.srs"}]}}
            """;
        var safe = JsonNode.Parse(HelperRules.Sanitize(config, "/run/jackivpn/1000", "/home/u/.local/share/jackivpn", p => p))!;

        safe["log"]!["output"].Should().BeNull();
        safe["experimental"]!["cache_file"]!["path"]!.GetValue<string>().Should().Be(Path.Combine("/run/jackivpn/1000", "cache.db"));
        safe["experimental"]!["clash_api"]!["external_ui"].Should().BeNull();

        FluentActions.Invoking(() => HelperRules.Sanitize("""{"experimental":{"clash_api":{"external_controller":"0.0.0.0:9090"}}}""", "/run/x", "/home/u/.local/share/jackivpn", p => p))
            .Should().Throw<HelperRejectedException>();
        FluentActions.Invoking(() => HelperRules.Sanitize("""{"route":{"rule_set":[{"path":"/home/u/.local/share/jackivpn/x.srs"}]}}""", "/run/x", "/home/u/.local/share/jackivpn", _ => "/etc/shadow"))
            .Should().Throw<HelperRejectedException>("a link out of the data folder is followed and refused");
        HelperRules.CoreArguments("/run/x").Should().Equal("run", "-c", "stdin", "-D", "/run/x", "--disable-color");
    }

    [Theory]
    [InlineData(new[] { "connection", "up", "IBOX Acme" }, true)]
    [InlineData(new[] { "connection", "down", "IBOX Acme" }, true)]
    [InlineData(new[] { "connection", "down", "Home Wi-Fi" }, false)]
    [InlineData(new[] { "connection", "modify", "Home Wi-Fi", "ipv4.dns", "1.1.1.1" }, true)]
    [InlineData(new[] { "connection", "modify", "Home Wi-Fi", "connection.permissions", "user:x" }, false)]
    [InlineData(new[] { "general", "hostname", "evil" }, false)]
    [InlineData(new[] { "connection", "up", "--ask" }, false)]
    public void Only_a_few_nmcli_calls_run_as_root(string[] args, bool allowed) =>
        (HelperRules.NmcliProblem(args) is null).Should().Be(allowed);

    [Fact]
    public async Task The_app_runs_sing_box_through_the_helper_and_it_stops_when_the_app_goes()
    {
        var toHelper = new Pipe();
        var fromHelper = new Pipe();
        var fake = new FakeCore();
        string? launchedExe = null;
        IReadOnlyList<string>? launchedArgs = null;
        var server = new HelperServer("/opt/jackivpn/cores/sing-box", "/run/jackivpn/1000", "/home/u/.local/share/jackivpn", (exe, args, _) =>
        {
            launchedExe = exe;
            launchedArgs = args;
            return fake;
        }, args => Task.FromResult((0, "ok")));
        var serving = server.RunAsync(new StreamReader(toHelper.Reader.AsStream()), new StreamWriter(fromHelper.Writer.AsStream(), new UTF8Encoding(false)) { AutoFlush = true }, CancellationToken.None);

        using var client = new HelperClient(new StreamReader(fromHelper.Reader.AsStream()), new StreamWriter(toHelper.Writer.AsStream(), new UTF8Encoding(false)) { AutoFlush = true });
        var core = client.Core();
        var lines = new List<string?>();
        core.Line += lines.Add;
        await core.StartAsync("""{"log":{"output":"/tmp/x"}}""", CancellationToken.None);

        launchedExe.Should().Be("/opt/jackivpn/cores/sing-box", "the app cannot choose what runs as root");
        launchedArgs.Should().Equal(HelperRules.CoreArguments("/run/jackivpn/1000"));
        fake.Config.Should().NotContain("/tmp/x");
        core.Id.Should().Be(4242);
        fake.Say("sing-box started");
        await WaitUntil(() => lines.Contains("sing-box started"));

        (await client.NmcliAsync(["general", "hostname", "evil"], CancellationToken.None)).Code.Should().Be(-1);
        (await client.NmcliAsync(["connection", "up", "IBOX Acme"], CancellationToken.None)).Should().Be((0, "ok"));

        // The app closes its end: the helper ends and takes the core with it.
        await toHelper.Writer.CompleteAsync();
        await serving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        fake.Killed.Should().BeTrue();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        condition().Should().BeTrue();
    }

    private sealed class FakeCore : IHostedProcess
    {
        public int Id => 4242;

        public event Action<string?>? Line;

        public event Action? Exited;

        public int ExitCode => 0;

        public string Config { get; private set; } = string.Empty;

        public bool Killed { get; private set; }

        public Task StartAsync(string config, CancellationToken ct)
        {
            Config = config;
            return Task.CompletedTask;
        }

        public void Say(string line) => Line?.Invoke(line);

        public void Kill()
        {
            Killed = true;
            Exited?.Invoke();
        }

        public void Dispose()
        {
        }
    }
}
