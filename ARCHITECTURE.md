# JackiVpn — Architecture

> Product name is a placeholder. Everything user-visible derives from
> `JackiVpn.Domain.ProductInfo` and `Directory.Build.props` (`ProductName`,
> `ProductDisplayName`). See "Renaming the product" at the end.

## 1. Goals and priorities

In strict order:

1. Security and leak prevention (DNS, IPv6, kill switch, credentials).
2. Stable connectivity (supervision, failover, network change handling).
3. Correct routing.
4. Crash recovery (every Windows state change is journaled and reversible).
5. Smart server selection.
6. Performance.
7. UI and animations.

UI polish never weakens items 1–4.

## 2. Process model: privilege separation

```
┌─────────────────────────────────────┐        ┌────────────────────────────────────────────┐
│ JackiVpn.Desktop.exe (medium IL)    │        │ JackiVpn.Service.exe (LocalSystem service) │
│  WPF UI · MVVM · tray · toasts      │  pipe  │  IPC server (ACL + client image check)     │
│  subscriptions · parsing · SQLite   │◀──────▶│  TUN sessions (sing-box + Wintun)          │
│  health probes (user-level cores)   │  JSON  │  WFP kill switch · WinHTTP proxy           │
│  System Proxy mode (WinINET, HKCU)  │ frames │  service network-state journal             │
│  user network-state journal         │        │  core supervision / crash detection        │
└─────────────────────────────────────┘        └────────────────────────────────────────────┘
          │ user-level sing-box/Xray                     │ elevated sing-box (TUN) [+ Xray backend]
          ▼ (proxy mode, health tests)                   ▼
```

Why the split looks like this:

| Operation | Needs admin? | Runs in |
|---|---|---|
| WinINET system proxy (per user, HKCU) | No | Desktop. The service runs as SYSTEM and cannot reach the user's HKCU correctly. |
| Proxy-mode core (loopback mixed inbound) | No | Desktop. Least privilege, and proxy mode works even without the service. |
| Health probes (batch cores on loopback) | No | Desktop |
| TUN adapter, routes, `strict_route` WFP rules | Yes | Service |
| Kill switch (WFP filters) | Yes | Service |
| WinHTTP machine proxy (optional) | Yes | Service |
| Repair Network (machine-wide parts) | Yes | Service; the user-level parts run in Desktop |

The service **never** accepts a raw core configuration. It accepts a validated,
normalized `TunnelStartRequest` (profile model, DNS/routing/IPv6/kill-switch
options) and generates the core configuration itself, with service-owned paths.
A raw config could otherwise abuse core features that write files as SYSTEM
(`log.output`, `cache_file.path`, `certificate_path`, …): see SECURITY.md §3.

If the service is missing or outdated, Desktop offers **Install/Repair service**.
That relaunches `JackiVpn.Service.exe install` through UAC (`runas`). Installing
registers the service, writes the trusted client image path and SHA-256 into
`HKLM\SOFTWARE\JackiVpn\Service`, and sets SCM recovery actions.

## 3. Solution layout and dependency graph

```
src/
  JackiVpn.Domain          pure models, enums, state machine rules, abstractions (no Windows APIs)
  JackiVpn.Security        redaction, DPAPI secret store, URL safety (SSRF), hashing, safe paths
  JackiVpn.Subscriptions   parsers → ProxyProfile, providers (PasarGuard, Generic), safe fetcher
  JackiVpn.Routing         rule model, validation, matcher, presets, rule-set manager
  JackiVpn.Cores           ICoreEngine, sing-box/Xray engines, capability negotiation,
                           config generation, validation, process host (job objects)
  JackiVpn.Networking      WinINET/WinHTTP proxy, WFP kill switch, journals, route/adapter
                           inspection, network change monitor, probes (DNS/TCP/TLS/HTTP)
  JackiVpn.Ipc             contracts, framing, pipe server/client, peer verification
  JackiVpn.Infrastructure  SQLite repositories, settings store, file logging, diagnostics export
  JackiVpn.Application     orchestration: connection state machine, sessions, smart selection,
                           health checks, subscription updates, notifications, failover
  JackiVpn.Service         Windows service host, TUN session manager, IPC handlers
  JackiVpn.Desktop         WPF app (MVVM, CommunityToolkit.Mvvm), tray, toasts
tests/
  JackiVpn.UnitTests       xUnit + FluentAssertions + NSubstitute + FsCheck
  JackiVpn.IntegrationTests  real cores on loopback, WireMock PasarGuard, pipes (no admin)
  JackiVpn.E2E             admin-only: TUN, DNS leak, kill switch, crash recovery, proxy restore
  JackiVpn.Benchmarks      BenchmarkDotNet
```

Dependencies only point downward, and none are circular:

