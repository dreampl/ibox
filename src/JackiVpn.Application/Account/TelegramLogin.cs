using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace JackiVpn.Application.Account;

/// <summary>
/// What the person sees while signing in: open <see cref="Link"/> in Telegram and compare <see cref="Code"/>; or, when
/// <see cref="Official"/>, open <see cref="Link"/> (Telegram's own oauth.telegram.org page) in the browser.
/// </summary>
public sealed record LoginChallenge(string Nonce, string Code, Uri Link, Uri? TelegramLink, TimeSpan ValidFor, string Verifier, bool Official = false);

/// <summary>The Telegram user an app session belongs to.</summary>
public sealed record TelegramUser(long Id, string FirstName, string? Username)
{
    public string? PhotoUrl { get; init; }
    public string Display => Username is { Length: > 0 } u ? "@" + u : FirstName;

    public static TelegramUser? From(JsonNode? n)
    {
        if (n is not JsonObject user || !long.TryParse(user["id"]?.ToString() ?? user["sub"]?.ToString(), out var id) || id <= 0)
        {
            return null;
        }

        return new TelegramUser(id, user["first_name"]?.ToString() ?? user["given_name"]?.ToString() ?? user["name"]?.ToString() ?? string.Empty,
            user["username"]?.ToString() ?? user["preferred_username"]?.ToString())
        {
            PhotoUrl = user["photo_url"]?.ToString() ?? user["picture"]?.ToString(),
        };
    }
}

public sealed record LoginResult(string Token, TelegramUser User, JsonObject Body);

public sealed class LoginFailedException(string message) : Exception(message);

public enum TelegramSignInStage { Preparing, OpeningTelegram, OpeningBrowser, Waiting, Confirm, Retrying, Verified, Success, Cancelled, Failed }
public sealed record TelegramSignInState(TelegramSignInStage Stage, string? Code = null)
{
    public string Title => Stage switch
    {
        TelegramSignInStage.Preparing => "در حال آماده‌سازی ورود تلگرام",
        TelegramSignInStage.OpeningTelegram => "در حال بازکردن تلگرام",
        TelegramSignInStage.OpeningBrowser => "در حال بازکردن ورود رسمی تلگرام",
        TelegramSignInStage.Waiting => "منتظر تایید تلگرام",
        TelegramSignInStage.Confirm => "درخواست در تلگرام باز شد",
        TelegramSignInStage.Retrying => "در حال اتصال دوباره",
        TelegramSignInStage.Verified => "تایید تلگرام دریافت شد",
        TelegramSignInStage.Success => "با موفقیت وارد شدی",
        TelegramSignInStage.Cancelled => "ورود تلگرام لغو شد",
        _ => "ورود تلگرام انجام نشد",
    };
    public string Detail => Stage switch
    {
        TelegramSignInStage.Waiting or TelegramSignInStage.Confirm when !string.IsNullOrEmpty(Code) => $"کد {Code} را در تلگرام بررسی و تایید کن.",
        TelegramSignInStage.Success => "حساب تلگرام به IBOX متصل شد.",
        TelegramSignInStage.Verified => "در حال بازکردن حساب IBOX…",
        TelegramSignInStage.Retrying => "درخواست ورود حفظ شده؛ دوباره تلاش می‌کنیم.",
        TelegramSignInStage.Failed => "جزئیات خطا در صفحهٔ ورود نمایش داده می‌شود.",
        _ => "IBOX · Telegram",
    };
}

/// <summary>
/// Sign-in with Telegram by bot deep link: the app keeps a random verifier and sends only its SHA-256; the bot asks the
/// Telegram user to approve; the app then trades nonce + verifier for a session token. Someone who only sees the
/// t.me link cannot collect the session. Used for the panel (subscription) and for music.
/// </summary>
public sealed class TelegramLogin
{
    private readonly IboxHttp _http;
    private readonly string _prefix;
    private TelegramSignInState? _lastState;
    public event EventHandler<TelegramSignInState>? StateChanged;

