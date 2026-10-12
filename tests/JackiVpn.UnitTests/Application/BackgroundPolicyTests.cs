using FluentAssertions;
using JackiVpn.Application.Background;
using JackiVpn.Application.Traffic;
using JackiVpn.Cores.Generation;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Usage;
using Microsoft.Extensions.Logging.Abstractions;

namespace JackiVpn.UnitTests.Application;

/// <summary>The app's own work yields to power saving, data cost, a hidden window and the user's traffic; traffic classes keep their order.</summary>
public sealed class BackgroundPolicyTests
{
    private sealed class Device : IDeviceConditions
    {
        public bool PowerSaver { get; set; }

        public bool Metered { get; set; }
    }

    [Fact]
    public void Upkeep_waits_for_power_saver_and_busy_heavy_work_also_for_hidden_and_metered()
    {
        var device = new Device();
        var busy = false;
        var policy = new BackgroundPolicy(device, () => busy, NullLogger.Instance);

        policy.Allows(BackgroundWork.Upkeep).Should().BeTrue();
        policy.Allows(BackgroundWork.Heavy).Should().BeTrue();

        policy.Hidden = true;
        policy.Allows(BackgroundWork.Upkeep).Should().BeTrue("subscriptions and update checks still run from the tray");
        policy.Allows(BackgroundWork.Heavy).Should().BeFalse();
        policy.Hidden = false;

        device.Metered = true;
        policy.Refresh();
        policy.Allows(BackgroundWork.Upkeep).Should().BeTrue("a few kilobytes are fine on a metered network");
        policy.Allows(BackgroundWork.Heavy).Should().BeFalse("megabytes and connection bursts wait");
        device.Metered = false;

        device.PowerSaver = true;
        policy.Refresh();
        policy.Allows(BackgroundWork.Upkeep).Should().BeFalse();
        policy.Stretch(TimeSpan.FromSeconds(2)).Should().Be(TimeSpan.FromSeconds(12));
        device.PowerSaver = false;
        policy.Refresh();

        busy = true;
        policy.Allows(BackgroundWork.Upkeep).Should().BeFalse("the user's game or download comes first");
    }

    [Fact]
    public async Task A_waiting_job_resumes_when_the_window_comes_back()
    {
        var policy = new BackgroundPolicy(new Device(), () => false, NullLogger.Instance) { Hidden = true };
        var wait = policy.WaitUntilAllowedAsync(BackgroundWork.Heavy, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        wait.IsCompleted.Should().BeFalse();

        policy.Hidden = false;
        await wait.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    /// <summary>Unlimited counts as the fastest rate.</summary>
    private static long Speed(ShapeRate r) => r.Block ? 0 : r.IsUnlimited ? long.MaxValue : r.DownBytesPerSecond;

    [Theory]
    [InlineData(TrafficMode.Gaming)]
    [InlineData(TrafficMode.Streaming)]
    public void Under_load_each_class_gets_no_more_than_the_one_above_it(TrafficMode mode)
    {
        var limits = TrafficPolicyBuilder.Limits(new AppSettings(), new TrafficContext { Mode = mode, GamePath = @"C:\Games\x\game.exe" });

        // game and calls are never relayed (unlimited) > browsing (bg) ≥ media players > downloads = updates
        var browsing = Speed(limits.For(ShapeClasses.Background));
        var media = Speed(limits.For(ShapeClasses.Media));
        var downloads = Speed(limits.For(ShapeClasses.Downloads));
        var gameUpdates = Speed(limits.For(ShapeClasses.Update(UpdateGroup.Games)));
        if (mode == TrafficMode.Gaming)
        {
            browsing.Should().BeGreaterThan(media);
        }

        media.Should().BeGreaterThan(downloads);
        downloads.Should().BeLessThan(browsing);
        gameUpdates.Should().Be(downloads, "automatic updates share the lowest class");
    }

    [Fact]
    public void Calls_are_never_relayed_and_media_and_downloaders_get_their_own_classes()
    {
        var policy = TrafficPolicyBuilder.Build(new AppSettings(), new TrafficContext());
        policy.PriorityApps.Should().Contain(["zoom.exe", "teams.exe", "telegram.exe"]);
        policy.MediaApps.Should().Contain("vlc.exe").And.NotContain("chrome.exe", "browsers are browsing");
        policy.DownloadApps.Should().Contain(["idman.exe", "qbittorrent.exe"]);
        SingBoxConfigBuilder.ShapeClassesOf(policy).Should().ContainInOrder(ShapeClasses.Media, ShapeClasses.Downloads, ShapeClasses.Background);
    }

    [Fact]
    public void Normal_mode_leaves_every_class_at_full_speed()
    {
        var limits = TrafficPolicyBuilder.Limits(new AppSettings(), new TrafficContext());
        limits.For(ShapeClasses.Media).IsUnlimited.Should().BeTrue();
        limits.For(ShapeClasses.Downloads).IsUnlimited.Should().BeTrue();
    }

    [Fact]
    public void A_user_limit_on_a_downloader_wins_over_the_category()
    {
        var settings = new AppSettings { Traffic = new TrafficControlSettings { Apps = [new AppRule { App = "qbittorrent.exe", DownKbps = 500 }] } };
        TrafficPolicyBuilder.Build(settings, new TrafficContext()).DownloadApps.Should().NotContain("qbittorrent.exe");
    }
}
