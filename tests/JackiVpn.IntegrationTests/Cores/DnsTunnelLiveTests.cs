using FluentAssertions;
using JackiVpn.Application.Sessions;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Profiles;
using JackiVpn.IntegrationTests.Infrastructure;
using JackiVpn.Subscriptions.Parsing;
using Microsoft.Extensions.Logging.Abstractions;

namespace JackiVpn.IntegrationTests.Cores;

/// <summary>
/// Real DNS tunnel servers through the app's own path: resolver race → dnstt client → sing-box frontend (a loopback
/// proxy only; the Windows proxy setting is never touched). Opt-in: JACKIVPN_DNSTT_LIVE holds one or more dnstt://
/// links separated by ';' (they carry credentials, so they never live in the repository).
/// </summary>
public sealed class DnsTunnelLiveTests(CoreFixture cores) : IClassFixture<CoreFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Live_dns_tunnel_servers_carry_https_through_the_app_path()
    {
        var links = Environment.GetEnvironmentVariable("JACKIVPN_DNSTT_LIVE");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(links), "Set JACKIVPN_DNSTT_LIVE to dnstt:// links to run against real servers.");
        cores.RequireCores();
        Assert.SkipUnless(cores.Catalog.IsInstalled(CoreKind.Dnstt), "dnstt-client is not in the cores folder (scripts/fetch-cores.ps1).");

        var parse = new ConfigParserPipeline().Parse(links!.Replace(';', '\n'), new ParseContext());
        parse.Profiles.Should().NotBeEmpty();
        var plans = new SessionPlanFactory(cores.Catalog);
        var failures = new List<string>();
        foreach (var profile in parse.Profiles)
        {
            var started = DateTimeOffset.UtcNow;
            await using var supervisor = new CoreSupervisor(cores.Catalog, cores.SingBox, cores.Xray, cores.WorkDir, NullLogger.Instance);
            try
            {
                var plan = await plans.CreateAsync(
                    new SessionRequest { Profile = profile, Mode = ConnectionMode.SystemProxy },
                    cores.SingBoxCaps!, cores.XrayCaps, Ct);
                var picked = DateTimeOffset.UtcNow;
                await supervisor.StartAsync(plan, Ct);

                using var http = NetHelpers.ClientVia($"http://127.0.0.1:{plan.Frontend.ListenPort}", TimeSpan.FromSeconds(60));
                var trace = await http.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace", Ct);
                var exit = string.Join(' ', trace.Split('\n').Where(l => l.StartsWith("ip=", StringComparison.Ordinal) || l.StartsWith("loc=", StringComparison.Ordinal)));
                var firstData = DateTimeOffset.UtcNow;

                var bytes = (await http.GetByteArrayAsync("https://speed.cloudflare.com/__down?bytes=200000", Ct)).Length;
                var kbps = bytes / 1024.0 / (DateTimeOffset.UtcNow - firstData).TotalSeconds;
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"{profile.Name}: resolver {plan.DnsTunnel?.Arguments[1] ?? "-"} (picked in {(picked - started).TotalSeconds:0.0}s), {exit}, " +
                    $"first request {(firstData - picked).TotalSeconds:0.0}s, {kbps:0.0} KB/s");
                exit.Should().Contain("ip=");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !Ct.IsCancellationRequested)
            {
                failures.Add($"{profile.Name}: {ex.GetType().Name}: {ex.Message}");
                TestContext.Current.TestOutputHelper?.WriteLine(failures[^1]);
            }
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    public void Dnstt_links_parse_into_dns_tunnel_profiles()
    {
        var p = new ConfigParserPipeline().Parse(
            "dnstt://u:p@t.example.test?pubkey=" + new string('a', 64) + "&resolver=auto#X", new ParseContext()).Profiles.Single();
        p.Protocol.Should().Be(ProxyProtocol.Dnstt);
        DnsTunnel.Candidates(p, () => []).Should().HaveCount(5);
    }
}
