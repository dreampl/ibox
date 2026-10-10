using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using JackiVpn.Cores;
using JackiVpn.Cores.Engines;
using JackiVpn.Cores.Hosting;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Cores;
using JackiVpn.Domain.Profiles;
using JackiVpn.Subscriptions.Parsing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JackiVpn.IntegrationTests.Infrastructure;

/// <summary>Real core binaries from the build output (hash-verified via the manifest).</summary>
public sealed class CoreFixture : IAsyncLifetime
{
    public CoreBinaryCatalog Catalog { get; } = new(Path.Combine(AppContext.BaseDirectory, "cores"));

    public SingBoxCoreEngine SingBox { get; private set; } = null!;
    public XrayCoreEngine Xray { get; private set; } = null!;
    public CoreCapabilities? SingBoxCaps { get; private set; }
    public CoreCapabilities? XrayCaps { get; private set; }
    public string WorkDir { get; } = Path.Combine(Path.GetTempPath(), "jackivpn-it-" + Guid.NewGuid().ToString("N")[..8]);
    public string? UnavailableReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(WorkDir);
        if (!Catalog.IsInstalled(CoreKind.SingBox) || !Catalog.IsInstalled(CoreKind.Xray))
        {
            UnavailableReason = "Core binaries are not in the test output. Run scripts/fetch-cores.ps1 and rebuild.";
            return;
        }

        SingBox = new SingBoxCoreEngine(Catalog, NullLogger<SingBoxCoreEngine>.Instance);
        Xray = new XrayCoreEngine(Catalog, NullLogger<XrayCoreEngine>.Instance);
        SingBoxCaps = await SingBox.GetCapabilitiesAsync(CancellationToken.None);
        XrayCaps = await Xray.GetCapabilitiesAsync(CancellationToken.None);
    }

    public void RequireCores()
    {
        if (UnavailableReason is not null)
        {
            Assert.Skip(UnavailableReason);
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(WorkDir, recursive: true);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }

    public static ProxyProfile ReferenceProfile() =>
        new ConfigParserPipeline().Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jacki-vless-ws-tls.singbox.json")), new ParseContext()).Profiles.Single();

    /// <summary>Starts an arbitrary config on a verified core (used for local test servers).</summary>
    public async Task<CoreProcess> StartRawAsync(CoreKind kind, string json, ILogger? logger = null)
    {
        var engine = kind == CoreKind.SingBox ? (ICoreEngine)SingBox : Xray;
        using var binary = Catalog.Verify(kind);
        var config = new GeneratedCoreConfig
        {
            Kind = kind,
            Session = SessionKind.SystemProxy,
            Json = json,
            Report = new CapabilityReport(kind, []),
        };
        return await CoreProcess.StartAsync(binary, engine.BuildLaunchSpec(WorkDir), config, logger ?? NullLogger.Instance, CancellationToken.None);
    }

    public async Task<CoreProcess> StartAsync(ICoreEngine engine, GeneratedCoreConfig config, ILogger? logger = null)
    {
        using var binary = Catalog.Verify(engine.Kind);
        return await CoreProcess.StartAsync(binary, engine.BuildLaunchSpec(WorkDir), config, logger ?? NullLogger.Instance, CancellationToken.None);
    }
}

/// <summary>Self-signed certificate written as PEM for a local TLS test server.</summary>
public sealed class TestCertificate : IDisposable
{
    public TestCertificate(string dir, string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        Sha256Hex = Convert.ToHexStringLower(SHA256.HashData(cert.RawData));
        CertPath = Path.Combine(dir, $"cert-{Guid.NewGuid():N}.pem");
        KeyPath = Path.Combine(dir, $"key-{Guid.NewGuid():N}.pem");
        File.WriteAllText(CertPath, cert.ExportCertificatePem());
        File.WriteAllText(KeyPath, key.ExportPkcs8PrivateKeyPem());
    }

    public string CertPath { get; }
    public string KeyPath { get; }

    /// <summary>Hex SHA-256 of the DER certificate (Xray pinnedPeerCertSha256 format).</summary>
    public string Sha256Hex { get; }

    public void Dispose()
    {
        File.Delete(CertPath);
        File.Delete(KeyPath);
    }
}
