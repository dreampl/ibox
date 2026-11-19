using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace JackiVpn.Application.Account;

/// <summary>What the person sees while signing in: open <see cref="Link"/> in Telegram and compare <see cref="Code"/>.</summary>
public sealed record LoginChallenge(string Nonce, string Code, Uri Link, Uri? TelegramLink, TimeSpan ValidFor, string Verifier);

/// <summary>The Telegram user an app session belongs to.</summary>
public sealed record TelegramUser(long Id, string FirstName, string? Username)
{
    public string Display => Username is { Length: > 0 } u ? "@" + u : FirstName;

    public static TelegramUser? From(JsonNode? n) =>
        n?["id"]?.GetValue<long>() is { } id
            ? new TelegramUser(id, n["first_name"]?.GetValue<string>() ?? string.Empty, n["username"]?.GetValue<string>())
            : null;
}

public sealed record LoginResult(string Token, TelegramUser User, JsonObject Body);

public sealed class LoginFailedException(string message) : Exception(message);

/// <summary>
/// Sign-in with Telegram by bot deep link: the app keeps a random verifier and sends only its SHA-256; the bot asks the
/// Telegram user to approve; the app then trades nonce + verifier for a session token. Someone who only sees the
/// t.me link cannot collect the session. Used for the panel (subscription) and for music.
/// </summary>
public sealed class TelegramLogin
{
    private readonly IboxHttp _http;
    private readonly string _prefix;

    /// <param name="http">Bases ending in …/api/ibox/ (panel) or …/m/api/ibox/ (music).</param>
    /// <param name="prefix">Extra path in front of login/start and login/poll; normally empty.</param>
    public TelegramLogin(IboxHttp http, string prefix = "")
    {
        _http = http;
        _prefix = prefix;
    }

    public static string NewVerifier() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string verifier) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(verifier))).ToLowerInvariant();

    public async Task<LoginChallenge> StartAsync(string deviceName, CancellationToken ct = default)
    {
        var verifier = NewVerifier();
        var r = await _http.PostAsync(_prefix + "login/start", new { vh = Hash(verifier), dev = deviceName }, ct: ct).ConfigureAwait(false);
        if (!r.Ok || r.Body is null)
        {
            throw new LoginFailedException(r.Error);
        }

        var link = r.Body["url"]?.GetValue<string>();
        if (!Uri.TryCreate(link, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || url.Host != "t.me")
        {
            throw new LoginFailedException("The server sent an unexpected sign-in link.");
        }

        Uri.TryCreate(r.Body["tg"]?.GetValue<string>(), UriKind.Absolute, out var tg);
        return new LoginChallenge(r.Body["nonce"]!.GetValue<string>(), r.Body["code"]?.GetValue<string>() ?? string.Empty, url,
            tg is { Scheme: "tg" } ? tg : null, TimeSpan.FromSeconds(r.Body["ttl"]?.GetValue<int>() ?? 600), verifier);
    }

    /// <summary>Long-polls until the person approves in Telegram; throws on deny, expiry or cancel.</summary>
    public async Task<LoginResult> WaitAsync(LoginChallenge challenge, IProgress<string>? state = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        var until = DateTime.UtcNow + challenge.ValidFor;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            ApiResult r;
            try
            {
                r = await _http.PostAsync(_prefix + "login/poll", new { nonce = challenge.Nonce, v = challenge.Verifier }, ct: ct).ConfigureAwait(false);
            }
            catch (ServiceUnreachableException)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                continue;
            }

            if (r.Status == HttpStatusCode.Accepted)
            {
                state?.Report(r.Body?["state"]?.GetValue<string>() ?? "wait");
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                continue;
            }

            if (r.Ok && r.Body?["token"]?.GetValue<string>() is { Length: > 0 } token && TelegramUser.From(r.Body["user"]) is { } user)
            {
                return new LoginResult(token, user, r.Body);
            }

            throw new LoginFailedException(r.Error);
        }

        throw new LoginFailedException("Sign-in expired; start again.");
    }
}
