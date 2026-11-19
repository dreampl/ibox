using System.Text;
using FluentAssertions;
using JackiVpn.Application.Account;
using JackiVpn.Security;
using Xunit;

namespace JackiVpn.UnitTests.Application;

/// <summary>The optional first-run Telegram sign-in: offered once, never again after it was seen or used.</summary>
public sealed class IboxSignInStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ibox-signin-" + Guid.NewGuid().ToString("N"));

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(string plaintext, string purpose) => Encoding.UTF8.GetBytes(plaintext).Select((b, i) => (byte)(b ^ purpose[i % purpose.Length])).ToArray();

        public string Unprotect(byte[] protectedData, string purpose) => Encoding.UTF8.GetString(protectedData.Select((b, i) => (byte)(b ^ purpose[i % purpose.Length])).ToArray());
    }

    [Fact]
    public void Fresh_install_is_offered_sign_in_once()
    {
        using (var ibox = new IboxServices(_dir, new XorProtector()))
        {
            ibox.SignedIn.Should().BeFalse();
            ibox.ShouldOfferSignIn.Should().BeTrue();
            ibox.MarkSignInOffered();
            ibox.ShouldOfferSignIn.Should().BeFalse();
        }

        using var again = new IboxServices(_dir, new XorProtector());
        again.ShouldOfferSignIn.Should().BeFalse("\"Not now\" is remembered across launches");
        again.SignedIn.Should().BeFalse("skipping does not sign in: music and the subscription manager stay off");
    }

    [Fact]
    public void Signed_in_user_is_not_offered_sign_in()
    {
        using var ibox = new IboxServices(_dir, new XorProtector());
        ibox.Tokens.Set(IboxAccount.TokenName, "t0ken");
        ibox.SignedIn.Should().BeTrue();
        ibox.ShouldOfferSignIn.Should().BeFalse();
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
