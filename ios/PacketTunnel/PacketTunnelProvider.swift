import NetworkExtension
import WireGuardKit

final class PacketTunnelProvider: NEPacketTunnelProvider {
    private var adapter: WireGuardAdapter?

    override func startTunnel(
        options: [String: NSObject]?,
        completionHandler: @escaping (Error?) -> Void
    ) {
        guard
            let tunnelProtocol = protocolConfiguration as? NETunnelProviderProtocol,
            let providerConfiguration = tunnelProtocol.providerConfiguration,
            let configText = providerConfiguration["wgQuickConfig"] as? String,
            !configText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        else {
            completionHandler(Self.error("No WireGuard profile was supplied to the tunnel."))
            return
        }

        do {
            let tunnelConfiguration = try WireGuardConfigParser.parse(
                configText,
                name: providerConfiguration["profileName"] as? String
            )
            let wireGuardAdapter = WireGuardAdapter(with: self) { level, message in
                // Never log configuration text, private keys, or peer secrets.
                NSLog("LRoute WireGuard [\(level)]: \(message)")
            }
            adapter = wireGuardAdapter
            wireGuardAdapter.start(tunnelConfiguration: tunnelConfiguration) { [weak self] error in
                if let error {
                    self?.adapter = nil
                    completionHandler(error)
                } else {
                    completionHandler(nil)
                }
            }
        } catch {
            completionHandler(error)
        }
    }

    override func stopTunnel(
        with reason: NEProviderStopReason,
        completionHandler: @escaping () -> Void
    ) {
        guard let adapter else {
            completionHandler()
            return
        }
        adapter.stop { [weak self] _ in
            self?.adapter = nil
            completionHandler()
        }
    }

    private static func error(_ message: String) -> NSError {
        NSError(
            domain: "net.gozar.lroute.PacketTunnel",
            code: 1002,
            userInfo: [NSLocalizedDescriptionKey: message]
        )
    }
}
