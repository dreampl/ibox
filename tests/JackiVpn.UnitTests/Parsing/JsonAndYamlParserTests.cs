using FluentAssertions;
using JackiVpn.Domain.Profiles;
using static JackiVpn.UnitTests.TestSupport;

namespace JackiVpn.UnitTests.Parsing;

public class JsonAndYamlParserTests
{
    [Fact]
    public void Owner_sample_singbox_outbound_keeps_every_important_field()
    {
        var r = Parse(Fixture("jacki-vless-ws-tls.singbox.json"));

        r.Format.Should().Be(ConfigFormat.SingBoxOutbound);
        var p = r.Profiles.Should().ContainSingle().Subject;
        p.Name.Should().Be("Jacki CDN ⚡ سرور اصلی");
        p.Protocol.Should().Be(ProxyProtocol.Vless);
        p.Server.Should().Be("104.16.123.45");
        p.Port.Should().Be(443);
        p.Credentials.Uuid.Should().Be("11111111-2222-4333-8444-555555555555");
        p.Transport.Type.Should().Be(TransportType.WebSocket);
        p.Transport.Host.Should().Be("edge.jacki-example.test");
        p.Transport.Path.Should().Be("/jacki-ws");
        p.Tls.Enabled.Should().BeTrue();
        p.Tls.ServerName.Should().Be("edge.jacki-example.test");
        p.Tls.Fingerprint.Should().Be("firefox");
        p.Tls.Alpn.Should().Equal("http/1.1");
        p.Tls.Fragment!.Enabled.Should().BeTrue();
        p.Tls.Fragment.FallbackDelay.Should().Be(TimeSpan.FromMilliseconds(20));
    }

    [Fact]
    public void Singbox_full_config_skips_non_proxy_outbounds_and_reads_endpoints()
    {
        const string json = """
        {
          "log": {"level": "info"},
          "outbounds": [
            {"type": "selector", "tag": "select", "outbounds": ["a"]},
            {"type": "vless", "tag": "a", "server": "a.example.com", "server_port": 443, "uuid": "11111111-2222-4333-8444-555555555555",
             "flow": "xtls-rprx-vision", "tcp_fast_open": true,
             "tls": {"enabled": true, "server_name": "a.example.com", "reality": {"enabled": true, "public_key": "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcdef", "short_id": "0123"}, "utls": {"enabled": true, "fingerprint": "chrome"}, "future_tls_knob": 7},
             "multiplex": {"enabled": true, "protocol": "h2mux", "max_connections": 4, "brutal": {"enabled": false}}},
            {"type": "hysteria2", "tag": "h", "server": "h.example.com", "server_port": 443, "password": "pw", "server_ports": ["2000:3000"], "tls": {"enabled": true}},
            {"type": "direct", "tag": "direct"}
          ],
          "endpoints": [
            {"type": "wireguard", "tag": "wg", "address": ["172.16.0.2/32"], "private_key": "iKf1k5ZEBJmZzqBqBpX1l+ZuHqFSh3b8cXK7N3r4t2Y=",
             "peers": [{"address": "162.159.192.1", "port": 2408, "public_key": "bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=", "allowed_ips": ["0.0.0.0/0"], "reserved": [1,2,3]}]}
          ]
        }
        """;
        var r = Parse(json);

        r.Format.Should().Be(ConfigFormat.SingBoxFull);
        r.Profiles.Select(p => p.Protocol).Should().Equal(ProxyProtocol.Vless, ProxyProtocol.Hysteria2, ProxyProtocol.WireGuard);
        var vless = r.Profiles[0];
        vless.Tls.IsReality.Should().BeTrue();
        vless.Mux!.Mode.Should().Be(MuxMode.On);
        vless.Mux.Protocol.Should().Be("h2mux");
        vless.Mux.Extensions!["brutal"].Should().NotBeNull();
        vless.Extensions!["tcp_fast_open"]!.GetValue<bool>().Should().BeTrue("unknown dial fields are preserved");
        vless.Tls.Extensions!["future_tls_knob"]!.GetValue<int>().Should().Be(7);
        r.Profiles[1].Hysteria2!.ServerPorts.Should().Equal("2000:3000");
        r.Profiles[2].WireGuard!.Reserved.Should().Equal(1, 2, 3);
        r.Profiles[2].Server.Should().Be("162.159.192.1");
    }

    [Fact]
    public void Xray_full_config_with_fragment_chain_is_normalized()
    {
        const string json = """
        {
          "remarks": "Jacki Xray",
          "outbounds": [
            {"tag": "proxy", "protocol": "vless",
             "settings": {"vnext": [{"address": "104.16.123.45", "port": 443, "users": [{"id": "11111111-2222-4333-8444-555555555555", "encryption": "none"}]}]},
             "streamSettings": {"network": "ws", "security": "tls",
               "tlsSettings": {"serverName": "edge.jacki-example.test", "fingerprint": "firefox", "alpn": ["http/1.1"]},
               "wsSettings": {"path": "/jacki-ws", "host": "edge.jacki-example.test"},
               "sockopt": {"dialerProxy": "fragment", "tcpKeepAliveIdle": 100}}},
            {"tag": "fragment", "protocol": "freedom", "settings": {"fragment": {"packets": "tlshello", "length": "100-200", "interval": "10-20"}}},
            {"tag": "direct", "protocol": "freedom"},
            {"tag": "block", "protocol": "blackhole"}
          ]
        }
        """;
        var r = Parse(json);

        r.Format.Should().Be(ConfigFormat.XrayFull);
        var p = r.Profiles.Should().ContainSingle().Subject;
        p.Name.Should().Be("Jacki Xray");
        p.Transport.Host.Should().Be("edge.jacki-example.test");
        p.Tls.Fingerprint.Should().Be("firefox");
        p.Tls.Fragment!.Packets.Should().Be("tlshello");
        p.Tls.Fragment.Length.Should().Be("100-200");
        p.Tls.Fragment.Interval.Should().Be("10-20");
        p.Extensions!["sockopt"]!["tcpKeepAliveIdle"]!.GetValue<int>().Should().Be(100);
    }

