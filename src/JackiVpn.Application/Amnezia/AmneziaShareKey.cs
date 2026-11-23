using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace JackiVpn.Application.Amnezia;

/// <summary>
/// An AmneziaVPN share key: <c>vpn://</c> + base64url of Qt's <c>qCompress</c> output (4-byte big-endian length,
/// then a zlib stream) of the server's JSON config. Older keys carry the JSON without compression.
/// Only the host and description are read; the rest of the config stays opaque.
/// </summary>
public sealed record AmneziaShareKey(string Key, string? HostName, string? Description)
{
    public const string Scheme = "vpn://";

    private const int MaxPayloadChars = 256 * 1024;
    private const int MaxJsonBytes = 1024 * 1024;

    /// <summary>Validates and normalises a key (trimmed, whitespace and padding removed, scheme in lower case).</summary>
    public static bool TryParse(string? text, out AmneziaShareKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (!trimmed.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var payload = new StringBuilder(trimmed.Length - Scheme.Length);
        foreach (var c in trimmed.AsSpan(Scheme.Length))
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '+' or '/' or '='))
            {
                return false;
            }

            payload.Append(c);
        }

        var body = payload.ToString().TrimEnd('=');
        if (body.Length is 0 or > MaxPayloadChars || body.Contains('='))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            var standard = body.Replace('-', '+').Replace('_', '/');
            bytes = Convert.FromBase64String(standard + new string('=', (4 - standard.Length % 4) % 4));
        }
        catch (FormatException)
        {
            return false;
        }

        if (Decode(bytes) is not { } json || ReadConfig(json) is not { } config)
        {
            return false;
        }

        key = new AmneziaShareKey(Scheme + body, config.Host, config.Description);
        return true;
    }

    /// <summary>Builds a key from a config JSON the way AmneziaVPN does (qCompress + base64url, no padding).</summary>
    public static string Encode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var raw = Encoding.UTF8.GetBytes(json);
        using var output = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)raw.Length);
        output.Write(length);
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return Scheme + Convert.ToBase64String(output.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[]? Decode(byte[] bytes)
    {
        if (bytes.Length > 0 && bytes[0] == (byte)'{')
        {
            return bytes;
        }

        if (bytes.Length < 6)
        {
            return null;
        }

        var expected = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (expected is 0 or > MaxJsonBytes)
        {
            return null;
        }

        try
        {
            using var input = new MemoryStream(bytes, 4, bytes.Length - 4);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            var result = new byte[expected];
            var read = 0;
            while (read < result.Length)
            {
                var n = zlib.Read(result, read, result.Length - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            return read == expected ? result : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static (string? Host, string? Description)? ReadConfig(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || (!root.TryGetProperty("containers", out _) && !root.TryGetProperty("hostName", out _)))
            {
                return null;
            }

            return (Text(root, "hostName"), Text(root, "description"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s ? s.Trim() : null;
}
