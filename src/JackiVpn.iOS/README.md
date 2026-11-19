# IBOX for iOS (xtool, work in progress)

SwiftPM app built with [xtool](https://github.com/xtool-org/xtool) (no Xcode). Skill: `.claude/skills/using-xtool`.

## Why native Swift, not MAUI
MAUI iOS needs a Mac with Xcode to compile and sign, and xtool can't drive MAUI output.
xtool builds SwiftPM apps on Linux/WSL, so this is a native SwiftUI app plus a NetworkExtension
packet tunnel that embeds sing-box (libbox).

## Layout
| Target | What |
|---|---|
| `IBOXCore` | Platform-neutral port of the desktop parsing: share links (vless, vmess, trojan, ss, socks, http, hy2, tuic, wireguard), Base64/plain subscription lists, `subscription-userinfo`, sing-box ≥1.12 config (TUN inbound, DNS, route), JSON store. |
| `IBOX` | SwiftUI app: connect/disconnect, server list grouped by subscription, add by URL or pasted links, update/delete subscription. English + Persian (RTL). |
| `PacketTunnel` | `NEPacketTunnelProvider`. Refuses to start until Libbox.xcframework is bundled. |
| `IBOXCoreTests` | XCTest for parser, config and store. Runs with plain `swift test` on Linux/Windows. |

IDs: app `app.ibox.ios`, extension `app.ibox.ios.PacketTunnel`, App Group `group.app.ibox.ios` (`AppShared.swift`).

## Status
**Nothing here has been compiled yet.** This host has no Swift toolchain and no WSL.

Blocked on:
1. Toolchain. Either WSL + Swift 6 + xtool (the full iOS path), or Swift for Windows just to run `swift test` on `IBOXCore`.
2. `xtool setup` needs an Xcode.xip (Apple ID download, about 3–4 GB, more once extracted; put it on C:).
3. The packet-tunnel entitlement needs a **paid** Apple Developer account. A free team can build and install the app shell, but it can't sign a working VPN.
4. Libbox.xcframework (sing-box `experimental/libbox`, gomobile). It is built on macOS, or taken from a sing-box Apple build. Then implement `LibboxPlatformInterface` in `PacketTunnelProvider` the way sing-box's own Apple client does.

Unverified xtool details: the per-extension entitlements (`PacketTunnel.entitlements` isn't referenced from `xtool.yml` yet), the extension bundle id xtool derives, and `NSExtensionPrincipalClass` = `PacketTunnel.PacketTunnelProvider`.

Not ported yet: sing-box/Xray JSON and Clash YAML subscriptions, dnstt, Xray-only transports (xhttp, kcp: shown with a warning), routing rules, latency test.

## WSL setup (after `wsl --install -d Ubuntu-24.04` + reboot + creating the Ubuntu user)
`scripts/wsl-setup.sh` has four steps, run from Windows (see the header of the script for exact commands):
`deps` (apt, as root) → `tools` (Swift 6.2 via swiftly + xtool AppImage) → `test` (`swift test` on IBOXCore, built in `~/ibox-ios` inside WSL) → `ipa` (only after you ran `xtool setup` with your Apple ID and Xcode.xip).
