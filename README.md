# 📦 IBOX
*(Formerly Jacki VPN)*

**A Next-Generation Network Toolkit & Advanced Proxy Client**

IBOX is a comprehensive, cross-platform network manager designed for maximum stability, privacy, and performance. 

## 🚀 Installation Guide

### Windows 10 / 11 (64-bit)
* Run the `JackiVpn-<version>-win-x64.msi` installer.
* If Windows displays a "Windows protected your PC" warning, click "More info" and select "Run anyway" since the app is not yet signed with a commercial certificate.
* Click Install and accept the administrator prompt to install the app in Program Files.
* The privileged service for TUN mode and the kill switch is registered and started automatically.
* No .NET installation is required.
* To uninstall, navigate to Windows Settings > Apps > IBOX > Uninstall.

### Android 10 or later
* Install the APK that matches your phone architecture.
* Select the `arm64-v8a` APK for almost every phone from recent years.
* Select the `armeabi-v7a` APK for very old 32-bit phones.
* Select the `x86_64` APK for emulators.
* Allow "install unknown apps" in Android settings if prompted, and bypass Play Protect warnings by choosing "More details" > "Install anyway".
* For the Kill switch, go to Android VPN settings, tap the gear next to IBOX, and turn on "Always-on VPN" and "Block connections without VPN".

## ✨ Features & Enhancements (New in 1.0.0+)
* Features a new phone design and Persian language support.
* Downloads automatic, signed updates from the official release server.
* Subscription updates automatically clean up earlier duplicate servers.
* Supports XHTTP and custom-path gRPC configs on phones after the first connection.
* The connect button turns magenta while gaming and light blue while streaming.
* Supports PasarGuard subscriptions, tracking device limits and the last-online time every 10 minutes.
* Sync device feature allows syncing when the app is open on two devices on the same Wi-Fi.
* Includes an "Emergency network restore" utility in the Windows Start menu.

## 🧩 Third-Party Components & Licenses
IBOX ships with the following unmodified third-party proxy cores:
* **sing-box 1.14.2**: Licensed under GPL-3.0-or-later.
* **Xray-core 26.3.27**: Licensed under MPL-2.0.
* **Wintun 0.14.1**: Signed by WireGuard LLC, distributed under a Prebuilt Binaries License.
* **hev-socks5-tunnel 2.18.0**: Used for Android tun2socks, licensed under MIT.

The application is built using frameworks and libraries including the .NET runtime, WPF, SQLite, Avalonia UI, and CommunityToolkit.Mvvm. The loading animations are a C# port of thinking-orbs by Jakub Antalik, licensed under MIT.

## 🔒 File Integrity (v1.3.0)
SHA-256 hashes for release verification:
* `0831f2dbc57c45973000455304ff7f4352823f3da065dae3a1ac79874928cc5b` for `JackiVpn-1.3.0-android-arm64-v8a.apk`
* `7ccf79bd8541d33622c3f92250a432b806c6e195b257cd70a6ff84fa1e2b74b6` for `JackiVpn-1.3.0-android-armeabi-v7a.apk`
* `4074460459cab7d85000dd5de9860952e51acbdd21a969b018f6f75a0b7dc45a` for `JackiVpn-1.3.0-android-x86_64.apk`
* `a7f7714a9ca5c000293e50e7696d93ae62931ab0c52484b961d76056e436453e` for `JackiVpn-1.3.0-win-x64.msi`