```
Desktop ─┬─▶ Application ─┬─▶ Subscriptions ─▶ Security ─▶ Domain
Service ─┘                ├─▶ Cores ─▶ Routing ─▶ Domain
                          ├─▶ Networking ─▶ Security
                          └─▶ Ipc ─▶ Domain
Desktop ─▶ Infrastructure ─▶ Security, Domain
```

`Domain` references no WPF and no Win32. Windows-only code lives in `Security` (DPAPI),
`Networking`, `Cores` (job objects), `Ipc` (peer verification), `Service` and `Desktop`.

## 4. Core abstraction

```csharp
public interface ICoreEngine
{
    CoreKind Kind { get; }
    Task<CoreVersionInfo> DetectVersionAsync(CancellationToken ct);
    CoreCapabilities GetCapabilities(CoreVersionInfo version);          // version + build tags
    CapabilityReport Negotiate(ProxyProfile profile, SessionOptions options, CoreCapabilities caps);
    GeneratedCoreConfig GenerateConfig(CoreSessionPlan plan);            // only negotiated features
    Task<CoreValidationResult> ValidateAsync(GeneratedCoreConfig config, CancellationToken ct);
    CoreLaunchSpec BuildLaunchSpec(GeneratedCoreConfig config);
}
```

* Capabilities come from the **actual binary**: `sing-box version` gives the version
  and build tags (`with_utls`, `with_quic`, `with_wireguard`, `with_clash_api`, …).
  `xray version` gives the version. The core manifest SHA-256 is verified first.
* **Negotiation before generation.** Each feature a profile needs is checked
  against the capabilities. Unsupported features are never dropped silently.
  They produce `CapabilityIssue`s with severity:
  * `Blocking`: the profile cannot work without it, for example Hysteria2 on a
    build without QUIC. The profile is marked unsupported for that core.
  * `Degraded`: the profile works but loses the feature, for example TLS
    fragment on a core without it. The UI shows it explicitly, and the Auto
    selector prefers a core without the issue.
* **Auto core selection** (`CoreSelector`) scores each engine's report.
  sing-box is preferred, and required for TUN. Xray is chosen when a profile
  needs Xray-only features (XHTTP/SplitHTTP, mKCP, …). In TUN mode with an
  Xray-only profile, the plan becomes a **chain**: sing-box owns TUN, DNS and
  routing, and forwards proxied traffic to a loopback SOCKS inbound of Xray. The
  server IPs are pre-resolved and added to `route_exclude_address`, so Xray
  reaches the server outside the tunnel with no loop and fragmentation intact.
* Configs are passed to cores through **stdin** (`sing-box run -c stdin`,
  `xray run -c stdin:`), so credentials are never written to disk. If a core
  build does not support stdin, a random file in an ACL-restricted directory is
  used and wiped after exit.
* Processes run inside a **Job Object** with `KILL_ON_JOB_CLOSE`, so no core can
  outlive its host (no zombies). Arguments are passed with `ArgumentList`, never
  a concatenated string.

## 5. Subscriptions and parsing

* `SubscriptionFetcher`: manual redirect handling (max 5 hops, http/https
  only, no https→http downgrade, no hop from a public host to a
  private/loopback/link-local host), a `ConnectCallback` that re-checks the
  resolved IP (DNS rebinding), compression, timeouts, a response size limit,
  and ETag / Last-Modified conditional requests.
* `PasarGuardSubscriptionProvider`: HEAD metadata first (GET fallback), HTML
  detection (a page is never parsed as config), endpoint probing (`/links`,
  `/links_base64`, `/sing_box`, `/xray`, `/clash_meta`, `/clash`), and the
  `/info` JSON for status and quota. Details are in PASARGUARD.md.
* The parsing pipeline detects the format (URI list, Base64, sing-box/Xray
  full or single outbound JSON, Clash / Clash Meta YAML), enforces limits
  (size, JSON depth, line count), and normalizes to `ProxyProfile`. Unknown
  fields are preserved in `Extensions` (a JSON object per section).
* Updates are **transactional**: parse fully, and if the result is empty,
  broken or HTML, the old profiles stay untouched. Otherwise merge by stable
  fingerprint (protocol + endpoint + identity + transport), keep favorites and
  manual selection, mark `Added` / `Removed` / `Changed`, and commit in one
  SQLite transaction.

## 6. Connection state machine

```
Disconnected → Preparing → (Testing) → Connecting → Connected
Connected → Reconnecting | SwitchingServer | Disconnecting | Faulted
Reconnecting → Connected | SwitchingServer | Disconnecting | Faulted
SwitchingServer → Connected | Reconnecting | Disconnecting | Faulted
Disconnecting → Disconnected | Faulted
Faulted → Disconnecting | Disconnected | Recovering | Preparing
Disconnected → Recovering → Disconnected | Faulted          (startup with dirty journal)
```

