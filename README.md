# 📦 IBOX
*(Formerly Jacki VPN)*

**A Next-Generation Network Toolkit & Advanced Proxy Client**

IBOX is a comprehensive, cross-platform network manager designed for maximum stability, privacy, and performance. 

## 🚀 Installation Guide

### Windows 10 / 11 (64-bit)[cite: 1, 2]
* Run the `JackiVpn-<version>-win-x64.msi` installer[cite: 1, 2].
* If Windows displays a "Windows protected your PC" warning, click "More info" and select "Run anyway" since the app is not yet signed with a commercial certificate[cite: 1, 2].
* Click Install and accept the administrator prompt to install the app in Program Files[cite: 1, 2].
* The privileged service for TUN mode and the kill switch is registered and started automatically[cite: 1, 2].
* No .NET installation is required[cite: 1, 2].
* To uninstall, navigate to Windows Settings > Apps > IBOX > Uninstall[cite: 1, 2].

### Android 10 or later[cite: 1, 2]
* Install the APK that matches your phone architecture[cite: 1, 2].
* Select the `arm64-v8a` APK for almost every phone from recent years[cite: 1, 2].
* Select the `armeabi-v7a` APK for very old 32-bit phones[cite: 1, 2].
* Select the `x86_64` APK for emulators[cite: 1, 2].
* Allow "install unknown apps" in Android settings if prompted, and bypass Play Protect warnings by choosing "More details" > "Install anyway"[cite: 1, 2].
* For the Kill switch, go to Android VPN settings, tap the gear next to IBOX, and turn on "Always-on VPN" and "Block connections without VPN"[cite: 1, 2].

## ✨ Features & Enhancements (New in 1.0.0+)
* Features a new phone design and Persian language support[cite: 1, 2].
* Downloads automatic, signed updates from connect.jackiairline.ir[cite: 1, 2].
* Subscription updates automatically clean up earlier duplicate servers[cite: 1, 2].
* Supports XHTTP and custom-path gRPC configs on phones after the first connection[cite: 1, 2].
* The connect button turns magenta while gaming and light blue while streaming[cite: 1, 2].
* Supports PasarGuard subscriptions, tracking device limits and the last-online time every 10 minutes[cite: 1, 2].
* Sync device feature allows syncing when the app is open on two devices on the same Wi-Fi[cite: 1, 2].
* Includes an "Emergency network restore" utility in the Windows Start menu[cite: 1, 2].

## 🧩 Third-Party Components & Licenses
IBOX ships with the following unmodified third-party proxy cores:
* **sing-box 1.14.2**: Licensed under GPL-3.0-or-later[cite: 4].
* **Xray-core 26.3.27**: Licensed under MPL-2.0[cite: 4].
* **Wintun 0.14.1**: Signed by WireGuard LLC, distributed under a Prebuilt Binaries License[cite: 4].
* **hev-socks5-tunnel 2.18.0**: Used for Android tun2socks, licensed under MIT[cite: 4].

The application is built using frameworks and libraries including the .NET runtime, WPF, SQLite, Avalonia UI, and CommunityToolkit.Mvvm[cite: 4]. The loading animations are a C# port of thinking-orbs by Jakub Antalik, licensed under MIT[cite: 4].

## 🔒 File Integrity (v1.3.0)
SHA-256 hashes for release verification:
* `0831f2dbc57c45973000455304ff7f4352823f3da065dae3a1ac79874928cc5b` for `JackiVpn-1.3.0-android-arm64-v8a.apk`[cite: 3].
* `7ccf79bd8541d33622c3f92250a432b806c6e195b257cd70a6ff84fa1e2b74b6` for `JackiVpn-1.3.0-android-armeabi-v7a.apk`[cite: 3].
* `4074460459cab7d85000dd5de9860952e51acbdd21a969b018f6f75a0b7dc45a` for `JackiVpn-1.3.0-android-x86_64.apk`[cite: 3].
* `a7f7714a9ca5c000293e50e7696d93ae62931ab0c52484b961d76056e436453e` for `JackiVpn-1.3.0-win-x64.msi`[cite: 3].
