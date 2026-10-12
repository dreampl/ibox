using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JackiVpn.Application.Settings;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;
using JackiVpn.Routing;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Routing;

/// <summary>What the editor page reads and writes.</summary>
public sealed record RoutingGraphDocument
{
    public IReadOnlyList<RoutingRule> Rules { get; init; } = [];
    public RuleActionType Final { get; init; } = RuleActionType.Proxy;
    public IReadOnlyDictionary<string, NodePoint> Layout { get; init; } = new Dictionary<string, NodePoint>();

    /// <summary>The rules this edit started from; a save over rules changed elsewhere meanwhile is refused.</summary>
    public string? Version { get; init; }

    /// <summary>Read only: rule-set tags the rules may name (GeoIp/GeoSite/RuleSet conditions).</summary>
    public IReadOnlyList<string> RuleSets { get; init; } = [];
}

public sealed record RoutingTestRequest(string? Domain, string? Ip, int? Port, string? Network, string? App);

public sealed record RoutingTestResult(string? RuleId, string Action, bool Indeterminate, string Explanation);

public sealed record RoutingSaveResult(bool Ok, string? Version, IReadOnlyList<RoutingProblem> Problems);

public sealed record RoutingProblem(string? RuleId, string Message);

/// <summary>
/// The node-based routing editor: a page in the user's browser, served by the app on the loopback interface only.
/// Every address carries a fresh 256-bit token (the page is unreachable without it, and other sites cannot read
/// or post to it: the Host and Origin are checked, no CORS is offered). A save is validated exactly as the
/// Routing page validates a rule, then applied to a running connection by <see cref="RoutingWatcher"/>.
/// The server stops by itself after half an hour without requests.
/// </summary>
public sealed class RoutingEditorServer : IAsyncDisposable
{
    public const int MaxBody = 1024 * 1024;
    public const int MaxRules = 500;
    public static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions Json = new(JsonDefaults.Storage)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SettingsStore _settings;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _token = string.Empty;
    private long _lastRequest;

