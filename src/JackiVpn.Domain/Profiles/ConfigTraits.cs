namespace JackiVpn.Domain.Profiles;

/// <summary>
/// What a config is, read from the provider's remark tags (the official panels mark relay configs with 🇮🇷 and
/// "Relay"). Used for ranking and grouping only; the connection itself always uses the config's real fields.
/// </summary>
public static class ConfigTraits
{
    /// <summary>A config through an Iranian bridge server (domestic relay to a foreign exit).</summary>
    public static bool IsRelay(ProxyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var name = profile.Name ?? string.Empty;
        return name.Contains("Relay", StringComparison.OrdinalIgnoreCase)
            || name.Contains("رله", StringComparison.Ordinal)
            || name.Contains("\U0001F1EE\U0001F1F7", StringComparison.Ordinal);
    }
}
