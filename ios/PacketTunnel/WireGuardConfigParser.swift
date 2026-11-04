import Foundation
import Network
import WireGuardKit

enum WireGuardConfigError: LocalizedError {
    case missingSection(String)
    case missingValue(section: String, key: String)
    case invalidValue(section: String, key: String, value: String)
    case duplicatePeerKey
    case noPeers

    var errorDescription: String? {
        switch self {
        case .missingSection(let section):
            return "Missing [\(section)] section."
        case .missingValue(let section, let key):
            return "Missing \(key) in [\(section)]."
        case .invalidValue(let section, let key, let value):
            return "Invalid \(key) in [\(section)]: \(value)"
        case .duplicatePeerKey:
            return "The configuration contains a duplicate peer public key."
        case .noPeers:
            return "The configuration has no [Peer] section."
        }
    }
}

/// Minimal, strict parser for standard WireGuard wg-quick configuration.
/// It intentionally does not execute desktop-only hooks such as PostUp/PostDown.
enum WireGuardConfigParser {
    private struct Section {
        let name: String
        var values: [String: String] = [:]
    }

    static func parse(_ text: String, name: String? = nil) throws -> TunnelConfiguration {
        let sections = try parseSections(text)
        guard let interface = sections.first(where: { $0.name == "Interface" }) else {
            throw WireGuardConfigError.missingSection("Interface")
        }
        let peerSections = sections.filter { $0.name == "Peer" }
        guard !peerSections.isEmpty else { throw WireGuardConfigError.noPeers }

        let privateKeyText = try required("PrivateKey", in: interface)
        guard let privateKey = PrivateKey(base64Key: privateKeyText) else {
            throw WireGuardConfigError.invalidValue(section: "Interface", key: "PrivateKey", value: "<redacted>")
        }

        var interfaceConfig = InterfaceConfiguration(privateKey: privateKey)
        interfaceConfig.addresses = try commaValues("Address", in: interface).map { value in
            guard let address = IPAddressRange(from: value) else {
                throw WireGuardConfigError.invalidValue(section: "Interface", key: "Address", value: value)
            }
            return address
        }
        guard !interfaceConfig.addresses.isEmpty else {
            throw WireGuardConfigError.missingValue(section: "Interface", key: "Address")
        }

        if let value = interface.values["ListenPort"] {
            guard let port = UInt16(value), port > 0 else {
                throw WireGuardConfigError.invalidValue(section: "Interface", key: "ListenPort", value: value)
            }
            interfaceConfig.listenPort = port
        }
        if let value = interface.values["MTU"] {
            guard let mtu = UInt16(value), mtu >= 576 else {
                throw WireGuardConfigError.invalidValue(section: "Interface", key: "MTU", value: value)
            }
            interfaceConfig.mtu = mtu
        }
        interfaceConfig.dns = try commaValues("DNS", in: interface).map { value in
            guard let dns = DNSServer(from: value) else {
                throw WireGuardConfigError.invalidValue(section: "Interface", key: "DNS", value: value)
            }
            return dns
        }

        var peers: [PeerConfiguration] = []
        var seenKeys = Set<String>()
        for section in peerSections {
            let publicKeyText = try required("PublicKey", in: section)
            guard let publicKey = PublicKey(base64Key: publicKeyText) else {
                throw WireGuardConfigError.invalidValue(section: "Peer", key: "PublicKey", value: "<invalid key>")
            }
            guard seenKeys.insert(publicKey.base64Key).inserted else {
                throw WireGuardConfigError.duplicatePeerKey
            }
            var peer = PeerConfiguration(publicKey: publicKey)
            if let value = section.values["PresharedKey"] {
                guard let key = PreSharedKey(base64Key: value) else {
                    throw WireGuardConfigError.invalidValue(section: "Peer", key: "PresharedKey", value: "<invalid key>")
                }
                peer.preSharedKey = key
            }
            peer.allowedIPs = try commaValues("AllowedIPs", in: section).map { value in
                guard let range = IPAddressRange(from: value) else {
                    throw WireGuardConfigError.invalidValue(section: "Peer", key: "AllowedIPs", value: value)
                }
                return range
            }
            guard !peer.allowedIPs.isEmpty else {
                throw WireGuardConfigError.missingValue(section: "Peer", key: "AllowedIPs")
            }
            if let value = section.values["Endpoint"] {
                guard let endpoint = Endpoint(from: value) else {
                    throw WireGuardConfigError.invalidValue(section: "Peer", key: "Endpoint", value: value)
                }
                peer.endpoint = endpoint
            }
            if let value = section.values["PersistentKeepalive"] {
                guard let keepalive = UInt16(value) else {
                    throw WireGuardConfigError.invalidValue(section: "Peer", key: "PersistentKeepalive", value: value)
                }
                peer.persistentKeepAlive = keepalive
            }
            peers.append(peer)
        }
        return TunnelConfiguration(name: name, interface: interfaceConfig, peers: peers)
    }

    private static func parseSections(_ text: String) throws -> [Section] {
        var sections: [Section] = []
        var current: Section?
        for (lineNumber, rawLine) in text.components(separatedBy: .newlines).enumerated() {
            let line = rawLine.trimmingCharacters(in: .whitespacesAndNewlines)
            if line.isEmpty || line.hasPrefix("#") || line.hasPrefix(";") { continue }
            if line.hasPrefix("[") && line.hasSuffix("]") {
                if let current { sections.append(current) }
                let name = String(line.dropFirst().dropLast()).trimmingCharacters(in: .whitespaces)
                guard name == "Interface" || name == "Peer" else {
                    throw WireGuardConfigError.invalidValue(section: "line \(lineNumber + 1)", key: "section", value: name)
                }
                current = Section(name: name)
                continue
            }
            guard var section = current, let equals = line.firstIndex(of: "=") else {
                throw WireGuardConfigError.invalidValue(section: "line \(lineNumber + 1)", key: "syntax", value: line)
            }
            let key = line[..<equals].trimmingCharacters(in: .whitespaces)
            let value = line[line.index(after: equals)...].trimmingCharacters(in: .whitespaces)
            guard !key.isEmpty, !value.isEmpty else {
                throw WireGuardConfigError.invalidValue(section: section.name, key: key, value: value)
            }
            let normalizedKey = key.lowercased()
            let canonical = [
                "privatekey": "PrivateKey", "address": "Address", "listenport": "ListenPort",
                "mtu": "MTU", "dns": "DNS", "publickey": "PublicKey",
                "presharedkey": "PresharedKey", "allowedips": "AllowedIPs",
                "endpoint": "Endpoint", "persistentkeepalive": "PersistentKeepalive"
            ][normalizedKey]
            guard let canonical else {
                throw WireGuardConfigError.invalidValue(section: section.name, key: key, value: "unsupported directive")
            }
            if let previous = section.values[canonical] {
                if canonical == "Address" || canonical == "DNS" || canonical == "AllowedIPs" {
                    section.values[canonical] = previous + "," + value
                } else {
                    throw WireGuardConfigError.invalidValue(section: section.name, key: key, value: "duplicate directive")
                }
            } else {
                section.values[canonical] = value
            }
            current = section
        }
        if let current { sections.append(current) }
        return sections
    }

    private static func required(_ key: String, in section: Section) throws -> String {
        guard let value = section.values[key], !value.isEmpty else {
            throw WireGuardConfigError.missingValue(section: section.name, key: key)
        }
        return value
    }

    private static func commaValues(_ key: String, in section: Section) throws -> [String] {
        guard let raw = section.values[key] else { return [] }
        return raw.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }
}
