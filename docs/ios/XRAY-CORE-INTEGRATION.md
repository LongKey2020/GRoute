# LRoute iOS — Xray Core integration plan

## Engine isolation

WireGuardKit's Go backend and libXray both embed Go runtimes. Go does not support
loading independently built Go runtimes into the same process. Therefore LRoute
must not link WireGuardKit and libXray into one Packet Tunnel extension process.

Use two independent Packet Tunnel extensions:
- LRoutePacketTunnel: WireGuardKit only.
- LRouteXrayTunnel: libXray only.

Each extension runs in its own iOS extension process. The containing app selects
the provider bundle identifier based on the imported profile type. Only one VPN
tunnel is active at a time through NetworkExtension.

## Xray engine build

Use upstream XTLS/libXray rather than attempting to gomobile-bind Xray-core's
main package directly. The upstream build script produces LibXray.xcframework
for iOS device and simulator targets.

The CI job builds the upstream framework on macOS and uploads it as a temporary
artifact. The framework is not committed into this repository. Pin the upstream
release tag and Go toolchain to make builds reproducible.

## Runtime contract

libXray exposes Invoke(JSON) and CGoInvoke/CGoFree. The Xray extension will:
1. Receive a normalized Xray JSON config from the app.
2. Obtain the utun descriptor from the NetworkExtension process.
3. Set the Xray config root env.xray.tun.fd to that descriptor.
4. Call libXray runXray through the versioned Invoke API.
5. Call stopXray from stopTunnel and report failures without logging secrets.

The Xray TUN inbound must be configured for iOS NetworkExtension. Routing and
DNS must avoid routing Xray's own upstream sockets back into the TUN.

## Supported protocol targets

The first milestone is VLESS, VMess, Trojan and Shadowsocks via libXray's
share-link/config conversion and Xray-core outbounds. Hysteria2 and other
supported Xray outbounds can be added after the basic iOS tunnel lifecycle is
validated. WireGuard remains on its existing dedicated extension.

## Security and validation

Do not log raw share links, config JSON, UUIDs, passwords, private keys or
Reality credentials. CI framework compilation does not prove a device tunnel
handshake. A signed physical-device test still requires the Packet Tunnel
Network Extension entitlement in the provisioning profile.
