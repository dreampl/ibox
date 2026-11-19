using System.Text;
using FluentAssertions;
using JackiVpn.Application.Account;
using JackiVpn.Security;
using Xunit;

namespace JackiVpn.UnitTests.Application;

/// <summary>The server mini apps inside IBOX: page addresses and what the page may make IBOX do.</summary>
public sealed class MiniAppPagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ibox-miniapp-" + Guid.NewGuid().ToString("N"));

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(string plaintext, string purpose) => Encoding.UTF8.GetBytes(plaintext);

        public string Unprotect(byte[] protectedData, string purpose) => Encoding.UTF8.GetString(protectedData);
    }

    [Fact]
    public void Subscription_page_is_the_panel_mini_app_with_the_session_in_the_fragment_only()
    {
        using var ibox = new IboxServices(_dir, new XorProtector());
        MiniAppPages.Subscription(ibox).Should().BeNull("signed out");
        ibox.Tokens.Set(IboxAccount.TokenName, "ibx1.abc-DEF_123");
        var page = MiniAppPages.Subscription(ibox, "fa")!;
        page.GetLeftPart(UriPartial.Path).Should().Be("https://sub.jackidrive.ir:2083/sub/_jp/app");
        page.Query.Should().BeEmpty("the session never goes to the server in the address");
        page.Fragment.Should().Be("#ibox=ibx1.abc-DEF_123&lang=fa");
    }

    [Theory]
    [InlineData("https://sub.jackidrive.ir:2083/sub/_jp/app?x=1", MiniAppAction.None, null)]
    [InlineData("ibox://open?u=https%3A%2F%2Ft.me%2FJacki_core_bot", MiniAppAction.OpenOutside, "https://t.me/Jacki_core_bot")]
    [InlineData("ibox://open?u=javascript%3Aalert(1)", MiniAppAction.None, null)]
    [InlineData("ibox://close", MiniAppAction.Close, null)]
    [InlineData("ibox://signedout", MiniAppAction.SignedOut, null)]
    [InlineData("https://zarinpal.example/pay", MiniAppAction.OpenOutside, "https://zarinpal.example/pay")]
    [InlineData("https://sub.jackidrive.ir/other-port", MiniAppAction.OpenOutside, "https://sub.jackidrive.ir/other-port")]
    public void Navigations_stay_inside_only_on_the_page_origin(string url, MiniAppAction action, string? target)
    {
        var (a, t) = MiniAppPages.Classify(new Uri("https://sub.jackidrive.ir:2083/sub/_jp/app#x"), url);
        a.Should().Be(action);
        t?.AbsoluteUri.TrimEnd('/').Should().Be(target);
        if (target is null)
        {
            t.Should().BeNull();
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
