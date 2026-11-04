# LRoute Windows — Clean Rebuild Plan

This branch is a clean Windows rewrite. Do not extend the previous experimental WPF screen or copy its placeholder controls.

## Product contract

- Product: LRoute for Windows.
- Visual direction: the approved NetBird-inspired two-panel dashboard, branded with the LRoute bird mark and LRoute wordmark; charcoal/near-black surfaces with orange accent.
- Keep normal Windows window chrome and system controls (minimize, maximize/restore, close).
- Port the Android APK's real features and workflows to Windows, not just its labels or visual placeholders.
- Explicit exclusions: WireGuard (all variants) and QR scanning.
- No forced update prompt, no splash logo, no third-party product branding.

## Android source-of-truth inventory

Use the existing Android implementation under `app/src/main/java/net/gozar/app/` as the behavioral reference.

### Core VPN and profiles
- Config parsing and profile model: `ConfigParser.kt`, `ProxyConfig.kt`
- Runtime config generation: `ConfigBuilder.kt`
- Tunnel lifecycle, auto-select, per-app routing and kill-switch behavior: `GozarVpnService.kt`, `MainActivity.kt`
- Profile CRUD, import/export, local/subscription profiles and backup/restore: `ConfigStore.kt`, `ConfigFile.kt`, `ConfigShare.kt`
- Subscription fetch, refresh, quota/expiry metadata: `SubscriptionFetcher.kt`, `Subscriptionrefresher.kt`, `Subscription.kt`
- Supported proxy transports/protocols must be confirmed from the Android parser/builder and ported without WireGuard branches. Expected Xray-compatible families include VLESS, VMess, Trojan, Shadowsocks, SOCKS/HTTP, Hysteria2 and supported IKEv2 paths where the Android implementation provides them.

### Network, diagnostics and utilities
- Config debugger: `Configdebug.kt`
- Config/transport probe and tunnel health: `Configprobe.kt`, `PanelProbe.kt`
- Server probe and IP intelligence: `ServerProbe.kt`, `Ipintelligence.kt`
- Network monitor: `Netmonitor.kt`
- Latency and download speed test: `SpeedTest.kt`
- Stability test, logs and diagnostics: `StabilityTest.kt`, `MainActivity.kt`
- DNS, split routing, ad-block, fake DNS, encrypted DNS, sniffing, fragmentation, mux, kill switch and block-when-off preferences: `ConfigStore.kt`, `ConfigBuilder.kt`

### Remote access and optional services
- SSH shell and session management: `SshShell.kt`, `sshmanager.kt`, `sshhost.kt`
- SFTP browser: `SftpBrowser.kt`
- Warp, Tor, Windscribe and Aether integrations: `Warp.kt`, `Torcontroller.kt`, `Windscribefetcher.kt`, `Aethercontroller.kt`
- Settings, themes, language, usage and backup/restore: `ConfigStore.kt`, `MainActivity.kt`

## Windows architecture target

Separate UI, application state, persistence, protocol/config domain, and runtime adapters. Avoid putting business logic in code-behind.

- `Presentation/`: dashboard, profiles/resources views, settings, tools, SSH/SFTP and diagnostics views.
- `Application/`: commands, view models, connection orchestration and navigation.
- `Domain/`: profile models, protocol capabilities, settings and validation contracts.
- `Infrastructure/Persistence/`: profiles, subscriptions, secrets, settings and backup storage.
- `Infrastructure/Engines/`: Xray process/TUN adapter and separately isolated IKEv2 adapter if required by Android parity.
- `Infrastructure/Networking/`: DNS, route/split-tunnel, probes, speed test and IP intelligence.
- `Infrastructure/RemoteAccess/`: SSH and SFTP.

## Delivery gates

1. Inventory and map every Android feature to a Windows screen/service/API.
2. Build the new branded shell and navigation with native window controls.
3. Implement profile persistence, import/export, parser and config builder.
4. Implement Xray engine lifecycle and accurate connection states.
5. Port DNS/split-routing and network protection features.
6. Port SSH/SFTP, subscription management and diagnostic tools.
7. Test each workflow on a real Windows machine; never label a placeholder as complete.

A feature is complete only when its UI action, application logic, persistence (where applicable), error handling and Windows runtime behavior have all been tested.
