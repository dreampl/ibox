namespace JackiVpn.Cores.Hosting;

/// <summary>A core run somewhere else than a local child process (Linux: by the privileged helper).</summary>
public interface IHostedProcess : IDisposable
{
    int Id { get; }

    /// <summary>One line of the core's output (null at the end).</summary>
    event Action<string?>? Line;

    event Action? Exited;

    int ExitCode { get; }

    /// <summary>Starts the core and hands it its configuration on standard input.</summary>
    Task StartAsync(string config, CancellationToken ct);

    void Kill();
}

/// <summary>Where cores run. Null (the default) = local child processes; Linux sets a host for TUN sessions.</summary>
public static class CoreHosting
{
    public static Func<CoreLaunchSpec, GeneratedCoreConfig, IHostedProcess?>? Host { get; set; }
}
