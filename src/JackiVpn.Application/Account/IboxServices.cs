using System.Net;
using JackiVpn.Security;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Account;

/// <summary>The IBOX account pieces each app (Windows, Android, Linux) composes once.</summary>
public sealed class IboxServices : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _offeredMarker;

    public IboxServices(string dataDir, ISecretProtector protector, ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDir);
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
        Watcher = new AccountWatcher(Account);
        Music = new MusicClient(new IboxHttp(_http, IboxEndpoints.Music), Tokens);
        _offeredMarker = Path.Combine(dataDir, "account", "signin-offered");
        Account.SignedOut += (_, _) => ForgetMusic();
    }

    /// <summary>Signed in with Telegram. Music and the in-app subscription manager exist only then.</summary>
    public bool SignedIn => Account.SignedIn;

    /// <summary>Raised after a sign-in, a sign-out here, or the server ending the session.</summary>
    public event EventHandler? SignInChanged;

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
        Account.Accept(result);
        MarkSignInOffered();
        Watcher.Start();
        SignInChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Signs out of the account and of music, and stops playback.</summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        Watcher.Stop();
        Player?.Stop();
        await Music.SignOutAsync(ct).ConfigureAwait(false);
        await Account.SignOutAsync(ct).ConfigureAwait(false);
        SignInChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ForgetMusic()
    {
        Watcher.Stop();
        Player?.Stop();
        Tokens.Remove(MusicClient.TokenName);
        SignInChanged?.Invoke(this, EventArgs.Empty);
    }

    public ISecureTokenStore Tokens { get; }

    public IboxAccount Account { get; }

    public AccountWatcher Watcher { get; }

    public MusicClient Music { get; }

    /// <summary>Set by the app once its audio output exists; also the notch's now-playing source.</summary>
    public MusicPlayer? Player { get; set; }

    public static string DeviceName(string platform) => $"{platform} · {Environment.MachineName}";

    public void Dispose()
    {
        Watcher.Dispose();
        Player?.Dispose();
        _http.Dispose();
    }
}