The transition table lives in `Domain`, and illegal transitions throw. The
orchestrator serializes operations with an async lock. `Disconnect` first
cancels any in-flight connect, then takes the lock. Each session carries a
generation number, so events from stale sessions (an old core exiting, for
example) are ignored.

## 7. Network state journal and recovery

Every mutation of Windows state is written to a journal **before** it is
applied (write-ahead). Writes are atomic: a temp file, then `File.Replace`.

| Owner | Journal | Entries |
|---|---|---|
| Desktop | `%LOCALAPPDATA%\JackiVpn\state\network-journal.json` | WinINET snapshot per connection, user-level core PIDs |
| Service | `%ProgramData%\JackiVpn\service\state\network-journal.json` | WFP provider/sublayer, WinHTTP snapshot, TUN adapter name, core PIDs |

A clean shutdown deletes the journal. At startup, a leftover journal means the
previous session was dirty, so recovery runs before anything else. Recovery
restores snapshots, removes WFP objects by provider key, kills orphaned cores
it recorded (only after checking the image path), removes stale Wintun adapters
and routes on them, and flushes the DNS cache. **Repair Network** runs the same
code unconditionally.

By design, JackiVpn never changes adapter DNS settings or IPv6 bindings. DNS
and IPv6 leak protection happen inside the TUN plus WFP, so nothing is left to
un-change after a crash.

## 8. Smart selection

`HealthProbeService` runs multi-stage probes: DNS resolve, TCP connect, TLS
handshake with the profile's SNI and ALPN, core startup, then an HTTP request
through the proxy (TTFB and total), against at least two configurable probe
URLs. Probes run with bounded concurrency (default 4, max 6), per-node result
caching (TTL), and full cancellation. Nodes are tested in **batches through one
core process**: one loopback inbound per node, routed to its outbound.

`HealthHistory` keeps a rolling window per node. Tukey-fence outlier removal
feeds the median, jitter, success rate (Laplace-smoothed), handshake median and
stability. `ScoreCalculator` has configurable weights plus penalties for recent
timeouts and disconnects. Labels (Excellent … Failed) are display only.
Decisions always use the numbers.

`FailoverPolicy` adds hysteresis. A switch happens only after N consecutive
failures, or when a candidate beats the current node by a relative margin
after a minimum dwell time. Recently abandoned nodes get a cooldown.

**Jacki Smart Connect** is enabled only for profiles whose ownership is
verified by `ProfileOwnershipVerifier`: a provider domain allow-list, a
configurable response header such as `X-Jacki-Profile: v1`, or an
ECDSA-P256-signed provider manifest. It adds proactive (hysteresis-guarded)
switching, an optional MUX benchmark, and a throughput sample.

## 9. Connection modes, DNS, IPv6, kill switch

These are summarized here and specified in NETWORKING.md.

* **System Proxy**: a Desktop core with a loopback `mixed` inbound; WinINET set
  through `INTERNET_OPTION_PER_CONNECTION_OPTION` (LAN plus RAS entries). The
  previous values are backed up exactly and restored. WinHTTP changes only on
  opt-in, through the service.
* **TUN**: sing-box `tun` with `auto_route`, `strict_route`, and
  `route.auto_detect_interface`. The v4 **and** v6 TUN addresses always capture
  all traffic. If the tunnel lacks IPv6, v6 destinations are rejected inside
  sing-box and DNS runs `ipv4_only`, so nothing leaks and no adapter setting
  changes.
* **DNS** (default Auto Secure): DoH through the tunnel for remote names, a
  separate bootstrap resolver used **only** for the proxy server's own
  hostname, a direct resolver only for direct-routed domains, and `hijack-dns`
  for port 53. FakeIP is off unless explicitly enabled.
* **Kill switch**: WFP provider and sublayer with persistent-per-boot filters,
  so it survives core **and** service crashes (fail closed). It permits
  loopback, the cores' app IDs, the TUN addresses, DHCP/NDP, and LAN only when
  Allow LAN is on. Everything else is blocked. Filters are tagged with the
  JackiVpn provider GUID and removed on disconnect, recovery or repair.

## 10. Security summary

These are summarized here and specified in SECURITY.md: DPAPI-protected secrets
(subscription URLs, credentials), mandatory log redaction (URL tokens, UUIDs,
passwords, keys, `Authorization`), redacted diagnostics, core hash
verification, SSRF controls, parser limits, named-pipe ACL, client image
verification, server owner verification, and telemetry off.

## 11. Renaming the product

1. Change `ProductName`, `ProductDisplayName` and `ProductCompany` in `Directory.Build.props`.
2. Change the constants in `src/JackiVpn.Domain/ProductInfo.cs`: service name,
   pipe name, data folder, Smart Connect brand, and provider header default.
3. Optionally rename the assemblies and namespaces (`JackiVpn.*`). Nothing else
   hard-codes the brand.
