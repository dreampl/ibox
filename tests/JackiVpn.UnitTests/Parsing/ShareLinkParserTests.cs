using FluentAssertions;
using JackiVpn.Domain.Profiles;
using JackiVpn.Subscriptions.Parsing;
using static JackiVpn.UnitTests.TestSupport;

namespace JackiVpn.UnitTests.Parsing;

public class ShareLinkParserTests
{
    private const string Uuid = "11111111-2222-4333-8444-555555555555";

    [Fact]
    public void Vless_ws_tls_with_cdn_ip_host_header_sni_utls_alpn_fragment()
    {
        var p = Link($"vless://{Uuid}@104.16.123.45:443?encryption=none&security=tls&sni=edge.jacki-example.test&alpn=http%2F1.1&fp=firefox&type=ws&host=edge.jacki-example.test&path=%2Fjacki-ws&fragment=1#Jacki%20CDN");

        p.Protocol.Should().Be(ProxyProtocol.Vless);
        p.Server.Should().Be("104.16.123.45");
        p.Port.Should().Be(443);
        p.Credentials.Uuid.Should().Be(Uuid);
        p.Credentials.Encryption.Should().Be("none");
        p.Transport.Type.Should().Be(TransportType.WebSocket);
        p.Transport.Host.Should().Be("edge.jacki-example.test");
        p.Transport.Path.Should().Be("/jacki-ws");
        p.Tls.Enabled.Should().BeTrue();
        p.Tls.ServerName.Should().Be("edge.jacki-example.test");
        p.Tls.Fingerprint.Should().Be("firefox");
        p.Tls.Alpn.Should().Equal("http/1.1");
        p.Tls.Fragment.Should().NotBeNull();
        p.Tls.Fragment!.Enabled.Should().BeTrue();
        p.Name.Should().Be("Jacki CDN");
        p.Fingerprint.Should().HaveLength(32);
        p.Origin.RawSource.Should().StartWith("vless://");
    }

    [Fact]
    public void Vless_reality_vision()
    {
        var p = Link($"vless://{Uuid}@example.com:443?security=reality&sni=www.microsoft.com&fp=chrome&pbk=AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcdef&sid=6ba85179e30d4fc2&spx=%2F&type=tcp&flow=xtls-rprx-vision#R");

        p.Tls.IsReality.Should().BeTrue();
        p.Tls.Reality!.PublicKey.Should().Be("AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcdef");
        p.Tls.Reality.ShortId.Should().Be("6ba85179e30d4fc2");
        p.Tls.Reality.SpiderX.Should().Be("/");
        p.Tls.ServerName.Should().Be("www.microsoft.com");
        p.Credentials.Flow.Should().Be("xtls-rprx-vision");
        p.Transport.Type.Should().Be(TransportType.Tcp);
    }

    [Fact]
    public void Vless_grpc_uses_service_name_not_path()
    {
        var p = Link($"vless://{Uuid}@example.com:443?security=tls&type=grpc&serviceName=my-grpc&mode=multi#g");

        p.Transport.Type.Should().Be(TransportType.Grpc);
        p.Transport.ServiceName.Should().Be("my-grpc");
        p.Transport.GrpcMultiMode.Should().BeTrue();
        p.Transport.Path.Should().BeNull();
    }

    [Fact]
    public void Vless_ipv6_bracketed_host()
    {
        var p = Link($"vless://{Uuid}@[2001:db8::10]:8443?type=ws&path=%2F#v6");

        p.Server.Should().Be("2001:db8::10");
        p.Port.Should().Be(8443);
    }

    [Fact]
    public void Ws_early_data_in_path_is_normalized()
    {
        var p = Link($"vless://{Uuid}@example.com:443?security=tls&type=ws&path=%2Fws%3Fed%3D2048#ed");

        p.Transport.Path.Should().Be("/ws");
        p.Transport.MaxEarlyData.Should().Be(2048);
        p.Transport.EarlyDataHeaderName.Should().Be("Sec-WebSocket-Protocol");
    }

    [Theory]
    [InlineData("%D8%B3%D8%B1%D9%88%D8%B1%20%D8%A2%D9%84%D9%85%D8%A7%D9%86", "سرور آلمان")]
    [InlineData("%F0%9F%87%A9%F0%9F%87%AA%20Germany%20%F0%9F%9A%80", "🇩🇪 Germany 🚀")]
    [InlineData("日本%E3%80%80Tokyo", "日本 Tokyo")]
    public void Unicode_persian_and_emoji_names_are_preserved(string encoded, string expected)
    {
        var p = Link($"vless://{Uuid}@example.com:443?type=tcp#{encoded}");

        p.Name.Should().Be(expected);
    }

    [Fact]
    public void Missing_name_gets_deterministic_default()
    {
        Link($"vless://{Uuid}@example.com:443?type=tcp").Name.Should().Be("Vless example.com:443");
    }

