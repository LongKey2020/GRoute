# WireGuardKit Integration Notes

## Engine selection

LRoute uses the official WireGuard for Apple implementation:
- Swift package: https://git.zx2c4.com/wireguard-apple
- Swift product: `WireGuardKit`
- Native backend: `wireguard-go`, built as `libwg-go.a`

## Build pipeline

The iOS CI workflow:
1. Generates the Xcode project with XcodeGen.
2. Resolves the WireGuardKit Swift package.
3. Builds the Go backend for the iOS Simulator arm64 SDK.
4. Links `libwg-go.a` into the app and Packet Tunnel extension targets.

This is an integration build step, not proof of a working VPN connection. The Packet Tunnel provider still needs configuration parsing, WireGuardAdapter lifecycle integration, and real-device validation.

## Next implementation gate

- Parse a WireGuard `.conf` safely; reject malformed or incomplete keys, addresses, and endpoints.
- Persist profile material only in the app's protected container / Keychain; do not log private keys.
- Pass the selected profile to the Packet Tunnel extension through provider configuration.
- Start and stop `WireGuardAdapter` from `NEPacketTunnelProvider`.
- Verify handshake, DNS, IPv4/IPv6 routing, reconnect, and leak behavior on a physical iPhone.

## Licensing

WireGuardKit is MIT-licensed. Preserve its license and notices when distributing derived binaries or source.
