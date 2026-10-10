using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Settings;
using JackiVpn.Security;

namespace JackiVpn.Infrastructure.Diagnostics;

public sealed record DiagnosticInputs
{
    public required string LogsDirectory { get; init; }
    public string? ServiceLogsDirectory { get; init; }
    public AppSettings? Settings { get; init; }
    public IReadOnlyDictionary<string, string> CoreVersions { get; init; } = new Dictionary<string, string>();
    public string? JournalPath { get; init; }
    public IReadOnlyList<string> ExtraNotes { get; init; } = [];

    /// <summary>Known secrets (e.g. current subscription URLs/tokens) that must not appear anywhere.</summary>
    public IReadOnlyList<string> KnownSecrets { get; init; } = [];
}

public sealed record DiagnosticResult(string Path, int Files, int RedactionsVerified);

/// <summary>
/// Builds a ZIP for support. Every text entry is redacted, then <b>verified</b>: if any known secret, UUID,
/// share link credential or private key survives redaction the export is aborted rather than leaking.
/// </summary>
public static partial class DiagnosticBundleExporter
{
    public static DiagnosticResult Export(DiagnosticInputs input, string destinationZip)
    {
        ArgumentNullException.ThrowIfNull(input);
        var entries = new List<(string Name, string Content)>
        {
            ("system.txt", SystemInfo(input)),
        };

        if (input.Settings is not null)
        {
            entries.Add(("settings.json", JsonSerializer.Serialize(input.Settings, new JsonSerializerOptions(JsonDefaults.Storage) { WriteIndented = true })));
        }

        if (input.JournalPath is { } jp && File.Exists(jp))
        {
            entries.Add(("network-journal.json", File.ReadAllText(jp)));
        }

        AddLogs(entries, input.LogsDirectory, "logs/app/");
        if (input.ServiceLogsDirectory is { } sl && Directory.Exists(sl))
        {
            AddLogs(entries, sl, "logs/service/");
        }

        foreach (var s in input.KnownSecrets)
        {
            SecretRedactor.Register(s);
        }

        var verified = 0;
        try
        {
            var tmp = destinationZip + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var (name, content) in entries)
                {
                    var redacted = SecretRedactor.Redact(content);
                    Verify(name, redacted, input.KnownSecrets);
                    verified++;
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    w.Write(redacted);
                }
            }

            File.Move(tmp, destinationZip, overwrite: true);
        }
        finally
        {
            foreach (var s in input.KnownSecrets)
            {
                SecretRedactor.Unregister(s);
            }
        }

        return new DiagnosticResult(destinationZip, entries.Count, verified);
    }

    /// <summary>Throws if anything that looks like a credential survived redaction.</summary>
    public static void Verify(string entryName, string redacted, IReadOnlyList<string> knownSecrets)
    {
        ArgumentNullException.ThrowIfNull(redacted);
        ArgumentNullException.ThrowIfNull(knownSecrets);
        foreach (var s in knownSecrets.Where(s => s.Length >= 6))
        {
            if (redacted.Contains(s, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Diagnostic export aborted: a known secret survived redaction in {entryName}.");
            }
        }

        if (UuidRegex().IsMatch(redacted) || PrivateKeyRegex().IsMatch(redacted) || CredentialLinkRegex().IsMatch(redacted))
        {
            throw new InvalidOperationException($"Diagnostic export aborted: credential-like data survived redaction in {entryName}.");
        }
    }

    private static void AddLogs(List<(string, string)> entries, string dir, string prefix)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var f in Directory.EnumerateFiles(dir, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(10))
        {
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            entries.Add((prefix + Path.GetFileName(f), r.ReadToEnd()));
        }
    }

    private static string SystemInfo(DiagnosticInputs input)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Product: {Domain.ProductInfo.DisplayName} {typeof(DiagnosticBundleExporter).Assembly.GetName().Version}");
        sb.AppendLine($"Generated (UTC): {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        foreach (var (k, v) in input.CoreVersions)
        {
            sb.AppendLine($"{k}: {v}");
        }

        foreach (var n in input.ExtraNotes)
        {
            sb.AppendLine(n);
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex UuidRegex();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----\s*[A-Za-z0-9+/=]{16,}")]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"(vless|trojan|tuic|hysteria2|hy2)://(?!\*\*\*)[^@\s]{6,}@", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialLinkRegex();
}