    private void Report(TelegramSignInStage stage, string? code = null)
    {
        var state = new TelegramSignInState(stage, code);
        if (state == _lastState) { return; }
        _lastState = state;
        StateChanged?.Invoke(this, state);
    }

    public void ReportFailure(bool cancelled) => Report(cancelled ? TelegramSignInStage.Cancelled : TelegramSignInStage.Failed);

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
        Report(TelegramSignInStage.Preparing);
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
        Report(TelegramSignInStage.OpeningTelegram);
        return new LoginChallenge(Nonce(r.Body), r.Body["code"]?.ToString() ?? string.Empty, url,
            tg is { Scheme: "tg" } ? tg : null, TimeSpan.FromSeconds(r.Body["ttl"]?.GetValue<int>() ?? 600), verifier);
    }

    /// <summary>
    /// Telegram's official "Log In with Telegram" (OpenID Connect) in the browser. The server holds the client secret and
    /// checks Telegram's signed id_token; the app keeps the same verifier and collects the session with
    /// <see cref="WaitAsync"/>. Null when the server has no official login set up (then use <see cref="StartAsync"/>).
    /// </summary>
    public async Task<LoginChallenge?> StartOfficialAsync(string deviceName, CancellationToken ct = default)
    {
        Report(TelegramSignInStage.Preparing);
        var verifier = NewVerifier();
        var r = await _http.PostAsync(_prefix + "oidc/start", new { vh = Hash(verifier), dev = deviceName }, ct: ct).ConfigureAwait(false);
        if (r.Status is HttpStatusCode.NotFound || r.Body?["fallback"]?.GetValue<bool>() == true)
        {
            return null;
        }

        if (!r.Ok || r.Body is null)
        {
            throw new LoginFailedException(r.Error);
        }

        if (!Uri.TryCreate(r.Body["url"]?.GetValue<string>(), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || url.Host != "oauth.telegram.org" || url.AbsolutePath != "/auth")
        {
            throw new LoginFailedException("The server sent an unexpected sign-in link.");
        }

        Report(TelegramSignInStage.OpeningBrowser);
        return new LoginChallenge(Nonce(r.Body), string.Empty, url, null,
            TimeSpan.FromSeconds(r.Body["ttl"]?.GetValue<int>() ?? 600), verifier, Official: true);
    }

    private static string Nonce(JsonObject body) => body["nonce"]?.ToString() is { Length: > 0 } nonce
        ? nonce : throw new LoginFailedException("The server did not return a sign-in challenge. Try again.");

    /// <summary>Long-polls until the person approves in Telegram; throws on deny, expiry or cancel.</summary>
    public async Task<LoginResult> WaitAsync(LoginChallenge challenge, IProgress<string>? state = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        Report(TelegramSignInStage.Waiting, challenge.Code);
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
                Report(TelegramSignInStage.Retrying);
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                continue;
            }

            if (r.Status == HttpStatusCode.Accepted)
            {
                var status = r.Body?["state"]?.GetValue<string>() ?? "wait";
                state?.Report(status);
                Report(status == "seen" ? TelegramSignInStage.Confirm : TelegramSignInStage.Waiting, challenge.Code);
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                continue;
            }

            if ((int)r.Status is 429 or 500 or 502 or 503 or 504)
            {
                state?.Report("retry");
                Report(TelegramSignInStage.Retrying);
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                continue;
            }

            if (r.Ok && r.Body?["token"]?.GetValue<string>() is { Length: > 0 } token && TelegramUser.From(r.Body["user"]) is { } user)
            {
                Report(TelegramSignInStage.Verified);
                return new LoginResult(token, user, r.Body);
            }

            throw new LoginFailedException(r.Error);
        }

        throw new LoginFailedException("Sign-in expired; start again.");
    }
}
