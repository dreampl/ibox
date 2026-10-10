using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace JackiVpn.Domain.Profiles;

/// <summary>
/// Computes stable identities used to match nodes across subscription updates.
/// <list type="bullet">
/// <item><b>Fingerprint</b> — everything that changes <i>where and how</i> we connect (endpoint, identity,
/// transport, TLS/Reality). Two nodes with equal fingerprints are the same node. Names are excluded so a
/// provider renaming a node does not lose favorites or history.</item>
/// <item><b>Endpoint key</b> — protocol + server + port + transport kind + path. Used as a fallback match to
/// detect "same logical node, updated parameters" (e.g. rotated UUID) which is reported as Changed.</item>
/// </list>
/// </summary>
public static class ProfileIdentity
{
    public static string ComputeFingerprint(ProxyProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var sb = new StringBuilder(256);
        Append(sb, p.Protocol.ToString());
        Append(sb, NormalizeHost(p.Server));
        Append(sb, p.Port.ToString(CultureInfo.InvariantCulture));
        Append(sb, p.Credentials.Uuid?.Trim().ToLowerInvariant());
        Append(sb, p.Credentials.Password);
        Append(sb, p.Credentials.Username);
        Append(sb, p.Credentials.Flow);
        Append(sb, p.Shadowsocks?.Method?.ToLowerInvariant());
        Append(sb, p.Shadowsocks?.Plugin);
        Append(sb, p.Shadowsocks?.PluginOptions);
        Append(sb, p.Transport.Type.ToString());
        Append(sb, p.Transport.Path);
        Append(sb, NormalizeHost(p.Transport.Host));
        Append(sb, p.Transport.ServiceName);
        Append(sb, p.Transport.HeaderType);
        Append(sb, p.Tls.Enabled ? "tls" : "plain");
        Append(sb, NormalizeHost(p.Tls.ServerName));
        Append(sb, p.Tls.Reality is { Enabled: true } r ? $"reality:{r.PublicKey}:{r.ShortId}" : null);
        Append(sb, p.Hysteria2?.ObfsPassword);
        Append(sb, p.WireGuard?.PeerPublicKey);
        Append(sb, p.WireGuard?.PrivateKey);
        return Hash(sb.ToString());
    }

    public static string ComputeEndpointKey(ProxyProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var sb = new StringBuilder(128);
        Append(sb, p.Protocol.ToString());
        Append(sb, NormalizeHost(p.Server));
        Append(sb, p.Port.ToString(CultureInfo.InvariantCulture));
        Append(sb, p.Transport.Type.ToString());
        Append(sb, p.Transport.Path);
        return Hash(sb.ToString());
    }

    public static ProxyProfile WithFingerprint(ProxyProfile p) => p with { Fingerprint = ComputeFingerprint(p) };

    private static void Append(StringBuilder sb, string? value)
    {
        // Length-prefixed to make the encoding injective ("a|b" vs "a" + "|b").
        value ??= string.Empty;
        sb.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
    }

    private static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        host = host.Trim().TrimEnd('.');
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }

        return host.ToLowerInvariant();
    }

    private static string Hash(string input)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(input), digest);
        return Convert.ToHexStringLower(digest[..16]);
    }
}
