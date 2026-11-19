namespace JackiVpn.Domain.Settings;

/// <summary>
/// Built-in automations ("Automations" in the UI; not the CLI/MCP <see cref="AgentSettings"/>). Every one is off
/// until the user turns it on.
/// </summary>
public sealed record AutomationSettings
{
    /// <summary>Ids of the automations the user turned on.</summary>
    public IReadOnlyList<string> Enabled { get; init; } = [];

    /// <summary>Wi-Fi names (SSIDs) where the VPN is not needed: it disconnects there and connects on any other Wi-Fi.</summary>
    public IReadOnlyList<string> TrustedNetworks { get; init; } = [];

    /// <summary>Local hour (0–23) for the nightly subscription refresh.</summary>
    public int NightlyHour { get; init; } = 4;

    /// <summary>Nightly refresh also deletes servers that failed every test for this many days (0 = only report them).</summary>
    public int RemoveDeadAfterDays { get; init; }

    /// <summary>Quota watcher: warn when this share of the traffic is used.</summary>
    public int QuotaWarnPercent { get; init; } = 90;

    /// <summary>Quota watcher: warn this many days before the subscription expires.</summary>
    public int ExpiryWarnDays { get; init; } = 3;

    public bool IsEnabled(string id) => Enabled.Contains(id, StringComparer.Ordinal);

    public AutomationSettings With(string id, bool on) => this with
    {
        Enabled = on
            ? Enabled.Append(id).Distinct(StringComparer.Ordinal).ToList()
            : Enabled.Where(e => !string.Equals(e, id, StringComparison.Ordinal)).ToList(),
    };
}
