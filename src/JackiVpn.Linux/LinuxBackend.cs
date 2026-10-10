using System.Diagnostics;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Sessions;
using JackiVpn.Cores;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Linux;

/// <summary>
/// Linux: sing-box runs as a child process. TUN mode (root) captures the whole system with auto-route; proxy mode
/// opens the loopback mixed port and, on GNOME (Ubuntu), points the desktop proxy at it until disconnect. In TUN
/// mode the desktop proxy is set too (optional, on by default): some apps (Claude, …) check the system proxy and
/// treat "no proxy" as "no VPN" even though the TUN already carries their traffic.
/// </summary>
public sealed class LinuxBackend(SessionPlanFactory plans, ICoreEngine singBox, ICoreEngine? xray, Func<ISessionRunner> runnerFactory, ConnectionMode mode, bool setDesktopProxy, ILogger logger) : IConnectionBackend
{
    private ISessionRunner? _runner;
    private CancellationTokenSource? _trafficCts;
    private GnomeProxy? _proxy;

    public ConnectionMode Mode => mode;

    public event EventHandler<BackendFault>? Faulted;

    public event EventHandler<TrafficSnapshot>? Traffic;

    public async Task<BackendStartResult> StartAsync(BackendStartRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await StopRunnerAsync().ConfigureAwait(false);
        var s = request.Settings;
        var sbCaps = await singBox.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        var xrCaps = xray is null ? null : await xray.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        var plan = await plans.CreateAsync(new SessionRequest
        {
            Profile = request.Profile,
            Mode = mode,
            Core = request.Profile.User.CoreOverride ?? s.Connection.Core,
            Dns = s.Dns,
            Ipv6 = s.Ipv6,
            TunnelSupportsIpv6 = request.TunnelSupportsIpv6,
            Routing = s.Routing,
            RuleSetPaths = request.RuleSetPaths,
            MuxEnabled = request.MuxEnabled,
            DebugLogging = s.Logging.MinimumLevel >= AppLogLevel.Debug,
            ListenPort = s.Connection.LocalProxyPort,
            Performance = s.Performance,
            Traffic = request.Traffic,
            TunSystemProxy = mode == ConnectionMode.Tun && setDesktopProxy && s.Connection.SystemProxyInTun,
        }, sbCaps, xrCaps, ct).ConfigureAwait(false);

        var runner = runnerFactory();
        runner.CoreExited += OnCoreExited;
        runner.SetTrafficLimits(request.Limits);
        _runner = runner;
        var notes = await runner.StartAsync(plan, ct).ConfigureAwait(false);
        var proxyPort = mode == ConnectionMode.SystemProxy ? plan.Frontend.ListenPort : plan.Frontend.TunProxyPort;
        if (setDesktopProxy && proxyPort > 0)
        {
            _proxy ??= GnomeProxy.Apply(proxyPort, logger);
        }

        _trafficCts = new CancellationTokenSource();
        _ = Task.Run(() => TrafficLoopAsync(runner, _trafficCts.Token), CancellationToken.None);
        return new BackendStartResult(plan.Selection.Core, plan.Chained, plan.Warnings.Concat(notes).ToArray(), proxyPort > 0 ? proxyPort : null);
    }

    public Task<Domain.Usage.CoreConnections?> GetConnectionsAsync(CancellationToken ct) =>
        _runner is { } r ? r.GetConnectionsAsync(ct) : Task.FromResult<Domain.Usage.CoreConnections?>(null);

    public Task SetTrafficLimitsAsync(Domain.Usage.TrafficLimits limits, CancellationToken ct)
    {
        _runner?.SetTrafficLimits(limits);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _proxy?.Restore();
        _proxy = null;
        await StopRunnerAsync().ConfigureAwait(false);
    }

    public Task<bool> IsKillSwitchHoldingAsync(CancellationToken ct) => Task.FromResult(false);

    private async Task StopRunnerAsync()
    {
        _trafficCts?.Cancel();
        if (_runner is not null)
        {
            _runner.CoreExited -= OnCoreExited;
            await _runner.DisposeAsync().ConfigureAwait(false);
            _runner = null;
        }
    }

