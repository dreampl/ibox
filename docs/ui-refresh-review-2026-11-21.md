# IBOX UI refresh review, 2026-11-21

The owner approved packaging. Version 2.3.4 packages were built for Windows x64, Android (arm64-v8a, armeabi-v7a, x86_64), and Linux x64.

## Direction

The owner's screenshots identify clipped tab backgrounds, insufficient distinction between navigation levels, an oversized server panel, and an empty navigation rail. Preserve the IBOX wordmark, existing accent, and native keyboard behavior.

Design read: ENERGY 1 / RHYTHM 2 / MOTION 1. Primary tabs use filled selection surfaces. Secondary tabs use an underline. Neutral gray and black surfaces keep blue reserved for actions and selection. The rail groups four labeled destinations between the wordmark and the account/status footer. The startup animation uses the existing four vertical IBOX bars, without a background or a repeating animation.

## Changes

- Replace the WPF TabPanel header arrangement with a horizontal stack in a padded scroll container to avoid clipping the last tab's selected or focused surface.
- Differentiate primary workspace navigation from secondary section tabs.
- Keep Connection and Usage as the Home tabs. Restore the Connection dashboard's responsive grid and top-align its cards.
- Replace the sparse icon rail with labeled navigation, a compact connection summary, and the profile entry.
- Add Gray and Black palettes alongside the original Blue palette. Palette caching now distinguishes dark palettes, so switching between them updates all dynamic resources.
- Replace the Windows splash rectangle with transparent, staggered four-bar entry and a short exit. Respect reduced motion. Linux uses the same transparent four-bar motif during initialization.
- Mirror the dashboard grid, labeled rail, primary navigation, and neutral palettes in Linux.

## Verification

- Windows desktop build: passed, no warnings or errors.
- Linux build: passed, no warnings or errors.
- Android Compile target, arm64: passed, no warnings or errors.
- Unit tests: 801 passed, 0 failed. Added coverage for Telegram avatar parsing, login stages, and informational Telegram notch messages that cannot request an approval decision.
- Diff whitespace check: passed.
- Running Windows preview: accessibility tree confirms four labeled navigation destinations, Connection and Usage, and the restored dashboard cards without nested Connection tabs.

## Latest desktop verification

Windows capture recovered. The current neutral dark preview was opened and inspected:

- Primary tab background: rounded corners visible across the whole bar.
- Dashboard: Traffic, Speed Test, and Server cards share their top and bottom edges.
- Servers: 47 real servers, five visible rows at the current window size, and no internal column scrollbars. Next changes the range from 1–5 to 6–10. Row count adapts to the available height; multiple subscription groups retain a single outer page scroll.
- Action icons: import, clipboard, file import, connect, paging, and profile actions render their corresponding symbols.
- Telegram avatar: the existing verified session supplied no photo URL. Its public Telegram photo was fetched and cached (25,213 bytes), then visually confirmed in the sidebar and profile header. A missing or inaccessible public photo keeps the existing avatar.
- Telegram status bridge: progress and completion preview notifications both received `ok` from the isolated preview's notch pipe. Preview text explicitly identified these as display tests. Real login stages feed this same path. Progress updates replace previous Telegram statuses, and the completion message retires after five seconds.
- IBAR build: passed, no warnings or errors, using the updated shared notch icon and Telegram message handling.

The profile is currently signed in, as independently confirmed by the app and its protected preview session. The current review did not sign the user out or perform a new authentication round trip. Preview data remains separate from the installed application's data.

## Pending verification

The Galaxy A54 performance and Telegram round trip still require a device run. Linux appearance requires a Linux desktop run. Full-screen shortcut behavior is not covered by compilation or unit tests.

## Delivery gate

Compilation, unit checks, and the scoped Windows visual and interaction checks above passed. Device checks and Linux appearance remain pending; package creation does not establish device runtime verification.

## Version 2.3.4 packaging

- Windows IBOX and bundled IBAR MSI packages built. IBOX ProductVersion verified as 2.3.4. WiX emitted one ICE60 language metadata warning for a payload file.
- All three Android APK signatures verified; ARM64 signing identity matches version 2.3.3. APK ABI contents and bundled sing-box/Xray libraries verified.
- Linux DEB and TAR packages built; DEB version/architecture and executable ELF app/core permissions verified.
- SHA-256 checksums generated for all seven packages. Build outputs and signing material remain outside tracked source files.
- The mini-player progress line is inset 12 pixels horizontally and 5 pixels from the bottom of its container.
