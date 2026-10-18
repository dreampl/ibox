using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using JackiVpn.Desktop.Views;
using JackiVpn.Domain.Bar;

namespace JackiVpn.Bar;

/// <summary>IBAR's settings (%LOCALAPPDATA%\IBAR\settings.json).</summary>
public sealed record BarSettings
{
    public GlassLevel Glass { get; init; } = GlassLevel.Drawn;

    public double GlassStrength { get; init; } = 0.7;

    /// <summary>Monitors (device names) without a notch.</summary>
    public IReadOnlyList<string> HiddenMonitors { get; init; } = [];

    public bool Shelf { get; init; } = true;

    public bool AiAssistants { get; init; } = true;

    public bool StartWithWindows { get; init; } = true;

    /// <summary>en or fa.</summary>
    public string Language { get; init; } = "en";

    /// <summary>Panel look: dark or light (the island itself is always black).</summary>
    public bool LightPanel { get; init; }

    public bool HideInFullScreen { get; init; } = true;

    public bool ShowNowPlaying { get; init; } = true;

    /// <summary>The fixed folder chat works in.</summary>
    public string? ChatFolder { get; init; }

    /// <summary>Chat: Claude Code (claude) or Codex (codex).</summary>
    public string ChatAgent { get; init; } = "claude";

    /// <summary>Chat may propose changes (each one still asks Allow / Deny on the notch); off = read-only / plan.</summary>
    public bool ChatMayChange { get; init; }

    public IReadOnlyList<ApprovedPlugin> Plugins { get; init; } = [];

    /// <summary>The start / done / error sounds.</summary>
    public bool Sounds { get; init; } = true;

    /// <summary>0…1.</summary>
    public double SoundVolume { get; init; } = 0.5;

    /// <summary>Tasks on the notch: browser downloads, download managers, file copies, Adobe renders and exports.</summary>
    public bool WatchDownloads { get; init; } = true;

    public bool WatchCopies { get; init; } = true;

    public bool WatchAdobe { get; init; } = true;

    /// <summary>Every app's Windows notifications on the notch (only the apps in <see cref="NotifyApps"/>).</summary>
    public bool WindowsNotifications { get; init; }

    /// <summary>Apps (AppUserModelId) whose notifications come to the notch.</summary>
    public IReadOnlyList<string> NotifyApps { get; init; } = [];

    /// <summary>Apps whose Windows banner IBAR turned off (ShowBanner = 0) — put back when unticked or IBAR is removed.</summary>
    public IReadOnlyList<string> QuietBanners { get; init; } = [];

    /// <summary>Apps seen sending notifications (offered in the list).</summary>
    public IReadOnlyList<KnownApp> SeenApps { get; init; } = [];

    /// <summary>Plugins the user turned down (asked again only after a new manifest).</summary>
    public IReadOnlyList<string> DeniedPlugins { get; init; } = [];
}

/// <summary>An app that sent a Windows notification (its id and the name Windows shows).</summary>
public sealed record KnownApp(string Id, string Name);

/// <summary>Loads and saves <see cref="BarSettings"/>; tells listeners after each change.</summary>
public sealed class BarSettingsStore
{
    private static readonly JsonSerializerOptions Json = new(BarPlugins.Json) { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private readonly string _path;

    public BarSettingsStore(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "settings.json");
        try
        {
            Current = File.Exists(_path) ? JsonSerializer.Deserialize<BarSettings>(File.ReadAllText(_path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Current = new();
        }
    }

    public string Folder { get; }

    public BarSettings Current { get; private set; }

    public event EventHandler<BarSettings>? Changed;

    public void Update(Func<BarSettings, BarSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Current = change(Current);
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // kept for this run
        }

        Changed?.Invoke(this, Current);
    }
}

/// <summary>The control panel's words, in English and Persian.</summary>
public static class T
{
    public static bool Persian { get; set; }

