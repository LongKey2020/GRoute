import Darwin
import Foundation
import NetworkExtension
import LibXray

final class XrayTunnelProvider: NEPacketTunnelProvider {
    private let invokeQueue = DispatchQueue(label: "net.gozar.lroute.xray.invoke")
    private var isRunning = false

    override func startTunnel(
        options: [String: NSObject]?,
        completionHandler: @escaping (Error?) -> Void
    ) {
        guard
            let tunnelProtocol = protocolConfiguration as? NETunnelProviderProtocol,
            let providerConfiguration = tunnelProtocol.providerConfiguration,
            let suppliedJSON = providerConfiguration["xrayJson"] as? String,
            !suppliedJSON.isEmpty
        else {
            completionHandler(Self.failure("No Xray JSON profile was supplied."))
            return
        }

        guard let tunFD = Self.findUtunFileDescriptor() else {
            completionHandler(Self.failure("Could not locate the NetworkExtension utun descriptor."))
            return
        }

        do {
            var config = try JSONSerialization.jsonObject(with: Data(suppliedJSON.utf8)) as? [String: Any] ?? [:]
            var env = config["env"] as? [String: Any] ?? [:]
            var xrayEnv = env["xray"] as? [String: Any] ?? [:]
            var tunEnv = xrayEnv["tun"] as? [String: Any] ?? [:]
            tunEnv["fd"] = String(tunFD)
            xrayEnv["tun"] = tunEnv
            env["xray"] = xrayEnv
            config["env"] = env

            let configData = try JSONSerialization.data(withJSONObject: config, options: [.sortedKeys])
            guard let configJSON = String(data: configData, encoding: .utf8) else {
                completionHandler(Self.failure("Could not encode Xray configuration."))
                return
            }
            let request = try Self.request(method: "runXray", payload: ["xrayJson": configJSON])
            invokeQueue.async { [weak self] in
                let response = GoLibXrayInvoke(request)
                let result = Self.parseResponse(response)
                DispatchQueue.main.async {
                    guard let self else {
                        completionHandler(Self.failure("Xray tunnel provider was released."))
                        return
                    }
                    if result.success {
                        self.isRunning = true
                        completionHandler(nil)
                    } else {
                        completionHandler(Self.failure(result.error ?? "Xray failed to start."))
                    }
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
        guard isRunning else {
            completionHandler()
            return
        }
        invokeQueue.async { [weak self] in
            if let request = try? Self.request(method: "stopXray", payload: [:]) {
                _ = GoLibXrayInvoke(request)
            }
            DispatchQueue.main.async {
                self?.isRunning = false
                completionHandler()
            }
        }
    }

    private static func request(method: String, payload: [String: Any]) throws -> String {
        let envelope: [String: Any] = ["apiVersion": 3, "method": method, "payload": payload]
        let data = try JSONSerialization.data(withJSONObject: envelope, options: [.sortedKeys])
        guard let value = String(data: data, encoding: .utf8) else {
            throw failure("Could not encode libXray request.")
        }
        return value
    }

    private static func parseResponse(_ response: String) -> (success: Bool, error: String?) {
        guard let data = response.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return (false, "libXray returned an invalid response.")
        }
        let success = object["success"] as? Bool ?? false
        let message = object["error"] as? String
        return (success, message?.isEmpty == false ? message : nil)
    }

    private static func findUtunFileDescriptor() -> Int32? {
        let maximum = getdtablesize()
        guard maximum > 0 else { return nil }
        for descriptor in 0..<maximum {
            var name = [CChar](repeating: 0, count: Int(IFNAMSIZ))
            var length = socklen_t(name.count)
            let result = name.withUnsafeMutableBufferPointer { buffer in
                getsockopt(descriptor, 2, 2, buffer.baseAddress, &length)
            }
            if result == 0, String(cString: name).hasPrefix("utun") {
                return descriptor
            }
        }
        return nil
    }

    private static func failure(_ message: String) -> NSError {
        NSError(
            domain: "net.gozar.lroute.XrayTunnel",
            code: 2001,
            userInfo: [NSLocalizedDescriptionKey: message]
        )
    }
}
