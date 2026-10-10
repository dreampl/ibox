using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using JackiVpn.Domain;
using JackiVpn.Domain.Common;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Infrastructure.Updates;

public sealed record AppUpdateAsset(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size);

/// <summary>
/// The signed app update feed (<c>latest.json</c>). One file serves every platform:
/// <c>windows</c> is the MSI, <c>android</c> maps an ABI (arm64-v8a, armeabi-v7a, x86_64) to its APK.
/// </summary>
public sealed record AppUpdateFeed(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("published")] DateTimeOffset Published,
    [property: JsonPropertyName("mandatory")] bool Mandatory,
    [property: JsonPropertyName("notes")] IReadOnlyDictionary<string, string>? Notes,
    [property: JsonPropertyName("windows")] AppUpdateAsset? Windows,
    [property: JsonPropertyName("android")] IReadOnlyDictionary<string, AppUpdateAsset>? Android);

/// <summary>An update for this device: newer than the running version, with the asset for this platform.</summary>
public sealed record AppUpdate(Version Version, string VersionText, bool Mandatory, string? NotesEn, string? NotesFa, AppUpdateAsset Asset, DateTimeOffset Published)
{
    public string Notes(bool persian) => (persian ? NotesFa ?? NotesEn : NotesEn ?? NotesFa) ?? string.Empty;
}

public sealed class AppUpdateException(string message) : Exception(message);

/// <summary>
/// Checks the update feed and downloads installers. Trust comes only from the pinned release key: the feed must
/// carry a valid ECDSA P-256 signature, an installer must match the signed SHA-256 and size, and installers are
/// only fetched over HTTPS from the official domains. Older or equal versions are never offered (no downgrade
/// through a replayed old feed).
/// </summary>
public sealed class AppUpdateService : IDisposable
{
    private const int MaxFeedBytes = 64 * 1024;
    private const long MaxInstallerBytes = 400L * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly string _feedUrl;
    private readonly string _publicKeyPem;
    private readonly ILogger _logger;

    public AppUpdateService(ILogger logger, string? feedUrl = null, string? publicKeyPem = null, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _feedUrl = feedUrl ?? ProductInfo.UpdateFeedUrl;
        _publicKeyPem = publicKeyPem ?? ProductInfo.UpdatePublicKeyPem;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(ProductInfo.UserAgentProduct + "-Updater/1");
    }

    /// <summary>Hosts allowed for installers: HTTPS on the official domains (tests may add their own).</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = ProductInfo.OfficialProviderDomains;

    /// <summary>Allows plain HTTP (tests on loopback only).</summary>
    public bool AllowInsecureForTests { get; init; }

    /// <summary>Returns the update for this platform, or null when this version is current.</summary>
    /// <param name="platform">"windows" or "android".</param>
    /// <param name="abi">Android ABI (arm64-v8a, armeabi-v7a, x86_64); ignored on Windows.</param>
    public async Task<AppUpdate?> CheckAsync(Version current, string platform, string? abi, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(current);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        byte[] feed;
        byte[] signature;
        try
        {
            feed = await GetSmallAsync(_feedUrl, timeout.Token).ConfigureAwait(false);
            var sigText = System.Text.Encoding.ASCII.GetString(await GetSmallAsync(_feedUrl + ".sig", timeout.Token).ConfigureAwait(false)).Trim();
            signature = Convert.FromBase64String(sigText);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or FormatException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            throw new AppUpdateException("Could not reach the update server: " + ex.Message);
        }

        var parsed = Verify(feed, signature, _publicKeyPem) ?? throw new AppUpdateException("The update information is not signed by IBOX; ignored.");
        if (!Version.TryParse(parsed.Version, out var latest) || latest <= Normalize(current))
        {
            return null;
        }

        var asset = platform == "windows" ? parsed.Windows : abi is not null && parsed.Android is { } map && map.TryGetValue(abi, out var a) ? a : null;
        if (asset is null)
        {
            _logger.LogInformation("Update {Version} has no installer for {Platform}/{Abi}.", parsed.Version, platform, abi);
            return null;
        }

        ValidateUrl(asset.Url);
        if (asset.Sha256.Length != 64 || asset.Size is <= 0 or > MaxInstallerBytes)
        {
            throw new AppUpdateException("The update information is invalid.");
        }

        string? Note(string lang) => parsed.Notes is { } n && n.TryGetValue(lang, out var v) ? v : null;
        _logger.LogInformation("Update available: {Current} -> {Latest}.", current, latest);
        return new AppUpdate(latest, parsed.Version, parsed.Mandatory, Note("en"), Note("fa"), asset, parsed.Published);
    }

    /// <summary>
    /// Downloads the installer into <paramref name="directory"/> and verifies size and SHA-256 while streaming.
    /// Returns the verified file; anything that does not match is deleted.
    /// </summary>
    public async Task<string> DownloadAsync(AppUpdate update, string directory, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        ValidateUrl(update.Asset.Url);
        Directory.CreateDirectory(directory);
        var name = Path.GetFileName(new Uri(update.Asset.Url).AbsolutePath);
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            name = "JackiVpn-update-" + update.VersionText;
        }

        var target = Path.Combine(directory, name);
        var partial = target + ".part";
        try
        {
            using var response = await _http.GetAsync(update.Asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            ValidateUrl(response.RequestMessage?.RequestUri?.ToString() ?? update.Asset.Url); // redirects stay on official hosts
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > update.Asset.Size)
                    {
                        throw new AppUpdateException("The download is larger than published; rejected.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report((double)total / update.Asset.Size);
                }

                if (total != update.Asset.Size || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), update.Asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AppUpdateException("The download does not match the signed checksum; rejected.");
                }
            }

            File.Move(partial, target, overwrite: true);
            _logger.LogInformation("Update {Version} downloaded and verified.", update.VersionText);
            return target;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or AppUpdateException)
        {
            TryDelete(partial);
            throw ex as AppUpdateException ?? new AppUpdateException("Download failed: " + ex.Message);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>Feed bytes are accepted only with a valid signature from the pinned key.</summary>
    public static AppUpdateFeed? Verify(byte[] feed, byte[] signature, string publicKeyPem)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(signature);
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        var format = signature.Length == 64 ? DSASignatureFormat.IeeeP1363FixedFieldConcatenation : DSASignatureFormat.Rfc3279DerSequence;
        if (!key.VerifyData(feed, signature, HashAlgorithmName.SHA256, format))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<AppUpdateFeed>(feed);
            return parsed is { SchemaVersion: 1 } ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Signs feed bytes (release machine only).</summary>
    public static string Sign(byte[] feed, string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(key.SignData(feed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <summary>1.2 == 1.2.0 == 1.2.0.0 for comparisons.</summary>
    public static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    private void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (AllowInsecureForTests && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            || !(NetText.IsHostInDomains(uri.IdnHost, AllowedHosts) || (AllowInsecureForTests && uri.IsLoopback)))
        {
            throw new AppUpdateException("The update download address is not an official IBOX address; rejected.");
        }
    }

    private async Task<byte[]> GetSmallAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxFeedBytes)
            {
                throw new IOException("Update information is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => _http.Dispose();
}
