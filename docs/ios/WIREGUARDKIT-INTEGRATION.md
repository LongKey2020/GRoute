# WireGuardKit integration status

The app now imports a standard WireGuard .conf file using the system document picker,
performs a basic front-end format check, and stores it in NETunnelProviderProtocol
providerConfiguration for the Packet Tunnel extension. The extension performs strict
directive parsing and starts/stops WireGuardAdapter.

The app uses NETunnelProviderManager to save the VPN configuration and request the
system VPN authorization when the user initiates the connection.

Important:
- The private key is included in the NetworkExtension provider configuration. Treat
  the device and its VPN preferences as sensitive; never log or export this value.
- iOS Network Extension entitlement and a matching provisioning profile are required
  for a signed, installable device build.
- Simulator CI success validates compilation only. Actual VPN connection, DNS, routing,
  and handshake must be tested on a physical iPhone with a valid profile and entitlement.
- Standard WireGuard .conf only. AmneziaWG-specific fields are not supported by this engine.
