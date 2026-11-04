# LRoute for iOS — Architecture

Status: architecture baseline; no iOS app target or IPA exists yet.

## 1. Repository assessment

The current repository is an Android application (Kotlin + Jetpack Compose). It has no Xcode project, Swift sources, iOS target, or Apple signing configuration.

The Android app documents these protocols: VLESS, VMess, Trojan, Shadowsocks, Hysteria2, WireGuard, IKEv2, SOCKS, and HTTP. Documented transports/security include TCP, WebSocket, gRPC, HTTPUpgrade, XHTTP, mKCP, HTTP/2, REALITY, TLS, and uTLS.

The current Go module pins Xray-core and the Android app consumes a prebuilt Android AAR. That Android AAR cannot be reused by iOS.

The Android README also describes Tor, Aether/MASQUE, Cloudflare WARP, Windscribe import, subscriptions, config chaining, fragmentation, and diagnostics. These are follow-on feature work, not assumed to be automatically portable.

## 2. Product goals

- Product name: LRoute
- Visual identity: pure black background, white typography, restrained grey surfaces; white L icon on black.
- Native iOS experience built with SwiftUI.
- Preserve compatible config-link parsing and server-profile semantics where possible.
- Keep VPN traffic processing on-device; no LRoute relay or account backend is introduced.
- Build and test using GitHub Actions macOS runners because the maintainer has no local Mac.

## 3. Proposed repository layout

    ios/
      LRoute.xcodeproj
      App/
        LRouteApp.swift
        UI/
          Home/
          Servers/
          Settings/
          Components/
        Models/
          ServerProfile.swift
          ImportFormat.swift
        Services/
          ConfigImportService.swift
          ProfileStore.swift
          VPNCoordinator.swift
      PacketTunnel/
        PacketTunnelProvider.swift
        PacketFlowAdapter.swift
        Engine/
          TunnelEngine.swift
          XrayEngineAdapter.swift
          WireGuardEngineAdapter.swift
      Shared/
        ConfigModels/
        Logging/
      Resources/
        Assets.xcassets/
    .github/workflows/
      lroute-ios-ci.yml
    docs/ios/
      ARCHITECTURE.md

The containing app owns UI, profile management, import/export, and user consent. The Packet Tunnel extension owns the packet tunnel lifecycle and invokes a selected engine adapter. App and extension communicate through a narrowly scoped App Group container and NetworkExtension provider configuration; secrets must not be written to logs.

## 4. VPN architecture

### Xray family

Build a separate iOS-compatible native bridge from the pinned Xray-core Go module. Do not reuse app/libs/gozarcore.aar.

The bridge must expose a small stable API: validate config, start, stop, read state, and deliver logs with credential redaction. The Packet Tunnel extension connects NetworkExtension packet flow to an iOS-compatible packet/TUN adapter. This packet-flow integration is a proof-of-concept gate: the Android gomobile wrapper is not presumed to work unchanged on iOS.

Initial protocol targets:

- VLESS (including supported transports and REALITY/TLS options)
- VMess
- Trojan
- Shadowsocks
- Hysteria2
- SOCKS and HTTP proxy profiles

Each transport/security combination must be validated against the exact pinned Xray-core version and iOS bridge. Unsupported combinations must fail validation with a useful message, not silently downgrade.

### WireGuard

Use an iOS-native WireGuardKit / wireguard-apple integration behind WireGuardEngineAdapter, hosted by the Packet Tunnel extension. Do not convert WireGuard profiles into Xray profiles.

### IKEv2

Use Apple's native NetworkExtension IKEv2 implementation (NEVPNManager / NEVPNProtocolIKEv2) as a separate system-managed connection path. This is not the same lifecycle as the custom Packet Tunnel provider; the UI should present one consistent status model while the implementation remains separate.

### Explicit capability gap

The current documented Android protocol table does not list AmneziaWG as a supported protocol. It must not be advertised as available on iOS until a compatible iOS engine, licensing review, and real-device handshake test exist. Aether/MASQUE, Tor, Windscribe importer, chaining, and advanced diagnostics are later milestones requiring their own feasibility tests.

## 5. iOS platform constraints

- Packet tunnels require Apple's NetworkExtension capability/entitlement and correct provisioning. A successful simulator build does not prove that a real device can install or establish a VPN.
- General per-app VPN selection is not available to an ordinary consumer app in the same way as Android; it is generally associated with managed-device deployments.
- Split routing is constrained by NetworkExtension route and DNS APIs. Implement only route/domain policies supported by iOS and clearly label limitations.
- A consumer VPN app cannot promise an Android-style universal kill switch. Use supported on-demand behavior where available and describe its limits.
- Background execution is controlled by iOS; tunnel lifetime and reconnection behavior must be tested on physical devices.
- App Store distribution and direct device installation require Apple signing/provisioning. CI can build unsigned/simulator artifacts without the user's Apple credentials, but cannot produce a user-installable signed IPA without suitable signing assets and entitlements.

## 6. Shared configuration contract

Create a platform-neutral schema for server profiles:

- stable profile ID and display name
- protocol discriminator
- endpoint and port
- authentication material stored separately from display metadata
- transport and TLS/REALITY options
- DNS and routing preferences
- import source and schema version

Import parsers should accept only known link/file formats, validate required fields, and redact secrets from diagnostics. Android and iOS may have separate storage implementations but must share test fixtures for supported links. Existing Android encrypted .grt files must be reverse-engineered and documented before claiming cross-platform import compatibility.

## 7. Build and delivery without a local Mac

Use a GitHub Actions macos-15 runner with a pinned Xcode version:

1. Resolve Swift packages and validate the project.
2. Build the app and Packet Tunnel extension for the iOS Simulator.
3. Run Swift unit tests for config parsing, validation, and profile migration.
4. Archive a device build only when signing configuration is explicitly provided.
5. Export a signed IPA only when Apple team ID, signing certificate, provisioning profiles, and NetworkExtension entitlement are available as protected GitHub secrets.

Never commit certificates, provisioning profiles, private keys, Apple credentials, or VPN server secrets to the repository.

## 8. Milestones and acceptance gates

1. Foundation: Xcode project, app + Packet Tunnel targets, bundle IDs, App Group, entitlements, CI simulator build.
2. UI and profiles: LRoute black/white SwiftUI screens, profile list, QR/link/file import, secure storage.
3. WireGuard proof of concept: establish and tear down a real-device tunnel; verify routes and DNS.
4. Xray proof of concept: compile the iOS bridge, route packets through one VLESS test profile, verify REALITY/TLS and lifecycle behavior.
5. Protocol coverage: add VMess, Trojan, Shadowsocks, Hysteria2, SOCKS/HTTP, and transport/security combinations with automated config fixtures and real-device tests.
6. IKEv2: integrate native IKEv2 and normalize status/error reporting.
7. Advanced features: assess Aether/MASQUE, Tor, subscriptions, fragmentation, chaining, diagnostics, and limited split-DNS/routing one by one.
8. Release: signing, entitlement approval, privacy review, App Store/TestFlight or ad hoc distribution.

No milestone is complete solely because it compiles. VPN milestones require a physical-device connection test and traffic/DNS verification.

## 9. Definition of done for the first iOS milestone

- A clean macOS CI simulator build for both app and Packet Tunnel targets.
- Swift tests pass for config models and import validation.
- The app displays LRoute branding and can save/import a non-secret sample profile.
- No claim of live VPN support until a real-device tunnel handshake and traffic test pass.
