namespace JackiVpn.Application.Connection;

/// <summary>Small rolling statistics for the live latency display.</summary>
public static class LatencyStats
{
    public const int Window = 3;

    /// <summary>
    /// Adds a sample and returns the window's median (robust against a single queued request) and jitter
    /// (mean absolute difference between consecutive samples, as RTP/VoIP tools report it).
    /// </summary>
    public static (double Median, double? Jitter) Add(Queue<double> window, double sample)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Enqueue(sample);
        while (window.Count > Window)
        {
            window.Dequeue();
        }

        var inOrder = window.ToArray();
        var sorted = inOrder.Order().ToArray();
        var median = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
        double? jitter = inOrder.Length < 2
            ? null
            : inOrder.Zip(inOrder.Skip(1), (a, b) => Math.Abs(b - a)).Average();
        return (median, jitter);
    }
}
