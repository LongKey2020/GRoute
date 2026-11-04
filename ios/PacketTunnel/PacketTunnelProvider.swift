import NetworkExtension

final class PacketTunnelProvider: NEPacketTunnelProvider {
    override func startTunnel(
        options: [String: NSObject]?,
        completionHandler: @escaping (Error?) -> Void
    ) {
        let error = NSError(
            domain: "net.gozar.lroute.PacketTunnel",
            code: 1001,
            userInfo: [NSLocalizedDescriptionKey:
                "VPN engine is not integrated in this development build."]
        )
        completionHandler(error)
    }

    override func stopTunnel(
        with reason: NEProviderStopReason,
        completionHandler: @escaping () -> Void
    ) {
        completionHandler()
    }
}
