using System.Text.RegularExpressions;
using JackiVpn.Cores.Capabilities;
using JackiVpn.Cores.Generation;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Cores;
using JackiVpn.Domain.Profiles;
using JackiVpn.Security;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Cores.Engines;

public abstract partial class CoreEngineBase : ICoreEngine
{
    private readonly SemaphoreSlim _capsGate = new(1, 1);
    private CoreCapabilities? _caps;

    protected CoreEngineBase(CoreBinaryCatalog catalog, ILogger logger)
    {
        Catalog = catalog;
        Logger = logger;
    }

    protected CoreBinaryCatalog Catalog { get; }

    protected ILogger Logger { get; }

    public abstract CoreKind Kind { get; }

    public TimeSpan ValidationTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public async Task<CoreCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        if (_caps is not null)
        {
            return _caps;
        }

        await _capsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_caps is not null)
            {
                return _caps;
            }

            using var binary = Catalog.Verify(Kind);
            var result = await CoreCommandRunner.RunAsync(binary.ExecutablePath, ["version"], null, Catalog.CoresDirectory, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            var version = ParseVersion(result.Combined)
                ?? throw new CoreIntegrityException($"Could not determine the {Kind} version from its output.");
            _caps = Derive(version);
            Logger.LogInformation("{Core} {Version} detected (features: {Features}).", Kind, version.DisplayVersion, _caps.Features);
            return _caps;
        }
        finally
        {
            _capsGate.Release();
        }
    }

    protected abstract CoreVersionInfo? ParseVersion(string output);

    protected abstract CoreCapabilities Derive(CoreVersionInfo version);

    public CapabilityReport Negotiate(ProxyProfile profile, CoreCapabilities capabilities, SessionKind session, bool muxEnabled) =>
        CapabilityMatrix.Negotiate(profile, capabilities, session, muxEnabled);

    public abstract GeneratedCoreConfig GenerateConfig(CoreSessionPlan plan, CoreCapabilities capabilities);

    public async Task<CoreValidationResult> ValidateAsync(GeneratedCoreConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        using var binary = Catalog.Verify(Kind);
        var result = await CoreCommandRunner.RunAsync(
            binary.ExecutablePath, CheckArguments(), config.Json, Catalog.CoresDirectory, ValidationTimeout, ct).ConfigureAwait(false);
        var output = SecretRedactor.Redact(StripAnsi(result.Combined));
        foreach (var s in config.Secrets)
        {
            output = output.Replace(s, SecretRedactor.Mask, StringComparison.Ordinal);
        }

        if (result.TimedOut)
        {
            return new CoreValidationResult(false, [$"{Kind} config check timed out."], output);
        }

        return result.ExitCode == 0 && !HasFatal(output)
            ? new CoreValidationResult(true, [], output)
            : new CoreValidationResult(false, ExtractErrors(output), output);
    }

    protected abstract IReadOnlyList<string> CheckArguments();

    public abstract CoreLaunchSpec BuildLaunchSpec(string workingDirectory);

    protected static bool HasFatal(string output) => output.Contains("FATAL", StringComparison.Ordinal) || output.Contains("Failed to start", StringComparison.Ordinal);

    /// <summary>Turns core output into short, user-presentable messages (already redacted).</summary>
    public static IReadOnlyList<string> ExtractErrors(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var errors = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var m = SingBoxFatalRegex().Match(line);
            if (m.Success)
            {
                errors.Add(Humanize(m.Groups["msg"].Value));
                continue;
            }

            var x = XrayFailRegex().Match(line);
            if (x.Success)
            {
                errors.Add(Humanize(x.Groups["msg"].Value));
            }
        }

        if (errors.Count == 0)
        {
            var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
            errors.Add(string.IsNullOrEmpty(last) ? "The core rejected the configuration." : Humanize(last));
        }

        return errors;
    }

    private static string Humanize(string msg)
    {
        msg = msg.Trim();
        return msg.Length > 400 ? msg[..400] + "…" : msg;
    }

    protected static string StripAnsi(string s) => AnsiRegex().Replace(s, string.Empty);

    [GeneratedRegex(@"(?:FATAL|ERROR)\[\d+\]\s*(?<msg>.+)$")]
    private static partial Regex SingBoxFatalRegex();

    [GeneratedRegex(@"Failed to start:\s*(?<msg>.+)$")]
    private static partial Regex XrayFailRegex();

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiRegex();
}

