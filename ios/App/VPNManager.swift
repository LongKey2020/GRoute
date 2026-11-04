import Foundation
import NetworkExtension
import Combine

@MainActor
final class VPNManager: ObservableObject {
    @Published private(set) var statusText = "NOT CONFIGURED"
    @Published private(set) var isConnected = false
    @Published private(set) var isConfigured = false
    @Published var errorMessage: String?

    private var manager: NETunnelProviderManager?
    private var statusObserver: NSObjectProtocol?

    init() {
        statusObserver = NotificationCenter.default.addObserver(
            forName: .NEVPNStatusDidChange,
            object: nil,
            queue: .main
        ) { [weak self] notification in
            Task { @MainActor in
                guard let connection = notification.object as? NEVPNConnection else { return }
                self?.apply(status: connection.status)
            }
        }
        Task { await reload() }
    }

    deinit {
        if let statusObserver {
            NotificationCenter.default.removeObserver(statusObserver)
        }
    }

    func reload() async {
        do {
            let managers = try await NETunnelProviderManager.loadAllFromPreferences()
            manager = managers.first {
                ($0.protocolConfiguration as? NETunnelProviderProtocol)?
                    .providerBundleIdentifier == "net.gozar.lroute.PacketTunnel"
            }
            guard let manager else {
                isConfigured = false
                statusText = "IMPORT A PROFILE"
                isConnected = false
                return
            }
            isConfigured = manager.isEnabled
            apply(status: manager.connection.status)
        } catch {
            errorMessage = "Could not load VPN settings: \(error.localizedDescription)"
        }
    }

    func importProfile(named name: String, config: String) async {
        guard config.contains("[Interface]"), config.contains("[Peer]"),
              config.contains("PrivateKey"), config.contains("PublicKey") else {
            errorMessage = "This does not look like a valid WireGuard .conf file."
            return
        }
        do {
            let tunnelManager = manager ?? NETunnelProviderManager()
            let tunnelProtocol = NETunnelProviderProtocol()
            tunnelProtocol.providerBundleIdentifier = "net.gozar.lroute.PacketTunnel"
            tunnelProtocol.serverAddress = name
            tunnelProtocol.providerConfiguration = [
                "wgQuickConfig": config,
                "profileName": name
            ]
            tunnelManager.protocolConfiguration = tunnelProtocol
            tunnelManager.localizedDescription = "LRoute · \(name)"
            tunnelManager.isEnabled = true
            try await tunnelManager.saveToPreferences()
            try await tunnelManager.loadFromPreferences()
            manager = tunnelManager
            isConfigured = true
            apply(status: tunnelManager.connection.status)
            errorMessage = nil
        } catch {
            errorMessage = "Could not save VPN profile: \(error.localizedDescription)"
        }
    }

    func connect() async {
        await reload()
        guard let manager, manager.isEnabled else {
            errorMessage = "Import a WireGuard profile first."
            return
        }
        do {
            try manager.connection.startVPNTunnel()
            statusText = "CONNECTING"
        } catch {
            errorMessage = "Could not start VPN: \(error.localizedDescription)"
        }
    }

    func disconnect() {
        manager?.connection.stopVPNTunnel()
        statusText = "DISCONNECTING"
    }

    private func apply(status: NEVPNStatus) {
        switch status {
        case .connected:
            statusText = "CONNECTED"
            isConnected = true
        case .connecting, .reasserting:
            statusText = "CONNECTING"
            isConnected = false
        case .disconnecting:
            statusText = "DISCONNECTING"
            isConnected = false
        case .disconnected:
            statusText = isConfigured ? "DISCONNECTED" : "IMPORT A PROFILE"
            isConnected = false
        case .invalid:
            statusText = "NOT CONFIGURED"
            isConnected = false
        @unknown default:
            statusText = "UNKNOWN"
            isConnected = false
        }
    }
}
