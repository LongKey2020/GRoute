import Foundation
import NetworkExtension
import Combine

struct VPNProfile: Identifiable, Equatable {
    let id: String
    let name: String
    let isActive: Bool
    let status: NEVPNStatus
}

@MainActor
final class VPNManager: ObservableObject {
    @Published private(set) var statusText = "NOT CONFIGURED"
    @Published private(set) var isConnected = false
    @Published private(set) var isConfigured = false
    @Published private(set) var profiles: [VPNProfile] = []
    @Published private(set) var selectedProfileID: String?
    @Published var errorMessage: String?

    private var managers: [String: NETunnelProviderManager] = [:]
    private var statusObserver: NSObjectProtocol?
    private let selectedProfileKey = "LRoute.SelectedProfileID"
    private let providerID = "net.gozar.lroute.PacketTunnel"

    init() {
        selectedProfileID = UserDefaults.standard.string(forKey: selectedProfileKey)
        statusObserver = NotificationCenter.default.addObserver(forName: .NEVPNStatusDidChange, object: nil, queue: .main) { [weak self] notification in
            Task { @MainActor in
                guard let connection = notification.object as? NEVPNConnection else { return }
                self?.refreshProfileStatuses()
                self?.apply(status: connection.status)
            }
        }
        Task { await reload() }
    }

    deinit {
        if let statusObserver { NotificationCenter.default.removeObserver(statusObserver) }
    }

    var selectedProfileName: String {
        profiles.first(where: { $0.id == selectedProfileID })?.name ?? "No profile selected"
    }

    func reload() async {
        do {
            let saved = try await NETunnelProviderManager.loadAllFromPreferences()
            var updated: [String: NETunnelProviderManager] = [:]
            var items: [VPNProfile] = []
            for item in saved {
                guard let tunnel = item.protocolConfiguration as? NETunnelProviderProtocol,
                      tunnel.providerBundleIdentifier == providerID else { continue }
                let config = tunnel.providerConfiguration ?? [:]
                let id = config["profileID"] as? String ?? item.localizedDescription ?? UUID().uuidString
                let name = config["profileName"] as? String ?? item.localizedDescription ?? "WireGuard"
                updated[id] = item
                items.append(VPNProfile(id: id, name: name, isActive: item.connection.status == .connected || item.connection.status == .connecting, status: item.connection.status))
            }
            managers = updated
            profiles = items.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
            if selectedProfileID == nil || managers[selectedProfileID!] == nil {
                selectedProfileID = profiles.first?.id
            }
            persistSelection()
            refreshProfileStatuses()
            if let manager = selectedManager {
                isConfigured = manager.isEnabled
                apply(status: manager.connection.status)
            } else {
                isConfigured = false
                isConnected = false
                statusText = "IMPORT A PROFILE"
            }
        } catch {
            errorMessage = "Could not load VPN settings: \(error.localizedDescription)"
        }
    }

    func selectProfile(_ id: String) {
        guard managers[id] != nil else { return }
        selectedProfileID = id
        persistSelection()
        if let manager = selectedManager {
            isConfigured = manager.isEnabled
            apply(status: manager.connection.status)
        }
    }

    func importProfile(named name: String, config: String) async {
        let trimmedName = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedName.isEmpty, config.contains("[Interface]"), config.contains("[Peer]"),
              config.contains("PrivateKey"), config.contains("PublicKey") else {
            errorMessage = "This does not look like a valid WireGuard .conf file."
            return
        }
        do {
            let id = UUID().uuidString
            let tunnelManager = NETunnelProviderManager()
            let tunnelProtocol = NETunnelProviderProtocol()
            tunnelProtocol.providerBundleIdentifier = providerID
            tunnelProtocol.serverAddress = trimmedName
            tunnelProtocol.providerConfiguration = ["wgQuickConfig": config, "profileName": trimmedName, "profileID": id]
            tunnelManager.protocolConfiguration = tunnelProtocol
            tunnelManager.localizedDescription = "LRoute · \(trimmedName)"
            tunnelManager.isEnabled = true
            try await tunnelManager.saveToPreferences()
            try await tunnelManager.loadFromPreferences()
            managers[id] = tunnelManager
            selectedProfileID = id
            persistSelection()
            await reload()
            errorMessage = nil
        } catch {
            errorMessage = "Could not save VPN profile: \(error.localizedDescription)"
        }
    }

    func deleteProfile(_ id: String) async {
        guard let target = managers[id] else { return }
        if target.connection.status == .connected || target.connection.status == .connecting {
            target.connection.stopVPNTunnel()
        }
        do {
            try await target.removeFromPreferences()
            managers.removeValue(forKey: id)
            if selectedProfileID == id { selectedProfileID = nil }
            await reload()
        } catch {
            errorMessage = "Could not delete profile: \(error.localizedDescription)"
        }
    }

    func connect() async {
        await reload()
        guard let manager = selectedManager, manager.isEnabled else {
            errorMessage = "Select or import a WireGuard profile first."
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
        selectedManager?.connection.stopVPNTunnel()
        statusText = "DISCONNECTING"
    }

    private var selectedManager: NETunnelProviderManager? {
        guard let id = selectedProfileID else { return nil }
        return managers[id]
    }

    private func persistSelection() {
        if let selectedProfileID { UserDefaults.standard.set(selectedProfileID, forKey: selectedProfileKey) }
        else { UserDefaults.standard.removeObject(forKey: selectedProfileKey) }
    }

    private func refreshProfileStatuses() {
        profiles = profiles.map { item in
            guard let manager = managers[item.id] else { return item }
            return VPNProfile(id: item.id, name: item.name, isActive: manager.connection.status == .connected || manager.connection.status == .connecting, status: manager.connection.status)
        }
    }

    private func apply(status: NEVPNStatus) {
        switch status {
        case .connected: statusText = "CONNECTED"; isConnected = true
        case .connecting, .reasserting: statusText = "CONNECTING"; isConnected = false
        case .disconnecting: statusText = "DISCONNECTING"; isConnected = false
        case .disconnected: statusText = isConfigured ? "DISCONNECTED" : "IMPORT A PROFILE"; isConnected = false
        case .invalid: statusText = "NOT CONFIGURED"; isConnected = false
        @unknown default: statusText = "UNKNOWN"; isConnected = false
        }
    }
}
