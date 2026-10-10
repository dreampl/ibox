using JackiVpn.Domain;

namespace JackiVpn.Security;

/// <summary>Well-known locations. Everything is overridable for tests (sandbox roots).</summary>
public sealed class AppPaths
{
    public AppPaths(string userDataRoot, string serviceDataRoot, string installRoot)
    {
        UserDataRoot = userDataRoot;
        ServiceDataRoot = serviceDataRoot;
        InstallRoot = installRoot;
    }

    /// <summary>%LOCALAPPDATA%\JackiVpn — per-user state (database, logs, caches, user journal).</summary>
    public string UserDataRoot { get; }

    /// <summary>%ProgramData%\JackiVpn\service — service state (ACL: SYSTEM + Administrators).</summary>
    public string ServiceDataRoot { get; }

    /// <summary>Directory containing the executables and <c>cores\</c>.</summary>
    public string InstallRoot { get; }

    public string DatabasePath => Path.Combine(UserDataRoot, "jackivpn.db");
    public string UserLogsDir => Path.Combine(UserDataRoot, "logs");
    public string UserStateDir => Path.Combine(UserDataRoot, "state");
    public string UserRuleSetCacheDir => Path.Combine(UserDataRoot, "cache", "rulesets");
    public string UserCoreWorkDir => Path.Combine(UserDataRoot, "run");
    public string UserJournalPath => Path.Combine(UserStateDir, "network-journal.json");
    public string DiagnosticsDir => Path.Combine(UserDataRoot, "diagnostics");

    public string ServiceLogsDir => Path.Combine(ServiceDataRoot, "logs");
    public string ServiceStateDir => Path.Combine(ServiceDataRoot, "state");
    public string ServiceRuleSetDir => Path.Combine(ServiceDataRoot, "rulesets");
    public string ServiceCoreWorkDir => Path.Combine(ServiceDataRoot, "run");
    public string ServiceJournalPath => Path.Combine(ServiceStateDir, "network-journal.json");

    public string CoresDir => Path.Combine(InstallRoot, "cores");
    public string CoreManifestPath => Path.Combine(CoresDir, "manifest.json");

    /// <summary>Environment variable that points the per-user data at another folder (portable use, UI previews, tests).</summary>
    public const string DataRootVariable = "JACKIVPN_DATA_ROOT";

    public static AppPaths Default()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var overrideRoot = Environment.GetEnvironmentVariable(DataRootVariable);
        return new AppPaths(
            overrideRoot is { Length: > 3 } root && Path.IsPathFullyQualified(root) ? root : Path.Combine(local, ProductInfo.DataFolderName),
            Path.Combine(programData, ProductInfo.DataFolderName, "service"),
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
    }
}
