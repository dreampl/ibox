using System.Globalization;
using System.Text.RegularExpressions;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Cores;

namespace JackiVpn.Cores.Capabilities;

public static partial class CoreVersionParser
{
    /// <summary>
    /// <c>sing-box version 1.14.2</c> … <c>Tags: with_gvisor,with_quic,…</c>.
    /// </summary>
    public static CoreVersionInfo? ParseSingBox(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var m = SingBoxVersionRegex().Match(output);
        if (!m.Success || !TryVersion(m.Groups["v"].Value, out var version))
        {
            return null;
        }

        var tags = TagsRegex().Match(output) is { Success: true } t
            ? t.Groups["t"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        return new CoreVersionInfo(CoreKind.SingBox, version, FirstLine(output), tags);
    }

    /// <summary><c>Xray 26.3.27 (Xray, Penetrates Everything.) d2758a0 (go1.26.1 windows/amd64)</c>.</summary>
    public static CoreVersionInfo? ParseXray(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var m = XrayVersionRegex().Match(output);
        if (!m.Success || !TryVersion(m.Groups["v"].Value, out var version))
        {
            return null;
        }

        return new CoreVersionInfo(CoreKind.Xray, version, FirstLine(output), []);
    }

    private static bool TryVersion(string s, out Version version)
    {
        var core = s.Split('-', '+')[0];
        var parts = core.Split('.');
        var nums = new int[3];
        for (var i = 0; i < 3; i++)
        {
            nums[i] = i < parts.Length && int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        }

        version = new Version(nums[0], nums[1], nums[2]);
        return parts.Length >= 2;
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }

    [GeneratedRegex(@"sing-box version (?<v>[0-9][0-9A-Za-z.\-+]*)")]
    private static partial Regex SingBoxVersionRegex();

    [GeneratedRegex(@"Tags:\s*(?<t>[^\r\n]+)")]
    private static partial Regex TagsRegex();

    [GeneratedRegex(@"^Xray (?<v>[0-9][0-9A-Za-z.\-+]*)", RegexOptions.Multiline)]
    private static partial Regex XrayVersionRegex();
}
