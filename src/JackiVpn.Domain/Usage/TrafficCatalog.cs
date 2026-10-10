namespace JackiVpn.Domain.Usage;

/// <summary>Built-in knowledge about apps and services (no network lookups).</summary>
public static class TrafficCatalog
{
    /// <summary>Download hosts of automatic updates (domain suffixes). Only download/CDN names, never sign-in or licensing.</summary>
    public static IReadOnlyList<string> UpdateDomains(UpdateGroup group) => group switch
    {
        UpdateGroup.Windows =>
        [
            "windowsupdate.com", "update.microsoft.com", "windowsupdate.microsoft.com", "delivery.mp.microsoft.com",
            "do.dsp.mp.microsoft.com", "wustat.windows.com", "ntservicepack.microsoft.com", "emdl.ws.microsoft.com",
            "tsfe.trafficshaping.dsp.mp.microsoft.com",
        ],
        UpdateGroup.Office => ["officecdn.microsoft.com", "officecdn.microsoft.com.edgesuite.net", "officeredir.microsoft.com"],
        UpdateGroup.Browsers =>
        [
            "update.googleapis.com", "edgedl.me.gvt1.com", "redirector.gvt1.com", "msedge.api.cdp.microsoft.com",
            "aus5.mozilla.org", "archive.mozilla.org", "autoupdate.geo.opera.com", "updates.bravesoftware.com",
        ],
        UpdateGroup.Games =>
        [
            "steamcontent.com", "steampipe.akamaized.net", "client-update.akamai.steamstatic.com",
            "download.epicgames.com", "download2.epicgames.com", "fastly-download.epicgames.com", "epicgames-download1.akamaized.net", "egdownload.fastly-edge.com",
            "blzddist1-a.akamaihd.net", "level3.blizzard.com", "blizzard.vo.llnwd.net",
            "lol.secure.dyn.riotcdn.net", "valorant.secure.dyn.riotcdn.net",
            "assets1.xboxlive.com", "assets2.xboxlive.com", "dlassets.xboxlive.com", "xvcf1.xboxlive.com", "xvcf2.xboxlive.com",
            "origin-a.akamaihd.net", "uplaypc-s-ubisoft.cdn.ubi.com",
        ],
        UpdateGroup.Apple =>
        [
            "swcdn.apple.com", "swscan.apple.com", "swdist.apple.com", "updates.cdn-apple.com", "mesu.apple.com",
            "appldnld.apple.com", "oscdn.apple.com", "osrecovery.apple.com", "gdmf.apple.com",
        ],
        UpdateGroup.Android => ["play.googleapis.com", "gvt1.com", "ota.googlezip.net", "ospserver.net", "update.miui.com", "bigota.d.miui.com"],
        UpdateGroup.Linux => ["archive.ubuntu.com", "security.ubuntu.com", "api.snapcraft.io", "snapcraftcontent.com", "fwupd.org", "dl.flathub.org"],
        UpdateGroup.Apps =>
        [
            "ardownload.adobe.com", "ardownload2.adobe.com", "ccmdl.adobe.com", "agsupdate.adobe.com",
            "update.code.visualstudio.com", "updates.tdesktop.com", "upgrade.scdn.co", "download.jetbrains.com",
        ],
        UpdateGroup.Drivers => ["download.nvidia.com", "us.download.nvidia.com", "gfwsl.geforce.com", "drivers.amd.com", "downloadmirror.intel.com"],
        UpdateGroup.CloudSync => ["storage.live.com", "content.dropboxapi.com", "block.dropbox.com"],
        _ => [],
    };

    /// <summary>Usage reporting and crash telemetry (not needed for any app to work).</summary>
    public static IReadOnlyList<string> TelemetryDomains { get; } =
    [
        "vortex.data.microsoft.com", "events.data.microsoft.com", "watson.telemetry.microsoft.com", "telemetry.microsoft.com",
        "settings-win.data.microsoft.com", "browser.events.data.msn.com", "telecommand.telemetry.microsoft.com",
        "google-analytics.com", "app-measurement.com", "firebaselogging-pa.googleapis.com", "crashlytics.com",
        "metrics.icloud.com", "incoming.telemetry.mozilla.org", "events.gfe.nvidia.com", "telemetry.nvidia.com",
        "doubleclick.net", "scorecardresearch.com", "hotjar.com",
    ];

    /// <summary>Known game executables (lower case).</summary>
    public static IReadOnlySet<string> GameExecutables { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "valorant-win64-shipping.exe", "cs2.exe", "csgo.exe", "dota2.exe", "fortniteclient-win64-shipping.exe", "r5apex.exe", "r5apex_dx12.exe",
        "league of legends.exe", "overwatch.exe", "gta5.exe", "gta5_enhanced.exe", "rocketleague.exe", "tslgame.exe", "cod.exe",
        "robloxplayerbeta.exe", "minecraft.windows.exe", "eldenring.exe", "destiny2.exe", "rainbowsix.exe", "rainbowsix_vulkan.exe",
        "deadlock.exe", "marvel-win64-shipping.exe", "pubg.exe", "wow.exe", "hearthstone.exe", "fc25.exe", "fc26.exe", "eafc25.exe", "eafc26.exe",
    };

