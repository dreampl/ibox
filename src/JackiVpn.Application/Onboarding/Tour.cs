namespace JackiVpn.Application.Onboarding;

/// <summary>What a tour step shows next to its text (each window draws its own picture for it).</summary>
public enum TourPicture
{
    Eye,
    Subscriptions,
    Mode,
    Power,
    More,
}

public sealed record TourStep(TourPicture Picture, string Title, string Body);

/// <summary>
/// The first-start tour of the desktop apps (Windows, Linux): how to get from a fresh install to a protected
/// connection, in plain words. Shown once before setup (no servers yet), again from About.
/// </summary>
public static class Tour
{
    public static IReadOnlyList<TourStep> Steps { get; } =
    [
        new(TourPicture.Eye, "Welcome to IBOX",
            "A private, fast connection in three steps. This short tour shows you how to start — it takes a minute."),
        new(TourPicture.Subscriptions, "1 · Add your servers",
            "Paste the subscription link from your provider on the Subscriptions page (or single server links on Servers). IBOX keeps them up to date by itself."),
        new(TourPicture.Mode, "2 · Choose how to connect",
            "TUN protects every app on this computer. System proxy covers browsers and the apps that follow it. Leave Smart Connect on: it picks the fastest working server for you."),
        new(TourPicture.Power, "3 · Press the eye",
            "The big button connects. The eye opens and looks around while you are protected. Press it again to disconnect — or while it is connecting, to cancel."),
        new(TourPicture.More, "Good to know",
            "Dual VPN reaches your company next to IBOX (Servers page). Your picture at the bottom left holds profiles and moving your account to another device. The notch at the top of the screen shows the state — hover it for details."),
    ];

    /// <summary>Shown by itself only on a fresh start: not seen yet and nothing to connect to.</summary>
    public static bool ShouldShow(bool seen, int servers) => !seen && servers == 0;
}
