using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Routing;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Routing;

public sealed record RuleSetDownload(int StatusCode, byte[]? Content, string? ETag, string? LastModified);

/// <summary>HTTP transport for rule-sets, implemented by the application layer (SSRF-safe fetcher).</summary>
public interface IRuleSetDownloader
{
    Task<RuleSetDownload> DownloadAsync(Uri url, string? etag, string? lastModified, long maxBytes, TimeSpan timeout, CancellationToken ct);
}

public sealed record RuleSetStatus(string Tag, bool Available, string? LocalPath, RuleSetCacheEntry? Entry, string? Error, bool UsedStaleCache);

/// <summary>
/// Maintains a local cache of remote rule-sets: conditional updates, SHA-256 pinning, format validation,
/// atomic replacement, and fallback to the last healthy cached copy when the remote is unavailable.
/// </summary>
public sealed class RuleSetManager
{
    public const long MaxRuleSetBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Storage;
    private readonly string _cacheDir;
    private readonly IRuleSetDownloader _downloader;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RuleSetManager(string cacheDir, IRuleSetDownloader downloader, ILogger logger, TimeProvider? time = null)
    {
        _cacheDir = cacheDir;
        _downloader = downloader;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Read-only folder with rule-sets shipped inside the app (<c>&lt;tag&gt;.srs</c>). Used when nothing is cached
    /// yet, so the first connection never has to wait for a download (which may be blocked before the VPN is up).
    /// </summary>
    public string? SeedDirectory { get; init; }

    /// <summary>
    /// Local only, no network: the cached copy (even if due for an update) or the shipped seed. Connecting uses
    /// this; <see cref="EnsureAsync"/> refreshes in the background afterwards.
    /// </summary>
    public async Task<RuleSetStatus> GetLocalAsync(RuleSetDefinition def, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(def);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_cacheDir);
            var path = PathFor(def);
            var meta = ReadMeta(def);
            if (meta is not null && File.Exists(path) && VerifyCachedFile(path, meta))
            {
                var due = _time.GetUtcNow() - meta.FetchedUtc >= def.UpdateInterval;
                return new RuleSetStatus(def.Tag, true, path, meta, null, due);
            }

            var seed = SeedDirectory is null ? null : Path.Combine(SeedDirectory, SafeTag(def.Tag) + (def.Format == RuleSetFormat.Binary ? ".srs" : ".json"));
            if (seed is null || !File.Exists(seed))
            {
                return new RuleSetStatus(def.Tag, false, null, null, "not downloaded yet", false);
            }

            var content = await File.ReadAllBytesAsync(seed, ct).ConfigureAwait(false);
            var (valid, version, error) = ValidateContent(content, def.Format);
            if (!valid)
            {
                return new RuleSetStatus(def.Tag, false, null, null, "shipped copy invalid: " + error, false);
            }

            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, content, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            var entry = new RuleSetCacheEntry
            {
                Tag = def.Tag,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
                Size = content.LongLength,
                FetchedUtc = DateTimeOffset.UnixEpoch, // refresh at the first chance
                Format = def.Format,
                Version = version,
            };
            WriteMeta(def, entry);
            _logger.LogInformation("Rule-set {Tag}: using the copy shipped with the app until it is updated.", def.Tag);
            return new RuleSetStatus(def.Tag, true, path, entry, null, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string PathFor(RuleSetDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);
        return Path.Combine(_cacheDir, SafeTag(def.Tag) + (def.Format == RuleSetFormat.Binary ? ".srs" : ".json"));
    }

    private string MetaPathFor(RuleSetDefinition def) => Path.Combine(_cacheDir, SafeTag(def.Tag) + ".meta.json");

    public async Task<IReadOnlyList<RuleSetStatus>> EnsureAsync(IEnumerable<RuleSetDefinition> definitions, bool force, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var results = new List<RuleSetStatus>();
        foreach (var def in definitions.Where(d => d.Enabled))
        {
            results.Add(await EnsureOneAsync(def, force, ct).ConfigureAwait(false));
        }

        return results;
    }

    public async Task<RuleSetStatus> EnsureOneAsync(RuleSetDefinition def, bool force, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(def);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_cacheDir);
            var path = PathFor(def);
            var meta = ReadMeta(def);
            var cachedValid = meta is not null && File.Exists(path) && VerifyCachedFile(path, meta);
            var fresh = cachedValid && _time.GetUtcNow() - meta!.FetchedUtc < def.UpdateInterval;
            if (fresh && !force)
            {
                return new RuleSetStatus(def.Tag, true, path, meta, null, false);
            }

            if (!Uri.TryCreate(def.Url, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            {
                return Fallback(def, path, meta, cachedValid, "Rule-set URL must be http(s).");
            }

            RuleSetDownload download;
            try
            {
                download = await _downloader.DownloadAsync(url, cachedValid ? meta!.ETag : null, cachedValid ? meta!.LastModified : null, MaxRuleSetBytes, DownloadTimeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Fallback(def, path, meta, cachedValid, "Download failed: " + ex.Message);
            }

            if (download.StatusCode == 304 && cachedValid)
            {
                var touched = meta! with { FetchedUtc = _time.GetUtcNow() };
                WriteMeta(def, touched);
                return new RuleSetStatus(def.Tag, true, path, touched, null, false);
            }

            if (download.StatusCode is < 200 or >= 300 || download.Content is null)
            {
                return Fallback(def, path, meta, cachedValid, $"HTTP {download.StatusCode}.");
            }

            var sha = Convert.ToHexStringLower(SHA256.HashData(download.Content));
            if (def.ExpectedSha256 is { Length: > 0 } pin && !string.Equals(pin, sha, StringComparison.OrdinalIgnoreCase))
            {
                return Fallback(def, path, meta, cachedValid, "Checksum mismatch; download rejected.");
            }

            var (valid, version, error) = ValidateContent(download.Content, def.Format);
            if (!valid)
            {
                return Fallback(def, path, meta, cachedValid, "Invalid rule-set: " + error);
            }

            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, download.Content, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            var entry = new RuleSetCacheEntry
            {
                Tag = def.Tag,
                Sha256 = sha,
                Size = download.Content.LongLength,
                FetchedUtc = _time.GetUtcNow(),
                ETag = download.ETag,
                LastModified = download.LastModified,
                Format = def.Format,
                Version = version,
            };
            WriteMeta(def, entry);
            _logger.LogInformation("Rule-set {Tag} updated ({Size} bytes, sha256 {Sha}).", def.Tag, entry.Size, sha[..12]);
            return new RuleSetStatus(def.Tag, true, path, entry, null, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private RuleSetStatus Fallback(RuleSetDefinition def, string path, RuleSetCacheEntry? meta, bool cachedValid, string error)
    {
        if (cachedValid)
        {
            _logger.LogWarning("Rule-set {Tag}: {Error} Using the last healthy cached copy from {Fetched:u}.", def.Tag, error, meta!.FetchedUtc);
            return new RuleSetStatus(def.Tag, true, path, meta, error, true);
        }

        _logger.LogWarning("Rule-set {Tag} unavailable: {Error}", def.Tag, error);
        return new RuleSetStatus(def.Tag, false, null, null, error, false);
    }

    /// <summary>
    /// Binary: sing-box SRS header ("SRS" + version byte) followed by a zlib stream that must inflate.
    /// Source: JSON object with numeric "version" and a "rules" array.
    /// </summary>
    public static (bool Valid, int Version, string? Error) ValidateContent(byte[] content, RuleSetFormat format)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length is 0 or > (int)MaxRuleSetBytes)
        {
            return (false, 0, "empty or too large");
        }

        if (format == RuleSetFormat.Binary)
        {
            if (content.Length < 6 || content[0] != (byte)'S' || content[1] != (byte)'R' || content[2] != (byte)'S')
            {
                return (false, 0, "missing SRS magic header");
            }

            var version = content[3];
            if (version is 0 or > 8)
            {
                return (false, version, $"unsupported SRS version {version}");
            }

            try
            {
                using var ms = new MemoryStream(content, 4, content.Length - 4);
                using var z = new ZLibStream(ms, CompressionMode.Decompress);
                var buffer = new byte[64 * 1024];
                long total = 0;
                int n;
                while ((n = z.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += n;
                    if (total > 256L * 1024 * 1024)
                    {
                        return (false, version, "decompressed size exceeds 256 MB");
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                return (false, version, "corrupt compressed body: " + ex.Message);
            }

            return (true, version, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(content, JsonDefaults.UntrustedDocument);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number
                || !doc.RootElement.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            {
                return (false, 0, "source rule-set must have numeric 'version' and a 'rules' array");
            }

            return (true, v.GetInt32(), null);
        }
        catch (JsonException ex)
        {
            return (false, 0, "invalid JSON: " + ex.Message);
        }
    }

    private static bool VerifyCachedFile(string path, RuleSetCacheEntry meta)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return string.Equals(Convert.ToHexStringLower(SHA256.HashData(fs)), meta.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private RuleSetCacheEntry? ReadMeta(RuleSetDefinition def)
    {
        var p = MetaPathFor(def);
        if (!File.Exists(p))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RuleSetCacheEntry>(File.ReadAllText(p), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void WriteMeta(RuleSetDefinition def, RuleSetCacheEntry entry)
    {
        var p = MetaPathFor(def);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entry, JsonOptions));
        File.Move(tmp, p, overwrite: true);
    }

    public static string SafeTag(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var chars = tag.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        var s = new string(chars);
        return s.Length is 0 or > 80 ? Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tag)))[..32] : s;
    }
}
