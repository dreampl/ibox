using System.Net;
using System.Text;
using FluentAssertions;
using JackiVpn.Application.Amnezia;

namespace JackiVpn.UnitTests.Application;

public class AmneziaShareKeyTests
{
    private const string Json = """{"containers":[{"container":"amnezia-awg"}],"defaultContainer":"amnezia-awg","description":"Nex DE","hostName":"203.0.113.7"}""";

    [Fact]
    public void Parses_compressed_key_and_reads_host()
    {
        var key = AmneziaShareKey.Encode(Json);

        AmneziaShareKey.TryParse(key, out var parsed).Should().BeTrue();
        parsed!.HostName.Should().Be("203.0.113.7");
        parsed.Description.Should().Be("Nex DE");
        parsed.Key.Should().Be(key);
    }

    [Fact]
    public void Normalizes_whitespace_padding_and_scheme_case()
    {
        var key = AmneziaShareKey.Encode(Json);
        var body = key["vpn://".Length..];
        var messy = "  VPN://" + body[..10] + "\n " + body[10..] + "==  ";

        AmneziaShareKey.TryParse(messy, out var parsed).Should().BeTrue();
        parsed!.Key.Should().Be(key);
    }

    [Fact]
    public void Accepts_uncompressed_legacy_json()
    {
        var legacy = "vpn://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        AmneziaShareKey.TryParse(legacy, out var parsed).Should().BeTrue();
        parsed!.HostName.Should().Be("203.0.113.7");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("vless://abc@host:443")]
    [InlineData("vpn://")]
    [InlineData("vpn://not base64 !!")]
    [InlineData("vpn://AAAAAAAAAAAA")]
    [InlineData("vpn://ab=cd")]
    public void Rejects_invalid_keys(string? text) =>
        AmneziaShareKey.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void Rejects_json_that_is_not_an_amnezia_config()
    {
        var other = AmneziaShareKey.Encode("""{"hello":"world"}""");
        AmneziaShareKey.TryParse(other, out _).Should().BeFalse();
    }

    [Fact]
    public void Rejects_truncated_compressed_payload()
    {
        var key = AmneziaShareKey.Encode(Json);
        AmneziaShareKey.TryParse(key[..^8], out _).Should().BeFalse();
    }
}

public class AmneziaDetectionTests
{
    private static readonly HashSet<IPAddress> Ours = [IPAddress.Parse("203.0.113.7")];

    [Theory]
    [InlineData("AmneziaVPN", "WireGuard Tunnel", true)]
    [InlineData("Ethernet", "AmneziaWG Tunnel", true)]
    [InlineData("Wi-Fi", "Intel Wireless", false)]
    [InlineData("wintun", "IBOX Tunnel", false)]
    public void Recognises_amnezia_adapters(string name, string description, bool expected) =>
        AmneziaDetector.IsAmneziaAdapter(new NetAdapterInfo(name, description, true)).Should().Be(expected);

    [Fact]
    public void Down_adapter_is_not_a_tunnel() =>
        AmneziaDetector.AnyTunnelUp([new NetAdapterInfo("AmneziaVPN", "", false)]).Should().BeFalse();

    [Fact]
    public void Evaluate_covers_every_case()
    {
        AmneziaDetector.Evaluate(false, false, "203.0.113.7", Ours).Should().Be(AmneziaLinkState.None);
        AmneziaDetector.Evaluate(true, true, "203.0.113.7", Ours).Should().Be(AmneziaLinkState.None);
        AmneziaDetector.Evaluate(true, false, "198.51.100.1", Ours).Should().Be(AmneziaLinkState.TunnelUp);
        AmneziaDetector.Evaluate(true, false, null, Ours).Should().Be(AmneziaLinkState.TunnelUp);
        AmneziaDetector.Evaluate(true, false, "203.0.113.7", Ours).Should().Be(AmneziaLinkState.OnOurServer);
        AmneziaDetector.Evaluate(true, false, "::ffff:203.0.113.7", Ours).Should().Be(AmneziaLinkState.OnOurServer);
    }

    [Fact]
    public async Task Watcher_matches_resolved_profile_hosts_and_remembered_keys()
    {
        var up = true;
        var egressCalls = 0;
        var watcher = new AmneziaWatcher(
            _ => Task.FromResult(up),
            () => false,
            _ => Task.FromResult<IEnumerable<string>>(["nex.example.com"]),
            _ => { egressCalls++; return Task.FromResult<string?>("198.51.100.9"); },
            (host, _) => Task.FromResult(host == "nex.example.com" ? [IPAddress.Parse("192.0.2.1")] : Array.Empty<IPAddress>()));
        var changes = 0;
        watcher.Changed += (_, _) => changes++;

        (await watcher.CheckAsync(CancellationToken.None)).Should().Be(AmneziaLinkState.TunnelUp);

        AmneziaShareKey.TryParse(AmneziaShareKey.Encode("""{"containers":[],"hostName":"198.51.100.9"}"""), out var key).Should().BeTrue();
        watcher.Remember(key!);
        (await watcher.CheckAsync(CancellationToken.None)).Should().Be(AmneziaLinkState.OnOurServer);
        egressCalls.Should().Be(1, "the exit IP is cached while the tunnel stays up");

        up = false;
        (await watcher.CheckAsync(CancellationToken.None)).Should().Be(AmneziaLinkState.None);
        changes.Should().Be(3);
    }

    [Fact]
    public async Task Watcher_asks_nothing_while_ibox_is_connected()
    {
        var egressCalls = 0;
        var watcher = new AmneziaWatcher(
            _ => Task.FromResult(true),
            () => true,
            _ => Task.FromResult<IEnumerable<string>>([]),
            _ => { egressCalls++; return Task.FromResult<string?>(null); });

        (await watcher.CheckAsync(CancellationToken.None)).Should().Be(AmneziaLinkState.None);
        egressCalls.Should().Be(0);
    }
}
