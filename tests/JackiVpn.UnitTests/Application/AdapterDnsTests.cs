using FluentAssertions;
using JackiVpn.Application.Connectivity;
using JackiVpn.Domain.Network;
using Microsoft.Extensions.Logging.Abstractions;

namespace JackiVpn.UnitTests.Application;

public class AdapterDnsTests
{
    private const string WifiId = "{12345678-ABCD-4ABC-8ABC-0123456789AB}";

    [Theory]
    [InlineData(WifiId, "1.1.1.1", true)]
    [InlineData("wlan0", "1.1.1.1 2606:4700:4700::1111", true)]
    [InlineData(WifiId, "", true)]
    [InlineData(WifiId, "1.1.1.1 1.1.1.1", false)]
    [InlineData(WifiId, "1.1.1.1 8.8.8.8 9.9.9.9 1.0.0.1 8.8.4.4", false)]
    [InlineData(WifiId, "dns.google", false)]
    [InlineData(WifiId, "1.1.1.1;calc.exe", false)]
    [InlineData(WifiId, "fe80::1%12", false)]
    [InlineData("wlan0 & rm", "1.1.1.1", false)]
    [InlineData("-o ProxyCommand", "1.1.1.1", false)]
    public void Changes_are_checked_before_anything_privileged_runs(string id, string servers, bool ok)
    {
        var change = new AdapterDnsChange(id, servers.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        (change.Problem() is null).Should().Be(ok);
    }

    [Fact]
    public void Typed_lists_are_split_on_commas_spaces_and_semicolons()
    {
        AdapterDnsChange.Parse(" 1.1.1.1, 8.8.8.8;9.9.9.9 ").Should().Equal("1.1.1.1", "8.8.8.8", "9.9.9.9");
    }

    [Fact]
    public async Task First_change_remembers_the_original_and_restore_puts_it_back()
    {
        var path = Path.Combine(Path.GetTempPath(), "ibox-adapter-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var control = new FakeControl();
            var service = new AdapterDnsService(control, path, NullLogger.Instance);
            var wifi = new AdapterDns { Id = WifiId, Name = "Wi-Fi", Automatic = false, Servers = ["192.168.1.1"] };

            (await service.SetAsync(wifi, ["1.1.1.1"], CancellationToken.None)).Should().BeNull();
            (await service.SetAsync(wifi with { Servers = ["1.1.1.1"] }, ["8.8.8.8"], CancellationToken.None)).Should().BeNull();
            service.CanRestore(WifiId).Should().BeTrue();

            // A new instance (app restarted) still knows the original.
            var again = new AdapterDnsService(control, path, NullLogger.Instance);
            await again.RestoreAsync(wifi, CancellationToken.None);
            control.Changes.Last().Servers.Should().Equal("192.168.1.1");
            again.CanRestore(WifiId).Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Bad_input_never_reaches_the_platform()
    {
        var control = new FakeControl();
        var service = new AdapterDnsService(control, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), NullLogger.Instance);
        (await service.SetAsync(new AdapterDns { Id = WifiId, Name = "Wi-Fi" }, ["not-an-ip"], CancellationToken.None)).Should().NotBeNull();
        control.Changes.Should().BeEmpty();
    }

    [Fact]
    public void Presets_are_valid_addresses()
    {
        AdapterDnsPresets.BuiltIn.SelectMany(p => p.Servers).Should().OnlyContain(s => AdapterDnsChange.IsAddress(s));
    }

    private sealed class FakeControl : IAdapterDnsControl
    {
        public List<AdapterDnsChange> Changes { get; } = [];

        public Task<IReadOnlyList<AdapterDns>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AdapterDns>>([]);

        public Task SetAsync(AdapterDnsChange change, CancellationToken ct)
        {
            Changes.Add(change);
            return Task.CompletedTask;
        }
    }
}
