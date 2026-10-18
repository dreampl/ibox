using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JackiVpn.Domain.Activity;
using JackiVpn.Domain.Bar;

namespace JackiVpn.BarHost;

/// <summary>
/// ibar-host: IBAR's bridge for programs that cannot open IBAR's pipe themselves.
/// <list type="bullet">
/// <item>Started by a browser (Native Messaging; registered under HKCU for the IBAR Downloads extension only): reads
/// the extension's download messages, keeps only name / sizes / state / speed / domain, and passes them to IBAR as
/// that browser's activity. It lives exactly as long as the browser keeps the connection (it ends when its input
/// ends).</item>
/// <item><c>ibar-host adobe &lt;app&gt;</c>: started by the IBAR panel inside an Adobe app; reads one JSON activity
/// per line (task, title, state, progress…) and passes it on for that app.</item>
/// </list>
/// IBAR recognises this program by its place (its own folder), so nothing here carries a secret.
/// </summary>
public static partial class Program
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] == "adobe")
            {
                return await AdobeAsync(args[1]).ConfigureAwait(false);
            }

            return await BrowserAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return 0;
        }
    }

    // ------------------------------------------------------------------ browser (Native Messaging)

    private static async Task<int> BrowserAsync()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        var fromParents = BrowserBridge.BrowserOf(Ancestors());
        var browser = fromParents;
        using var ibar = new IbarPipe();
        var meters = new Dictionary<long, RateMeter>();
        while (await BrowserBridge.ReadAsync(input).ConfigureAwait(false) is { } message)
        {
            var type = message["type"]?.GetValueKind() == JsonValueKind.String ? message["type"]!.GetValue<string>() : null;
            if (type == "hello")
            {
                // The program that started us is trusted over what the extension says about itself.
                var claimed = message["browser"]?.GetValueKind() == JsonValueKind.String ? message["browser"]!.GetValue<string>() : null;
                browser = fromParents ?? (PluginRules.IsBridge(claimed) && claimed != "firefox" ? claimed : "chromium");
                var ok = await ibar.SendAsync(new PluginRequest { Op = "hello", Bridge = browser }).ConfigureAwait(false);
                await BrowserBridge.WriteAsync(output, new JsonObject { ["ibar"] = ok }).ConfigureAwait(false);
                continue;
            }

            browser ??= "chromium";
            if (type != "download" || message["id"] is not JsonValue idValue || !idValue.TryGetValue<long>(out var id))
            {
                continue;
            }

            if (BrowserBridge.Cancelled(message))
            {
                meters.Remove(id);
                await ibar.SendAsync(new PluginRequest { Op = "activity.end", Bridge = browser, Task = "dl-" + id, State = "failed", Detail = "cancelled" }).ConfigureAwait(false);
                continue;
            }

            if (!meters.TryGetValue(id, out var meter))
            {
                meter = new RateMeter(TimeSpan.FromSeconds(4));
                meters[id] = meter;
            }

            var received = message["received"] is JsonValue r && r.TryGetValue<double>(out var bytes) ? (long)bytes : 0;
            var rate = meter.Add(DateTimeOffset.UtcNow, received);
            if (BrowserBridge.ToRequest(message, browser, rate) is { } request)
            {
                if (request.Op == "activity.end")
                {
                    meters.Remove(id);
                }

                await ibar.SendAsync(request).ConfigureAwait(false);
            }
        }

        return 0;
    }

    // ------------------------------------------------------------------ Adobe panel (one JSON object per line)

    private static async Task<int> AdobeAsync(string app)
    {
        if (!PluginRules.IsBridge(app) || app is "chrome" or "edge" or "opera" or "brave" or "vivaldi" or "chromium" or "firefox")
        {
            return 2;
        }

        using var ibar = new IbarPipe();
        await ibar.SendAsync(new PluginRequest { Op = "hello", Bridge = app }).ConfigureAwait(false);
        using var reader = new StreamReader(Console.OpenStandardInput(), Utf8);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.Length > BarPlugins.MaxLineBytes)
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<PluginRequest>(line, BarPlugins.Json) is { } r && r.Op.StartsWith("activity.", StringComparison.Ordinal))
                {
                    // Only the activity fields pass, always for the app named on the command line.
                    await ibar.SendAsync(new PluginRequest
                    {
                        Op = r.Op,
                        Bridge = app,
                        Task = r.Task,
                        Kind = r.Kind,
                        Title = r.Title,
                        Detail = r.Detail,
                        State = r.State,
                        Progress = r.Progress,
                        Accuracy = r.Accuracy,
                    }).ConfigureAwait(false);
                }
            }
            catch (JsonException)
            {
                // not a message
            }
        }

        return 0;
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>The programs above this one (parent, grandparent…), to tell which browser started it.</summary>
    private static IEnumerable<string> Ancestors()
    {
        var pid = Environment.ProcessId;
        for (var i = 0; i < 4; i++)
        {
            var parent = ParentOf(pid);
            if (parent <= 0)
            {
                yield break;
            }

            string? name = null;
            try
            {
                using var p = Process.GetProcessById(parent);
                name = p.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                name = null;
            }

            if (name is null)
            {
                yield break;
            }

            yield return name;
            pid = parent;
        }
    }

    private static int ParentOf(int pid)
    {
        var process = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, (uint)pid);
        if (process == 0)
        {
            return 0;
        }

        try
        {
            var info = new ProcessBasicInformation();
            return NtQueryInformationProcess(process, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0 ? (int)info.InheritedFromUniqueProcessId : 0;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int infoClass, ref ProcessBasicInformation info, int size, out int returned);

    /// <summary>The connection to IBAR, opened when needed and again after IBAR restarts (never in a loop).</summary>
    private sealed class IbarPipe : IDisposable
    {
        private readonly string _name = Environment.GetEnvironmentVariable("IBAR_PIPE") is { Length: > 0 } test ? test : BarPlugins.PipeName(Environment.UserName);
        private NamedPipeClientStream? _pipe;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private PluginRequest? _hello;

        public async Task<bool> SendAsync(PluginRequest request)
        {
            if (request.Op == "hello")
            {
                _hello = request;
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (_pipe is null)
                {
                    if (!await ConnectAsync().ConfigureAwait(false))
                    {
                        return false;
                    }

                    if (_hello is not null && request.Op != "hello")
                    {
                        await ExchangeAsync(_hello).ConfigureAwait(false);
                    }
                }

                try
                {
                    return await ExchangeAsync(request).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    Close();
                }
            }

            return false;
        }

        private async Task<bool> ExchangeAsync(PluginRequest request)
        {
            await _writer!.WriteLineAsync(JsonSerializer.Serialize(request, BarPlugins.Json)).ConfigureAwait(false);
            var reply = await _reader!.ReadLineAsync().ConfigureAwait(false);
            if (reply is null)
            {
                throw new IOException("IBAR closed the connection.");
            }

            return reply.Contains("\"ok\"", StringComparison.Ordinal);
        }

        private async Task<bool> ConnectAsync()
        {
            var pipe = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(500).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return false;
            }

            _pipe = pipe;
            _reader = new StreamReader(pipe, Utf8, false, 4096, leaveOpen: true);
            _writer = new StreamWriter(pipe, Utf8, 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            return true;
        }

        private void Close()
        {
            _reader?.Dispose();
            _writer?.Dispose();
            _pipe?.Dispose();
            _pipe = null;
        }

        public void Dispose() => Close();
    }
}
