using System.Text.RegularExpressions;

namespace JackiVpn.Subscriptions.Providers;

/// <summary>A PasarGuard subscription URL split into its base (…/{token}) and an optional endpoint suffix.</summary>
public sealed record PasarGuardUrl(Uri BaseUrl, string Token, string? Endpoint)
{
    public Uri For(string endpoint) => endpoint == PasarGuardEndpoints.Base
        ? BaseUrl
        : new Uri(BaseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + endpoint);
}

/// <summary>
/// PasarGuard subscription routes (panel <c>app/routers/subscription.py</c>):
/// <c>/{token}</c> (GET/HEAD, format by User-Agent, HTML for <c>Accept: text/html</c>), <c>/{token}/info</c>,
/// <c>/{token}/usage</c>, <c>/{token}/apps</c>, <c>/{token}/{client_type}</c> where client_type ∈
/// sing_box, clash_meta, clash, xray, links, links_base64, outline, wireguard.
/// </summary>
public static partial class PasarGuardEndpoints
{
    public const string Base = "base";
    public const string Info = "info";
    public const string Usage = "usage";
    public const string Links = "links";
    public const string LinksBase64 = "links_base64";
    public const string SingBox = "sing_box";
    public const string Xray = "xray";
    public const string Clash = "clash";
    public const string ClashMeta = "clash_meta";

    public static readonly IReadOnlyList<string> ClientTypes = [SingBox, ClashMeta, Clash, Xray, Links, LinksBase64, "outline", "wireguard"];

    public static readonly IReadOnlyList<string> MetaEndpoints = [Info, Usage, "apps", "raw"];

    /// <summary>Default content endpoint order: richest native formats first (they carry fragment/uTLS options).</summary>
    public static readonly IReadOnlyList<string> DefaultContentOrder = [SingBox, Xray, Links, LinksBase64, ClashMeta, Base];

    public static bool TryParse(Uri? url, out PasarGuardUrl? result)
    {
        result = null;
        if (url is null || !url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        string? endpoint = null;
        var tokenIndex = segments.Length - 1;
        var last = segments[^1];
        if (ClientTypes.Contains(last, StringComparer.OrdinalIgnoreCase) || MetaEndpoints.Contains(last, StringComparer.OrdinalIgnoreCase))
        {
            if (segments.Length < 2)
            {
                return false;
            }

            endpoint = last.ToLowerInvariant();
            tokenIndex--;
        }

        var token = Uri.UnescapeDataString(segments[tokenIndex]);
        if (!TokenRegex().IsMatch(token))
        {
            return false;
        }

        var basePath = "/" + string.Join('/', segments.Take(tokenIndex + 1));
        var builder = new UriBuilder(url.Scheme, url.Host, url.Port, basePath) { Query = string.Empty, Fragment = string.Empty };
        if (url.IsDefaultPort)
        {
            builder.Port = -1;
        }

        result = new PasarGuardUrl(builder.Uri, token, endpoint);
        return true;
    }

    public static IReadOnlyList<string> ContentOrder(string? preferred, string? userEndpoint)
    {
        var order = new List<string>();
        void Add(string? e)
        {
            if (!string.IsNullOrEmpty(e) && !order.Contains(e) && (e == Base || ClientTypes.Contains(e)) && e is not ("outline" or "wireguard"))
            {
                order.Add(e);
            }
        }

        Add(userEndpoint);
        Add(preferred);
        foreach (var e in DefaultContentOrder)
        {
            Add(e);
        }

        return order;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-.=~]{8,512}$")]
    private static partial Regex TokenRegex();
}