    /// <summary>Folders of game libraries (anything installed there is a game or part of one).</summary>
    public static IReadOnlyList<string> GameLibraries { get; } =
    [
        @"\steamapps\common\", @"\epic games\", @"\riot games\", @"\ea games\", @"\electronic arts\",
        @"\ubisoft game launcher\games\", @"\gog galaxy\games\", @"\xboxgames\", @"\rockstar games\", @"\battlestate games\",
        "/steamapps/common/", "/games/",
    ];

    /// <summary>
    /// Programs games depend on while running: launchers, anti-cheat, voice chat, overlays. Slowing them breaks
    /// matchmaking, sign-in or voice, so Gaming priority never touches them.
    /// </summary>
    public static IReadOnlySet<string> GameCompanions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "steamwebhelper.exe", "steamservice.exe", "gameoverlayui.exe",
        "epicgameslauncher.exe", "epicwebhelper.exe", "easyanticheat.exe", "easyanticheat_eos.exe", "eossdk-win64-shipping.exe",
        "beservice.exe", "beservice_x64.exe", "vgc.exe", "vgtray.exe", "riotclientservices.exe", "riotclientux.exe", "riotclientuxrender.exe", "leagueclient.exe", "leagueclientux.exe",
        "battle.net.exe", "agent.exe", "blizzard error reporter.exe", "eadesktop.exe", "eabackgroundservice.exe", "origin.exe", "upc.exe", "uplaywebcore.exe",
        "galaxyclient.exe", "gamingservices.exe", "gamingservicesnet.exe", "xboxpcapp.exe", "xboxappservices.exe",
        "faceit.exe", "faceitclient.exe", "faceitservice.exe", "esea.exe", "pnkbstra.exe", "pnkbstrb.exe",
        "discord.exe", "teamspeak.exe", "ts3client_win64.exe", "mumble.exe", "overwolf.exe",
        "steam", "steamwebhelper", "discord", "lutris", "heroic",
    };

    /// <summary>Players, browsers and broadcast tools that carry a stream.</summary>
    public static IReadOnlySet<string> StreamingApps { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe", "browser.exe", "msedgewebview2.exe",
        "vlc.exe", "mpc-hc64.exe", "mpc-be64.exe", "potplayermini64.exe", "mpv.exe", "wmplayer.exe", "video.ui.exe", "stremio.exe", "kodi.exe", "plex.exe",
        "spotify.exe", "obs64.exe", "obs32.exe", "streamlabs obs.exe", "streamlabs desktop.exe", "xsplit.core.exe", "twitchstudio.exe",
        "chrome", "firefox", "brave", "vlc", "mpv", "obs", "spotify",
    };

    /// <summary>Other VPN/proxy clients (their own traffic is the carrier of other apps' traffic).</summary>
    public static IReadOnlyList<string> VpnAppMarkers { get; } =
    [
        "v2ray", "xray", "clash", "mihomo", "nekoray", "nekobox", "hiddify", "karing", "sing-box", "singbox", "openvpn", "wireguard",
        "psiphon", "shadowsocks", "naive", "hysteria", "tuic", "trojan", "outline", "warp", "oblivion", "lantern", "proton",
        "nordvpn", "expressvpn", "surfshark", "windscribe", "mullvad",
    ];

    /// <summary>System programs that must keep their connection (cutting them breaks Windows, DNS or the VPN itself).</summary>
    public static IReadOnlySet<string> Uncuttable { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "system", "svchost.exe", "lsass.exe", "services.exe", "wininit.exe", "csrss.exe", "smss.exe", "dnscache", "dns (system)",
        "jackivpn.exe", "jackivpn.service.exe", "sing-box.exe", "xray.exe", "sing-box", "xray", "jackivpn", "systemd-resolved", "networkmanager",
    };

    public static bool IsVpnApp(string app) => VpnAppMarkers.Any(m => app.Contains(m, StringComparison.OrdinalIgnoreCase));

    public static bool IsGame(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var name = System.IO.Path.GetFileName(path);
        return GameExecutables.Contains(name) || GameLibraries.Any(l => path.Contains(l, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Display name of an app from its executable path ("C:\…\Chrome.exe" → "chrome.exe").</summary>
    public static string AppName(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return "?";
        }

        var p = processPath.Trim();
        var slash = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
        return (slash >= 0 ? p[(slash + 1)..] : p).ToLowerInvariant();
    }

    /// <summary>Which update family a host belongs to (null = not an update host).</summary>
    public static UpdateGroup? UpdateGroupOf(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return null;
        }

        foreach (var g in Enum.GetValues<UpdateGroup>())
        {
            if (UpdateDomains(g).Any(d => MatchesSuffix(host, d)))
            {
                return g;
            }
        }

        return null;
    }

    public static bool MatchesSuffix(string host, string suffix) =>
        host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
        || (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && host[host.Length - suffix.Length - 1] == '.');
}
