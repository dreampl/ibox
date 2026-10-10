using System.Text.RegularExpressions;
using JackiVpn.Domain.Common;

namespace JackiVpn.Domain.Profiles;

/// <summary>
/// Structural validation of a normalized profile. Used on import, before persisting, and — critically —
/// by the privileged service on every request, so it must reject anything that could smuggle control
/// characters, paths or oversized values into a generated core configuration.
/// </summary>
public static partial class ProfileValidator
{
    public const int MaxNameLength = 256;
    public const int MaxFieldLength = 2048;

    public static readonly IReadOnlySet<string> KnownUtlsFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "firefox", "edge", "safari", "360", "qq", "ios", "android", "random", "randomized",
    };

    public static readonly IReadOnlySet<string> KnownShadowsocksMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "none", "aes-128-gcm", "aes-192-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305",
        "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305",
        "aes-128-ctr", "aes-192-ctr", "aes-256-ctr", "aes-128-cfb", "aes-192-cfb", "aes-256-cfb",
        "rc4-md5", "chacha20-ietf", "xchacha20",
    };

    public static ValidationResult Validate(ProxyProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var r = new ValidationResult();

        if (string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 128 || NetText.HasControlChars(p.Id))
        {
            r.Error(nameof(p.Id), "Profile id is missing or invalid.");
        }

        if (p.Name.Length > MaxNameLength || NetText.HasControlChars(p.Name))
        {
            r.Error(nameof(p.Name), "Name is too long or contains control characters.");
        }

        if (p.Protocol == ProxyProtocol.Unknown)
        {
            r.Error(nameof(p.Protocol), "Unknown protocol.");
        }

        if (!NetText.IsValidServerAddress(p.Server))
        {
            r.Error(nameof(p.Server), "Server must be a valid host name or IP address.");
        }

        if (!NetText.IsValidPort(p.Port))
        {
            r.Error(nameof(p.Port), "Port must be between 1 and 65535.");
        }

        CheckText(r, "Credentials.Uuid", p.Credentials.Uuid);
        CheckText(r, "Credentials.Password", p.Credentials.Password);
        CheckText(r, "Credentials.Username", p.Credentials.Username);
        CheckText(r, "Credentials.Flow", p.Credentials.Flow, 64);
        CheckText(r, "Credentials.Encryption", p.Credentials.Encryption, 64);

        switch (p.Protocol)
        {
            case ProxyProtocol.Vless:
            case ProxyProtocol.Vmess:
                if (string.IsNullOrWhiteSpace(p.Credentials.Uuid))
                {
                    r.Error("Credentials.Uuid", $"{p.Protocol} requires a user id.");
                }
                else if (!Guid.TryParse(p.Credentials.Uuid, out _) && p.Credentials.Uuid.Length > 30)
                {
                    // Xray maps arbitrary strings ≤ 30 bytes to UUIDv5; longer non-UUID ids are invalid.
                    r.Error("Credentials.Uuid", "User id is neither a UUID nor a short id string.");
                }

                if (p.Credentials.Flow is { Length: > 0 } flow && !flow.StartsWith("xtls-rprx-", StringComparison.Ordinal))
                {
                    r.Error("Credentials.Flow", "Unsupported VLESS flow.");
                }

                break;
            case ProxyProtocol.Trojan:
            case ProxyProtocol.Hysteria2:
                if (string.IsNullOrEmpty(p.Credentials.Password))
                {
                    r.Error("Credentials.Password", $"{p.Protocol} requires a password.");
                }

                break;
            case ProxyProtocol.Tuic:
                if (string.IsNullOrWhiteSpace(p.Credentials.Uuid))
                {
                    r.Error("Credentials.Uuid", "TUIC requires a UUID.");
                }

                break;
            case ProxyProtocol.Shadowsocks:
                if (p.Shadowsocks is null || string.IsNullOrWhiteSpace(p.Shadowsocks.Method))
                {
                    r.Error("Shadowsocks.Method", "Shadowsocks requires a cipher method.");
                }
                else if (!KnownShadowsocksMethods.Contains(p.Shadowsocks.Method))
                {
                    r.Warn("Shadowsocks.Method", $"Unrecognized cipher '{p.Shadowsocks.Method}'. The core will decide.");
                }

                if (p.Shadowsocks?.Plugin is { Length: > 0 } plugin && !PluginNameRegex().IsMatch(plugin))
                {
                    r.Error("Shadowsocks.Plugin", "Invalid plugin name.");
                }

                break;
            case ProxyProtocol.WireGuard:
                if (p.WireGuard is null)
                {
                    r.Error("WireGuard", "WireGuard settings are missing.");
                }
                else
                {
                    if (!IsWireGuardKey(p.WireGuard.PrivateKey))
                    {
                        r.Error("WireGuard.PrivateKey", "Private key must be 32 bytes, base64.");
                    }

                    if (!IsWireGuardKey(p.WireGuard.PeerPublicKey))
                    {
                        r.Error("WireGuard.PeerPublicKey", "Peer public key must be 32 bytes, base64.");
                    }

                    if (p.WireGuard.PreSharedKey is { Length: > 0 } psk && !IsWireGuardKey(psk))
                    {
                        r.Error("WireGuard.PreSharedKey", "Pre-shared key must be 32 bytes, base64.");
                    }

                    if (p.WireGuard.LocalAddresses.Count == 0)
                    {
                        r.Error("WireGuard.LocalAddresses", "At least one interface address is required.");
                    }

                    foreach (var a in p.WireGuard.LocalAddresses.Concat(p.WireGuard.AllowedIps))
                    {
                        if (!NetText.TryParseCidr(a, out _, out _))
                        {
                            r.Error("WireGuard.Addresses", $"Invalid CIDR '{Truncate(a)}'.");
                        }
                    }

                    if (p.WireGuard.Reserved.Count is not (0 or 3) || p.WireGuard.Reserved.Any(b => b is < 0 or > 255))
                    {
                        r.Error("WireGuard.Reserved", "Reserved must be exactly three bytes.");
                    }
                }

                break;
        }

        ValidateTransport(r, p.Transport);
        ValidateTls(r, p);
        return r;
    }

    private static void ValidateTransport(ValidationResult r, TransportOptions t)
    {
        if (t.Path is { Length: > 0 } path)
        {
            if (path[0] != '/')
            {
                r.Error("Transport.Path", "Path must start with '/'.");
            }

            CheckText(r, "Transport.Path", path);
        }

        if (t.Host is { Length: > 0 } host)
        {
            foreach (var h in host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!NetText.IsValidServerAddress(h.Split(':')[0]))
                {
                    r.Error("Transport.Host", "Host header must be a valid host name.");
                    break;
                }
            }
        }

        foreach (var (k, v) in t.Headers)
        {
            if (!HeaderNameRegex().IsMatch(k) || NetText.HasControlChars(v) || v.Length > MaxFieldLength)
            {
                r.Error("Transport.Headers", "Header name/value contains invalid characters.");
                break;
            }
        }

        CheckText(r, "Transport.ServiceName", t.ServiceName, 256);
        if (t.MaxEarlyData is < 0 or > 65536)
        {
            r.Error("Transport.MaxEarlyData", "Early data size out of range.");
        }
    }

    private static void ValidateTls(ValidationResult r, ProxyProfile p)
    {
        var tls = p.Tls;
        if (!tls.Enabled)
        {
            if (tls.IsReality)
            {
                r.Error("Tls.Reality", "Reality requires TLS to be enabled.");
            }

            return;
        }

        if (tls.ServerName is { Length: > 0 } sni && !NetText.IsValidServerAddress(sni))
        {
            r.Error("Tls.ServerName", "SNI must be a valid host name.");
        }

        foreach (var alpn in tls.Alpn)
        {
            if (alpn.Length is 0 or > 64 || NetText.HasControlChars(alpn) || alpn.Contains(',', StringComparison.Ordinal))
            {
                r.Error("Tls.Alpn", "Invalid ALPN token.");
                break;
            }
        }

        if (tls.Fingerprint is { Length: > 0 } fp && !KnownUtlsFingerprints.Contains(fp))
        {
            r.Warn("Tls.Fingerprint", $"Unknown uTLS fingerprint '{Truncate(fp)}'; the core may reject it.");
        }

        if (tls.AllowInsecure)
        {
            r.Warn("Tls.AllowInsecure", "Certificate verification is disabled for this profile (insecure).");
        }

        if (tls.Reality is { Enabled: true } reality)
        {
            if (string.IsNullOrWhiteSpace(reality.PublicKey) || !IsBase64Url32(reality.PublicKey))
            {
                r.Error("Tls.Reality.PublicKey", "Reality public key must be 32 bytes (base64url).");
            }

            if (reality.ShortId is { Length: > 0 } sid && !ShortIdRegex().IsMatch(sid))
            {
                r.Error("Tls.Reality.ShortId", "Reality short id must be 0-16 hex digits.");
            }

            if (string.IsNullOrWhiteSpace(tls.ServerName))
            {
                r.Error("Tls.ServerName", "Reality requires an SNI (server name).");
            }
        }

        if (tls.Fragment is { FallbackDelay: { } d } && (d < TimeSpan.Zero || d > TimeSpan.FromSeconds(5)))
        {
            r.Error("Tls.Fragment.FallbackDelay", "Fragment fallback delay must be between 0 and 5 seconds.");
        }

        if (tls.Fragment is { } f)
        {
            if (f.Length is { Length: > 0 } len && !RangeRegex().IsMatch(len))
            {
                r.Error("Tls.Fragment.Length", "Fragment length must look like '100-200'.");
            }

            if (f.Interval is { Length: > 0 } iv && !RangeRegex().IsMatch(iv))
            {
                r.Error("Tls.Fragment.Interval", "Fragment interval must look like '10-20'.");
            }

            if (f.Packets is { Length: > 0 } pk && pk != "tlshello" && !RangeRegex().IsMatch(pk))
            {
                r.Error("Tls.Fragment.Packets", "Fragment packets must be 'tlshello' or a range.");
            }
        }
    }

    private static void CheckText(ValidationResult r, string field, string? value, int max = MaxFieldLength)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length > max || NetText.HasControlChars(value))
        {
            r.Error(field, "Value is too long or contains control characters.");
        }
    }

    public static bool IsWireGuardKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 64)
        {
            return false;
        }

        Span<byte> buf = stackalloc byte[48];
        return Convert.TryFromBase64String(key.Trim(), buf, out var n) && n == 32;
    }

    private static bool IsBase64Url32(string s)
    {
        var std = s.Trim().Replace('-', '+').Replace('_', '/');
        std = std.PadRight(std.Length + ((4 - (std.Length % 4)) % 4), '=');
        Span<byte> buf = stackalloc byte[48];
        return Convert.TryFromBase64String(std, buf, out var n) && n == 32;
    }

    private static string Truncate(string s) => s.Length <= 32 ? s : s[..32] + "…";

    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,128}$")]
    private static partial Regex HeaderNameRegex();

    [GeneratedRegex("^[0-9a-fA-F]{0,16}$")]
    private static partial Regex ShortIdRegex();

    [GeneratedRegex("^[0-9]{1,5}(-[0-9]{1,5})?$")]
    private static partial Regex RangeRegex();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex PluginNameRegex();
}
