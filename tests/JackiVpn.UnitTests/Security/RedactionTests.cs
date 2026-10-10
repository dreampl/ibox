using FluentAssertions;
using JackiVpn.Security;
using static JackiVpn.UnitTests.TestSupport;

namespace JackiVpn.UnitTests.Security;

public class RedactionTests
{
    private const string Uuid = "11111111-2222-4333-8444-555555555555";
    private const string Token = "Zx9QkLm2Pq8RtY7uV6wX";

    [Fact]
    public void Subscription_url_token_is_masked()
    {
        var masked = SecretRedactor.MaskUrl($"https://panel.example.com/sub/{Token}/links?token=abc");

        masked.Should().StartWith("https://panel.example.com/sub/");
        masked.Should().NotContain(Token);
        masked.Should().Contain("/links");
        masked.Should().NotContain("abc");
    }

    [Fact]
    public void Urls_inside_log_lines_are_redacted()
    {
        var line = $"Fetching https://panel.example.com/sub/{Token} failed";

        SecretRedactor.Redact(line).Should().NotContain(Token).And.Contain("panel.example.com");
    }

    [Fact]
    public void Share_links_lose_credentials_and_secret_params()
    {
        var link = $"vless://{Uuid}@a.example.com:443?security=reality&pbk=PUBKEYVALUE&sid=abcd&type=tcp#name";
        var r = SecretRedactor.Redact("imported " + link);

        r.Should().NotContain(Uuid).And.NotContain("PUBKEYVALUE").And.NotContain("sid=abcd");
        r.Should().Contain("a.example.com:443");
    }

    [Fact]
    public void Vmess_and_legacy_ss_are_fully_masked()
    {
        SecretRedactor.Redact("vmess://" + B64("{\"id\":\"" + Uuid + "\"}")).Should().Be("vmess://***");
        SecretRedactor.Redact("ss://" + B64("aes-128-gcm:pw@1.2.3.4:1")).Should().StartWith("ss://***");
    }

    [Theory]
    [InlineData("{\"uuid\": \"secret-value\"}")]
    [InlineData("{\"password\":\"secret-value\"}")]
    [InlineData("{\"private_key\":\"secret-value\"}")]
    [InlineData("{\"privateKey\":\"secret-value\"}")]
    [InlineData("{\"short_id\":\"secret-value\"}")]
    [InlineData("{\"public_key\":\"secret-value\"}")]
    [InlineData("{\"obfs-password\":\"secret-value\"}")]
    [InlineData("{\"id\":\"secret-value\"}")]
    [InlineData("password=secret-value")]
    [InlineData("token: secret-value")]
    [InlineData("Authorization: Bearer secret-value")]
    [InlineData("Cookie: session=secret-value")]
    public void Secret_keys_are_redacted(string input)
    {
        SecretRedactor.Redact(input).Should().NotContain("secret-value");
    }

    [Fact]
    public void Bare_uuids_and_private_keys_are_redacted()
    {
        SecretRedactor.Redact("user " + Uuid + " connected").Should().NotContain(Uuid);
        var pem = "-----BEGIN PRIVATE KEY-----\nMIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg\n-----END PRIVATE KEY-----";
        SecretRedactor.Redact(pem).Should().NotContain("MIGHAgEAMBMG");
    }

    [Fact]
    public void Registered_secrets_are_redacted_anywhere()
    {
        const string secret = "my-very-unique-secret-42";
        SecretRedactor.Register(secret);
        try
        {
            SecretRedactor.Redact("core said: dial " + secret + " failed").Should().NotContain(secret);
        }
        finally
        {
            SecretRedactor.Unregister(secret);
        }
    }

    [Fact]
    public void Harmless_text_is_untouched()
    {
        const string text = "Connected to Germany-1 via sing-box 1.14.2 in 312 ms";
        SecretRedactor.Redact(text).Should().Be(text);
    }
}
