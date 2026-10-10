using System.Text;
using FluentAssertions;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Profiles;
using JackiVpn.Subscriptions.Parsing;
using static JackiVpn.UnitTests.TestSupport;

namespace JackiVpn.UnitTests.Parsing;

public class PipelineTests
{
    private const string Uuid = "11111111-2222-4333-8444-555555555555";

    private static readonly string PlainList = string.Join('\n',
        $"vless://{Uuid}@a.example.com:443?type=ws&security=tls#A",
        "# comment line",
        "",
        "trojan://pw@b.example.com:443#B",
        "ss://" + B64Url("aes-128-gcm:x") + "@c.example.com:8388#C");

    [Fact]
    public void Plain_subscription_is_parsed()
    {
        var r = Parse(PlainList);

        r.Format.Should().Be(ConfigFormat.UriList);
        r.Profiles.Should().HaveCount(3);
        r.Profiles.Select(p => p.Name).Should().Equal("A", "B", "C");
        r.Failed.Should().Be(0);
    }

    [Fact]
    public void Base64_subscription_is_parsed_with_and_without_padding_and_line_breaks()
    {
        var b64 = B64(PlainList);
        var wrapped = string.Join("\r\n", Enumerable.Range(0, (b64.Length + 75) / 76).Select(i => b64.Substring(i * 76, Math.Min(76, b64.Length - (i * 76)))));

        foreach (var variant in new[] { b64, b64.TrimEnd('='), wrapped, B64Url(PlainList) })
        {
            var r = Parse(variant);
            r.Format.Should().Be(ConfigFormat.Base64UriList);
            r.Profiles.Should().HaveCount(3);
        }
    }

    [Fact]
    public void Invalid_base64_is_reported_not_thrown()
    {
        var r = Parse("@@@not base64 at all@@@");

        r.HasProfiles.Should().BeFalse();
        r.Issues.Should().Contain(i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Html_page_is_never_treated_as_config()
    {
        var r = Parse("<!DOCTYPE html><html><head><title>Subscription</title></head><body>vless://x@y:1</body></html>");

        r.Format.Should().Be(ConfigFormat.Html);
        r.IsHtml.Should().BeTrue();
        r.HasProfiles.Should().BeFalse();
    }

    [Fact]
    public void Broken_lines_do_not_discard_good_ones()
    {
        var r = Parse(PlainList + "\nvless://broken\nhysteria://unsupported@x:1\n");

        r.Profiles.Should().HaveCount(3);
        r.Issues.Should().Contain(i => i.Severity == IssueSeverity.Error && i.Line == 6);
        r.Issues.Should().Contain(i => i.Severity == IssueSeverity.Warning && i.Message.Contains("Unsupported link scheme"));
    }

    [Fact]
    public void Issue_snippets_are_redacted()
    {
        var r = Parse($"vless://{Uuid}@example.com:99999?type=tcp#x");

        r.Issues.Should().ContainSingle();
        r.Issues[0].RedactedSnippet.Should().NotContain(Uuid);
    }

    [Fact]
    public void Entry_limit_is_enforced()
    {
        var many = string.Join('\n', Enumerable.Range(1, 50).Select(i => $"trojan://pw@h{i}.example.com:443#n{i}"));
        var r = new ConfigParserPipeline().Parse(many, Ctx with { Limits = new ParseLimits { MaxEntries = 10 } });

        r.Profiles.Should().HaveCount(10);
        r.Issues.Should().Contain(i => i.Message.Contains("Entry limit"));
    }

    [Fact]
    public void Oversized_input_is_rejected()
    {
        var r = new ConfigParserPipeline().Parse(new string('A', 2000), Ctx with { Limits = new ParseLimits { MaxInputChars = 1000 } });

        r.HasProfiles.Should().BeFalse();
        r.Issues.Single().Message.Should().Contain("too large");
    }

    [Fact]
    public void Nested_base64_depth_is_bounded()
    {
        var nested = B64(B64(B64(PlainList)));

        Parse(nested).HasProfiles.Should().BeFalse("three nested encodings exceed MaxNestedDecodes=2");
        Parse(B64(B64(PlainList))).Profiles.Should().HaveCount(3);
    }

    [Fact]
    public void Duplicate_configs_produce_identical_fingerprints()
    {
        var r = Parse(string.Join('\n', $"vless://{Uuid}@a.example.com:443?type=ws#one", $"vless://{Uuid}@a.example.com:443?type=ws#two"));

        r.Profiles.Should().HaveCount(2);
        r.Profiles[0].Fingerprint.Should().Be(r.Profiles[1].Fingerprint);
    }

    [Fact]
    public void Invalid_utf8_after_base64_decode_is_rejected()
    {
        var bytes = new byte[] { 0xC3, 0x28, 0xA0, 0xA1, 0xFF, 0xFE, 0x76, 0x6C };
        var r = Parse(Convert.ToBase64String(bytes));

        r.HasProfiles.Should().BeFalse();
    }

    [Fact]
    public void Empty_input()
    {
        Parse("   \r\n  ").Issues.Single().Message.Should().Contain("empty");
    }

    [Fact]
    public void Bom_is_ignored()
    {
        var withBom = Encoding.UTF8.GetString(Encoding.UTF8.GetPreamble()) + PlainList;
        Parse(withBom).Profiles.Should().HaveCount(3);
    }
}
