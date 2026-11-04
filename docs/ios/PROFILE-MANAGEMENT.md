# LRoute iOS — WireGuard profile management

The app supports multiple standard WireGuard profiles through separate
NETunnelProviderManager preferences. Each imported profile receives a UUID stored as
non-secret metadata in providerConfiguration. The selected profile ID is stored in
UserDefaults; it contains no VPN credentials.

The WireGuard configuration, including its private key, is persisted in the
NetworkExtension VPN preferences providerConfiguration and passed to the Packet Tunnel
extension when iOS starts that tunnel. The app does not write raw .conf files or private
keys to its own Documents/Caches directory. Configuration values must never be logged.

This is OS-managed VPN preference storage, not an app-controlled Keychain implementation.
A shared Keychain access group requires matching signing entitlements for both app and
extension; do not claim Keychain protection until implemented and provisioned. Treat
device VPN preferences and imported configuration files as sensitive.

Features: import multiple standard WireGuard profiles, select, connect/disconnect,
delete, and reload profile state. AmneziaWG-specific directives are not supported.
Simulator CI validates compilation and parser tests only. A physical iPhone handshake
requires an eligible provisioning profile with the Packet Tunnel entitlement.
