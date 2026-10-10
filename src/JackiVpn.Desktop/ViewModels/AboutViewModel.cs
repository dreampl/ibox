using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JackiVpn.Desktop.Composition;
using JackiVpn.Domain.Connection;

namespace JackiVpn.Desktop.ViewModels;

public sealed record ComponentInfo(string Name, string Version, string License, string Source);

public sealed class AboutViewModel
    : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public AboutViewModel(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        DataFolder = services.Paths.UserDataRoot;
        InstallFolder = services.Paths.InstallRoot;
        var components = new List<ComponentInfo>();
        foreach (var (kind, name) in new[] { (CoreKind.SingBox, "sing-box"), (CoreKind.Xray, "Xray-core") })
        {
            var entry = services.Catalog.ManifestEntry(kind);
            components.Add(new ComponentInfo(name, entry?.Version ?? "not installed", entry?.License ?? "—", entry?.Source ?? "—"));
        }

        components.Add(new ComponentInfo("Wintun", "0.14.1", "Prebuilt binaries license (redistributable, unmodified)", "https://www.wintun.net"));
        components.Add(new ComponentInfo("CommunityToolkit.Mvvm", "8.4", "MIT", "https://github.com/CommunityToolkit/dotnet"));
        components.Add(new ComponentInfo("Microsoft.Data.Sqlite / Microsoft.Extensions.*", "10.0", "MIT", "https://github.com/dotnet"));
        components.Add(new ComponentInfo("YamlDotNet", "18.1", "MIT", "https://github.com/aaubry/YamlDotNet"));
        Components = components;

        var notices = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md");
        Notices = File.Exists(notices) ? File.ReadAllText(notices) : "THIRD_PARTY_NOTICES.md is shipped with the release build.";
    }

    public string ProductName { get; } = Domain.ProductInfo.DisplayName;

    public string Version { get; } = typeof(AboutViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";

    public string DataFolder { get; }

    public string InstallFolder { get; }

    public IReadOnlyList<ComponentInfo> Components { get; }

    public string Notices { get; }

    public string Privacy { get; } =
        "No telemetry. No analytics. Subscription URLs and credentials are encrypted with Windows DPAPI and never written to logs. " +
        "Network changes are journaled and undone on disconnect, crash or Repair Network.";

    public int CoreCount => Components.Count(c => c.Version != "not installed");
}
