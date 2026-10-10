namespace JackiVpn.Domain.Settings;

/// <summary>Upgrades stored settings to the current schema (pure function; never loses user choices).</summary>
public static class SettingsMigrations
{
    public static AppSettings Upgrade(AppSettings stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var s = stored;
        if (s.SchemaVersion < 2)
        {
            // v2: latency-oriented defaults. FakeIP (TUN) and the exit IP/country lookup became defaults; in v1
            // they were off and not exposed during first-run, so no explicit user choice is overridden.
            var w = s.Smart.Weights;
            var untouchedV1Weights = w.Latency == 0.30 && w.Jitter == 0.15 && w.SuccessRate == 0.25 && w.Handshake == 0.10 && w.Stability == 0.15 && w.Throughput == 0.05;
            s = s with
            {
                Smart = untouchedV1Weights ? s.Smart with { Weights = w with { Latency = 0.35, Jitter = 0.10, Handshake = 0.15, Stability = 0.10 } } : s.Smart,
                Dns = s.Dns with { FakeIpEnabled = true },
                Probe = s.Probe with
                {
                    CheckPublicIp = true,
                    PublicIpUrl = s.Probe.PublicIpUrl == "https://api.ipify.org" ? new ProbeSettings().PublicIpUrl : s.Probe.PublicIpUrl,
                },
            };
        }

        if (s.SchemaVersion < 3 && s.Connection.LocalProxyPort == 0)
        {
            // v3: a stable proxy port (random ports broke apps that copy the proxy address once, like Claude).
            s = s with { Connection = s.Connection with { LocalProxyPort = ConnectionSettings.DefaultLocalProxyPort } };
        }

        if (s.SchemaVersion < 5)
        {
            // v5: "Sync device" replaced the hard-to-use opt-in Ecosystem page; being findable by the user's own
            // devices is on by default (an incoming subscription still needs the user's approval).
            s = s with { Ecosystem = s.Ecosystem with { Enabled = true } };
        }

        // Invariant (every version): a valid random device ID exists for device-limited providers.
        if (!Subscriptions.DeviceIdentity.IsValidHwid(s.Device.Hwid))
        {
            s = s with { Device = s.Device with { Hwid = Subscriptions.DeviceIdentity.NewHwid() } };
        }

        // Invariant: stable random credentials for the Android proxy-only mode (users enter them in their apps once).
        if (string.IsNullOrEmpty(s.MobileProxy.Username) || string.IsNullOrEmpty(s.MobileProxy.Password))
        {
            s = s with
            {
                MobileProxy = s.MobileProxy with
                {
                    Username = "jacki",
                    Password = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)),
                },
            };
        }

        return s.SchemaVersion == AppSettings.CurrentSchemaVersion ? s : s with { SchemaVersion = AppSettings.CurrentSchemaVersion };
    }
}
