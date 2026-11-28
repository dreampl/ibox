using System.Net;
using System.Text.Json;
using JackiVpn.Security;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Account;

/// <summary>The IBOX account pieces each app (Windows, Android, Linux) composes once.</summary>
public sealed class IboxServices : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _offeredMarker;
    private readonly string _photoFile;
    private CancellationTokenSource? _photoRequest;
    private Task? _photoTask;
    private readonly ILogger? _log;

    public IboxServices(string dataDir, ISecretProtector protector, ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDir);
        _log = log;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(12),
        })
        { Timeout = TimeSpan.FromSeconds(45) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("IBOX-Account/1");
        Tokens = new ProtectedFileTokenStore(Path.Combine(dataDir, "account"), protector);
        Account = new IboxAccount(new IboxHttp(_http, IboxEndpoints.Panel), Tokens, log);
        Account.Login.StateChanged += (_, state) => LoginStateChanged?.Invoke(this, state);
        Watcher = new AccountWatcher(Account);
        Music = new MusicClient(new IboxHttp(_http, IboxEndpoints.Music), Tokens);
        _offeredMarker = Path.Combine(dataDir, "account", "signin-offered");
        _photoFile = Path.Combine(dataDir, "account", "telegram.jpg");
        Watcher.Updated += (_, view) =>
        {
            if (view.User is { } user && (user.PhotoUrl is { Length: > 0 } && user.PhotoUrl != User?.PhotoUrl || PhotoPath is null && _photoTask?.IsCompleted != false))
            {
                SaveIdentity(user.Id == 0 && User is { } known ? user with { Id = known.Id, PhotoUrl = user.PhotoUrl ?? known.PhotoUrl } : user);
            }
        };
        Account.SignedOut += (_, _) => ForgetMusic();
        if (SignedIn && User is { } saved && PhotoPath is null) { SaveIdentity(saved); }
    }

    /// <summary>Signed in with Telegram. Music and the in-app subscription manager exist only then.</summary>
    public bool SignedIn => Account.SignedIn;

    /// <summary>Raised after a sign-in, a sign-out here, or the server ending the session.</summary>
    public event EventHandler? SignInChanged;
    public event EventHandler? AvatarChanged;
    public event EventHandler<TelegramSignInState>? LoginStateChanged;

    public TelegramUser? User
    {
        get
        {
            try { return Tokens.Get("telegram-user") is { } json ? JsonSerializer.Deserialize<TelegramUser>(json) : null; }
            catch (JsonException) { return null; }
        }
    }

    public string? PhotoPath => SignedIn && File.Exists(_photoFile) ? _photoFile : null;

    private void SaveIdentity(TelegramUser user)
    {
        if (User?.Id != user.Id)
        {
            try { File.Delete(_photoFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Tokens.Set("telegram-user", JsonSerializer.Serialize(user));
        _photoRequest?.Cancel();
        _photoRequest?.Dispose();
        _photoRequest = new CancellationTokenSource();
        _photoTask = CachePhotoAsync(user, _photoRequest.Token);
    }

    private async Task CachePhotoAsync(TelegramUser user, CancellationToken ct)
    {
        try
        {
            var url = await TelegramAvatar.ResolveAsync(_http, user, ct).ConfigureAwait(false);
            if (url is null)
            {
                return;
            }
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 2_097_152) { return; }
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + read > 2_097_152) { return; }
                bytes.Write(buffer, 0, read);
            }
            var data = bytes.ToArray();
            if (data.Length < 8 || !(data[0] == 0xFF && data[1] == 0xD8 || data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))) { return; }
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(_photoFile)!);
            var temporary = _photoFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, data, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                File.Move(temporary, _photoFile, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            AvatarChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            if (ex is not OperationCanceledException) { _log?.LogDebug("Telegram avatar unavailable ({Reason})", ex.GetType().Name); }
        }
    }

    /// <summary>True until the optional first-run sign-in has been shown once (or the user is already signed in).</summary>
    public bool ShouldOfferSignIn => !SignedIn && !File.Exists(_offeredMarker);

    public void MarkSignInOffered()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_offeredMarker)!);
            File.WriteAllText(_offeredMarker, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // asked again next launch: harmless
        }
    }

    public void SignIn(LoginResult result)
    {
        if (User?.Id != result.User.Id) { Tokens.Remove("allow-remote-control"); }
        Account.Accept(result);
        SaveIdentity(result.User);
        MarkSignInOffered();
        Watcher.Start();
        _ = EnsureMusicAsync();
        SignInChanged?.Invoke(this, EventArgs.Empty);
        LoginStateChanged?.Invoke(this, new TelegramSignInState(TelegramSignInStage.Success));
    }

    /// <summary>
    /// Signed in to the account but not to music (music runs on another server with its own session): opens the music
    /// session with a one-use ticket from the account, so the person never signs in twice. False when that is not possible.
    /// </summary>
    public async Task<bool> EnsureMusicAsync(CancellationToken ct = default)
    {
        if (Music.SignedIn)
        {
            return true;
        }

        if (!SignedIn)
        {
            return false;
        }

        try
        {
            return await Account.MusicTicketAsync(ct).ConfigureAwait(false) is { } ticket
                && await Music.TicketLoginAsync(ticket, DeviceName(OperatingSystem.IsAndroid() ? "Android" : OperatingSystem.IsLinux() ? "Linux" : "Windows"), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is ServiceUnreachableException or InvalidOperationException or HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>Signs out of the account and of music, and stops playback.</summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        ClearIdentity();
        Watcher.Stop();
        Player?.Stop();
        await Music.SignOutAsync(ct).ConfigureAwait(false);
        await Account.SignOutAsync(ct).ConfigureAwait(false);
        SignInChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ForgetMusic()
    {
        ClearIdentity();
        Watcher.Stop();
        Player?.Stop();
        Tokens.Remove(MusicClient.TokenName);
        SignInChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearIdentity()
    {
        _photoRequest?.Cancel();
        Tokens.Remove("telegram-user");
        try { File.Delete(_photoFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        AvatarChanged?.Invoke(this, EventArgs.Empty);
    }

    public ISecureTokenStore Tokens { get; }

    public IboxAccount Account { get; }

    public AccountWatcher Watcher { get; }

    public MusicClient Music { get; }
    public AccountControl? Control { get; private set; }
    public event EventHandler? ControlAttached;
    public void AttachControl(Domain.Abstractions.IProfileRepository profiles, Connection.ConnectionOrchestrator connection, string platform, string version)
    {
        Control?.Dispose();
        Control = new AccountControl(this, profiles, connection, platform, version, _log);
        ControlAttached?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Set by the app once its audio output exists; also the notch's now-playing source.</summary>
    public MusicPlayer? Player { get; set; }

    public static string DeviceName(string platform) => $"{platform} · {Environment.MachineName}";

    public void Dispose()
    {
        _photoRequest?.Cancel();
        _photoRequest?.Dispose();
        Control?.Dispose();
        Watcher.Dispose();
        Player?.Dispose();
        _http.Dispose();
    }
}
