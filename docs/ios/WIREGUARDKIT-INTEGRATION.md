# WireGuard Packet Tunnel Integration

The Packet Tunnel extension now consumes a WireGuard quick-config string from
NETunnelProviderProtocol.providerConfiguration["wgQuickConfig"], parses the
supported Interface/Peer directives, and starts/stops the official WireGuardAdapter.

Supported directives:
- Interface: PrivateKey, Address, DNS, ListenPort, MTU
- Peer: PublicKey, PresharedKey, AllowedIPs, Endpoint, PersistentKeepalive

Unsupported desktop-only directives (including PostUp/PostDown, Table, SaveConfig)
are rejected rather than silently ignored. Private and preshared keys are never logged.

This commit integrates the engine lifecycle in the extension. A companion app-side
profile import and NETunnelProviderManager setup is still required to populate
providerConfiguration and request the user's VPN permission. A real-device handshake
test is still required; simulator compilation is not a VPN connectivity test.