    [Fact]
    public void Unknown_query_parameters_are_preserved_in_extensions()
    {
        var p = Link($"vless://{Uuid}@example.com:443?type=tcp&futureOption=42&pqv=abc#x");

        p.Extensions.Should().NotBeNull();
        p.Extensions!["uri_params"]!["futureOption"]!.GetValue<string>().Should().Be("42");
        p.Extensions["uri_params"]!["pqv"]!.GetValue<string>().Should().Be("abc");
    }

    [Theory]
    [InlineData("tlshello,100-200,10-20", "tlshello", "100-200", "10-20")]
    [InlineData("100-200,10-20,tlshello", "tlshello", "100-200", "10-20")]
    [InlineData("1", null, null, null)]
    public void Fragment_parameter_variants(string value, string? packets, string? length, string? interval)
    {
        var p = Link($"vless://{Uuid}@example.com:443?security=tls&type=ws&fragment={Uri.EscapeDataString(value)}#f");

        p.Tls.Fragment!.Enabled.Should().BeTrue();
        p.Tls.Fragment.Packets.Should().Be(packets);
        p.Tls.Fragment.Length.Should().Be(length);
        p.Tls.Fragment.Interval.Should().Be(interval);
    }

    [Fact]
    public void Vmess_v2rayn_base64_json_with_string_port()
    {
        var json = $$"""{"v":"2","ps":"وی‌مس","add":"vm.example.com","port":"8443","id":"{{Uuid}}","aid":"0","scy":"auto","net":"ws","type":"none","host":"cdn.example.com","path":"/vm","tls":"tls","sni":"cdn.example.com","alpn":"h2,http/1.1","fp":"chrome","extraField":"keep"}""";
        var p = Link("vmess://" + B64(json));

        p.Protocol.Should().Be(ProxyProtocol.Vmess);
        p.Name.Should().Be("وی‌مس");
        p.Server.Should().Be("vm.example.com");
        p.Port.Should().Be(8443);
        p.Credentials.Uuid.Should().Be(Uuid);
        p.Credentials.AlterId.Should().Be(0);
        p.Transport.Type.Should().Be(TransportType.WebSocket);
        p.Transport.Host.Should().Be("cdn.example.com");
        p.Tls.Alpn.Should().Equal("h2", "http/1.1");
        p.Tls.Fingerprint.Should().Be("chrome");
        p.Extensions!["vmess_json"]!["extraField"]!.GetValue<string>().Should().Be("keep");
    }

    [Fact]
    public void Vmess_numeric_port_and_grpc()
    {
        var json = $$"""{"v":"2","ps":"g","add":"1.2.3.4","port":443,"id":"{{Uuid}}","aid":0,"net":"grpc","path":"svc","type":"gun","tls":"tls"}""";
        var p = Link("vmess://" + B64(json));

        p.Port.Should().Be(443);
        p.Transport.Type.Should().Be(TransportType.Grpc);
        p.Transport.ServiceName.Should().Be("svc");
    }

