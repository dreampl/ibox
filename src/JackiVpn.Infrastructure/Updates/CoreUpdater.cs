using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using JackiVpn.Security;

namespace JackiVpn.Infrastructure.Updates;

public sealed record ReleaseFile(
    [property: JsonPropertyName("archivePath")] string ArchivePath,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size);

/// <summary>One core version published in the update feed (cores are versioned independently of the app).</summary>
public sealed record CoreRelease(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("files")] IReadOnlyList<ReleaseFile> Files);

public sealed record AppRelease(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("notes")] string? Notes);

public sealed record UpdateManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("published")] DateTimeOffset Published,
    [property: JsonPropertyName("app")] AppRelease? App,
    [property: JsonPropertyName("cores")] IReadOnlyList<CoreRelease> Cores);

public sealed record UpdateResult(bool Success, string Message, string? BackupDirectory);

/// <summary>
/// Update feed verification: the manifest is accepted only with a valid ECDSA P-256 signature from the
/// pinned release key. Transport (HTTPS) is not trusted for integrity.
/// </summary>
public static class UpdateManifestVerifier
{
    public static UpdateManifest? Verify(byte[] manifestJson, byte[] signature, string pinnedPublicKeyPem)
    {
        ArgumentNullException.ThrowIfNull(manifestJson);
        ArgumentNullException.ThrowIfNull(signature);
        using var key = ECDsa.Create();
        key.ImportFromPem(pinnedPublicKeyPem);
        var ok = key.VerifyData(manifestJson, signature, HashAlgorithmName.SHA256, signature.Length == 64 ? DSASignatureFormat.IeeeP1363FixedFieldConcatenation : DSASignatureFormat.Rfc3279DerSequence);
        return ok ? JsonSerializer.Deserialize<UpdateManifest>(manifestJson) : null;
    }
}

/// <summary>
/// Installs a downloaded core release into the cores directory:
/// verify archive hash → extract to staging → verify every file hash/size → swap directories → re-verify
/// → on any failure roll back to the previous directory. The previous version is kept for manual rollback.
/// Must run elevated (the service) because the cores directory lives under Program Files.
/// </summary>
public sealed class CoreUpdater(string coresDirectory)
{
    private readonly string _cores = Path.GetFullPath(coresDirectory).TrimEnd(Path.DirectorySeparatorChar);

    public UpdateResult Install(CoreRelease release, byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(archive);
        var archiveHash = FileIntegrity.ComputeSha256(archive);
        if (!FileIntegrity.DigestEquals(archiveHash, release.Sha256))
        {
            return new UpdateResult(false, $"Archive hash mismatch for {release.Id} {release.Version}; update rejected.", null);
        }

        var parent = Path.GetDirectoryName(_cores)!;
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var staging = Path.Combine(parent, $"cores.staging-{stamp}");
        var backup = Path.Combine(parent, $"cores.backup-{stamp}");
        try
        {
            CopyDirectory(_cores, staging);
            using (var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read))
            {
                foreach (var file in release.Files)
                {
                    var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), file.ArchivePath, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"{file.ArchivePath} missing from archive.");
                    var target = SafePath.CombineUnderRoot(staging, file.Target);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                    var info = new FileInfo(target);
                    if (info.Length != file.Size || !FileIntegrity.DigestEquals(FileIntegrity.ComputeSha256(target), file.Sha256))
                    {
                        throw new InvalidDataException($"{file.Target} does not match its published hash/size.");
                    }
                }
            }

            UpdateManifestFile(Path.Combine(staging, "manifest.json"), release);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            TryDelete(staging);
            return new UpdateResult(false, $"Staging failed: {ex.Message}", null);
        }

        try
        {
            Directory.Move(_cores, backup);
            try
            {
                Directory.Move(staging, _cores);
                foreach (var file in release.Files)
                {
                    var p = SafePath.CombineUnderRoot(_cores, file.Target);
                    if (!FileIntegrity.DigestEquals(FileIntegrity.ComputeSha256(p), file.Sha256))
                    {
                        throw new InvalidDataException($"Post-swap verification failed for {file.Target}.");
                    }
                }
            }
            catch
            {
                // Roll back: restore the previous directory exactly.
                if (Directory.Exists(_cores))
                {
                    Directory.Move(_cores, staging + ".failed");
                }

                Directory.Move(backup, _cores);
                TryDelete(staging + ".failed");
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            TryDelete(staging);
            return new UpdateResult(false, $"Swap failed and was rolled back: {ex.Message}", null);
        }

        return new UpdateResult(true, $"{release.Id} updated to {release.Version}.", backup);
    }

    /// <summary>Restores a backup created by <see cref="Install"/>.</summary>
    public UpdateResult Rollback(string backupDirectory)
    {
        if (!Directory.Exists(backupDirectory))
        {
            return new UpdateResult(false, "Backup not found.", null);
        }

        var failed = _cores + ".rolledback-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        Directory.Move(_cores, failed);
        Directory.Move(backupDirectory, _cores);
        TryDelete(failed);
        return new UpdateResult(true, "Previous core version restored.", null);
    }

    private static void UpdateManifestFile(string path, CoreRelease release)
    {
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject();
        var cores = root["cores"] as JsonObject ?? new JsonObject();
        cores[release.Id] = new JsonObject
        {
            ["version"] = release.Version,
            ["archiveSha256"] = release.Sha256,
            ["files"] = new JsonArray(release.Files.Select(f => (JsonNode?)new JsonObject
            {
                ["path"] = f.Target.Replace('\\', '/'),
                ["sha256"] = f.Sha256,
                ["size"] = f.Size,
            }).ToArray()),
        };
        root["cores"] = cores;
        root["schemaVersion"] = 1;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, destination, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, destination, StringComparison.OrdinalIgnoreCase), overwrite: true);
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
