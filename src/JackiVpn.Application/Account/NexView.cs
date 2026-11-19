using System.Globalization;
using System.Text.Json.Nodes;

namespace JackiVpn.Application.Account;

/// <summary>An exit country a multi-country (Nex) subscription can leave through.</summary>
public sealed record NexExit(string Backend, string Label);

/// <summary>The Nex part of one subscription (what the mini app's Nex tab shows).</summary>
public sealed record NexInfo(bool ServiceOn, bool Member, string Exit, IReadOnlyList<NexExit> Exits, long Price, Uri? ComboLink)
{
    public bool CanPickExit => Member && Exits.Count > 1;
}

/// <summary>An open or decided renew / volume request.</summary>
public sealed record SubRequest(string Id, string Kind, long Value, string Status, string Stage, long Price, string? Note, string? Pay, bool Receipt)
{
    public bool Pending => Status == "pending";

    public bool NeedsAccept => Pending && Stage == "accept";

    public bool NeedsReceipt => Pending && Stage == "pay" && !Receipt;
}

/// <summary>One subscription of the signed-in Telegram user, as the mini app shows it.</summary>
public sealed record SubscriptionCard(
    string Lid,
    string Username,
    string Status,
    string Server,
    long UsedBytes,
    long LimitBytes,
    DateTimeOffset? Expires,
    Uri? Link,
    bool Multi,
    NexInfo? Nex,
    IReadOnlyDictionary<string, IReadOnlyList<long>> Options,
    IReadOnlyList<string> SwitchTo,
    IReadOnlyList<SubRequest> Requests)
{
    public bool Unlimited => LimitBytes <= 0;

    public double UsedFraction => Unlimited ? 0 : Math.Clamp((double)UsedBytes / LimitBytes, 0, 1);

    public TimeSpan? Left(DateTimeOffset now) => Expires is { } e ? e - now : null;
}

/// <summary>Everything the subscription screen shows.</summary>
public sealed record AccountView(TelegramUser? User, IReadOnlyList<SubscriptionCard> Subscriptions, string? Bot, JsonObject Raw);

/// <summary>Reads the panel's sp_app JSON (the mini app's data) into typed cards. Unknown fields are ignored.</summary>
public static class NexViewParser
{
    public static AccountView Parse(JsonObject app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var subs = new List<SubscriptionCard>();
        foreach (var s in app["subs"] as JsonArray ?? [])
        {
            if (s is not JsonObject o || Str(o, "lid") is not { Length: > 0 } lid)
            {
                continue;
            }

            var d = o["data"] as JsonObject ?? [];
            var exp = Long(d, "expire");
            subs.Add(new SubscriptionCard(
                lid,
                Str(d, "username") ?? string.Empty,
                Str(d, "status") ?? "unknown",
                Str(d, "label") ?? Str(d, "backend") ?? string.Empty,
                Long(d, "used_traffic"),
                Long(d, "data_limit"),
                exp > 0 ? DateTimeOffset.FromUnixTimeSeconds(exp) : null,
                Https(Str(o, "url")),
                d["multi"]?.GetValue<bool>() == true,
                Nex(o["nex"] as JsonObject),
                Options(o["opts"] as JsonObject, out var sw),
                sw,
                Requests(o["reqs"] as JsonArray)));
        }

        return new AccountView(Named(app["user"]), subs, Str(app, "bot"), app);
    }

    private static TelegramUser? Named(JsonNode? u) =>
        u is JsonObject o ? new TelegramUser(0, Str(o, "name") ?? string.Empty, Str(o, "username")) : null;

    private static NexInfo? Nex(JsonObject? n)
    {
        if (n is null)
        {
            return null;
        }

        var exits = (n["exits"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(x => new NexExit(Str(x, "b") ?? string.Empty, Str(x, "label") ?? Str(x, "b") ?? string.Empty))
            .Where(x => x.Backend.Length > 0)
            .ToList();
        return new NexInfo(Bool(n, "on"), Bool(n, "member"), Str(n, "exit") ?? string.Empty, exits, Long(n, "price"), Https(Str(n, "combo")));
    }

    private static Dictionary<string, IReadOnlyList<long>> Options(JsonObject? opts, out IReadOnlyList<string> switchTo)
    {
        var map = new Dictionary<string, IReadOnlyList<long>>(StringComparer.Ordinal);
        switchTo = [];
        foreach (var (k, v) in opts ?? [])
        {
            if (v is not JsonArray a)
            {
                continue;
            }

            if (k == "switch")
            {
                switchTo = a.Select(x => x?.ToString() ?? string.Empty).Where(x => x.Length > 0).ToList();
            }
            else
            {
                map[k] = a.Select(x => x is JsonValue jv && jv.TryGetValue<long>(out var l) ? l : 0).Where(x => x > 0).ToList();
            }
        }

        return map;
    }

    private static List<SubRequest> Requests(JsonArray? reqs) =>
        (reqs ?? []).OfType<JsonObject>()
            .Select(r => new SubRequest(Str(r, "id") ?? string.Empty, Str(r, "kind") ?? string.Empty, Long(r, "value"), Str(r, "status") ?? string.Empty,
                Str(r, "stage") ?? "pay", Long(r, "price"), Str(r, "note"), Str(r, "pay"), Bool(r, "receipt")))
            .Where(r => r.Id.Length > 0)
            .ToList();

    internal static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static long Long(JsonObject o, string k) =>
        o[k] is JsonValue v ? (v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) ? (long)d
            : v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out l) ? l : 0) : 0;

    internal static bool Bool(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    internal static Uri? Https(string? s) => Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u : null;
}