    [Fact]
    public void Vmess_standard_uri_form()
    {
        var p = Link($"vmess://{Uuid}@vm.example.com:443?type=ws&security=tls&path=%2Fa&host=h.example.com#std");

        p.Protocol.Should().Be(ProxyProtocol.Vmess);
        p.Transport.Path.Should().Be("/a");
        p.Tls.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Trojan_defaults_to_tls()
    {
        var p = Link("trojan://p%40ss%3Aword@tr.example.com:443?sni=tr.example.com&type=ws&path=%2Ft&allowInsecure=1#T");

        p.Protocol.Should().Be(ProxyProtocol.Trojan);
        p.Credentials.Password.Should().Be("p@ss:word");
        p.Tls.Enabled.Should().BeTrue();
        p.Tls.AllowInsecure.Should().BeTrue();
    }

    [Fact]
    public void Shadowsocks_sip002_base64_userinfo_with_plugin()
    {
        var p = Link("ss://" + B64Url("aes-256-gcm:secret-pass") + "@ss.example.com:8388/?plugin=obfs-local%3Bobfs%3Dhttp%3Bobfs-host%3Dwww.bing.com#SS");

        p.Protocol.Should().Be(ProxyProtocol.Shadowsocks);
        p.Shadowsocks!.Method.Should().Be("aes-256-gcm");
        p.Credentials.Password.Should().Be("secret-pass");
        p.Shadowsocks.Plugin.Should().Be("obfs-local");
        p.Shadowsocks.PluginOptions.Should().Be("obfs=http;obfs-host=www.bing.com");
    }

    [Fact]
    public void Shadowsocks_2022_plain_percent_encoded_userinfo()
    {
        var p = Link("ss://2022-blake3-aes-128-gcm:YctPZ6U7xPPcU%2Bgp3u%2B0tx%2FtRizJN9K8y%2BuKlW2qjlI%3D@[::1]:8888#ss2022");

        p.Shadowsocks!.Method.Should().Be("2022-blake3-aes-128-gcm");
        p.Credentials.Password.Should().Be("YctPZ6U7xPPcU+gp3u+0tx/tRizJN9K8y+uKlW2qjlI=");
        p.Server.Should().Be("::1");
    }

    [Fact]
    public void Shadowsocks_legacy_full_base64()
    {
        var p = Link("ss://" + B64("chacha20-ietf-poly1305:pw@198.51.100.7:8388") + "#legacy");

        p.Shadowsocks!.Method.Should().Be("chacha20-ietf-poly1305");
        p.Credentials.Password.Should().Be("pw");
        p.Server.Should().Be("198.51.100.7");
        p.Port.Should().Be(8388);
        p.Name.Should().Be("legacy");
    }

    [Fact]
    public void Socks_base64_userinfo_and_http_plain()
    {
        var s = Link("socks://" + B64("user:pass") + "@127.0.0.1:1080#s");
        s.Protocol.Should().Be(ProxyProtocol.Socks);
        s.Credentials.Username.Should().Be("user");
        s.Credentials.Password.Should().Be("pass");

        var h = Link("https://u:p@proxy.example.com:8443#h");
        h.Protocol.Should().Be(ProxyProtocol.Http);
        h.Tls.Enabled.Should().BeTrue();
        h.Port.Should().Be(8443);
    }

    [Fact]
    public void Hysteria2_with_obfs_and_port_hopping()
    {
        var p = Link("hysteria2://letmein@hy.example.com:443,20000-30000/?sni=hy.example.com&obfs=salamander&obfs-password=ob&insecure=1#hy2");

        p.Protocol.Should().Be(ProxyProtocol.Hysteria2);
        p.Port.Should().Be(443);
        p.Credentials.Password.Should().Be("letmein");
        p.Hysteria2!.ObfsType.Should().Be("salamander");
        p.Hysteria2.ObfsPassword.Should().Be("ob");
        p.Hysteria2.ServerPorts.Should().Contain("20000:30000");
        p.Tls.AllowInsecure.Should().BeTrue();
    }

    [Fact]
    public void Tuic_uuid_and_password()
    {
        var p = Link($"tuic://{Uuid}:pw@tuic.example.com:443?congestion_control=bbr&udp_relay_mode=native&alpn=h3&sni=tuic.example.com#t");

        p.Protocol.Should().Be(ProxyProtocol.Tuic);
        p.Credentials.Uuid.Should().Be(Uuid);
        p.Credentials.Password.Should().Be("pw");
        p.Tuic!.CongestionControl.Should().Be("bbr");
        p.Tls.Alpn.Should().Equal("h3");
    }

    [Fact]
    public void WireGuard_link()
    {
        const string key = "iKf1k5ZEBJmZzqBqBpX1l+ZuHqFSh3b8cXK7N3r4t2Y=";
        var p = Link($"wireguard://{Uri.EscapeDataString(key)}@162.159.192.1:2408?publickey=bmXOC%2BF1FxEMF9dyiK2H5%2F1SUtzH0JuVo51h2wPfgyo%3D&address=172.16.0.2%2F32,2606:4700:110:8a36::1&reserved=1,2,3&mtu=1280#wg");

        p.Protocol.Should().Be(ProxyProtocol.WireGuard);
        p.WireGuard!.PrivateKey.Should().Be(key);
        p.WireGuard.PeerPublicKey.Should().Be("bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=");
        p.WireGuard.LocalAddresses.Should().Equal("172.16.0.2/32", "2606:4700:110:8a36::1/128");
        p.WireGuard.Reserved.Should().Equal(1, 2, 3);
        p.WireGuard.Mtu.Should().Be(1280);
    }

    [Theory]
    [InlineData("vless://@example.com:443?type=tcp")]
    [InlineData("vless://11111111-2222-4333-8444-555555555555@example.com:99999")]
    [InlineData("vless://11111111-2222-4333-8444-555555555555@example.com:443?type=carrier-pigeon")]
    [InlineData("vless://11111111-2222-4333-8444-555555555555@:443")]
    [InlineData("vless://11111111-2222-4333-8444-555555555555@[2001:db8::1:443")]
    [InlineData("trojan://@example.com:443")]
    [InlineData("ss://not-base64-and-no-at")]
    [InlineData("vmess://%%%")]
    [InlineData("vless://11111111-2222-4333-8444-555555555555@example.com:443?security=quantum")]
    public void Malformed_links_throw_profile_parse_exception(string link)
    {
        var act = () => Link(link);

        act.Should().Throw<ProfileParseException>();
    }

    [Fact]
    public void Control_characters_are_rejected()
    {
        var act = () => Link("vless://" + Uuid + "@example.com:443?type=ws&host=a" + (char)0x0A + "b");

        act.Should().Throw<ProfileParseException>();
    }

    [Fact]
    public void Fingerprint_ignores_name_but_changes_with_identity()
    {
        var a = Link($"vless://{Uuid}@example.com:443?type=ws&path=%2Fa#Name1");
        var b = Link($"vless://{Uuid}@example.com:443?type=ws&path=%2Fa#Name2");
        var c = Link($"vless://{Uuid}@example.com:443?type=ws&path=%2Fb#Name1");

        a.Fingerprint.Should().Be(b.Fingerprint);
        a.Fingerprint.Should().NotBe(c.Fingerprint);
        ProfileIdentity.ComputeEndpointKey(a).Should().NotBe(ProfileIdentity.ComputeEndpointKey(c));
    }
}
