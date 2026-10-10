namespace JackiVpn.Domain;

/// <summary>
/// Single source of truth for product identity. Renaming the product means changing these
/// constants plus the properties in Directory.Build.props — nothing else hard-codes the brand.
/// </summary>
public static class ProductInfo
{
    public const string ProductName = "JackiVpn";
    public const string DisplayName = "IBOX";
    public const string CompanyName = "Jacki";

    /// <summary>Windows service name (SCM key). Must not contain spaces.</summary>
    public const string ServiceName = "JackiVpnService";
    public const string ServiceDisplayName = "IBOX Service";

    /// <summary>Named pipe used between Desktop and Service. Versioned so incompatible builds never talk.</summary>
    public const string PipeName = "JackiVpn.Service.v1";

    /// <summary>Folder name under %LOCALAPPDATA% / %ProgramData%.</summary>
    public const string DataFolderName = "JackiVpn";

    /// <summary>Brand name of the owner-only smart mode.</summary>
    public const string SmartConnectName = "Jacki Smart Connect";

    /// <summary>Default name of the custom provider marker header (configurable at runtime).</summary>
    public const string DefaultProviderMarkerHeader = "X-Jacki-Profile";
    public const string DefaultProviderMarkerValue = "v1";
    public const string DefaultProviderManifestHeader = "X-Jacki-Manifest";

    /// <summary>Name given to the TUN adapter created by sing-box (shown in the system's network list).</summary>
    public const string TunInterfaceName = "IBOX";

    /// <summary>Adapter name of versions before 1.3 (still swept if a crash left one behind).</summary>
    public const string LegacyTunInterfaceName = "JackiVpn";

    /// <summary>AppUserModelID used for toast notifications.</summary>
    public const string AppUserModelId = "Jacki.JackiVpn.Desktop";

    /// <summary>HKLM key written by the service installer (admin-only).</summary>
    public const string ServiceRegistryKey = @"SOFTWARE\JackiVpn\Service";

    public const string UserAgentProduct = "JackiVpn";

    /// <summary>
    /// The owner's official subscription domains (and their sub-domains). Built in, so no subscription can make
    /// itself official; matching is exact or by sub-domain, never by substring ("fakejacki.com" is not official).
    /// </summary>
    public static IReadOnlyList<string> OfficialProviderDomains { get; } = ["jacki-inc.ir", "jackidrive.ir", "jackiairline.ir"];

    /// <summary>
    /// App update feed: <c>latest.json</c> plus <c>latest.json.sig</c> (ECDSA P-256 over the exact file bytes).
    /// Installers are only accepted from HTTPS URLs on the official domains and only with the signed SHA-256.
    /// </summary>
    public const string UpdateFeedUrl = "https://connect.jackiairline.ir/update/latest.json";

    /// <summary>Public half of the release signing key (the private key never leaves the release machine).</summary>
    public const string UpdatePublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEgbuvcaJBeuqR8QrjDhr8Y7c8VmcH
        B5clql2WV69fDhOeJ8e+kG5x7YlPq5jRA9W8e9OeDqH9kqfaFfmrBHkiUA==
        -----END PUBLIC KEY-----
        """;
}
