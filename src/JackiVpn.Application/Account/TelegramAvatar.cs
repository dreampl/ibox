using System.Net;
using System.Text.RegularExpressions;

namespace JackiVpn.Application.Account;

public static partial class TelegramAvatar
{
    public static async Task<Uri?> ResolveAsync(HttpClient http, TelegramUser user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(user);
        if (Uri.TryCreate(user.PhotoUrl, UriKind.Absolute, out var supplied) && supplied.Scheme == Uri.UriSchemeHttps) { return supplied; }
        var username = user.Username?.TrimStart('@');
        if (string.IsNullOrWhiteSpace(username) || username.Length > 64 || username.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) { return null; }
        using var response = await http.GetAsync(new Uri("https://t.me/" + username), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 524288) { return null; }
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + read > 524288) { return null; }
            bytes.Write(buffer, 0, read);
        }
        return ParsePublicPhoto(System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
    }

    internal static Uri? ParsePublicPhoto(string html)
    {
        foreach (Match image in Images().Matches(html))
        {
            var tag = image.Value;
            var classes = ClassAttribute().Match(tag);
            if (!classes.Success || !classes.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("tgme_page_photo_image", StringComparer.Ordinal)) { continue; }
            var source = SourceAttribute().Match(tag);
            if (!source.Success || !Uri.TryCreate(WebUtility.HtmlDecode(source.Groups[1].Value), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) { return null; }
            var host = uri.IdnHost;
            return new[] { "t.me", "telegram.org", "telesco.pe", "cdn-telegram.org" }.Any(d => host == d || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)) ? uri : null;
        }
        return null;
    }

    [GeneratedRegex("<img\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex Images();
    [GeneratedRegex("\\bclass\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex ClassAttribute();
    [GeneratedRegex("\\bsrc\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex SourceAttribute();
}
