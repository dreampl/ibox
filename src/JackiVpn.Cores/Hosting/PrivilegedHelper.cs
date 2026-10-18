using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace JackiVpn.Cores.Hosting;

/// <summary>
/// The Linux privileged helper: one <c>pkexec</c> when the app opens, then no password again for connecting,
/// disconnecting or switching servers. The helper is the same program in "helper" mode, running as root only for the
/// app's session and talking only over its own standard input and output (so nothing else can reach it); it exits
/// when the app closes. It does exactly two things, both checked here: run the installed sing-box (fixed path, fixed
/// arguments, its working folder and every file path in the configuration forced into a root-owned folder or the
/// user's own IBOX data) and a small set of nmcli calls. Everything else is refused.
/// </summary>
public sealed record HelperMessage
{
    /// <summary>Requests: spawn, kill, nmcli, bye. Events: started, line, exited, done, error.</summary>
    public string Op { get; init; } = string.Empty;

    public int Id { get; init; }

    public IReadOnlyList<string>? Args { get; init; }

    /// <summary>spawn: the configuration (sent on the core's standard input).</summary>
    public string? Config { get; init; }

    public string? Text { get; init; }

    public int? Pid { get; init; }

    public int? Code { get; init; }

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}

public sealed class HelperRejectedException(string message) : Exception(message);

/// <summary>What the helper accepts (no I/O here: tested on any platform).</summary>
public static class HelperRules
{
    public const int MaxLine = 1024 * 1024;

    /// <summary>The only argument shape sing-box is ever run with; the working folder is the helper's own.</summary>
    public static IReadOnlyList<string> CoreArguments(string runDir) => ["run", "-c", "stdin", "-D", runDir, "--disable-color"];