public sealed class SingBoxCoreEngine(CoreBinaryCatalog catalog, ILogger<SingBoxCoreEngine> logger) : CoreEngineBase(catalog, logger)
{
    public override CoreKind Kind => CoreKind.SingBox;

    protected override CoreVersionInfo? ParseVersion(string output) => CoreVersionParser.ParseSingBox(output);

    protected override CoreCapabilities Derive(CoreVersionInfo version) => CapabilityMatrix.ForSingBox(version);

    protected override IReadOnlyList<string> CheckArguments() => ["check", "-c", "stdin", "--disable-color"];

    public override CoreLaunchSpec BuildLaunchSpec(string workingDirectory) => new(
        CoreKind.SingBox,
        Catalog.PathOf(CoreKind.SingBox),
        ["run", "-c", "stdin", "-D", workingDirectory, "--disable-color"],
        workingDirectory,
        new Dictionary<string, string>());

    public override GeneratedCoreConfig GenerateConfig(CoreSessionPlan plan, CoreCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var issues = plan.Kind == SessionKind.ProbeBatch
            ? []
            : plan.ChainBackend is null
                ? Negotiate(plan.Profiles[0], capabilities, plan.Kind, plan.MuxEnabled).Issues
                : [];
        if (issues.Any(i => i.Impact == CapabilityImpact.Blocking))
        {
            throw new CoreConfigException("The profile cannot run on sing-box: " + string.Join(" ", issues.Where(i => i.Impact == CapabilityImpact.Blocking).Select(i => i.Message)));
        }

        var (config, secrets, notes, ports) = SingBoxConfigBuilder.Build(plan, capabilities);
        return new GeneratedCoreConfig
        {
            Kind = CoreKind.SingBox,
            Session = plan.Kind,
            Json = config.ToJsonString(JsonDefaults.CoreConfig),
            Report = new CapabilityReport(CoreKind.SingBox, issues),
            Secrets = secrets.Concat(plan.ClashApi is { } api ? [api.Secret] : []).Distinct().ToArray(),
            ClashApi = plan.ClashApi,
            ListenPorts = ports,
            Notes = notes,
        };
    }
}

public sealed class XrayCoreEngine(CoreBinaryCatalog catalog, ILogger<XrayCoreEngine> logger) : CoreEngineBase(catalog, logger)
{
    public override CoreKind Kind => CoreKind.Xray;

    protected override CoreVersionInfo? ParseVersion(string output) => CoreVersionParser.ParseXray(output);

    protected override CoreCapabilities Derive(CoreVersionInfo version) => CapabilityMatrix.ForXray(version);

    protected override IReadOnlyList<string> CheckArguments() => ["run", "-test", "-c", "stdin:"];

    public override CoreLaunchSpec BuildLaunchSpec(string workingDirectory) => new(
        CoreKind.Xray,
        Catalog.PathOf(CoreKind.Xray),
        ["run", "-c", "stdin:"],
        workingDirectory,
        new Dictionary<string, string>
        {
            // geoip.dat / geosite.dat are shipped next to xray.exe (hash-verified manifest directory).
            ["XRAY_LOCATION_ASSET"] = Catalog.CoresDirectory,
        });

    public override GeneratedCoreConfig GenerateConfig(CoreSessionPlan plan, CoreCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var issues = plan.Kind == SessionKind.ProbeBatch ? [] : Negotiate(plan.Profiles[0], capabilities, plan.Kind, plan.MuxEnabled).Issues;
        if (issues.Any(i => i.Impact == CapabilityImpact.Blocking))
        {
            throw new CoreConfigException("The profile cannot run on Xray: " + string.Join(" ", issues.Where(i => i.Impact == CapabilityImpact.Blocking).Select(i => i.Message)));
        }

        var (config, secrets, notes, ports) = XrayConfigBuilder.Build(plan, capabilities);
        return new GeneratedCoreConfig
        {
            Kind = CoreKind.Xray,
            Session = plan.Kind,
            Json = config.ToJsonString(JsonDefaults.CoreConfig),
            Report = new CapabilityReport(CoreKind.Xray, issues),
            Secrets = secrets.Distinct().ToArray(),
            ListenPorts = ports,
            Notes = notes,
        };
    }
}

public sealed class CoreConfigException(string message) : Exception(message);
