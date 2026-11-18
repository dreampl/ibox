using FluentAssertions;
using JackiVpn.Application.Media;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Visuals;

namespace JackiVpn.UnitTests.Domain;

public class NotchToolsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Focus_timer_runs_work_then_break_then_stops()
    {
        var f = new FocusTimer();
        f.Start(T0);
        f.Phase.Should().Be(FocusPhase.Work);
        f.Remaining(T0).Should().Be(TimeSpan.FromMinutes(25));
        FocusTimer.Clock(f.Remaining(T0.AddSeconds(0.4))).Should().Be("25:00");
        f.Advance(T0.AddMinutes(24)).Should().BeNull();

        f.Advance(T0.AddMinutes(25).AddSeconds(3)).Should().Be(FocusPhase.Work);
        f.Phase.Should().Be(FocusPhase.Break);

        // The break counts from the end of the work, not from when the UI noticed.
        f.NextDue.Should().Be(T0.AddMinutes(30));
        f.Advance(T0.AddMinutes(30)).Should().Be(FocusPhase.Break);
        f.Phase.Should().Be(FocusPhase.Idle);
        f.NextDue.Should().BeNull();
    }

    [Fact]
    public void Focus_timer_pause_keeps_the_time_left()
    {
        var f = new FocusTimer(10, 2);
        f.Start(T0);
        f.Pause(T0.AddMinutes(4));
        f.Remaining(T0.AddHours(3)).Should().Be(TimeSpan.FromMinutes(6));
        f.NextDue.Should().BeNull();
        f.Advance(T0.AddHours(3)).Should().BeNull();

        f.Resume(T0.AddHours(3));
        f.NextDue.Should().Be(T0.AddHours(3).AddMinutes(6));
        f.Progress(T0.AddHours(3)).Should().BeApproximately(0.4, 1e-9);
    }

    [Fact]
    public void Focus_timer_clamps_lengths_and_survives_sleeping_through_everything()
    {
        var f = new FocusTimer(0, 500);
        f.Work.Should().Be(TimeSpan.FromMinutes(1));
        f.Break.Should().Be(TimeSpan.FromMinutes(60));

        f.Start(T0);
        f.Advance(T0.AddDays(1)).Should().Be(FocusPhase.Work);
        f.Phase.Should().Be(FocusPhase.Idle);
    }

    [Fact]
    public void Now_playing_position_moves_on_only_while_playing()
    {
        var item = new NowPlaying("Song", "Artist", "app", IsPlaying: true)
        {
            Length = TimeSpan.FromMinutes(3),
            Position = TimeSpan.FromSeconds(30),
            PositionAt = T0,
        };

        item.PositionNow(T0.AddSeconds(10)).Should().Be(TimeSpan.FromSeconds(40));
        item.PositionNow(T0.AddMinutes(10)).Should().Be(TimeSpan.FromMinutes(3));
        (item with { IsPlaying = false }).PositionNow(T0.AddSeconds(10)).Should().Be(TimeSpan.FromSeconds(30));
        new NowPlaying("Live", null, null, true).PositionNow(T0).Should().BeNull();
        NowPlaying.Clock(TimeSpan.FromSeconds(187)).Should().Be("3:07");
        NowPlaying.Clock(TimeSpan.FromSeconds(3765)).Should().Be("1:02:45");
    }

    [Fact]
    public void Now_playing_reports_that_only_moved_on_are_not_changes()
    {
        var art = new byte[] { 1, 2, 3 };
        var first = new NowPlaying("Song", "Artist", "app", true) { Length = TimeSpan.FromMinutes(3), Position = TimeSpan.FromSeconds(30), PositionAt = T0, Artwork = art };
        var later = first with { Position = TimeSpan.FromSeconds(41), PositionAt = T0.AddSeconds(10) };
        var seeked = first with { Position = TimeSpan.FromSeconds(90), PositionAt = T0.AddSeconds(10) };

        later.SameAsPredicted(first, TimeSpan.FromSeconds(1.5)).Should().BeTrue();
        seeked.SameAsPredicted(first, TimeSpan.FromSeconds(1.5)).Should().BeFalse();
        (later with { IsPlaying = false }).SameAsPredicted(first, TimeSpan.FromSeconds(1.5)).Should().BeFalse();
        (later with { Artwork = [1, 2, 3] }).SameAsPredicted(first, TimeSpan.FromSeconds(1.5)).Should().BeFalse();
    }

    [Theory]
    [InlineData(FrontWindow.Normal, true, true, NotchPresence.Normal)]
    [InlineData(FrontWindow.FullScreen, true, true, NotchPresence.GhostLine)]
    [InlineData(FrontWindow.FullScreen, false, true, NotchPresence.Line)]
    [InlineData(FrontWindow.FullScreen, false, false, NotchPresence.Normal)]
    [InlineData(FrontWindow.Maximized, true, true, NotchPresence.Line)]
    [InlineData(FrontWindow.Maximized, true, false, NotchPresence.Normal)]
    public void Notch_presence_follows_the_front_window(FrontWindow front, bool hideInFullScreen, bool lineWhenMaximized, NotchPresence expected) =>
        NotchPresenceRules.For(front, hideInFullScreen, lineWhenMaximized).Should().Be(expected);

    [Fact]
    public void Notch_settings_default_to_the_classic_notch_with_click_through_on()
    {
        var s = new NotchSettings();
        s.Style.Should().Be(NotchStyle.Classic);
        s.PeekThrough.Should().BeTrue();
        s.LineWhenMaximized.Should().BeTrue();

        var json = System.Text.Json.JsonSerializer.Serialize(s with { Style = NotchStyle.Island });
        json.Should().Contain("\"Island\"");
        System.Text.Json.JsonSerializer.Deserialize<NotchSettings>(json)!.Style.Should().Be(NotchStyle.Island);
        System.Text.Json.JsonSerializer.Deserialize<NotchSettings>("{}")!.Style.Should().Be(NotchStyle.Classic);
    }
}