    /// <summary>
    /// The configuration as it may run as root: the cache goes to the root-owned run folder, logs only to the
    /// output the app reads, no web UI folder, the control API on loopback only, and local rule sets only from the
    /// user's own IBOX data folder (by their real path — a link elsewhere is refused).
    /// </summary>
    public static string Sanitize(string config, string runDir, string dataDir, Func<string, string>? realPath = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        realPath ??= Path.GetFullPath;
        if (JsonNode.Parse(config) is not JsonObject root)
        {
            throw new HelperRejectedException("The configuration is not a JSON object.");
        }

        if (root["log"] is JsonObject log)
        {
            log.Remove("output");
        }

        if (root["experimental"] is JsonObject experimental)
        {
            if (experimental["cache_file"] is JsonObject cache)
            {
                cache["path"] = Path.Combine(runDir, "cache.db");
            }

            if (experimental["clash_api"] is JsonObject api)
            {
                api.Remove("external_ui");
                api.Remove("external_ui_download_url");
                var controller = api["external_controller"]?.GetValue<string>();
                if (controller is not null && !controller.StartsWith("127.0.0.1:", StringComparison.Ordinal) && !controller.StartsWith("[::1]:", StringComparison.Ordinal))
                {
                    throw new HelperRejectedException("The control API must listen on loopback only.");
                }
            }

            experimental.Remove("v2ray_api");
        }

        var data = dataDir.TrimEnd('/') + "/";
        foreach (var set in (root["route"]?["rule_set"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (set["path"]?.GetValue<string>() is { } path)
            {
                var real = realPath(path);
                if (!real.StartsWith(data, StringComparison.Ordinal) || real.Contains("/../", StringComparison.Ordinal))
                {
                    throw new HelperRejectedException($"Rule set outside IBOX's data folder: {path}");
                }
            }
        }

        return root.ToJsonString();
    }

    /// <summary>nmcli as root: bring IBOX's own VPN connections up or down, or set DNS on a connection.</summary>
    public static string? NmcliProblem(IReadOnlyList<string>? args)
    {
        if (args is not { Count: >= 3 } || args.ToArray() is not { } a || a.Any(x => x.Length > 200 || x.Any(char.IsControl) || x.StartsWith('-')))
        {
            return "nmcli: not an allowed call.";
        }

        return a switch
        {
            ["connection", "up" or "down", var name] when name.StartsWith("IBOX ", StringComparison.Ordinal) => null,
            ["connection", "modify", _, .. var pairs] when pairs.Length is > 0 and <= 8 && pairs.Length % 2 == 0
                && pairs.Where((_, i) => i % 2 == 0).All(k => k is "ipv4.dns" or "ipv6.dns" or "ipv4.ignore-auto-dns" or "ipv6.ignore-auto-dns" or "ipv4.never-default" or "ipv6.never-default") => null,
            ["connection", "up", _] => null,
            _ => "nmcli: not an allowed call.",
        };
    }
}

/// <summary>The root side: reads requests on its input, runs what the rules allow, reports on its output.</summary>
public sealed class HelperServer(string corePath, string runDir, string dataDir, Func<string, IReadOnlyList<string>, string, IHostedProcess> launch, Func<IReadOnlyList<string>, Task<(int Code, string Output)>> nmcli)
{
    private readonly ConcurrentDictionary<int, IHostedProcess> _children = new();
    private readonly SemaphoreSlim _write = new(1, 1);

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        try
        {
            while (!ct.IsCancellationRequested && await input.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.Length > HelperRules.MaxLine)
                {
                    continue;
                }

                HelperMessage? m;
                try
                {
                    m = JsonSerializer.Deserialize<HelperMessage>(line, HelperMessage.Json);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (m is null)
                {
                    continue;
                }

                switch (m.Op)
                {
                    case "spawn":
                        await SpawnAsync(m, output, ct).ConfigureAwait(false);
                        break;
                    case "kill" when _children.TryGetValue(m.Id, out var child):
                        child.Kill();
                        break;
                    case "nmcli":
                        if (HelperRules.NmcliProblem(m.Args) is { } problem)
                        {
                            await SendAsync(output, new HelperMessage { Op = "error", Id = m.Id, Text = problem }).ConfigureAwait(false);
                        }
                        else
                        {
                            var (code, text) = await nmcli(m.Args!).ConfigureAwait(false);
                            await SendAsync(output, new HelperMessage { Op = "done", Id = m.Id, Code = code, Text = text }).ConfigureAwait(false);
                        }

                        break;
                    case "bye":
                        return;
                }
            }
        }
        finally
        {
            // The app went away: nothing it started keeps running as root.
            foreach (var child in _children.Values)
            {
                child.Kill();
            }
        }
    }

    private async Task SpawnAsync(HelperMessage m, TextWriter output, CancellationToken ct)
    {
        try
        {
            var config = HelperRules.Sanitize(m.Config ?? throw new HelperRejectedException("No configuration."), runDir, dataDir);
            var child = launch(corePath, HelperRules.CoreArguments(runDir), runDir);
            _children[m.Id] = child;
            child.Line += line =>
            {
                if (line is not null)
                {
                    _ = SendAsync(output, new HelperMessage { Op = "line", Id = m.Id, Text = line });
                }
            };
            child.Exited += () =>
            {
                _children.TryRemove(m.Id, out _);
                _ = SendAsync(output, new HelperMessage { Op = "exited", Id = m.Id, Code = child.ExitCode });
            };
            await child.StartAsync(config, ct).ConfigureAwait(false);
            await SendAsync(output, new HelperMessage { Op = "started", Id = m.Id, Pid = child.Id }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HelperRejectedException or JsonException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await SendAsync(output, new HelperMessage { Op = "error", Id = m.Id, Text = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(TextWriter output, HelperMessage message)
    {
        await _write.WaitAsync().ConfigureAwait(false);
        try
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(message, HelperMessage.Json)).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }
}

/// <summary>The app side: cores and nmcli calls through the helper, over the helper's standard input and output.</summary>
public sealed class HelperClient : IDisposable
{
    private readonly TextWriter _toHelper;
    private readonly ConcurrentDictionary<int, RemoteCore> _cores = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<HelperMessage>> _calls = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private int _next;

    public HelperClient(TextReader fromHelper, TextWriter toHelper)
    {
        _toHelper = toHelper;
        _ = Task.Run(() => ReadAsync(fromHelper));
    }

    /// <summary>False once the helper has gone (the app then runs cores itself again).</summary>
    public bool Alive { get; private set; } = true;

    public IHostedProcess Core() => new RemoteCore(this, Interlocked.Increment(ref _next));

    /// <summary>An nmcli call as root (exit code, output), or a refusal.</summary>
    public async Task<(int Code, string Output)> NmcliAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _next);
        var done = new TaskCompletionSource<HelperMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[id] = done;
        await SendAsync(new HelperMessage { Op = "nmcli", Id = id, Args = args }).ConfigureAwait(false);
        var reply = await done.Task.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return (reply.Op == "done" ? reply.Code ?? -1 : -1, reply.Text ?? string.Empty);
    }

    private async Task SendAsync(HelperMessage m)
    {
        await _write.WaitAsync().ConfigureAwait(false);
        try
        {
            await _toHelper.WriteLineAsync(JsonSerializer.Serialize(m, HelperMessage.Json)).ConfigureAwait(false);
            await _toHelper.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReadAsync(TextReader input)
    {
        try
        {
            while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                HelperMessage? m;
                try
                {
                    m = JsonSerializer.Deserialize<HelperMessage>(line, HelperMessage.Json);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (m is null)
                {
                    continue;
                }

                if (_cores.TryGetValue(m.Id, out var core))
                {
                    core.On(m);
                }
                else if (_calls.TryRemove(m.Id, out var call))
                {
                    call.TrySetResult(m);
                }
            }
        }
        catch (IOException)
        {
            // the helper is gone
        }

        Alive = false;
        foreach (var core in _cores.Values)
        {
            core.On(new HelperMessage { Op = "exited", Code = -1 });
        }

        foreach (var call in _calls.Values)
        {
            call.TrySetResult(new HelperMessage { Op = "error", Text = "The helper has closed." });
        }
    }

    public void Dispose()
    {
        _ = SendAsync(new HelperMessage { Op = "bye" });
        _write.Dispose();
    }

    private sealed class RemoteCore(HelperClient client, int id) : IHostedProcess
    {
        private readonly TaskCompletionSource<HelperMessage> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id { get; private set; }

        public event Action<string?>? Line;

        public event Action? Exited;

        public int ExitCode { get; private set; }

        public async Task StartAsync(string config, CancellationToken ct)
        {
            client._cores[id] = this;
            await client.SendAsync(new HelperMessage { Op = "spawn", Id = id, Config = config }).ConfigureAwait(false);
            var reply = await _started.Task.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (reply.Op != "started")
            {
                throw new InvalidOperationException("The helper refused to start the core: " + reply.Text);
            }

            Id = reply.Pid ?? 0;
        }

        public void On(HelperMessage m)
        {
            switch (m.Op)
            {
                case "started":
                case "error":
                    _started.TrySetResult(m);
                    break;
                case "line":
                    Line?.Invoke(m.Text);
                    break;
                case "exited":
                    ExitCode = m.Code ?? -1;
                    _started.TrySetResult(m);
                    client._cores.TryRemove(id, out _);
                    Exited?.Invoke();
                    break;
            }
        }

        public void Kill() => _ = client.SendAsync(new HelperMessage { Op = "kill", Id = id });

        public void Dispose() => client._cores.TryRemove(id, out _);
    }
}

/// <summary>A core as a local child process (used inside the helper).</summary>
public sealed class LocalHostedProcess(string exe, IReadOnlyList<string> args, string cwd) : IHostedProcess
{
    private Process? _process;

    public int Id => _process?.Id ?? 0;

    public event Action<string?>? Line;

    public event Action? Exited;

    public int ExitCode => _process is { HasExited: true } p ? p.ExitCode : -1;

    public async Task StartAsync(string config, CancellationToken ct)
    {
        Directory.CreateDirectory(cwd);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd, StandardInputEncoding = new UTF8Encoding(false) };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => Line?.Invoke(e.Data);
        _process.ErrorDataReceived += (_, e) => Line?.Invoke(e.Data);
        _process.Exited += (_, _) => Exited?.Invoke();
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        await _process.StandardInput.WriteAsync(config.AsMemory(), ct).ConfigureAwait(false);
        _process.StandardInput.Close();
    }

    public void Kill()
    {
        try
        {
            _process?.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
    }

    public void Dispose() => _process?.Dispose();
}
