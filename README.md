# IBOX

Windows · Android · Linux

[فارسی](#فارسی) · [English](#english) · [Downloads / دانلود](https://github.com/dreampl/ibox/releases) · [Changes in 2.3.4](release-notes/2.3.4.md)

IBOX یک کلاینت مدیریت اتصال، اشتراک و مسیریابی با ورود اختیاری تلگرام است. نسخهٔ این کد **2.3.5** است.

IBOX is a connection, subscription and routing client with optional Telegram sign-in. This source tree is version **2.3.5**.

## فارسی

### دانلود

[صفحهٔ رسمی دانلود در GitHub Releases](https://github.com/dreampl/ibox/releases)

از بخش **Assets** همان Release، فایل مناسب را انتخاب کنید. شمارهٔ نسخهٔ کد با آخرین Release منتشرشده الزاماً یکسان نیست؛ اگر Release با شمارهٔ 2.3.5 را نمی‌بینید، بسته‌های آن هنوز روی GitHub منتشر نشده‌اند.

| دستگاه | بستهٔ 2.3.5 | انتخاب مناسب |
| --- | --- | --- |
| ویندوز ۱۰ یا ۱۱، ۶۴بیتی | `IBOX-2.3.5-win-x64.msi` | نصب‌کنندهٔ ویندوز |
| اندروید ۱۰ به بالا، ARM64 | `IBOX-2.3.5-android-arm64-v8a.apk` | بیشتر گوشی‌ها، از جمله Samsung A54 |
| اندروید ۱۰ به بالا، ARM32 | `IBOX-2.3.5-android-armeabi-v7a.apk` | دستگاه‌های ۳۲بیتی سازگار |
| اندروید x86_64 | `IBOX-2.3.5-android-x86_64.apk` | شبیه‌ساز یا دستگاه x86_64 |
| لینوکس x64، Debian / Ubuntu | `ibox_2.3.5_amd64.deb` | نصب همراه وابستگی‌ها با apt |
| لینوکس x64، آرشیو | `ibox-2.3.5-linux-x64.tar.gz` | استخراج و اجرای دستی |
| IBAR مستقل، ویندوز x64 | `IBAR-2.3.5-win-x64.msi` | ناچ مستقل و اختیاری |

در این مجموعه خروجی نصب برای iOS یا macOS ارائه نشده است. برای بررسی فایل‌ها از `SHA256SUMS.txt` همان Release استفاده کنید.

### نصب

**ویندوز**

1. MSI مناسب را اجرا و نصب را کامل کنید. نصب سرویس TUN به دسترسی مدیر نیاز دارد.
2. IBOX را از منوی Start باز کنید؛ نصب جداگانهٔ .NET لازم نیست.
3. در اجرای اول Easy Setup را دنبال کنید، یا از بخش Servers اشتراک و کانفیگ خود را اضافه کنید.
4. حذف برنامه: Windows Settings → Apps → IBOX → Uninstall.

**اندروید**

1. APK مناسب معماری دستگاه را نصب کنید؛ برای A54 نسخهٔ ARM64 را بگیرید.
2. در صورت نیاز، مجوز نصب فایل APK را به برنامه‌ای که فایل را باز می‌کند بدهید.
3. IBOX را باز کنید، سرور اضافه کنید و هنگام اولین اتصال مجوز VPN اندروید را تأیید کنید.
4. برای قطع دسترسی خارج از VPN، گزینه‌های Always-on VPN و Block connections without VPN در تنظیمات VPN اندروید در دسترس‌اند.

**لینوکس، Debian / Ubuntu**

```bash
sudo apt install ./ibox_2.3.5_amd64.deb
ibox gui
```

اجرای گرافیکی از منوی برنامه‌ها هم ممکن است. حالت کل سیستم برای راه‌اندازی کمک‌پردازش شبکه ممکن است درخواست رمز مدیر نشان بدهد.

برای آرشیو، ساختار پوشه‌ها را حفظ کنید:

```bash
mkdir -p ibox-2.3.5
tar -xzf ibox-2.3.5-linux-x64.tar.gz -C ibox-2.3.5
./ibox-2.3.5/opt/jackivpn/jackivpn gui
```

حذف بسته:

```bash
sudo apt remove jackivpn
```

### شروع استفاده

1. **تلگرام، اختیاری:** از پروفایل وارد حساب شوید. اشتراک‌های مرتبط با همان حساب را می‌توانید از From my Telegram account دریافت کنید.
2. **لینک اشتراک:** در ویندوز مسیر Servers → Subscriptions → Add subscription را باز کنید. در لینوکس از بخش Subscriptions و در اندروید از صفحهٔ اشتراک‌ها استفاده کنید.
3. **کانفیگ مستقل:** از Paste / Import، کلیپ‌بورد یا فایل استفاده کنید. قالب‌های پشتیبانی‌شده شامل VLESS، VMess، Trojan، Shadowsocks، Hysteria 2 و TUIC هستند؛ پشتیبانی هر ترکیب انتقال به هسته و دستگاه بستگی دارد.
4. یک سرور را انتخاب کنید یا **Smart Connect** را فعال کنید؛ سپس دکمهٔ اتصال در Home را بزنید. فعال‌کردن Smart Connect به‌تنهایی اتصال را شروع نمی‌کند.
5. در دسکتاپ، **TUN** برای مسیریابی کل سیستم و **System Proxy** برای برنامه‌هایی است که پروکسی سیستم را رعایت می‌کنند.

در ویندوز، Easy Setup از **Settings → About → Easy setup** دوباره باز می‌شود. چهار بخش اصلی دسکتاپ Home، Servers، Routing و Settings هستند. تنظیمات شبکهٔ محل کار در Routing قرار دارند.

### دستگاه‌ها و خانواده

- صفحهٔ **Devices & Nex Family** در ویندوز از نوار بالا یا پروفایل، در لینوکس از پروفایل و در اندروید از صفحهٔ حساب IBOX باز می‌شود.
- نشست‌های همان حساب، وضعیت آنلاین، کانفیگ جاری و زمان اتصال نمایش داده می‌شوند. دستگاه‌های متصل در Home نشان سبز دارند.
- برای کنترل بین دستگاه‌ها، نسخهٔ 2.3.5 را روی هر دو دستگاه نصب کنید و روی دستگاه مقصد **Allow remote control** را فعال کنید.
- از طریق رله می‌توانید یکی از کانفیگ‌های موجود روی دستگاه مقصد را انتخاب، وصل یا قطع کنید؛ نیازی به وای‌فای مشترک نیست. خروج اجباری نشست دستگاه دیگر هم در دسترس است.
- مقصد باید برنامه و دسترسی شبکهٔ فعال داشته باشد. برنامهٔ بسته یا متوقف‌شده توسط سیستم‌عامل بیدار نمی‌شود. مجوز VPN اندروید باید ابتدا روی خود دستگاه داده شده باشد.
- Nex Family به پشتیبانی سرویس‌دهنده نیاز دارد. شناسهٔ تلگرامِ سرپرست و اعضا باید به خانواده مجوز داده شده باشد. ساخت حساب جدید به مجوز جداگانه، تعداد جایگاه و بودجهٔ تعیین‌شده توسط مدیر سرویس وابسته است.
- سرپرست مجاز می‌تواند اعضا، سقف مصرف و لینک اشتراک آن‌ها را مدیریت کند؛ افزایش حجم کل خریداری‌شده از کلاینت ممکن نیست.
- راهنمای اتصال پنل و مجوزها: [Account control API](server/README.md).

همگام‌سازی محلی **Sync device** هم برای انتقال بین دستگاه‌های روی یک شبکه در دسترس است. برنامه باید روی هر دو دستگاه باز باشد؛ دستگاه مورد نظر را انتخاب و جریان جفت‌سازی را تأیید کنید.

### موزیک و پروفایل

ورود به حساب IBOX امکانات حساب و موزیک را فعال می‌کند. در ویندوز، پنل موزیک مستقل است؛ بستن آن نشست پخش را حفظ می‌کند و هنگام پخش، دکمهٔ بالای برنامه به مینی‌پلیر تبدیل می‌شود. نام و آواتار حساب از دادهٔ موجود تلگرام خوانده می‌شوند؛ عکس خصوصی یا غیرقابل‌دسترسی ممکن است بازیابی نشود.

### شورت‌کات‌ها

| کلید | کاربرد | محدوده |
| --- | --- | --- |
| `Ctrl + Alt + I` | نمایش یا مخفی‌کردن ناچ | ویندوز، از جمله روی برنامه‌های تمام‌صفحه؛ لینوکس با X11 |
| `Esc` | بستن سایدبار باز | ویندوز و لینوکس |
| `Tab` / `Shift + Tab` | حرکت بین کنترل‌ها | دسکتاپ |
| `Enter` | تأیید فرم دارای دکمهٔ پیش‌فرض | فرم‌های سازگار؛ فیلد چندخطی ممکن است Enter را برای خط جدید استفاده کند |

اگر میان‌بر سراسری توسط برنامهٔ دیگری گرفته شده باشد، ممکن است ثبت نشود. برای Wayland، میان‌بر سراسری مستقلی در این نسخه پیاده‌سازی نشده است.

### دادهٔ حساب و رفع اشکال

ورود تلگرام، هویت تأییدشدهٔ قابل دریافت از تلگرام را در سرویس حساب ثبت می‌کند. مدیریت دستگاه‌ها نام دستگاه، نسخه، وضعیت اتصال و نام/شناسهٔ کانفیگ‌ها را به همان سرویس می‌فرستد؛ URI کامل کانفیگ، رمز و کلید خصوصی برای این فهرست ارسال نمی‌شوند. شمارهٔ تلفن به اشتراک‌گذاری اختیاری کاربر در تلگرام وابسته است.

- اگر ورود رسمی تلگرام در 2.3.2 به حساب اشتباه وصل شده بود، یک بار از تلگرام در پروفایل خارج و دوباره وارد شوید.
- اگر برنامه‌ای پس از تغییر پروکسی درست کار نمی‌کند، آن را دوباره اجرا کنید یا حالت TUN را بررسی کنید.
- برای مشکل مسیر ایران یا سرویس‌های خارجی، قواعد Routing، حالت Direct/Global/Iran-friendly و DNS را بررسی کنید و متن خطا را همراه نسخه و سیستم‌عامل گزارش دهید.
- ابزار **IBOX - Emergency network restore** در منوی Start ویندوز برای بازیابی تنظیمات شبکه در دسترس است.
- گزارش اشکال: [GitHub Issues](https://github.com/dreampl/ibox/issues). پیش از ارسال لاگ، لینک اشتراک، توکن، رمز و کلید خصوصی را حذف کنید.

### تغییرات و مجوز

- [تغییرات 2.3.4 نسبت به 2.3.2، فارسی و انگلیسی](release-notes/2.3.4.md)
- [یادداشت نسخهٔ 2.3.5، فارسی](release-notes/2.3.5.fa.txt)
- [مجوز پروژه](LICENSE) و [اجزای شخص ثالث](THIRD_PARTY_NOTICES.txt)

## English

### Download

[Official GitHub Releases](https://github.com/dreampl/ibox/releases)

Open the matching release and choose its **Assets** section. The source version may be newer than the latest published release. If release 2.3.5 is absent, its installers have not been published to GitHub yet.

| Platform | Version 2.3.5 package | Intended use |
| --- | --- | --- |
| Windows 10 / 11 x64 | `IBOX-2.3.5-win-x64.msi` | Windows installer |
| Android 10+ ARM64 | `IBOX-2.3.5-android-arm64-v8a.apk` | Most recent phones, including Samsung A54 |
| Android 10+ ARM32 | `IBOX-2.3.5-android-armeabi-v7a.apk` | Compatible 32-bit devices |
| Android x86_64 | `IBOX-2.3.5-android-x86_64.apk` | Emulators and x86_64 devices |
| Linux x64 Debian / Ubuntu | `ibox_2.3.5_amd64.deb` | Install with apt |
| Linux x64 archive | `ibox-2.3.5-linux-x64.tar.gz` | Extract and run manually |
| Standalone IBAR, Windows x64 | `IBAR-2.3.5-win-x64.msi` | Optional independent notch |

This package set does not include iOS or macOS installers. Verify files against `SHA256SUMS.txt` from the same release.

### Install

**Windows:** run the MSI and complete installation. Administrator access is needed to install the TUN service. Open IBOX from Start; a separate .NET installation is not required. To uninstall, use Windows Settings → Apps → IBOX → Uninstall.

**Android:** install the matching APK, allowing APK installation for the opening app if necessary. Open IBOX, add servers and accept Android's VPN permission request on first connection. Android's VPN settings offer Always-on VPN and Block connections without VPN.

**Debian / Ubuntu:**

```bash
sudo apt install ./ibox_2.3.5_amd64.deb
ibox gui
```

You can also launch IBOX from the applications menu. Whole-system mode may request administrator authentication to start the network helper.

**Linux archive:**

```bash
mkdir -p ibox-2.3.5
tar -xzf ibox-2.3.5-linux-x64.tar.gz -C ibox-2.3.5
./ibox-2.3.5/opt/jackivpn/jackivpn gui
```

Keep the extracted directory structure intact. Remove the Debian package with `sudo apt remove jackivpn`.

### First connection

1. Optionally sign in with Telegram from your profile, then select subscriptions through **From my Telegram account**.
2. Add a subscription URL. On Windows use **Servers → Subscriptions → Add subscription**; Linux and Android have a Subscriptions section.
3. For individual configs, use Paste / Import, clipboard or file import. Supported formats include VLESS, VMess, Trojan, Shadowsocks, Hysteria 2 and TUIC; transport combinations depend on the core and platform.
4. Choose a server or enable **Smart Connect**, then press the Home connect button. Enabling Smart Connect alone does not connect.
5. On desktop, **TUN** routes whole-system traffic. **System Proxy** serves applications that honor the system proxy.

Windows Easy Setup can be reopened from **Settings → About → Easy setup**. Desktop navigation has four destinations: Home, Servers, Routing and Settings. Workplace networking settings are under Routing.

### Devices and Nex Family

- Open **Devices & Nex Family** from the Windows header/profile, Linux profile or Android IBOX account page.
- View account sessions, online state, active config and connection time. Connected devices have green Home indicators.
- Install 2.3.5 on both devices and enable **Allow remote control** on the target. Through the relay, select an existing target config, connect or disconnect it, or revoke another device's session. A shared LAN is not required.
- The target app must remain running with network access. This does not wake an app killed or suspended by the OS. Android VPN permission must first be granted locally.
- Nex Family requires provider support and explicit assignment of the head/member Telegram IDs. New account creation additionally requires granted slots and a member volume budget. Authorized heads can manage members, caps and subscription links; clients cannot increase the purchased family total.
- See the [account-control API and deployment guide](server/README.md).

Local **Sync device** remains available for devices on the same network. Keep IBOX open on both devices and complete the pairing flow.

### Music and profiles

Signing in enables account and music features. On Windows, Music has its own sidebar; closing it preserves the playback session. During playback the header button expands into a mini player. Profile names and avatars use available Telegram identity data; private or unavailable photos may not be retrievable.

### Keyboard shortcuts

| Shortcut | Action | Availability |
| --- | --- | --- |
| `Ctrl + Alt + I` | Show/hide the notch | Windows, including over full-screen apps; Linux X11 |
| `Esc` | Close the open sidebar | Windows and Linux |
| `Tab` / `Shift + Tab` | Navigate controls | Desktop |
| `Enter` | Submit a form with a default button | Supported forms; multiline inputs may use Enter for a new line |

A global shortcut can fail to register if another application owns it. This version has no separate Wayland global shortcut implementation.

### Account data and troubleshooting

Telegram sign-in records the Telegram-verified identity available to the account service. Device management sends device labels, version, connection state and config IDs/names to that service. Full config URIs, passwords and private keys are not uploaded for this device list. Phone sharing remains optional in Telegram.

If official Telegram sign-in in 2.3.2 connected you to the wrong account, sign out from Profiles once and sign in again. Restart applications that cached an earlier system proxy, or check TUN mode. For split-routing issues, review Routing and DNS settings and include the error text, app version and OS in your report.

Windows includes **IBOX - Emergency network restore** in Start. Report issues on [GitHub Issues](https://github.com/dreampl/ibox/issues); remove subscription URLs, tokens, passwords and private keys from logs before posting.

### Changes and license

- [2.3.4 compared with 2.3.2, Persian and English](release-notes/2.3.4.md)
- [2.3.5 release notes, English](release-notes/2.3.5.en.txt)
- [Project license](LICENSE) and [third-party notices](THIRD_PARTY_NOTICES.txt)