    public RoutingEditorServer(SettingsStore settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public int Port { get; private set; }

    public bool IsRunning => _listener is not null;

    /// <summary>The page's address (starts the server, or keeps the running one).</summary>
    public string Open(AppTheme theme)
    {
        lock (_gate)
        {
            if (_listener is null)
            {
                _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start(16);
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _cts = new CancellationTokenSource();
                var listener = _listener;
                var token = _cts.Token;
                _ = Task.Run(() => AcceptLoopAsync(listener, token), CancellationToken.None);
                _logger.LogInformation("Routing editor opened on loopback port {Port}.", Port);
            }

            Interlocked.Exchange(ref _lastRequest, Environment.TickCount64);
            var t = theme switch { AppTheme.Dark => "dark", AppTheme.Light => "light", _ => "system" };
            return $"http://127.0.0.1:{Port}/{_token}/?theme={t}";
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _listener?.Stop();
            _listener = null;
            _token = string.Empty;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        using var idle = new PeriodicTimer(TimeSpan.FromMinutes(1));
        _ = Task.Run(async () =>
        {
            while (await idle.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (Environment.TickCount64 - Interlocked.Read(ref _lastRequest) > IdleStop.TotalMilliseconds)
                {
                    _logger.LogInformation("Routing editor closed (idle).");
                    Stop();
                    return;
                }
            }
        }, CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(20));
                        await ServeAsync(client.GetStream(), timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
                    {
                        _logger.LogDebug("Routing editor connection: {Error}", ex.Message);
                    }
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>One HTTP/1.1 request per connection (Connection: close).</summary>
    public async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        var request = await HttpRequestReader.ReadAsync(stream, MaxBody, ct).ConfigureAwait(false);
        if (request is null)
        {
            await WriteAsync(stream, 400, "text/plain", "Bad request."u8.ToArray(), ct).ConfigureAwait(false);
            return;
        }

        var (status, type, body) = Handle(request);
        await WriteAsync(stream, status, type, body, ct).ConfigureAwait(false);
    }

    /// <summary>The routing of one request (public for tests).</summary>
    public (int Status, string ContentType, byte[] Body) Handle(HttpRequestReader.Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string token;
        int port;
        lock (_gate)
        {
            (token, port) = (_token, Port);
        }

        // DNS rebinding: a page on another site that resolves its name to 127.0.0.1 sends its own Host.
        var host = request.Header("host");
        if (token.Length == 0 || (host != $"127.0.0.1:{port}" && host != $"localhost:{port}"))
        {
            return Text(421, "Wrong host.");
        }

        var prefix = "/" + token + "/";
        var path = request.Path.Split('?', 2)[0];
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(path[1..(token.Length + 1)]), Encoding.ASCII.GetBytes(token)))
        {
            return Text(404, "Not found.");
        }

        Interlocked.Exchange(ref _lastRequest, Environment.TickCount64);
        var route = path[prefix.Length..];
        if (request.Method != "GET")
        {
            // Writes only from the page itself, as JSON (a cross-site form cannot send that without CORS).
            var origin = request.Header("origin");
            if (origin is not null && origin != $"http://127.0.0.1:{port}" && origin != $"http://localhost:{port}")
            {
                return Text(403, "Wrong origin.");
            }

            if (request.Header("content-type")?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
            {
                return Text(415, "JSON only.");
            }
        }

        try
        {
            return (request.Method, route) switch
            {
                ("GET", "") or ("GET", "index.html") => (200, "text/html; charset=utf-8", Page()),
                ("GET", "api/graph") => JsonReply(200, Current()),
                ("PUT", "api/graph") => Save(JsonSerializer.Deserialize<RoutingGraphDocument>(request.Body, Json)),
                ("POST", "api/test") => JsonReply(200, Test(JsonSerializer.Deserialize<RoutingTestRequest>(request.Body, Json))),
                _ => Text(404, "Not found."),
            };
        }
        catch (JsonException)
        {
            return Text(400, "Invalid JSON.");
        }
    }

    public RoutingGraphDocument Current()
    {
        var s = _settings.Current;
        return new RoutingGraphDocument
        {
            Rules = s.Routing.Rules,
            Final = s.Routing.Final,
            Layout = s.Ui.RoutingLayout,
            Version = VersionOf(s.Routing),
            RuleSets = KnownTags(s),
        };
    }

    public static string VersionOf(RoutingSettings routing) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { routing.Rules, routing.Final }, JsonDefaults.Storage)))[..16].ToLowerInvariant();

    private static IReadOnlyList<string> KnownTags(AppSettings s) =>
        s.Routing.RuleSets.Select(r => r.Tag).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private (int, string, byte[]) Save(RoutingGraphDocument? doc)
    {
        if (doc is null)
        {
            return Text(400, "Empty document.");
        }

        var problems = Check(doc, KnownTags(_settings.Current));
        if (problems.Count > 0)
        {
            return JsonReply(422, new RoutingSaveResult(false, null, problems));
        }

        string? version = null;
        var stale = false;
        _settings.Update(s =>
        {
            if (doc.Version is not null && doc.Version != VersionOf(s.Routing))
            {
                stale = true;
                return s;
            }

            var layout = doc.Layout.Where(kv => kv.Key.Length <= 160 && double.IsFinite(kv.Value.X) && double.IsFinite(kv.Value.Y))
                .Take(4000)
                .ToDictionary(kv => kv.Key, kv => new NodePoint(Math.Round(Math.Clamp(kv.Value.X, -100_000, 100_000)), Math.Round(Math.Clamp(kv.Value.Y, -100_000, 100_000))), StringComparer.Ordinal);
            var routing = s.Routing with { Rules = doc.Rules, Final = doc.Final, Preset = RoutingPreset.Custom };
            if (VersionOf(routing) == VersionOf(s.Routing))
            {
                routing = s.Routing; // only the layout moved: no reconnect
            }

            version = VersionOf(routing);
            return s with { Routing = routing, Ui = s.Ui with { RoutingLayout = layout } };
        });

        if (stale)
        {
            return JsonReply(409, new RoutingSaveResult(false, null, [new RoutingProblem(null, "The rules were changed elsewhere (Routing page or an agent) since this page loaded. Reload to edit the current rules.")]));
        }

        _logger.LogInformation("Routing editor saved {Count} rule(s).", doc.Rules.Count);
        return JsonReply(200, new RoutingSaveResult(true, version, []));
    }

    /// <summary>The same checks as the Routing page, with each problem pointing at its rule.</summary>
    public static IReadOnlyList<RoutingProblem> Check(RoutingGraphDocument doc, IReadOnlyCollection<string> tags)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var problems = new List<RoutingProblem>();
        if (doc.Rules.Count > MaxRules)
        {
            return [new RoutingProblem(null, $"At most {MaxRules} rules.")];
        }

        if (doc.Final is not (RuleActionType.Proxy or RuleActionType.Direct or RuleActionType.Block))
        {
            problems.Add(new RoutingProblem(null, "Everything else must go through the VPN, direct, or be blocked."));
        }

        foreach (var group in doc.Rules.GroupBy(r => r.Id).Where(g => g.Count() > 1))
        {
            problems.Add(new RoutingProblem(group.Key, "Two rules share one id."));
        }

        foreach (var rule in doc.Rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 64 || NetText.HasControlChars(rule.Id))
            {
                problems.Add(new RoutingProblem(null, "A rule has no valid id."));
                continue;
            }

            if (rule.Action.Type is RuleActionType.Outbound or RuleActionType.HijackDns)
            {
                problems.Add(new RoutingProblem(rule.Id, "Connect the rule to Proxy, Direct or Block."));
                continue;
            }

            var v = RuleValidator.Validate(rule, tags);
            problems.AddRange(v.Errors.Select(e => new RoutingProblem(rule.Id, e.Message)));
        }

        return problems;
    }

    private RoutingTestResult Test(RoutingTestRequest? t)
    {
        if (t is null)
        {
            return new RoutingTestResult(null, "Proxy", true, "Nothing to test.");
        }

        IPAddress? ip = null;
        if (!string.IsNullOrWhiteSpace(t.Ip) && !IPAddress.TryParse(t.Ip.Trim(), out ip))
        {
            return new RoutingTestResult(null, _settings.Current.Routing.Final.ToString(), true, "Not an IP address.");
        }

        var domain = string.IsNullOrWhiteSpace(t.Domain) ? null : t.Domain.Trim();
        if (domain is not null && IPAddress.TryParse(domain, out var asIp))
        {
            (ip, domain) = (asIp, null);
        }

        var app = string.IsNullOrWhiteSpace(t.App) ? null : t.App.Trim();
        var s = _settings.Current;
        var matcher = new RuleMatcher(s.Routing.Rules, new RuleAction(s.Routing.Final));
        var d = matcher.Match(new RouteRequest
        {
            Domain = domain,
            DestinationIp = ip,
            Port = t.Port is > 0 and <= 65535 ? t.Port : 443,
            Network = t.Network is "udp" ? "udp" : "tcp",
            ProcessName = app is null ? null : Path.GetFileName(app.Replace('\\', '/')),
            ProcessPath = app is not null && (app.Contains('/', StringComparison.Ordinal) || app.Contains('\\', StringComparison.Ordinal)) ? app : null,
        });
        var explanation = d.Explanation + (d.Indeterminate ? " Some geo / rule-set lists could not be checked here; the core decides those." : string.Empty);
        return new RoutingTestResult(d.Rule?.Id, d.Action.Type.ToString(), d.Indeterminate, explanation);
    }

    private static byte[]? _page;

    private static byte[] Page()
    {
        if (_page is { } cached)
        {
            return cached;
        }

        using var stream = typeof(RoutingEditorServer).Assembly.GetManifestResourceStream("JackiVpn.Application.Routing.RoutingEditor.html")
            ?? throw new InvalidOperationException("The routing editor page is missing from the build.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return _page = ms.ToArray();
    }

    private static (int, string, byte[]) Text(int status, string text) => (status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    private static (int, string, byte[]) JsonReply(int status, object value) => (status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, Json));

    private static async Task WriteAsync(Stream stream, int status, string type, byte[] body, CancellationToken ct)
    {
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict", 415 => "Unsupported Media Type", 421 => "Misdirected Request", 422 => "Unprocessable Content", _ => "Error" };
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(type).Append("\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Referrer-Policy: no-referrer\r\n")
            .Append("X-Frame-Options: DENY\r\n")
            .Append("Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'\r\n")
            .Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A deliberately small HTTP/1.1 request reader for the loopback editor (no chunked bodies, no keep-alive).</summary>
public static class HttpRequestReader
{
    public sealed record Request(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    public static async Task<Request?> ReadAsync(Stream stream, int maxBody, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[16 * 1024];
        var filled = 0;
        int headerEnd;
        while ((headerEnd = IndexOf(buffer.AsSpan(0, filled), "\r\n\r\n"u8)) < 0)
        {
            if (filled == buffer.Length)
            {
                return null; // headers too large
            }

            var n = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return null;
            }

            filled += n;
        }

        var lines = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || first[0] is not ("GET" or "PUT" or "POST") || !first[1].StartsWith('/'))
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                return null;
            }

            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (headers.ContainsKey("transfer-encoding"))
        {
            return null;
        }

        var length = 0;
        if (headers.TryGetValue("content-length", out var cl) && (!int.TryParse(cl, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out length) || length > maxBody))
        {
            return null;
        }

        var body = new byte[length];
        var already = Math.Min(length, filled - headerEnd - 4);
        Array.Copy(buffer, headerEnd + 4, body, 0, already);
        var read = already;
        while (read < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return null;
            }

            read += n;
        }

        return new Request(first[0], first[1], headers, body);
    }

    private static int IndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value) => data.IndexOf(value);
}
