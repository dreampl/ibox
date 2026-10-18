using System.Text.Json.Nodes;
using FluentAssertions;
using JackiVpn.Agent;
using JackiVpn.Domain.Activity;
using JackiVpn.Domain.Agent;
using JackiVpn.Domain.Bar;

namespace JackiVpn.UnitTests.Agent;

/// <summary>The activity bus, the plugin / bridge activity rules, the browser bridge and chat's CLI handling.</summary>
public class ActivityTests
{
    private static ActivityItem Item(string id = "a", double? progress = null, ProgressAccuracy accuracy = ProgressAccuracy.Unknown, ActivityState state = ActivityState.Running) =>
        new() { Id = id, Source = "chrome", Kind = "download", Title = "file.iso", Progress = progress, Accuracy = accuracy, State = state };

    // ------------------------------------------------------------------ the bus

    [Fact]
    public void A_number_without_a_stated_accuracy_is_never_shown_as_a_number()
    {
        ActivityBus.Normalize(Item(progress: 0.5)).Progress.Should().BeNull();
        ActivityBus.Normalize(Item(progress: 0.5, accuracy: ProgressAccuracy.Estimate)).Progress.Should().Be(0.5);
        ActivityBus.Normalize(Item(progress: 7, accuracy: ProgressAccuracy.Exact)).Progress.Should().Be(1);
        ActivityBus.Normalize(Item(progress: double.NaN, accuracy: ProgressAccuracy.Exact)).Should().Match<ActivityItem>(i => i.Progress == null && i.Accuracy == ProgressAccuracy.Unknown);
    }

    [Fact]
    public void Byte_counts_make_a_progress_only_with_a_known_total()
    {
        ActivityBus.Normalize(Item(accuracy: ProgressAccuracy.Exact) with { Done = 25, Total = 100 }).Progress.Should().Be(0.25);
        ActivityBus.Normalize(Item(accuracy: ProgressAccuracy.Exact) with { Done = 25 }).Progress.Should().BeNull();
        ActivityBus.Normalize(Item() with { Done = 25, Total = 100 }).Progress.Should().BeNull("an unknown accuracy stays unknown");
        ActivityBus.Normalize(Item() with { Done = -3, Total = 0, Rate = -1 }).Should().Match<ActivityItem>(i => i.Done == null && i.Total == null && i.Rate == null);
    }

    [Fact]
    public void Starting_updating_and_ending_are_told_apart_and_finished_tasks_leave()
    {
        var now = DateTimeOffset.UnixEpoch;
        var bus = new ActivityBus(() => now);
        var seen = new List<ActivityChange>();
        bus.Changed += (_, c) => seen.Add(c.Change);

        bus.Report(Item());
        bus.Report(Item(progress: 0.3, accuracy: ProgressAccuracy.Exact));
        bus.End("chrome", "a").Should().Match<ActivityItem>(i => i.State == ActivityState.Done && i.Progress == 1);
        seen.Should().Equal(ActivityChange.Started, ActivityChange.Updated, ActivityChange.Ended);

        bus.Sweep().Should().Be(ActivityBus.Linger);
        now += ActivityBus.Linger;
        bus.Sweep().Should().BeNull();
        bus.Snapshot().Should().BeEmpty();
        seen.Last().Should().Be(ActivityChange.Removed);
    }

    [Fact]
    public void A_source_that_goes_quiet_is_cleared_and_the_list_has_a_limit()
    {
        var now = DateTimeOffset.UnixEpoch;
        var bus = new ActivityBus(() => now);
        for (var i = 0; i < ActivityBus.MaxItems + 3; i++)
        {
            bus.Report(Item("t" + i));
            now += TimeSpan.FromSeconds(1);
        }

        bus.Snapshot().Should().HaveCount(ActivityBus.MaxItems);
        bus.Snapshot().Select(i => i.Id).Should().NotContain("t0", "the oldest go first");

        now += ActivityBus.Stale;
        bus.Sweep();
        bus.Snapshot().Should().BeEmpty();

        bus.Report(Item("x"));
        bus.Report(Item("y") with { Source = "edge" });
        bus.RemoveSource("chrome");
        bus.Snapshot().Should().ContainSingle().Which.Source.Should().Be("edge");
    }

    [Fact]
    public void Running_tasks_come_first_newest_first()
    {
        var now = DateTimeOffset.UnixEpoch;
        var bus = new ActivityBus(() => now);
        bus.Report(Item("old"));
        now += TimeSpan.FromSeconds(1);
        bus.Report(Item("new"));
        now += TimeSpan.FromSeconds(1);
        bus.Report(Item("finished", state: ActivityState.Done));
        bus.Snapshot().Select(i => i.Id).Should().Equal("new", "old", "finished");
    }

