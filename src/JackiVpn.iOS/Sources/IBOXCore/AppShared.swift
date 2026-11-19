import Foundation

/// Identifiers shared by the app and the packet tunnel extension. Must match xtool.yml,
/// the entitlements files and the provisioning profile.
public enum AppShared {
    public static let appBundleId = "app.ibox.ios"
    public static let tunnelBundleId = "app.ibox.ios.PacketTunnel"
    public static let appGroup = "group.app.ibox.ios"
    public static let stateFileName = "servers.json"
    /// Key in the tunnel start options that carries the generated sing-box config.
    public static let configOptionKey = "singbox_config"

    /// App Group container when available (signed build), else Application Support (simulator/tests).
    public static func stateFileURL() -> URL {
        let fm = FileManager.default
        #if os(iOS) || os(macOS)
        if let group = fm.containerURL(forSecurityApplicationGroupIdentifier: appGroup) {
            return group.appendingPathComponent(stateFileName)
        }
        #endif
        let base = (try? fm.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true))
            ?? fm.temporaryDirectory
        return base.appendingPathComponent("IBOX", isDirectory: true).appendingPathComponent(stateFileName)
    }
}

/// English and Persian UI text, like the desktop and Android apps.
public enum L10n {
    public static var isPersian: Bool {
        Locale.preferredLanguages.first?.lowercased().hasPrefix("fa") ?? false
    }

    static func t(_ en: String, _ fa: String) -> String { isPersian ? fa : en }

    public static var connect: String { t("Connect", "اتصال") }
    public static var disconnect: String { t("Disconnect", "قطع اتصال") }
    public static var connecting: String { t("Connecting…", "در حال اتصال…") }
    public static var disconnecting: String { t("Disconnecting…", "در حال قطع…") }
    public static var connected: String { t("Connected", "متصل") }
    public static var notConnected: String { t("Not connected", "متصل نیست") }
    public static var servers: String { t("Servers", "سرورها") }
    public static var noServers: String { t("No servers yet. Tap + to add a subscription link or paste a config.", "هنوز سروری نیست. برای افزودن لینک اشتراک یا کانفیگ، + را بزنید.") }
    public static var addTitle: String { t("Add servers", "افزودن سرور") }
    public static var addHint: String { t("Subscription URL, or one or more vless:// vmess:// trojan:// ss:// hy2:// tuic:// links", "لینک اشتراک، یا یک یا چند لینک vless:// vmess:// trojan:// ss:// hy2:// tuic://") }
    public static var pasteClipboard: String { t("Paste from clipboard", "چسباندن از کلیپ‌بورد") }
    public static var add: String { t("Add", "افزودن") }
    public static var cancel: String { t("Cancel", "انصراف") }
    public static var update: String { t("Update", "به‌روزرسانی") }
    public static var delete: String { t("Delete", "حذف") }
    public static var manual: String { t("Manual", "دستی") }
    public static var nothingFound: String { t("No supported servers found.", "سرور پشتیبانی‌شده‌ای پیدا نشد.") }
    public static func added(_ n: Int) -> String { t("Added \(n) servers.", "\(n) سرور اضافه شد.") }
    public static var needsXray: String { t("Needs Xray (not in iOS yet)", "به Xray نیاز دارد (هنوز در iOS نیست)") }
    public static var coreMissing: String { t("The sing-box core is not bundled in this build.", "هسته sing-box در این نسخه وجود ندارد.") }
}
