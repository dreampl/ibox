using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Account;

/// <summary>Addresses of the IBOX services (fixed in the app; the servers are the user's own).</summary>
public static class IboxEndpoints
{
    public static readonly Uri Panel = new("https://sub.jackidrive.ir:2083/sub/_jp/api/ibox/");

    /// <summary>US first; the Iranian relay (same app) when the US address is blocked.</summary>
    public static readonly Uri[] Music = [new("https://radio-us.jackidrive.ir/m/api/ibox/"), new("https://m3.jackidrive.ir:2083/m/api/ibox/")];
}

/// <summary>
/// The signed-in IBOX account (Telegram identity) and the subscription actions of the Telegram mini app, natively:
/// usage, expiry, servers / exit country, allow-my-address, renew / add volume, accept a price, send a receipt.
/// A person without a session must sign in with Telegram first.
/// </summary>
public sealed class IboxAccount
{
    public const string TokenName = "panel";
    private readonly IboxHttp _http;
    private readonly ISecureTokenStore _tokens;
    private readonly ILogger? _log;

    public IboxAccount(IboxHttp http, ISecureTokenStore tokens, ILogger? log = null)
    {
        _http = http;
        _tokens = tokens;
        _log = log;
        Login = new TelegramLogin(http);
    }

    public TelegramLogin Login { get; }

    public bool SignedIn => _tokens.Get(TokenName) is { Length: > 0 };

    /// <summary>Raised when the server says the session is gone (signed out elsewhere, expired).</summary>
    public event EventHandler? SignedOut;

    public void Accept(LoginResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _tokens.Set(TokenName, result.Token);
        _log?.LogInformation("IBOX account signed in with Telegram");
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        if (_tokens.Get(TokenName) is { } t)
        {
            try
            {
                await _http.PostAsync("logout", null, t, ct: ct).ConfigureAwait(false);
            }
            catch (ServiceUnreachableException)
            {
                // the local token is dropped anyway; the server forgets it after 90 days
            }
        }

        _tokens.Remove(TokenName);
    }

    /// <summary>A 2-minute, one-use ticket the music server trades for a music session (no second Telegram sign-in).</summary>
    public async Task<string?> MusicTicketAsync(CancellationToken ct = default)
    {
        var r = await CallAsync("music/ticket", null, null, ct).ConfigureAwait(false);
        return r.Ok ? r.Body?["ticket"]?.GetValue<string>() : null;
    }

    /// <summary>The mini-app view. With <paramref name="etag"/> and no change: NotModified. wait > 0 long-polls.</summary>
    public async Task<(ApiResult Result, AccountView? View)> GetAsync(string? etag = null, int waitSeconds = 0, CancellationToken ct = default)
    {
        var r = await CallAsync(waitSeconds > 0 ? $"app?wait={Math.Clamp(waitSeconds, 1, 25)}" : "app", null, etag, ct).ConfigureAwait(false);
        return (r, r.Ok && r.Body is { } b ? NexViewParser.Parse(b) : null);
    }

    public Task<ApiResult> RequestAsync(string lid, string kind, long value, string? toServer = null, CancellationToken ct = default) =>
        CallAsync("req", toServer is null ? new { lid, kind, value } : new { lid, kind, value, to = toServer }, null, ct);

    public Task<ApiResult> AcceptPriceAsync(string rid, CancellationToken ct = default) => CallAsync("accept", new { rid }, null, ct);

    public Task<ApiResult> CancelRequestAsync(string rid, CancellationToken ct = default) => CallAsync("cancel", new { rid }, null, ct);

    /// <summary>A JPG / PNG of the bank receipt, up to 1.5 MB.</summary>
    public Task<ApiResult> SendReceiptAsync(string rid, byte[] image, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length > 1_500_000)
        {
            throw new ArgumentException("The receipt image must be 1.5 MB or smaller.", nameof(image));
        }

        return CallAsync("receipt", new { rid, img = Convert.ToBase64String(image) }, null, ct);
    }

    /// <summary>Nex configs (AmneziaWG / WireGuard / L2TP / OpenVPN) of one subscription.</summary>
    public Task<ApiResult> NexConfigsAsync(string lid, CancellationToken ct = default) => CallAsync("nex", new { lid }, null, ct);

    /// <summary>Registers this network's public address for L2TP / OpenVPN. Works only with the VPN off.</summary>
    public Task<ApiResult> NexAllowAsync(string lid, CancellationToken ct = default) => CallAsync("nexallow", new { lid }, null, ct);

    /// <summary>Exit country of a multi-country subscription; "" = automatic.</summary>
    public Task<ApiResult> NexExitAsync(string lid, string exit, CancellationToken ct = default) => CallAsync("nexexit", new { lid, exit }, null, ct);

    public Task<ApiResult> SessionsAsync(string? revoke = null, CancellationToken ct = default) =>
        CallAsync("sessions", revoke is null ? null : new { revoke }, null, ct);

    private async Task<ApiResult> CallAsync(string path, object? body, string? etag, CancellationToken ct)
    {
        var token = _tokens.Get(TokenName) ?? throw new InvalidOperationException("Sign in with Telegram first.");
        var r = await _http.PostAsync(path, body, token, etag, ct).ConfigureAwait(false);
        if (r.SignedOut)
        {
            _tokens.Remove(TokenName);
            SignedOut?.Invoke(this, EventArgs.Empty);
        }

        return r;
    }
}