    private void OnCoreExited(object? sender, CoreExitInfo info)
    {
        _proxy?.Restore();
        _proxy = null;
        Faulted?.Invoke(this, new BackendFault($"{info.Core} exited unexpectedly: {info.LastOutput}", false, false));
    }

    private async Task TrafficLoopAsync(ISessionRunner runner, CancellationToken ct)
    {
        long lastUp = 0, lastDown = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                if (await runner.GetTrafficAsync(ct).ConfigureAwait(false) is { } t)
                {
                    Traffic?.Invoke(this, new TrafficSnapshot(t.UploadTotal, t.DownloadTotal, Math.Max(0, t.UploadTotal - lastUp), Math.Max(0, t.DownloadTotal - lastDown), DateTimeOffset.UtcNow));
                    lastUp = t.UploadTotal;
                    lastDown = t.DownloadTotal;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}

/// <summary>GNOME desktop proxy (gsettings): applied while connected, the previous values restored afterwards.</summary>
public sealed class GnomeProxy
{
    private readonly string? _mode;
    private readonly ILogger _logger;

    private GnomeProxy(string? mode, ILogger logger)
    {
        _mode = mode;
        _logger = logger;
    }

    /// <summary>Remembers the user's own proxy mode while ours is applied, so a crash can be undone on the next start.</summary>
    public static string? MarkerPath { get; set; }

    /// <summary>After a crash the desktop would keep pointing at a dead port: put the user's mode back.</summary>
    public static void RecoverStale(ILogger logger)
    {
        if (MarkerPath is not { } marker || !File.Exists(marker))
        {
            return;
        }

        try
        {
            var mode = File.ReadAllText(marker).Trim();
            if (Run("set", "org.gnome.system.proxy", "mode", mode is "manual" or "auto" or "none" ? mode : "none") is not null)
            {
                logger.LogInformation("Restored the desktop proxy left over from an earlier session.");
            }

            File.Delete(marker);
        }
        catch (IOException ex)
        {
            logger.LogWarning("Could not restore the earlier desktop proxy: {Error}", ex.Message);
        }
    }

    public static GnomeProxy? Apply(int port, ILogger logger)
    {
        var previous = Run("get", "org.gnome.system.proxy", "mode");
        if (previous is null)
        {
            logger.LogInformation("gsettings not available; set the proxy manually: 127.0.0.1:{Port}", port);
            return null;
        }

        var mode = previous.Trim().Trim('\'');
        if (MarkerPath is { } marker)
        {
            try
            {
                // A marker from a crashed session already holds the user's real mode; ours must not replace it.
                if (!File.Exists(marker))
                {
                    File.WriteAllText(marker, mode);
                }
                else
                {
                    mode = File.ReadAllText(marker).Trim();
                }
            }
            catch (IOException)
            {
            }
        }

        foreach (var scheme in new[] { "http", "https", "socks" })
        {
            Run("set", $"org.gnome.system.proxy.{scheme}", "host", "127.0.0.1");
            Run("set", $"org.gnome.system.proxy.{scheme}", "port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Run("set", "org.gnome.system.proxy", "ignore-hosts", "['localhost', '127.0.0.0/8', '::1', '10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16']");
        Run("set", "org.gnome.system.proxy", "mode", "manual");
        return new GnomeProxy(mode, logger);
    }

    public void Restore()
    {
        if (Run("set", "org.gnome.system.proxy", "mode", string.IsNullOrEmpty(_mode) ? "none" : _mode) is null)
        {
            _logger.LogWarning("Could not restore the GNOME proxy mode.");
            return;
        }

        try
        {
            if (MarkerPath is { } marker)
            {
                File.Delete(marker);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string? Run(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("gsettings") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            if (Environment.UserName == "root" && Environment.GetEnvironmentVariable("SUDO_USER") is { Length: > 0 } user
                && Environment.GetEnvironmentVariable("SUDO_UID") is { Length: > 0 } uid && uid.All(char.IsAsciiDigit))
            {
                // Started with sudo (TUN): as root, gsettings would change root's settings — run it as the desktop
                // user, on their session bus.
                psi.FileName = "sudo";
                foreach (var a in new[] { "-u", user, "env", $"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/{uid}/bus", "gsettings" })
                {
                    psi.ArgumentList.Add(a);
                }
            }

            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
