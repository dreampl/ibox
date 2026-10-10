using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Cores;
using JackiVpn.Domain.Profiles;

namespace JackiVpn.Cores.Engines;

public sealed record CoreSelection(
    CoreKind Core,
    bool UseChain,
    CapabilityReport Report,
    IReadOnlyList<CapabilityReport> AllReports,
    string Reason)
{
    public bool IsUsable => Report.IsUsable;
}

/// <summary>
/// Chooses the core for a profile. Auto: the usable engine with the fewest degradations wins; sing-box wins
/// ties (it is the TUN/DNS/routing frontend anyway, so no second process is needed). A profile that only
/// Xray can carry runs as an Xray backend chained behind sing-box.
/// </summary>
public static class CoreSelector
{
    public static CoreSelection Select(
        ProxyProfile profile,
        CorePreference preference,
        SessionKind session,
        bool muxEnabled,
        CoreCapabilities singBox,
        CoreCapabilities? xray)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(singBox);
        var effective = profile.User.CoreOverride ?? preference;
        var sbReport = Capabilities.CapabilityMatrix.Negotiate(profile, singBox, session, muxEnabled);
        var xrReport = xray is null ? null : Capabilities.CapabilityMatrix.Negotiate(profile, xray, session, muxEnabled);
        var all = xrReport is null ? new[] { sbReport } : new[] { sbReport, xrReport };

        switch (effective)
        {
            case CorePreference.SingBox:
                return new CoreSelection(CoreKind.SingBox, false, sbReport, all, "sing-box selected by user.");
            case CorePreference.Xray when xrReport is not null:
                return new CoreSelection(CoreKind.Xray, true, xrReport, all, "Xray selected by user (runs behind the sing-box frontend).");
            case CorePreference.Xray:
                return new CoreSelection(CoreKind.Xray, true, new CapabilityReport(CoreKind.Xray, [new CapabilityIssue(CoreFeature.None, CapabilityImpact.Blocking, "Xray is not installed.")]), all, "Xray selected but not installed.");
        }

        if (!sbReport.IsUsable && xrReport is { IsUsable: true })
        {
            return new CoreSelection(CoreKind.Xray, true, xrReport, all, "Auto: only Xray supports this profile.");
        }

        if (sbReport.IsUsable && xrReport is { IsUsable: true } && xrReport.DegradedCount < sbReport.DegradedCount)
        {
            return new CoreSelection(CoreKind.Xray, true, xrReport, all, "Auto: Xray preserves more of this profile's features.");
        }

        return new CoreSelection(CoreKind.SingBox, false, sbReport, all, sbReport.IsUsable ? "Auto: sing-box (native)." : "Auto: no core supports this profile.");
    }
}