/// <summary>
/// Keeps the subscription view fresh: long-polls the panel with the last ETag, so a change on the server (renewal
/// approved, volume added, exit changed) shows within seconds and an unchanged view costs a 304.
/// </summary>
public sealed class AccountWatcher : IDisposable
{
    private readonly IboxAccount _account;
    private CancellationTokenSource? _run;
    private string? _etag;

    public AccountWatcher(IboxAccount account) => _account = account;

    public AccountView? Current { get; private set; }

    public event EventHandler<AccountView>? Updated;

    public event EventHandler<string>? Problem;

    public void Start()
    {
        if (_run is not null)
        {
            return;
        }

        _run = new CancellationTokenSource();
        _ = LoopAsync(_run.Token);
    }

    public void Stop()
    {
        _run?.Cancel();
        _run?.Dispose();
        _run = null;
    }

    /// <summary>Fetch now (after an action), without the long wait.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var (r, view) = await _account.GetAsync(null, 0, ct).ConfigureAwait(false);
        Apply(r, view);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var backoff = 2;
        while (!ct.IsCancellationRequested && _account.SignedIn)
        {
            try
            {
                var (r, view) = await _account.GetAsync(_etag, Current is null ? 0 : 25, ct).ConfigureAwait(false);
                Apply(r, view);
                backoff = 2;
                if (!r.Ok && !r.NotModified)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is ServiceUnreachableException or InvalidOperationException)
            {
                Problem?.Invoke(this, e.Message);
                await Task.Delay(TimeSpan.FromSeconds(backoff), ct).ConfigureAwait(false);
                backoff = Math.Min(backoff * 2, 120);
            }
        }
    }

    private void Apply(ApiResult r, AccountView? view)
    {
        if (view is not null)
        {
            _etag = r.ETag;
            Current = view;
            Updated?.Invoke(this, view);
        }
        else if (!r.NotModified)
        {
            Problem?.Invoke(this, r.Error);
        }
    }

    public void Dispose() => Stop();
}

/// <summary>Plain-language lines for the subscription screen (English / Persian).</summary>
public static class AccountText
{
    public static string Bytes(long b) => b switch
    {
        <= 0 => "0",
        < 1L << 30 => $"{b / 1048576.0:0} MB",
        _ => $"{b / 1073741824.0:0.##} GB",
    };

    public static string Status(string s, bool fa) => s switch
    {
        "active" => fa ? "فعال" : "Active",
        "limited" => fa ? "حجم تمام شده" : "Out of data",
        "expired" => fa ? "زمان تمام شده" : "Expired",
        "disabled" => fa ? "غیرفعال" : "Disabled",
        "on_hold" => fa ? "منتظر اولین اتصال" : "Waiting for the first connection",
        _ => s,
    };

    public static string Kind(string kind, long value, bool fa) => kind switch
    {
        "add_gb" => fa ? $"{value} گیگ حجم اضافه" : $"+{value} GB",
        "extend" => fa ? $"تمدید {value} روزه" : $"Renew {value} days",
        "to_unlim" => fa ? "تبدیل به نامحدود ۳۰ روزه" : "Switch to 30-day unlimited",
        "to_gb" => fa ? $"تبدیل به حجمی {value} گیگ" : $"Switch to a {value} GB plan",
        "nex" => fa ? "افزودن Nex" : "Add Nex",
        _ => kind,
    };

    public static string Left(SubscriptionCard c, DateTimeOffset now, bool fa)
    {
        if (c.Left(now) is not { } left)
        {
            return fa ? "بدون محدودیت زمان" : "No time limit";
        }

        return left <= TimeSpan.Zero ? (fa ? "تمام شده" : "Ended")
            : fa ? $"{(int)left.TotalDays} روز و {left.Hours} ساعت مانده" : $"{(int)left.TotalDays} d {left.Hours} h left";
    }

    public static JsonArray Configs(ApiResult r) => r.Body?["configs"] as JsonArray ?? [];
}
