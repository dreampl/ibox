# IBOX for iOS (xtool, work in progress)

SwiftPM app built with [xtool](https://github.com/xtool-org/xtool) (no Xcode). Skill: `.claude/skills/using-xtool`.

## Why native Swift, not MAUI
MAUI iOS needs a Mac with Xcode for compilation and signing; xtool can't drive MAUI output.
xtool builds SwiftPM apps from Linux/WSL, so the path is a native SwiftUI app plus a
NetworkExtension packet tunnel embedding sing-box (libbox, cross-built with the Darwin SDK xtool extracts).

## Status
- Done: skeleton (Package.swift, xtool.yml, SwiftUI shell with connect button). Not compiled yet.
- Blocked on host setup:
  1. WSL is not installed on this host (`wsl --install` needs admin + reboot; user decision).
  2. Swift 6 toolchain + xtool inside WSL, then `xtool setup` needs an Xcode.xip (Apple ID to download, ~3–4 GB, more extracted).
  3. Packet tunnel (NetworkExtension) entitlement needs a **paid** Apple Developer account; free teams can't sign it.
- Remaining: subscription import + server list (port from JackiVpn.Subscriptions), tunnel extension target, libbox build, `xtool dev build --ipa`.
