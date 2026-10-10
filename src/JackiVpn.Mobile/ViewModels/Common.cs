using System.Globalization;
using JackiVpn.Domain.Profiles;

namespace JackiVpn.Mobile.ViewModels;

public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];
    public const string Unknown = "Unknown";

    public static string Bytes(long? bytes)
    {
        if (bytes is not { } b)
        {
            return Unknown;
        }

        double v = b;
        var i = 0;
        while (v >= 1024 && i < Units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return i == 0 ? $"{b} B" : v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", Inv) + " " + Units[i];
    }

    /// <summary>Regional-indicator emoji for an ISO country code, or a globe when unknown.</summary>
    public static string Flag(string? countryCode) => countryCode is { Length: 2 } cc && cc.All(char.IsAsciiLetter)
        ? string.Concat(cc.ToUpperInvariant().Select(c => char.ConvertFromUtf32(0x1F1E6 + c - 'A')))
        : "🌐";

    public static string Speed(double bytesPerSecond) => Bytes((long)bytesPerSecond) + "/s";

    public static string Ms(double? ms) => ms is { } v ? v.ToString("0", Inv) + " ms" : "—";

    public static string Connect(double? ms) => ms is double v && v >= 1000 ? (v / 1000).ToString("0.00", Inv) + " s" : Ms(ms);

    public static string Clock(TimeSpan t) => t.TotalHours >= 1
        ? string.Create(Inv, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
        : string.Create(Inv, $"{t.Minutes:00}:{t.Seconds:00}");

    public static string When(DateTimeOffset? utc) => utc is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Inv) : "Never";

    public static string Remaining(TimeSpan? span) => span switch
    {
        null => Unknown,
        { } t when t < TimeSpan.Zero => "Expired",
        { TotalDays: >= 1 } t => $"{(int)t.TotalDays}d {t.Hours}h",
        { } t => $"{(int)t.TotalHours}h {t.Minutes:00}m",
    };

    public static string Protocol(ProxyProtocol p) => p switch
    {
        ProxyProtocol.Vless => "VLESS",
        ProxyProtocol.Vmess => "VMess",
        ProxyProtocol.Trojan => "Trojan",
        ProxyProtocol.Shadowsocks => "Shadowsocks",
        ProxyProtocol.Socks => "SOCKS",
        ProxyProtocol.Http => "HTTP",
        ProxyProtocol.Hysteria2 => "Hysteria2",
        ProxyProtocol.Tuic => "TUIC",
        ProxyProtocol.WireGuard => "WireGuard",
        _ => p.ToString(),
    };

    public static string Transport(TransportType t) => t switch
    {
        TransportType.WebSocket => "WS",
        TransportType.Grpc => "gRPC",
        TransportType.HttpUpgrade => "HTTPUpgrade",
        TransportType.Http2 => "H2",
        TransportType.Quic => "QUIC",
        TransportType.XHttp => "XHTTP",
        TransportType.Kcp => "mKCP",
        _ => "TCP",
    };

    public static void OnUi(Action action)
    {
        if (MainThread.IsMainThread)
        {
            action();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(action);
        }
    }
}
