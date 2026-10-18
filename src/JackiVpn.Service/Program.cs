using System.Reflection;
using System.Security.Cryptography;
using JackiVpn.Application.Sessions;
using JackiVpn.Cores;
using JackiVpn.Cores.Engines;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Cores;
using JackiVpn.Infrastructure.Logging;
using JackiVpn.Ipc;
using JackiVpn.Networking.Journal;
using JackiVpn.Networking.KillSwitch;
using JackiVpn.Networking.Recovery;
using JackiVpn.Networking.SystemProxy;
using JackiVpn.Security;
using JackiVpn.Service;
using JackiVpn.Subscriptions.Ownership;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;

// When launched elevated by the app (no visible console), output also goes to a result file it can read.
var resultPath = Option(args, "--result");
var transcript = new System.Text.StringBuilder();
void Out(string line)
{
    Console.WriteLine(line);
    transcript.AppendLine(line);
}

int code;
try
{
    code = command switch
    {
        "install" or "repair" => Install(args, Out),
        "uninstall" => Uninstall(Out),
        "manifest-keygen" => ManifestKeygen(args),
        "manifest-sign" => ManifestSign(args),
        "update-feed" => UpdateFeed(args),
        "console" => await RunHostAsync(args, console: true).ConfigureAwait(false),
        "--help" or "-h" or "help" => Usage(),
        _ => await RunHostAsync(args, console: false).ConfigureAwait(false),
    };
}
catch (InstallException ex)
{
    Out("ERROR: " + ex.Message);
    code = 2;
}
catch (System.ComponentModel.Win32Exception ex)
{
    Out($"ERROR: {ex.Message} (Win32 {ex.NativeErrorCode})");
    code = 3;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
{
    Out("ERROR: " + ex.Message);
    code = 4;
}

if (resultPath is not null && command is "install" or "repair" or "uninstall")
{
    try
    {
        File.WriteAllText(resultPath, transcript.ToString());
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("Could not write the result file: " + ex.Message);
    }
}

return code;

static int Usage()
{
    Console.WriteLine($"""
        {ProductInfo.DisplayName} Service

          install  [--client <IBOX.exe>  ] [--dev]   register the trusted app and install/start the service (admin)
          repair   [--client <IBOX.exe>  ] [--dev]   same as install; re-registers the app after an update (admin)
          uninstall                                     stop and remove the service (admin)
          console  [--data <dir>] [--pipe <name>] [--same-user]
                                                        run in the foreground for diagnostics
          manifest-keygen --out <private.pem>           create an ECDSA P-256 key for provider manifests
          manifest-sign --key <private.pem> --hosts a.example,b.example [--days 30]
                                                        print an {ProductInfo.DefaultProviderManifestHeader} header value
          update-feed --release <dir> --version <x.y.z> --key <private.pem> --out <dir>
                      [--base-url https://connect.jackiairline.ir/update] [--notes-en ".."] [--notes-fa ".."] [--mandatory]
                                                        write the signed app update feed (latest.json + latest.json.sig)

        --dev allows installing from a user-writable folder (developer machines only; insecure).
        """);
    return 0;
}

static string? Option(string[] args, string name)
{
    var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static bool Flag(string[] args, string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

static int Install(string[] args, Action<string> output)
{
    var serviceExe = Environment.ProcessPath ?? throw new InstallException("Cannot determine the service executable path.");
    var client = Option(args, "--client") ?? Path.Combine(Path.GetDirectoryName(serviceExe)!, "IBOX.exe");
    var warnings = ServiceInstaller.Install(serviceExe, client, Flag(args, "--dev"), output);
    foreach (var w in warnings)
    {
        output(w);
    }

    output("Done.");
    return 0;
}

static int Uninstall(Action<string> output)
{
    ServiceInstaller.Uninstall(output);
    output("Done.");
    return 0;
}

static int ManifestKeygen(string[] args)
{
    var output = Option(args, "--out") ?? throw new InstallException("--out <private.pem> is required.");
    if (File.Exists(output))
    {
        throw new InstallException("Refusing to overwrite an existing key file.");
    }

    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    SecureFileSystem.WriteAllTextAtomic(output, key.ExportPkcs8PrivateKeyPem());
    Console.WriteLine("Private key written (keep it secret, never commit it).");
    Console.WriteLine("Public key for OwnershipPolicy.ManifestPublicKeyPem:");
    Console.WriteLine(key.ExportSubjectPublicKeyInfoPem());
    return 0;
}

static int ManifestSign(string[] args)
{
    var keyPath = Option(args, "--key") ?? throw new InstallException("--key <private.pem> is required.");
    var hosts = (Option(args, "--hosts") ?? throw new InstallException("--hosts is required."))
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var days = int.TryParse(Option(args, "--days"), out var d) ? Math.Clamp(d, 1, 365) : 30;
    using var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(keyPath));
    Console.WriteLine(ProfileOwnershipVerifier.CreateManifest(hosts, DateTimeOffset.UtcNow.AddDays(days), key));
    return 0;
}

static int UpdateFeed(string[] args)
{
    var release = Option(args, "--release") ?? throw new InstallException("--release <dir> is required.");
    var version = Option(args, "--version") ?? throw new InstallException("--version is required.");
    var keyPath = Option(args, "--key") ?? throw new InstallException("--key <private.pem> is required.");
    var output = Option(args, "--out") ?? throw new InstallException("--out <dir> is required.");
    var baseUrl = (Option(args, "--base-url") ?? "https://connect.jackiairline.ir/update").TrimEnd('/');
    if (!Version.TryParse(version, out _))
    {
        throw new InstallException("--version must look like 1.2.3.");
    }

    JackiVpn.Infrastructure.Updates.AppUpdateAsset Asset(string file)
    {
        var path = Path.Combine(release, file);
        if (!File.Exists(path))
        {
            throw new InstallException($"{file} is missing in {release}.");
        }

        using var fs = File.OpenRead(path);
        return new(baseUrl + "/" + version + "/" + file, Convert.ToHexStringLower(SHA256.HashData(fs)), new FileInfo(path).Length);
    }

    var notes = new Dictionary<string, string>();
    if (Option(args, "--notes-en") is { Length: > 0 } en)
    {
        notes["en"] = en;
    }

    if (Option(args, "--notes-fa") is { Length: > 0 } fa)
    {
        notes["fa"] = fa;
    }

    var feed = new JackiVpn.Infrastructure.Updates.AppUpdateFeed(1, version, DateTimeOffset.UtcNow, Flag(args, "--mandatory"), notes,
        Asset($"IBOX-{version}-win-x64.msi"),
        new Dictionary<string, JackiVpn.Infrastructure.Updates.AppUpdateAsset>
        {
            ["arm64-v8a"] = Asset($"IBOX-{version}-android-arm64-v8a.apk"),
            ["armeabi-v7a"] = Asset($"IBOX-{version}-android-armeabi-v7a.apk"),
            ["x86_64"] = Asset($"IBOX-{version}-android-x86_64.apk"),
        });
    var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(feed, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    var signature = JackiVpn.Infrastructure.Updates.AppUpdateService.Sign(bytes, File.ReadAllText(keyPath));
    if (JackiVpn.Infrastructure.Updates.AppUpdateService.Verify(bytes, Convert.FromBase64String(signature), ProductInfo.UpdatePublicKeyPem) is null)
    {
        throw new InstallException("The key does not match the public key built into the app (ProductInfo.UpdatePublicKeyPem).");
    }

    Directory.CreateDirectory(output);
    File.WriteAllBytes(Path.Combine(output, "latest.json"), bytes);
    File.WriteAllText(Path.Combine(output, "latest.json.sig"), signature);
    Console.WriteLine($"Wrote {Path.Combine(output, "latest.json")} and latest.json.sig (verified with the built-in public key).");
    return 0;
}

static async Task<int> RunHostAsync(string[] args, bool console)
{
    var defaults = AppPaths.Default();
    var dataOverride = console ? Option(args, "--data") : null;
    var paths = dataOverride is null ? defaults : new AppPaths(defaults.UserDataRoot, Path.GetFullPath(dataOverride), defaults.InstallRoot);
    var pipeName = (console ? Option(args, "--pipe") : null) ?? ProductInfo.PipeName;
    var sameUser = console && Flag(args, "--same-user");

    var scope = dataOverride is null ? PrivateScope.ServiceOnly : PrivateScope.CurrentUser;
    SecureFileSystem.CreatePrivateDirectory(paths.ServiceDataRoot, scope);
    foreach (var dir in new[] { paths.ServiceStateDir, paths.ServiceLogsDir, paths.ServiceRuleSetDir, paths.ServiceCoreWorkDir })
    {
        SecureFileSystem.CreatePrivateDirectory(dir, scope);
    }

    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
    builder.Services.AddWindowsService(o => o.ServiceName = ProductInfo.ServiceName);
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Information);
    builder.Logging.AddProvider(new RedactingFileLoggerProvider(new LoggingOptions { Directory = paths.ServiceLogsDir, FilePrefix = "service" }));
    if (console)
    {
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
    }

    var version = typeof(TunnelHostService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    var s = builder.Services;
    s.AddSingleton(paths);
    s.AddSingleton(_ => new CoreBinaryCatalog(paths.CoresDir));
    s.AddSingleton<SingBoxCoreEngine>();
    s.AddSingleton<XrayCoreEngine>();
    s.AddSingleton(_ => new NetworkStateJournal(paths.ServiceJournalPath));
    s.AddSingleton<IKillSwitch>(sp => new WfpKillSwitch(sp.GetRequiredService<ILoggerFactory>().CreateLogger("JackiVpn.KillSwitch")));
    s.AddSingleton(sp => new WinHttpProxyManager(new WinHttpProxyApi(), sp.GetRequiredService<NetworkStateJournal>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("JackiVpn.WinHttp")));
    s.AddSingleton(sp => new SessionPlanFactory(sp.GetRequiredService<CoreBinaryCatalog>()) { FlushDns = static () => JackiVpn.Networking.Adapters.NetworkInspector.FlushDnsCache() });
    s.AddSingleton(sp =>
    {
        var catalog = sp.GetRequiredService<CoreBinaryCatalog>();
        var singBox = sp.GetRequiredService<SingBoxCoreEngine>();
        var xray = sp.GetRequiredService<XrayCoreEngine>();
        var plans = sp.GetRequiredService<SessionPlanFactory>();
        var journal = sp.GetRequiredService<NetworkStateJournal>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("JackiVpn.Tunnel");
        return new TunnelSessionManager(
            async (request, ct) => await plans.CreateAsync(request, await singBox.GetCapabilitiesAsync(ct).ConfigureAwait(false), await TryCaps(xray, ct).ConfigureAwait(false), ct).ConfigureAwait(false),
            () => new SupervisorRunner(new CoreSupervisor(catalog, singBox, catalog.IsInstalled(CoreKind.Xray) ? xray : null, paths.ServiceCoreWorkDir, logger, journal)),
            sp.GetRequiredService<IKillSwitch>(),
            journal,
            paths.ServiceRuleSetDir,
            [catalog.PathOf(CoreKind.SingBox), catalog.PathOf(CoreKind.Xray)],
            logger);
    });
    s.AddSingleton(sp =>
    {
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("JackiVpn.Recovery");
        var killSwitch = sp.GetRequiredService<IKillSwitch>();
        return new RecoveryCoordinator(
            sp.GetRequiredService<NetworkStateJournal>(),
            [
                new OrphanCoreHandler(logger),
                new KillSwitchRecoveryHandler(killSwitch),
                new WinHttpRecoveryHandler(sp.GetRequiredService<WinHttpProxyManager>()),
                new TunAdapterRecoveryHandler(logger),
            ],
            [
                new KillSwitchSweep(killSwitch),
                new StaleTunSweep(ProductInfo.TunInterfaceName, () => sp.GetRequiredService<TunnelSessionManager>().Status.State is not (TunnelState.Stopped or TunnelState.Failed), logger),
                new StaleTunSweep(ProductInfo.LegacyTunInterfaceName, () => false, logger),
                new DnsFlushStep(),
            ],
            logger);
    });
    s.AddSingleton(sp => new ServiceRequestHandler(
        sp.GetRequiredService<TunnelSessionManager>(),
        sp.GetRequiredService<RecoveryCoordinator>(),
        sp.GetRequiredService<WinHttpProxyManager>(),
        sp.GetRequiredService<CoreBinaryCatalog>(),
        sp.GetRequiredService<SingBoxCoreEngine>(),
        sp.GetRequiredService<XrayCoreEngine>(),
        sp.GetRequiredService<ILogger<ServiceRequestHandler>>()));
    s.AddSingleton(sp => new IpcServer(
        new IpcServerOptions { PipeName = pipeName, ServerVersion = version },
        sameUser ? new SameUserAuthorizer() : new TrustedImageAuthorizer(),
        sp.GetRequiredService<ServiceRequestHandler>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("JackiVpn.Ipc")));
    s.AddHostedService<TunnelHostService>();

    using var host = builder.Build();
    if (sameUser)
    {
        host.Services.GetRequiredService<ILogger<TunnelHostService>>().LogWarning("DIAGNOSTIC MODE: any process of the current user may use this pipe. Never use this for a real installation.");
    }

    await host.RunAsync().ConfigureAwait(false);
    return 0;
}

static async Task<CoreCapabilities?> TryCaps(ICoreEngine engine, CancellationToken ct)
{
    try
    {
        return await engine.GetCapabilitiesAsync(ct).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is CoreIntegrityException or IOException or InvalidOperationException or CoreStartException)
    {
        return null;
    }
}
