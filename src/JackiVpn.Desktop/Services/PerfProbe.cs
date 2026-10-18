#if DEBUG
using System;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Desktop.Services;

/// <summary>
/// Debug builds with JACKIVPN_PERF=1: every 5 s logs how smoothly the window draws (frames, slow frames over 33 ms,
/// the longest gap) and how long the UI thread was blocked (an Input-priority ping every 50 ms). For measuring
/// lag before and after a change; never in release builds.
/// </summary>
internal sealed class PerfProbe
{
    private readonly ILogger _logger;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastFrame;
    private int _frames;
    private int _slowFrames;
    private double _worstFrame;
    private double _worstStall;
    private int _stalls;

    private PerfProbe(ILogger logger)
    {
        _logger = logger;
        CompositionTarget.Rendering += OnRendering;
        var ping = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) }; // what a click or a key waits for
        var lastTick = 0.0;
        ping.Tick += (_, _) =>
        {
            // The timer runs every 50 ms unless the UI thread is busy: any extra delay is a stall.
            var now = _clock.Elapsed.TotalMilliseconds;
            var waited = lastTick == 0 ? 0 : now - lastTick - 50;
            lastTick = now;
            _worstStall = Math.Max(_worstStall, waited);
            if (waited > 50)
            {
                _stalls++;
            }
        };
        ping.Start();
        // Which UI-thread operations take long (name of the delegate and what it belongs to).
        var started = new System.Collections.Generic.Dictionary<DispatcherOperation, double>();
        var hooks = Dispatcher.CurrentDispatcher.Hooks;
        hooks.OperationStarted += (_, e) => started[e.Operation] = _clock.Elapsed.TotalMilliseconds;
        hooks.OperationCompleted += (_, e) =>
        {
            if (!started.Remove(e.Operation, out var at))
            {
                return;
            }

            var took = _clock.Elapsed.TotalMilliseconds - at;
            var name = Describe(e.Operation);
            var cut = name.IndexOf(" arg=", StringComparison.Ordinal) is var i and > 0 && name.IndexOf(" sm=", StringComparison.Ordinal) is var j and > 0 ? name[..i] + name[j..] : name;
            _ops.TryGetValue(cut, out var sum);
            _ops[cut] = (sum.Count + 1, sum.Ms + took);
            if (took > 40)
            {
                _slowOps.Add($"{took:0} ms {e.Operation.Priority} {Describe(e.Operation)}");
            }
        };
        hooks.OperationAborted += (_, e) => started.Remove(e.Operation);
        var report = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        report.Tick += (_, _) => Report();
        report.Start();
    }

    public static void StartIfAsked(ILogger logger)
    {
        if (Environment.GetEnvironmentVariable("JACKIVPN_PERF") == "1")
        {
            _ = new PerfProbe(logger);
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs r || r.RenderingTime == _lastFrame)
        {
            return;
        }

        var gap = (r.RenderingTime - _lastFrame).TotalMilliseconds;
        _lastFrame = r.RenderingTime;
        _frames++;
        if (gap < 1000)
        {
            _worstFrame = Math.Max(_worstFrame, gap);
            if (gap > 33.4)
            {
                _slowFrames++;
            }
        }
    }

    private readonly System.Collections.Generic.List<string> _slowOps = [];
    private TimeSpan _pause;
    private (int, int, int) _gcs;
    private long _alloc;
    private TimeSpan _cpu;
    private readonly System.Collections.Generic.Dictionary<string, (int Count, double Ms)> _ops = new(StringComparer.Ordinal);

    private static string Describe(DispatcherOperation op)
    {
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        var method = op.GetType().GetField("_method", Any)?.GetValue(op) as Delegate;
        var args = op.GetType().GetField("_args", Any)?.GetValue(op);
        var text = method is null ? op.GetType().Name : $"{method.Method.DeclaringType?.Name}.{method.Method.Name}";
        if (method?.Target is { } target && target.GetType().FullName is { } t && !t.StartsWith("System.Windows", StringComparison.Ordinal))
        {
            text += " target=" + t;
        }

        // Continuations posted through the synchronization context: the state machine says which async method.
        if (args is Delegate d && d.Target is { } inner)
        {
            text += " arg=" + inner.GetType().FullName;
            var box = inner.GetType().GetField("m_stateMachine", Any)?.GetValue(inner) ?? inner.GetType().GetField("StateMachine", Any)?.GetValue(inner);
            if (box is not null)
            {
                text += " sm=" + box.GetType().FullName;
            }
        }
        else if (args is not null)
        {
            text += " arg=" + args.GetType().FullName;
        }

        return text;
    }

    private void Report()
    {
        foreach (var op in _slowOps)
        {
            _logger.LogInformation("PERF slow-op {Op}", op);
        }

        _slowOps.Clear();
        foreach (var (op, (count, ms)) in System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(_ops, o => o.Value.Ms), 6))
        {
            _logger.LogInformation("PERF ops {Count}x {Ms:0} ms {Op}", count, ms, op);
        }

        _ops.Clear();
        var pause = GC.GetTotalPauseDuration();
        var gcs = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        _logger.LogInformation("PERF frames={Frames} slow={Slow} worstFrameMs={Worst:0} uiStalls={Stalls} worstStallMs={Stall:0} shadows={Shadows} shadowMs={ShadowMs:0} gc0={G0} gc1={G1} gc2={G2} gcPauseMs={Pause:0} allocMB={Alloc:0.0} cpuMs={Cpu:0}",
            _frames, _slowFrames, _worstFrame, _stalls, _worstStall, Views.SoftShadow.Renders, Views.SoftShadow.RenderMs,
            gcs.Item1 - _gcs.Item1, gcs.Item2 - _gcs.Item2, gcs.Item3 - _gcs.Item3, (pause - _pause).TotalMilliseconds,
            (GC.GetTotalAllocatedBytes() - _alloc) / 1048576.0, (Process.GetCurrentProcess().TotalProcessorTime - _cpu).TotalMilliseconds);
        (_pause, _gcs, _alloc, _cpu) = (pause, gcs, GC.GetTotalAllocatedBytes(), Process.GetCurrentProcess().TotalProcessorTime);
        (Views.SoftShadow.Renders, Views.SoftShadow.RenderMs) = (0, 0);

        (_frames, _slowFrames, _worstFrame, _stalls, _worstStall) = (0, 0, 0, 0, 0);
    }
}
#endif