    [Fact]
    public void A_rate_is_measured_never_guessed()
    {
        var meter = new RateMeter(TimeSpan.FromSeconds(4));
        var t = DateTimeOffset.UnixEpoch;
        meter.Add(t, 0).Should().BeNull("one sample is no rate");
        meter.Add(t + TimeSpan.FromSeconds(1), 1000).Should().Be(1000);
        meter.Add(t + TimeSpan.FromSeconds(2), 3000).Should().Be(1500);
    }

    // ------------------------------------------------------------------ plugin and bridge rules

    private static readonly ApprovedPlugin Alita = new(new PluginManifest { Id = "alita", Name = "Alita" }, "T", "H");

    [Theory]
    [InlineData("""{"op":"activity.start","task":"t1","title":"Render"}""", null)]
    [InlineData("""{"op":"activity.start","task":"t1"}""", "activity.start: a title.")]
    [InlineData("""{"op":"activity.update","task":"T 1"}""", "task:")]
    [InlineData("""{"op":"activity.update","task":"t1","progress":0.5}""", "accuracy:")]
    [InlineData("""{"op":"activity.update","task":"t1","progress":1.5,"accuracy":"exact"}""", "progress:")]
    [InlineData("""{"op":"activity.update","task":"t1","state":"done"}""", "state:")]
    [InlineData("""{"op":"activity.end","task":"t1","state":"running"}""", "activity.end:")]
    [InlineData("""{"op":"activity.update","task":"t1","done":5,"total":3}""", "done / total")]
    [InlineData("""{"op":"activity.update","task":"t1","color":"red"}""", "color:")]
    [InlineData("""{"op":"sound","sound":"done"}""", null)]
    [InlineData("""{"op":"sound","sound":"siren"}""", "sound:")]
    public void Activity_messages_follow_the_rules(string json, string? problem)
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<PluginRequest>(json, BarPlugins.Json)!;
        var found = PluginRules.Problem(Alita, request);
        if (problem is null)
        {
            found.Should().BeNull();
        }
        else
        {
            found.Should().StartWith(problem);
        }
    }

    [Fact]
    public void An_activity_message_becomes_a_task_of_its_source()
    {
        var item = PluginRules.ActivityFrom(new PluginRequest { Op = "activity.update", Task = "t1", Title = "Export", Progress = 0.4, Accuracy = "good", State = "paused" }, "plugin:alita", null);
        item.Should().Match<ActivityItem>(i => i.Source == "plugin:alita" && i.Accuracy == ProgressAccuracy.Good && i.State == ActivityState.Paused);
        PluginRules.ActivityFrom(new PluginRequest { Op = "activity.end", Task = "t1" }, "x", null).State.Should().Be(ActivityState.Done);
        PluginRules.IsBridge("chrome").Should().BeTrue();
        PluginRules.IsBridge("plugin:alita").Should().BeFalse();
    }

    [Fact]
    public void Ibar_ctl_speaks_activity_and_sound()
    {
        var start = BarCtl.Build(["activity", "start", "--plugin", "alita", "--task", "r1", "--title", "Render", "--progress", "0.25", "--accuracy", "estimate", "--total", "100"], TextReader.Null)!;
        start.Should().Match<PluginRequest>(r => r.Op == "activity.start" && r.Task == "r1" && r.Progress == 0.25 && r.Accuracy == "estimate" && r.Total == 100);
        BarCtl.Build(["activity", "end", "--plugin", "alita", "--task", "r1", "--failed"], TextReader.Null)!.State.Should().Be("failed");
        BarCtl.Build(["sound", "error", "--plugin", "alita"], TextReader.Null)!.Sound.Should().Be("error");
        BarCtl.Build(["activity", "start", "--plugin", "alita"], TextReader.Null).Should().BeNull("a task id is required");
    }

    // ------------------------------------------------------------------ browser bridge

    [Fact]
    public async Task Native_messaging_frames_round_trip_and_big_ones_are_refused()
    {
        var stream = new MemoryStream();
        await BrowserBridge.WriteAsync(stream, new JsonObject { ["type"] = "hello", ["browser"] = "chrome" }, TestContext.Current.CancellationToken);
        stream.Position = 0;
        (await BrowserBridge.ReadAsync(stream, TestContext.Current.CancellationToken))!["browser"]!.GetValue<string>().Should().Be("chrome");
        (await BrowserBridge.ReadAsync(stream, TestContext.Current.CancellationToken)).Should().BeNull("the browser closed");

        var big = new MemoryStream([0, 0, 0x10, 0]);
        (await BrowserBridge.ReadAsync(big, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public void A_download_keeps_only_name_sizes_state_speed_and_domain()
    {
        var message = JsonNode.Parse("""{"type":"download","id":7,"file":"C:\\Users\\me\\Downloads\\ubuntu.iso","received":250,"total":1000,"state":"in_progress","paused":false,"domain":"releases.ubuntu.com","url":"https://secret/path?token=1"}""")!.AsObject();
        var r = BrowserBridge.ToRequest(message, "chrome", 125)!;
        r.Should().Match<PluginRequest>(x => x.Bridge == "chrome" && x.Task == "dl-7" && x.Title == "ubuntu.iso" && x.Progress == 0.25 && x.Accuracy == "exact" && x.Rate == 125 && x.State == "running");
        r.Detail.Should().Be("releases.ubuntu.com");
        System.Text.Json.JsonSerializer.Serialize(r, BarPlugins.Json).Should().NotContain("secret").And.NotContain("Users");

        var unknown = BrowserBridge.ToRequest(JsonNode.Parse("""{"type":"download","id":8,"file":"x.zip","received":10,"total":0,"state":"in_progress"}""")!.AsObject(), "edge", null)!;
        unknown.Progress.Should().BeNull("no total: no percentage");
        unknown.Done.Should().Be(10);

        var failed = BrowserBridge.ToRequest(JsonNode.Parse("""{"type":"download","id":9,"file":"x.zip","state":"interrupted","error":"NETWORK_FAILED"}""")!.AsObject(), "edge", null)!;
        failed.Should().Match<PluginRequest>(x => x.Op == "activity.end" && x.State == "failed" && x.Detail == "failed: network failed");
        BrowserBridge.ToRequest(JsonNode.Parse("""{"type":"download","id":9,"state":"complete","received":5,"total":5}""")!.AsObject(), "edge", null)!.State.Should().Be("done");
        BrowserBridge.Cancelled(JsonNode.Parse("""{"error":"USER_CANCELED"}""")!.AsObject()).Should().BeTrue();
        BrowserBridge.Domain("evil.com/path").Should().BeNull();
        BrowserBridge.ToRequest(JsonNode.Parse("""{"type":"hello"}""")!.AsObject(), "edge", null).Should().BeNull();
    }

    [Fact]
    public void The_browser_is_the_program_that_started_the_host()
    {
        BrowserBridge.BrowserOf(["cmd", "chrome", "explorer"]).Should().Be("chrome");
        BrowserBridge.BrowserOf(["cmd.exe", "msedge.exe"]).Should().Be("edge");
        BrowserBridge.BrowserOf(["firefox"]).Should().Be("firefox");
        BrowserBridge.BrowserOf(["powershell", "explorer"]).Should().BeNull();
    }

    // ------------------------------------------------------------------ chat

    [Fact]
    public void The_newest_bundled_claude_is_chosen()
    {
        ChatCli.Newest([
            @"C:\U\AppData\Roaming\Claude\claude-code\2.1.293\83cb\claude.exe",
            @"C:\U\AppData\Roaming\Claude\claude-code\2.1.295\d2e2\claude.exe",
            @"C:\U\AppData\Roaming\Claude\claude-code\2.1.10\aaaa\claude.exe",
        ]).Should().Contain("2.1.295");
        ChatCli.Newest([]).Should().BeNull();
        ChatCli.VersionOf(@"C:\x\claude.exe").Should().Be(new Version(0, 0));
    }

    [Fact]
    public void Stream_json_is_read_piece_by_piece()
    {
        ChatCli.ClaudeArguments(false, null).Should().ContainInOrder("-p", "--output-format", "stream-json", "--verbose").And.Contain("plan");
        ChatCli.ClaudeArguments(true, "h.json").Should().Contain("default").And.EndWith(["--settings", "h.json"]);
        ChatCli.CodexArguments(false).Should().Contain("read-only").And.EndWith("-");

        ChatCli.Read("""{"type":"system","subtype":"init","model":"claude-x"}""")!.Kind.Should().Be("init");
        ChatCli.Read("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hel"}}}""")!.Should().Be(new ChatCli.StreamPiece("delta", "Hel"));
        ChatCli.Read("""{"type":"assistant","message":{"content":[{"type":"text","text":"Hello"},{"type":"tool_use"}]}}""")!.Text.Should().Be("Hello");
        ChatCli.Read("""{"type":"result","is_error":true,"result":"Not logged in · Please run /login"}""")!.Should().Match<ChatCli.StreamPiece>(p => p.Error && ChatCli.NeedsLogin(p.Text));
        ChatCli.Read("Not logged in · Please run /login")!.Kind.Should().Be("text");
        ChatCli.Read("""{"type":"stream_event","event":{"type":"message_start"}}""").Should().BeNull();
        ChatCli.NeedsLogin("All good").Should().BeFalse();
    }

    // ------------------------------------------------------------------ Media Encoder's log

    [Fact]
    public void Media_encoders_log_gives_finished_jobs_only()
    {
        const string log = "Encoding Time: 00:00:04\r\nOutput File: C:\\Renders\\intro.mp4\r\nFile Successfully Encoded\r\nOutput File: D:\\x\\b.mov\r\nEncoding Failed\r\n";
        AmeLog.Jobs(log).Should().Equal(new AmeLog.Job("intro.mp4", true), new AmeLog.Job("b.mov", false));
        AmeLog.Jobs("Output File: half-written").Should().BeEmpty();
    }
}
