using System.Diagnostics;
using JackiVpn.Domain.Connection;

namespace JackiVpn.Application.Connection;

public enum SpeedTestPhase
{
    Latency = 0,
    Download,
    Upload,
    Done,
}

public sealed record SpeedTestProgress(SpeedTestPhase Phase, double CurrentMbps, double Fraction);

public sealed record SpeedTestResult(double? DownloadMbps, double? UploadMbps, double? IdleLatencyMs, double? LoadedLatencyMs, string Server, string? Error);

/// <summary>
/// Throughput test through the active connection against Cloudflare's speed endpoints (the same backend as
/// speed.cloudflare.com). Several parallel streams, like the "multi" mode of common speed tests, so the result
/// reflects the tunnel rather than one TCP window; latency is sampled idle and under load (bufferbloat).
/// </summary>
public sealed class SpeedTestService
{
    public const int Streams = 4;
    private static readonly Uri DownUrl = new("https://speed.cloudflare.com/__down?bytes=50000000");
    private static readonly Uri UpUrl = new("https://speed.cloudflare.com/__up");
    private static readonly Uri PingUrl = new("https://speed.cloudflare.com/__down?bytes=0");

    public TimeSpan DownloadDuration { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan UploadDuration { get; init; } = TimeSpan.FromSeconds(6);

    public async Task<SpeedTestResult> RunAsync(ConnectionMode mode, int? localProxyPort, IProgress<SpeedTestProgress>? progress, CancellationToken ct)
    {
        using var http = ConnectionHttp.Create(mode, localProxyPort, TimeSpan.FromSeconds(30), keepAlive: true);
        try
        {
            progress?.Report(new SpeedTestProgress(SpeedTestPhase.Latency, 0, 0));
            var idle = await LatencyAsync(http, 6, ct).ConfigureAwait(false);

            var loaded = new List<double>();
            using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var loadedTask = Task.Run(async () =>
            {
                using var pinger = ConnectionHttp.Create(mode, localProxyPort, TimeSpan.FromSeconds(10), keepAlive: true);
                await Task.Delay(1500, loadCts.Token).ConfigureAwait(false);
                while (!loadCts.IsCancellationRequested)
                {
                    if (await TimeAsync(pinger, loadCts.Token).ConfigureAwait(false) is { } ms)
                    {
                        lock (loaded)
                        {
                            loaded.Add(ms);
                        }
                    }

                    await Task.Delay(400, loadCts.Token).ConfigureAwait(false);
                }
            }, CancellationToken.None);

            var down = await TransferAsync(http, upload: false, DownloadDuration, progress, ct).ConfigureAwait(false);
            await loadCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await loadedTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            var up = await TransferAsync(http, upload: true, UploadDuration, progress, ct).ConfigureAwait(false);
            progress?.Report(new SpeedTestProgress(SpeedTestPhase.Done, 0, 1));
            double? loadedMedian;
            lock (loaded)
            {
                loadedMedian = Smart.HealthStatistics.Median(loaded);
            }

            return new SpeedTestResult(down, up, idle, loadedMedian, "speed.cloudflare.com", down is null && up is null ? "No data could be transferred." : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new SpeedTestResult(null, null, null, null, "speed.cloudflare.com", ex.Message);
        }
    }

    private static async Task<double?> LatencyAsync(HttpClient http, int samples, CancellationToken ct)
    {
        await TimeAsync(http, ct).ConfigureAwait(false); // opens the kept-alive connection
        var times = new List<double>();
        for (var i = 0; i < samples; i++)
        {
            if (await TimeAsync(http, ct).ConfigureAwait(false) is { } ms)
            {
                times.Add(ms);
            }
        }

        return Smart.HealthStatistics.Median(times);
    }

    private static async Task<double?> TimeAsync(HttpClient http, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var r = await http.GetAsync(PingUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return r.IsSuccessStatusCode ? sw.Elapsed.TotalMilliseconds : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<double?> TransferAsync(HttpClient http, bool upload, TimeSpan duration, IProgress<SpeedTestProgress>? progress, CancellationToken ct)
    {
        long total = 0;
        var phase = upload ? SpeedTestPhase.Upload : SpeedTestPhase.Download;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(duration);
        var sw = Stopwatch.StartNew();
        var payload = new byte[256 * 1024];
        System.Security.Cryptography.RandomNumberGenerator.Fill(payload); // incompressible upload data

        async Task Worker()
        {
            var buffer = new byte[64 * 1024];
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    if (upload)
                    {
                        using var content = new CountingContent(payload, 4, n => Interlocked.Add(ref total, n));
                        using var r = await http.PostAsync(UpUrl, content, cts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        using var r = await http.GetAsync(DownUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                        await using var s = await r.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                        int n;
                        while ((n = await s.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                        {
                            Interlocked.Add(ref total, n);
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
                {
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }

                    await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        var workers = Enumerable.Range(0, Streams).Select(_ => Task.Run(Worker, CancellationToken.None)).ToArray();

        // Report a smoothed live rate; ignore the first second (TCP slow start) in the final figure.
        long warmBytes = 0;
        var warmAt = TimeSpan.Zero;
        long lastBytes = 0;
        var lastAt = TimeSpan.Zero;
        double smooth = 0;
        while (!Task.WhenAll(workers).IsCompleted)
        {
            await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
            var now = sw.Elapsed;
            var bytes = Interlocked.Read(ref total);
            var instant = (bytes - lastBytes) * 8 / Math.Max(0.001, (now - lastAt).TotalSeconds) / 1_000_000;
            smooth = smooth == 0 ? instant : smooth * 0.7 + instant * 0.3;
            lastBytes = bytes;
            lastAt = now;
            if (warmAt == TimeSpan.Zero && now >= TimeSpan.FromSeconds(1))
            {
                warmAt = now;
                warmBytes = bytes;
            }

            progress?.Report(new SpeedTestProgress(phase, smooth, Math.Min(1, now / duration)));
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var elapsed = sw.Elapsed - warmAt;
        var measured = Interlocked.Read(ref total) - warmBytes;
        return measured <= 0 || elapsed <= TimeSpan.Zero ? null : measured * 8 / elapsed.TotalSeconds / 1_000_000;
    }

    /// <summary>Upload body that reports bytes as they are written (repeats one random block).</summary>
    private sealed class CountingContent(byte[] block, int repeat, Action<int> written) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
        {
            for (var i = 0; i < repeat; i++)
            {
                for (var o = 0; o < block.Length; o += 16 * 1024)
                {
                    var n = Math.Min(16 * 1024, block.Length - o);
                    await stream.WriteAsync(block.AsMemory(o, n), cancellationToken).ConfigureAwait(false);
                    written(n);
                }
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = (long)block.Length * repeat;
            return true;
        }
    }
}
