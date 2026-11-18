using System.Net;
using JackiVpn.Security;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Account;

/// <summary>The IBOX account pieces each app (Windows, Android, Linux) composes once.</summary>
public sealed class IboxServices : IDisposable
{
    private readonly HttpClient _http;

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
