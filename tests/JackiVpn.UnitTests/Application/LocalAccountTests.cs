using System.Text;
using FluentAssertions;
using JackiVpn.Application.Accounts;
using JackiVpn.Application.Settings;
using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Settings;
using JackiVpn.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace JackiVpn.UnitTests.Application;

public sealed class LocalAccountTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ibox-accounts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static SettingsStore Store() => new(new MemorySettings(), NullLogger.Instance);

    private async Task<(LocalAccountService Accounts, SettingsStore Settings)> StartAsync(SettingsStore? settings = null)
    {
        settings ??= Store();
        var accounts = new LocalAccountService(_dir, new XorProtector(), settings, NullLogger.Instance);
        await accounts.InitializeAsync(CancellationToken.None);
        return (accounts, settings);
    }

    [Fact]
    public async Task First_start_makes_one_profile_from_the_current_settings()
    {
        var (accounts, _) = await StartAsync();
        accounts.Accounts.Should().ContainSingle();
        accounts.Active.Should().NotBeNull();
    }

    [Fact]
    public async Task Each_profile_keeps_its_own_settings()
    {
        var (accounts, settings) = await StartAsync();
        settings.Update(s => s with { Routing = s.Routing with { Final = JackiVpn.Domain.Routing.RuleActionType.Direct } });
        var first = accounts.Active!.Id;

        var (work, problem) = await accounts.CreateAsync("Work", 2, copyCurrent: false, pin: null, CancellationToken.None);
        problem.Should().BeNull();
        (await accounts.SwitchAsync(work!.Id, null, CancellationToken.None)).Should().BeNull();
        settings.Current.Routing.Final.Should().Be(JackiVpn.Domain.Routing.RuleActionType.Proxy, "a new profile starts from the defaults");

        settings.Update(s => s with { Dns = s.Dns with { CacheEnabled = false } });
        (await accounts.SwitchAsync(first, null, CancellationToken.None)).Should().BeNull();
        settings.Current.Routing.Final.Should().Be(JackiVpn.Domain.Routing.RuleActionType.Direct);
        settings.Current.Dns.CacheEnabled.Should().BeTrue();

        (await accounts.SwitchAsync(work.Id, null, CancellationToken.None)).Should().BeNull();
        settings.Current.Dns.CacheEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Locked_profile_needs_its_pin_and_its_settings_are_not_readable_without_it()
    {
        var (accounts, settings) = await StartAsync();
        var (kids, _) = await accounts.CreateAsync("Kids", 4, copyCurrent: true, pin: "2468", CancellationToken.None);
        kids!.IsLocked.Should().BeTrue();

        // Restart: nothing unlocked any more.
        var (again, settings2) = await StartAsync(Store());
        (await again.SwitchAsync(kids.Id, null, CancellationToken.None)).Should().Be("Wrong PIN.");
        (await again.SwitchAsync(kids.Id, "1111", CancellationToken.None)).Should().Be("Wrong PIN.");
        (await again.SwitchAsync(kids.Id, "2468", CancellationToken.None)).Should().BeNull();
        again.Active!.Id.Should().Be(kids.Id);

        // The stored file holds no readable settings even after the OS layer is removed.
        var raw = await File.ReadAllBytesAsync(Path.Combine(_dir, kids.Id + ".settings"), TestContext.Current.CancellationToken);
        var outer = new XorProtector().Unprotect(raw, "ibox-profile:" + kids.Id);
        outer.Should().NotContain("Routing").And.NotContain("Dns");
        _ = settings;
        _ = settings2;
    }

    [Theory]
    [InlineData("12")]
    [InlineData("abcd")]
    [InlineData("1234567890123")]
    public void Pins_are_4_to_12_digits(string pin) => LocalAccountService.PinProblem(pin).Should().NotBeNull();

    [Fact]
    public async Task Active_profile_cannot_be_deleted()
    {
        var (accounts, _) = await StartAsync();
        (await accounts.DeleteAsync(accounts.Active!.Id, CancellationToken.None)).Should().NotBeNull();
    }

    private sealed class MemorySettings : ISettingsRepository
    {
        private AppSettings _s = new();

        public Task<AppSettings> LoadAsync(CancellationToken ct) => Task.FromResult(_s);

        public Task SaveAsync(AppSettings settings, CancellationToken ct)
        {
            _s = settings;
            return Task.CompletedTask;
        }
    }

    /// <summary>Stand-in for DPAPI: reversible, purpose-bound.</summary>
    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(string plaintext, string purpose) => Encoding.UTF8.GetBytes(plaintext).Select((b, i) => (byte)(b ^ purpose[i % purpose.Length])).ToArray();

        public string Unprotect(byte[] protectedData, string purpose) => Encoding.UTF8.GetString(protectedData.Select((b, i) => (byte)(b ^ purpose[i % purpose.Length])).ToArray());
    }
}
