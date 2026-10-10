using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using JackiVpn.Domain.Connection;
using JackiVpn.Security;

namespace JackiVpn.Cores.Hosting;

public sealed record CoreManifestFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size);

public sealed record CoreManifestEntry(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("license")] string? License,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("files")] IReadOnlyList<CoreManifestFile> Files);

public sealed record CoreManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("cores")] IReadOnlyDictionary<string, CoreManifestEntry> Cores);

public sealed class CoreIntegrityException(string message) : Exception(message);

/// <summary>
/// A verified core binary. While the lease is alive the executable is held open with read-only sharing,
/// so it cannot be replaced between hash verification and process creation (TOCTOU).
/// </summary>
public sealed class VerifiedCoreBinary : IDisposable
{
    private readonly FileStream _lock;

    internal VerifiedCoreBinary(CoreKind kind, string path, string sha256, string manifestVersion, FileStream lockHandle)
    {
        Kind = kind;
        ExecutablePath = path;
        Sha256 = sha256;
        ManifestVersion = manifestVersion;
        _lock = lockHandle;
    }

    public CoreKind Kind { get; }
    public string ExecutablePath { get; }
    public string Sha256 { get; }
    public string ManifestVersion { get; }

    public void Dispose() => _lock.Dispose();
}

/// <summary>Resolves core executables from the trusted install directory and verifies them against the manifest.</summary>
public sealed class CoreBinaryCatalog
{
    private readonly string _coresDir;
    private readonly string _manifestPath;
    private readonly Lazy<CoreManifest> _manifest;

    /// <param name="coresDirectory">Directory holding the core executables.</param>
    /// <param name="manifestPath">
    /// Manifest location when it cannot live next to the binaries (Android: executables are in the read-only
    /// native library directory of the signed APK).
    /// </param>
    public CoreBinaryCatalog(string coresDirectory, string? manifestPath = null)
    {
        _coresDir = Path.GetFullPath(coresDirectory);
        _manifestPath = manifestPath ?? Path.Combine(_coresDir, "manifest.json");
        _manifest = new Lazy<CoreManifest>(LoadManifest, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string CoresDirectory => _coresDir;

    public static string ManifestId(CoreKind kind) => kind switch
    {
        CoreKind.SingBox => "sing-box",
        CoreKind.Xray => "xray",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// Platform file name. Android only executes files from the APK's native library directory, so the cores are
    /// packaged there as <c>lib*.so</c> (they are standalone executables, not libraries).
    /// </summary>
    public static string ExecutableName(CoreKind kind) => (kind, OperatingSystem.IsAndroid(), OperatingSystem.IsWindows()) switch
    {
        (CoreKind.SingBox, true, _) => "libsingbox.so",
        (CoreKind.Xray, true, _) => "libxray.so",
        (CoreKind.SingBox, _, true) => "sing-box.exe",
        (CoreKind.Xray, _, true) => "xray.exe",
        (CoreKind.SingBox, _, _) => "sing-box",
        (CoreKind.Xray, _, _) => "xray",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public string PathOf(CoreKind kind) => SafePath.CombineUnderRoot(_coresDir, ExecutableName(kind));

    public bool IsInstalled(CoreKind kind)
    {
        if (!File.Exists(PathOf(kind)) || !File.Exists(_manifestPath))
        {
            return false;
        }

        try
        {
            return ManifestEntry(kind) is not null;
        }
        catch (CoreIntegrityException)
        {
            return false;
        }
    }

    public CoreManifestEntry? ManifestEntry(CoreKind kind) =>
        _manifest.Value.Cores.TryGetValue(ManifestId(kind), out var e) ? e : null;

    public string? WintunPath => File.Exists(Path.Combine(_coresDir, "wintun.dll")) ? Path.Combine(_coresDir, "wintun.dll") : null;

    /// <summary>Verifies the executable (and its companion DLLs) and returns a lease that pins the file.</summary>
    public VerifiedCoreBinary Verify(CoreKind kind)
    {
        var entry = ManifestEntry(kind) ?? throw new CoreIntegrityException($"{ManifestId(kind)} is not listed in the core manifest.");
        var exeName = ExecutableName(kind);
        var exeEntry = entry.Files.FirstOrDefault(f => string.Equals(f.Path, exeName, StringComparison.OrdinalIgnoreCase))
            ?? throw new CoreIntegrityException($"Manifest has no hash for {exeName}.");

        // Companion DLLs (e.g. libcronet.dll) are verified too; they are loaded from the same directory.
        foreach (var dll in entry.Files.Where(f => f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            var dllPath = SafePath.CombineUnderRoot(_coresDir, dll.Path);
            if (!File.Exists(dllPath) || !FileIntegrity.DigestEquals(FileIntegrity.ComputeSha256(dllPath), dll.Sha256))
            {
                throw new CoreIntegrityException($"{dll.Path} is missing or does not match the expected SHA-256.");
            }
        }

        var path = PathOf(kind);
        if (!File.Exists(path))
        {
            throw new CoreIntegrityException($"{exeName} is not installed at {path}.");
        }

        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        try
        {
            if (fs.Length != exeEntry.Size)
            {
                throw new CoreIntegrityException($"{exeName} size does not match the manifest.");
            }

            var hash = Convert.ToHexStringLower(SHA256.HashData(fs));
            if (!FileIntegrity.DigestEquals(hash, exeEntry.Sha256))
            {
                throw new CoreIntegrityException($"{exeName} SHA-256 mismatch: the binary was modified or replaced. Refusing to run it.");
            }

            return new VerifiedCoreBinary(kind, path, hash, entry.Version, fs);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    private CoreManifest LoadManifest()
    {
        var manifestPath = _manifestPath;
        if (!File.Exists(manifestPath))
        {
            throw new CoreIntegrityException($"Core manifest not found at {manifestPath}. Run scripts/fetch-cores.ps1.");
        }

        try
        {
            return JsonSerializer.Deserialize<CoreManifest>(File.ReadAllText(manifestPath))
                ?? throw new CoreIntegrityException("Core manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new CoreIntegrityException("Core manifest is corrupt: " + ex.Message);
        }
    }
}
