using System.Text.Json.Nodes;

namespace JackiVpn.Application.Account;

/// <summary>A song from the person's library or a playlist. <see cref="Stream"/> is a signed, seekable link.</summary>
public sealed record MusicTrack(string Key, string Artist, string Title, double Seconds, Uri? Cover, Uri? Stream, string Channel)
{
    public string Line => Artist.Length > 0 ? $"{Title} — {Artist}" : Title;
}

public sealed record MusicPlaylist(string Id, string Name, IReadOnlyList<MusicTrack> Songs);

public sealed record MusicLibrary(TelegramUser? Me, IReadOnlyList<MusicTrack> Songs, IReadOnlyList<MusicTrack> Likes,
    IReadOnlyList<MusicPlaylist> Playlists, MusicTrack? Resume, double ResumeAt);

/// <summary>
/// The music mini app's account, natively. Telegram is the main sign-in (and required the first time); afterwards the
/// person may set a backup password (stored hashed on the server) to sign in when Telegram is not at hand.
/// </summary>
public sealed class MusicClient
{
    public const string TokenName = "music";
    private readonly IboxHttp _http;
    private readonly ISecureTokenStore _tokens;

    public MusicClient(IboxHttp http, ISecureTokenStore tokens)
    {
        _http = http;
        _tokens = tokens;
        Login = new TelegramLogin(http);
    }

    public TelegramLogin Login { get; }

    public bool SignedIn => _tokens.Get(TokenName) is { Length: > 0 };

    /// <summary>After a Telegram sign-in: true when a backup password still has to be chosen.</summary>
    public bool NeedsPassword { get; private set; }

    public void Accept(LoginResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _tokens.Set(TokenName, result.Token);
        NeedsPassword = result.Body["pass_set"]?.GetValue<bool>() != true;
    }

    /// <summary>Backup sign-in: Telegram id or @username plus the password the person set in IBOX.</summary>
    public async Task<TelegramUser> PasswordLoginAsync(string user, string password, string deviceName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentException.ThrowIfNullOrEmpty(password);
        var r = await _http.PostAsync("pass/login", new { user = user.Trim(), pass = password, dev = deviceName }, ct: ct).ConfigureAwait(false);
        if (!r.Ok || r.Body?["token"]?.GetValue<string>() is not { Length: > 0 } token || TelegramUser.From(r.Body["user"]) is not { } me)
        {
            throw new LoginFailedException(r.Error);
        }

        _tokens.Set(TokenName, token);
        NeedsPassword = false;
        return me;
    }

