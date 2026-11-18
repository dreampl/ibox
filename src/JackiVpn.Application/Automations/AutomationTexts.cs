namespace JackiVpn.Application.Automations;

/// <summary>Titles and one-line descriptions shown in every app's Automations page (English and Persian).</summary>
public static class AutomationTexts
{
    private static readonly Dictionary<string, (string En, string EnHint, string Fa, string FaHint)> Texts = new(StringComparer.Ordinal)
    {
        [ServerWatchdogAutomation.AutomationId] = (
            "Switch away from a bad server",
            "When the current server keeps failing or gets much slower, move to the fastest healthy one (at most every 10 minutes; Smart mode does this itself).",
            "تعویض سرور خراب",
            "اگر سرور فعلی مدام قطع شود یا خیلی کند شود، به سریع‌ترین سرور سالم برو (حداکثر هر ۱۰ دقیقه یک بار؛ حالت هوشمند خودش این کار را می‌کند)."),
        [TrustedNetworksAutomation.AutomationId] = (
            "Trusted Wi-Fi",
            "Disconnect on the Wi-Fi networks you trust and connect on any other Wi-Fi. Acts once when the network changes.",
            "وای‌فای امن",
            "روی وای‌فای‌های امنِ تو قطع کن و روی هر وای‌فای دیگری وصل شو. فقط یک بار، وقتی شبکه عوض می‌شود."),
        [NightlyRefreshAutomation.AutomationId] = (
            "Nightly refresh",
            "Refresh every subscription once a night and report servers that stopped working (or remove them, if you choose).",
            "به‌روزرسانی شبانه",
            "هر شب همه‌ی اشتراک‌ها را به‌روز کن و سرورهایی را که دیگر کار نمی‌کنند گزارش کن (یا اگر بخواهی حذف کن)."),
        [QuotaWatcherAutomation.AutomationId] = (
            "Quota watcher",
            "Warn before a subscription's traffic runs out or it expires.",
            "مراقب حجم",
            "قبل از تمام شدن حجم یا تاریخ اشتراک هشدار بده."),
    };

    public static string Title(string id, bool persian = false) => Texts.TryGetValue(id, out var t) ? (persian ? t.Fa : t.En) : id;

    public static string Hint(string id, bool persian = false) => Texts.TryGetValue(id, out var t) ? (persian ? t.FaHint : t.EnHint) : string.Empty;

    public static string Format(AutomationLogEntry e) => $"{e.Utc.ToLocalTime():MM-dd HH:mm}  {Title(e.AutomationId)}: {e.Message}";
}