    [Fact]
    public void PasarGuard_xray_array_of_configs()
    {
        const string json = """
        [
          {"remarks": "🇩🇪 DE", "outbounds": [{"tag": "proxy", "protocol": "trojan", "settings": {"servers": [{"address": "de.example.com", "port": 443, "password": "pw"}]}, "streamSettings": {"network": "grpc", "security": "tls", "grpcSettings": {"serviceName": "tg"}, "tlsSettings": {"serverName": "de.example.com"}}}]},
          {"remarks": "🇳🇱 NL", "outbounds": [{"tag": "proxy", "protocol": "vmess", "settings": {"vnext": [{"address": "nl.example.com", "port": 8443, "users": [{"id": "11111111-2222-4333-8444-555555555555", "alterId": 0, "security": "auto"}]}]}, "streamSettings": {"network": "tcp", "security": "reality", "realitySettings": {"serverName": "www.apple.com", "publicKey": "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcdef", "shortId": "ab"}}}]}
        ]
        """;
        var r = Parse(json);

        r.Profiles.Select(p => p.Name).Should().Equal("🇩🇪 DE", "🇳🇱 NL");
        r.Profiles[0].Transport.ServiceName.Should().Be("tg");
        r.Profiles[1].Tls.IsReality.Should().BeTrue();
    }

    [Fact]
    public void Malformed_json_is_reported()
    {
        var r = Parse("{\"outbounds\": [ {\"type\": \"vless\", ");

        r.HasProfiles.Should().BeFalse();
        r.Issues.Should().Contain(i => i.Message.Contains("Malformed JSON"));
    }

    [Fact]
    public void Excessively_deep_json_is_rejected()
    {
        var deep = new string('[', 500) + new string(']', 500);
        var r = Parse(deep);

        r.HasProfiles.Should().BeFalse();
    }

    [Fact]
    public void Clash_meta_yaml()
    {
        const string yaml = """
        port: 7890
        proxies:
          - name: "سرور ۱"
            type: vless
            server: v.example.com
            port: 443
            uuid: 11111111-2222-4333-8444-555555555555
            network: ws
            tls: true
            servername: v.example.com
            client-fingerprint: firefox
            alpn: [http/1.1]
            ws-opts:
              path: /ws
              headers:
                Host: v.example.com
          - name: ss
            type: ss
            server: 1.2.3.4
            port: 8388
            cipher: aes-128-gcm
            password: "pw"
            plugin: obfs
            plugin-opts: { mode: http, host: bing.com }
          - name: hy
            type: hysteria2
            server: hy.example.com
            port: 443
            password: pw
            obfs: salamander
            obfs-password: ob
          - name: unsupported
            type: snell
            server: x
            port: 1
        proxy-groups: []
        """;
        var r = Parse(yaml);

        r.Format.Should().Be(ConfigFormat.ClashMetaYaml);
        r.Profiles.Should().HaveCount(3);
        r.Profiles[0].Name.Should().Be("سرور ۱");
        r.Profiles[0].Transport.Host.Should().Be("v.example.com");
        r.Profiles[0].Tls.Fingerprint.Should().Be("firefox");
        r.Profiles[1].Shadowsocks!.Plugin.Should().Be("obfs-local");
        r.Profiles[1].Shadowsocks!.PluginOptions.Should().Be("obfs=http;obfs-host=bing.com");
        r.Profiles[2].Hysteria2!.ObfsPassword.Should().Be("ob");
        r.Failed.Should().Be(1);
    }

    [Fact]
    public void Clash_plain_yaml_is_detected_as_classic_clash()
    {
        const string yaml = "proxies:\n  - {name: t, type: trojan, server: t.example.com, port: 443, password: pw, sni: t.example.com}\n";
        var r = Parse(yaml);

        r.Format.Should().Be(ConfigFormat.ClashYaml);
        r.Profiles.Single().Tls.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Yaml_alias_bomb_is_bounded()
    {
        const string bomb = """
        a: &a ["x","x","x","x","x","x","x","x","x","x"]
        b: &b [*a,*a,*a,*a,*a,*a,*a,*a,*a,*a]
        c: &c [*b,*b,*b,*b,*b,*b,*b,*b,*b,*b]
        d: &d [*c,*c,*c,*c,*c,*c,*c,*c,*c,*c]
        e: &e [*d,*d,*d,*d,*d,*d,*d,*d,*d,*d]
        f: &f [*e,*e,*e,*e,*e,*e,*e,*e,*e,*e]
        g: &g [*f,*f,*f,*f,*f,*f,*f,*f,*f,*f]
        proxies: [*g]
        """;
        var r = Parse(bomb);

        r.HasProfiles.Should().BeFalse();
        r.Issues.Should().Contain(i => i.Message.Contains("too large", StringComparison.OrdinalIgnoreCase));
    }
}