    /// <summary>First time: only <paramref name="password"/>. To change it: <paramref name="current"/> is the old one or the admin's recovery code.</summary>
    public async Task SetPasswordAsync(string password, string? current = null, CancellationToken ct = default)
    {
        var problem = PasswordProblem(password);
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(password));
        }

        var r = await CallAsync("pass/set", current is null ? new { pass = password } : new { pass = password, old = current }, ct).ConfigureAwait(false);
        if (!r.Ok)
        {
            throw new LoginFailedException(r.Error);
        }

        NeedsPassword = false;
    }

    /// <summary>Same rule as the server, checked before sending.</summary>
    public static string? PasswordProblem(string? password) =>
        password is null || password.Length is < 8 or > 128 ? "The password must be 8 to 128 characters."
        : password.All(char.IsAsciiDigit) && password.Length < 10 ? "Use more than a short number."
        : null;

    public async Task<bool> PasswordIsSetAsync(CancellationToken ct = default)
    {
        var r = await CallAsync("pass/state", null, ct).ConfigureAwait(false);
        NeedsPassword = r.Ok && r.Body?["pass_set"]?.GetValue<bool>() != true;
        return !NeedsPassword;
    }

    public async Task<MusicLibrary> LibraryAsync(CancellationToken ct = default)
    {
        var r = await CallAsync("me", null, ct).ConfigureAwait(false);
        if (!r.Ok || r.Body is null)
        {
            throw new LoginFailedException(r.Error);
        }

        return ParseLibrary(r.Body, _http.Base);
    }

    public Task<ApiResult> LikeAsync(MusicTrack t, bool on, CancellationToken ct = default) =>
        CallAsync("like", new { k = t?.Key, c = t?.Channel, on }, ct);

    /// <summary>Where the person stopped, so the Telegram mini app resumes there too.</summary>
    public Task<ApiResult> SavePositionAsync(MusicTrack t, double seconds, CancellationToken ct = default) =>
        CallAsync("pos", new { k = t?.Key, c = t?.Channel, t = Math.Round(seconds, 1) }, ct);

    public Task<ApiResult> PlaylistAsync(string op, string? id = null, string? name = null, MusicTrack? track = null, CancellationToken ct = default) =>
        CallAsync("pl", new { op, id, name, k = track?.Key, c = track?.Channel }, ct);

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        if (_tokens.Get(TokenName) is not null)
        {
            try
            {
                await CallAsync("logout", null, ct).ConfigureAwait(false);
            }
            catch (ServiceUnreachableException)
            {
                // dropped locally anyway
            }
        }

        _tokens.Remove(TokenName);
    }

    public static MusicLibrary ParseLibrary(JsonObject me, Uri apiBase)
    {
        ArgumentNullException.ThrowIfNull(me);
        ArgumentNullException.ThrowIfNull(apiBase);
        var site = new Uri(apiBase, "/");
        var app = new Uri(apiBase, "../../");
        var stream = NexViewParser.Https(NexViewParser.Str(me, "stream")) ?? app;
        List<MusicTrack> Songs(JsonNode? a, string channel = "") => (a as JsonArray ?? []).OfType<JsonObject>().Select(x => Track(x, stream, app, site, channel)).Where(x => x is not null).Select(x => x!).ToList();
        var resume = me["resume"] as JsonObject;
        return new MusicLibrary(
            TelegramUser.From(me["me"]),
            Songs(me["songs"]),
            Songs(me["likes"]),
            (me["pls"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(p => new MusicPlaylist(NexViewParser.Str(p, "id") ?? string.Empty, NexViewParser.Str(p, "name") ?? string.Empty, Songs(p["songs"])))
                .ToList(),
            resume?["song"] is JsonObject rs ? Track(rs, stream, app, site, string.Empty) : null,
            resume?["t"] is JsonValue tv && tv.TryGetValue<double>(out var at) ? at : 0);
    }

    private static MusicTrack? Track(JsonObject x, Uri stream, Uri app, Uri site, string channel)
    {
        var key = NexViewParser.Str(x, "k");
        if (key is null)
        {
            return null;
        }

        var s = NexViewParser.Str(x, "s");
        Uri? url = null;
        if (s is { Length: > 2 } && (s.StartsWith("s/", StringComparison.Ordinal) || s.StartsWith("c/", StringComparison.Ordinal) || s.StartsWith("p/", StringComparison.Ordinal)))
        {
            url = new Uri(s[0] == 'p' ? app : stream, s);           // partner-channel links need the live membership check (US only)
        }

        var img = NexViewParser.Str(x, "img");
        Uri? cover = img is { Length: > 0 } && Uri.TryCreate(site, img, out var c) && c.Scheme == Uri.UriSchemeHttps ? c : null;
        return new MusicTrack(key, NexViewParser.Str(x, "a") ?? string.Empty, NexViewParser.Str(x, "t") ?? key,
            x["d"] is JsonValue d && d.TryGetValue<double>(out var sec) ? sec : 0, cover, url, NexViewParser.Str(x, "c") ?? channel);
    }

    private async Task<ApiResult> CallAsync(string path, object? body, CancellationToken ct)
    {
        var token = _tokens.Get(TokenName) ?? throw new InvalidOperationException("Sign in with Telegram first.");
        var r = await _http.PostAsync(path, body, token, ct: ct).ConfigureAwait(false);
        if (r.SignedOut)
        {
            _tokens.Remove(TokenName);
        }

        return r;
    }
}
