using FluentAssertions;
using JackiVpn.Application.Account;

namespace JackiVpn.UnitTests.Application;

public sealed class AccountControlInputTests
{
    [Theory]
    [InlineData("۲۵٫۵")]
    [InlineData("٢٥.٥")]
    [InlineData("25.5")]
    public void Member_volume_accepts_localized_digits(string value) => AccountInput.Gigabytes(value).Should().Be(25.5);
    [Theory]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("")]
    [InlineData("100001")]
    public void Invalid_volume_cannot_reach_the_family_api(string value) => ((Action)(() => AccountInput.Gigabytes(value))).Should().Throw<ArgumentException>();
    [Fact]
    public void Zero_removes_a_member_cap_but_cannot_create_an_unlimited_account()
    {
        AccountInput.Gigabytes("۰").Should().Be(0);
        ((Action)(() => AccountInput.Gigabytes("۰", allowZero: false))).Should().Throw<ArgumentException>();
    }
}