    private static readonly Dictionary<string, string> Fa = new(StringComparer.Ordinal)
    {
        ["IBAR settings"] = "تنظیمات IBAR",
        ["Glass"] = "شیشه",
        ["Off"] = "خاموش",
        ["Drawn"] = "ساختگی",
        ["Real"] = "واقعی",
        ["Strength"] = "شدت",
        ["Real glass reads the screen behind the open notch a few times a second."] = "شیشهٔ واقعی چند بار در ثانیه پشت ناچِ باز را از صفحه می‌خواند.",
        ["Monitors"] = "مانیتورها",
        ["Notch on"] = "ناچ روی",
        ["Shelf"] = "قفسه",
        ["Keep dropped files, text and links on the notch"] = "فایل، متن و لینکِ رهاشده روی ناچ بماند",
        ["AI assistants"] = "دستیارهای هوش مصنوعی",
        ["Claude and ChatGPT notifications and requests on the notch"] = "اعلان‌ها و درخواست‌های Claude و ChatGPT روی ناچ",
        ["Connect Claude Code"] = "اتصال Claude Code",
        ["Disconnect Claude Code"] = "قطع Claude Code",
        ["Connect Codex"] = "اتصال Codex",
        ["Disconnect Codex"] = "قطع Codex",
        ["Plugins"] = "افزونه‌ها",
        ["No plugins yet."] = "هنوز افزونه‌ای نیست.",
        ["Remove"] = "حذف",
        ["Chat"] = "چت",
        ["Work folder"] = "پوشهٔ کار",
        ["Choose…"] = "انتخاب…",
        ["May propose changes (each asks Allow / Deny)"] = "اجازهٔ پیشنهاد تغییر (هر تغییر Allow / Deny می‌خواهد)",
        ["Start with Windows"] = "شروع همراه ویندوز",
        ["Hide while a full-screen app is in front"] = "پنهان هنگام برنامهٔ تمام‌صفحه",
        ["Language"] = "زبان",
        ["Light panel"] = "پنل روشن",
        ["Quit IBAR"] = "خروج از IBAR",
        ["Close"] = "بستن",
        ["Settings…"] = "تنظیمات…",
        ["Chat…"] = "چت…",
        ["Ask"] = "بپرس",
        ["Send"] = "ارسال",
        ["Stop"] = "توقف",
        ["Read-only (plan)"] = "فقط‌خواندنی (plan)",
        ["Not found on this computer"] = "روی این کامپیوتر پیدا نشد",
        ["Real glass bends what is behind the open notch, on the GPU, only when it changes."] = "شیشهٔ واقعی پشتِ ناچِ باز را با GPU خم می‌کند، فقط وقتی تصویر عوض شود.",
        ["Tasks on the notch"] = "کارها روی ناچ",
        ["One circle beside the notch shows how far a task is; click it for the others."] = "یک دایره کنار ناچ نشان می‌دهد کار چقدر پیش رفته؛ با کلیک، بقیه می‌آیند.",
        ["Browser downloads and download managers"] = "دانلودهای مرورگر و دانلود منیجرها",
        ["Copying and moving files (Explorer)"] = "کپی و انتقال فایل (اکسپلورر)",
        ["Adobe renders and exports"] = "رندر و خروجی Adobe",
        ["Browser extension"] = "افزونهٔ مرورگر",
        ["Exact sizes and speed from the browser itself. Load it once: the browser's extensions page opens with the folder to load."] = "حجم و سرعت دقیق از خود مرورگر. یک بار نصبش کنید: صفحهٔ افزونه‌های مرورگر و پوشهٔ افزونه باز می‌شود.",
        ["Install extension"] = "نصب افزونه",
        ["Sounds"] = "صدا",
        ["Soft start, done and error sounds; silent while a full-screen app or game is in front."] = "صدای نرمِ شروع، پایان و خطا؛ هنگام برنامهٔ تمام‌صفحه یا بازی بی‌صدا.",
        ["Play sounds"] = "پخش صدا",
        ["Volume"] = "بلندی",
        ["Start"] = "شروع",
        ["Done"] = "پایان",
        ["Error"] = "خطا",
        ["Windows notifications"] = "اعلان‌های ویندوز",
        ["Notifications of the apps you tick appear on the notch (title, text, icon; a click opens the app). Windows does not let another app press their buttons."] = "اعلانِ برنامه‌هایی که تیک بزنید روی ناچ می‌آید (عنوان، متن، آیکن؛ کلیک برنامه را باز می‌کند). ویندوز اجازه نمی‌دهد برنامهٔ دیگری دکمه‌های اعلان را بزند.",
        ["Show other apps' notifications on the notch"] = "نمایش اعلانِ برنامه‌های دیگر روی ناچ",
        ["Apps appear here after they send a notification."] = "برنامه‌ها بعد از اولین اعلان اینجا می‌آیند.",
        ["hide Windows banner"] = "پنهان کردن بنر ویندوز",
        ["Hiding the banner writes Windows' own per-app switch (undone when unticked); Windows may apply it only after you sign in again."] = "پنهان کردن بنر، کلید خودِ ویندوز برای همان برنامه را می‌نویسد (با برداشتن تیک برمی‌گردد)؛ ممکن است ویندوز فقط بعد از ورود دوباره اعمالش کند.",
        ["Sign in"] = "ورود",
        ["Ask about the files in the work folder. Read-only unless changes are allowed in the settings."] = "دربارهٔ فایل‌های پوشهٔ کار بپرسید. فقط‌خواندنی، مگر در تنظیمات اجازهٔ تغییر داده شود.",
        ["Claude Code on this computer is signed in only inside the Claude app. Sign in once for chat on the notch:"] = "Claude Code روی این کامپیوتر فقط داخل اپ Claude وارد شده است. برای چت روی ناچ یک بار وارد شوید:",
    };

    public static string S(string english) => Persian && Fa.TryGetValue(english, out var fa) ? fa : english;
}
