namespace JackiVpn.Application.Account;

/// <summary>What the mini app page inside IBOX asked the app to do (it navigates to ibox://…).</summary>
public enum MiniAppAction
{
    None,
    OpenOutside,
    Close,
    SignedOut,
}

/// <summary>
/// The server's own Telegram mini apps (subscription on the panel, music on the music server), shown inside IBOX with
/// the IBOX session instead of Telegram. The server puts a small bridge in front of each page that switches on only for
/// <c>#ibox=&lt;session&gt;</c>, plays the Telegram host, and sends the page's calls to the IBOX session routes.
/// </summary>
public static class MiniAppPages
{
    /// <summary>The subscription mini app with the account session, or null when signed out.</summary>
    public static Uri? Subscription(IboxServices ibox, string? lang = null) =>
        ibox.Tokens.Get(IboxAccount.TokenName) is { Length: > 0 } t ? Page(IboxEndpoints.Panel, "../app", t, lang) : null;

    /// <summary>The music mini app with the music session (opened by single sign-on first), or null.</summary>
    public static async Task<Uri?> MusicAsync(IboxServices ibox, string? lang = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ibox);
        if (!await ibox.EnsureMusicAsync(ct).ConfigureAwait(false) || ibox.Tokens.Get(MusicClient.TokenName) is not { Length: > 0 } t)
        {
            return null;
        }

        return Page(ibox.Music.ApiBase, "../", t, lang);
    }

    /// <summary>
    /// Where a navigation inside the page should go: the page's own origin stays inside, ibox:// links are the bridge
    /// asking for something, anything else opens outside (browser / Telegram).
    /// </summary>
    public static (MiniAppAction Action, Uri? Target) Classify(Uri page, string url)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return (MiniAppAction.None, null);
        }

        if (u.Scheme == "ibox")
        {
            if (u.Host == "close")
            {
                return (MiniAppAction.Close, null);
            }

            if (u.Host == "signedout")
            {
                return (MiniAppAction.SignedOut, null);
            }

            var q = System.Web.HttpUtility.ParseQueryString(u.Query);
            return u.Host == "open" && Uri.TryCreate(q["u"], UriKind.Absolute, out var target) && target.Scheme is "https" or "http" or "tg"
                ? (MiniAppAction.OpenOutside, target)
                : (MiniAppAction.None, null);
        }

        if (u.Scheme is "about" or "data" or "blob")
        {
            return (MiniAppAction.None, null);
        }

        return string.Equals(u.Host, page.Host, StringComparison.OrdinalIgnoreCase) && u.Port == page.Port
            ? (MiniAppAction.None, null)
            : (MiniAppAction.OpenOutside, u);
    }

    private static Uri Page(Uri apiBase, string relative, string token, string? lang) =>
        new(new Uri(new Uri(apiBase, ".."), relative), "#ibox=" + Uri.EscapeDataString(token) + (lang is "en" or "fa" ? "&lang=" + lang : string.Empty));
}
