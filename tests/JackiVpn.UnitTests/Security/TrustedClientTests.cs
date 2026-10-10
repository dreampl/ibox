using System.Security.Principal;
using FluentAssertions;
using JackiVpn.Ipc;
using JackiVpn.Security;

namespace JackiVpn.UnitTests.Security;

/// <summary>The service only talks to the registered app: launcher exe and the assembly that actually runs.</summary>
public sealed class TrustedClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jacki-trust-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _exe;
    private readonly string _dll;

    public TrustedClientTests()
    {
        Directory.CreateDirectory(_dir);
        _exe = Path.Combine(_dir, "JackiVpn.exe");
        _dll = Path.Combine(_dir, "JackiVpn.dll");
        File.WriteAllText(_exe, "launcher stub");
        File.WriteAllText(_dll, "real application code");
    }

    private IpcClientContext Client(string? image = null) =>
        new(1234, image ?? _exe, WindowsIdentity.GetCurrent().User, 1);

    [Fact]
    public void Registered_exe_and_assembly_are_accepted()
    {
        var exeHash = FileIntegrity.ComputeSha256(_exe);
        var dllHash = FileIntegrity.ComputeSha256(_dll);
        var auth = new TrustedImageAuthorizer(() => (_exe, exeHash), () => dllHash);
        auth.Authorize(Client()).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Swapped_assembly_behind_an_unchanged_launcher_is_rejected()
    {
        var exeHash = FileIntegrity.ComputeSha256(_exe);
        var dllHash = FileIntegrity.ComputeSha256(_dll);
        var auth = new TrustedImageAuthorizer(() => (_exe, exeHash), () => dllHash);
        File.WriteAllText(_dll, "attacker code");
        File.SetLastWriteTimeUtc(_dll, DateTime.UtcNow.AddMinutes(1));

        var (allowed, reason) = auth.Authorize(Client());
        allowed.Should().BeFalse();
        reason.Should().Contain("assembly");
    }

    [Fact]
    public void Missing_assembly_is_rejected_when_one_is_registered()
    {
        var exeHash = FileIntegrity.ComputeSha256(_exe);
        var dllHash = FileIntegrity.ComputeSha256(_dll);
        var auth = new TrustedImageAuthorizer(() => (_exe, exeHash), () => dllHash);
        File.Delete(_dll);
        auth.Authorize(Client()).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Other_executables_and_unregistered_installs_are_rejected()
    {
        var other = Path.Combine(_dir, "Other.exe");
        File.WriteAllText(other, "launcher stub");
        var auth = new TrustedImageAuthorizer(() => (_exe, FileIntegrity.ComputeSha256(_exe)), () => null);
        auth.Authorize(Client(other)).Allowed.Should().BeFalse("same bytes, different path");
        new TrustedImageAuthorizer(() => (null, null), () => null).Authorize(Client()).Allowed.Should().BeFalse();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
