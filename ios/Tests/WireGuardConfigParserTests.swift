import XCTest
import Network
import WireGuardKit

final class WireGuardConfigParserTests: XCTestCase {
    private let validConfig = """
    [Interface]
    PrivateKey = AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=
    Address = 10.66.0.2/32
    DNS = 1.1.1.1, 2606:4700:4700::1111
    MTU = 1280

    [Peer]
    PublicKey = AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=
    Endpoint = 203.0.113.10:51820
    AllowedIPs = 0.0.0.0/0, ::/0
    PersistentKeepalive = 25
    """

    func testParsesStandardWireGuardConfig() throws {
        let config = try WireGuardConfigParser.parse(validConfig, name: "CI Test")
        XCTAssertEqual(config.name, "CI Test")
        XCTAssertEqual(config.interface.addresses.count, 1)
        XCTAssertEqual(config.interface.dns.count, 2)
        XCTAssertEqual(config.interface.mtu, 1280)
        XCTAssertEqual(config.peers.count, 1)
        XCTAssertEqual(config.peers[0].allowedIPs.count, 2)
        XCTAssertEqual(config.peers[0].persistentKeepAlive, 25)
        XCTAssertEqual(config.peers[0].endpoint?.stringRepresentation, "203.0.113.10:51820")
    }

    func testRejectsInvalidPrivateKey() {
        let config = validConfig.replacingOccurrences(
            of: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            with: "not-a-wireguard-key"
        )
        XCTAssertThrowsError(try WireGuardConfigParser.parse(config))
    }

    func testRejectsMissingAllowedIPs() {
        let config = validConfig.replacingOccurrences(
            of: "AllowedIPs = 0.0.0.0/0, ::/0",
            with: "PersistentKeepalive = 25"
        )
        XCTAssertThrowsError(try WireGuardConfigParser.parse(config))
    }

    func testRejectsUnsupportedDirectiveInsteadOfIgnoringIt() {
        let config = validConfig.replacingOccurrences(
            of: "MTU = 1280",
            with: "MTU = 1280\nPostUp = echo unsafe"
        )
        XCTAssertThrowsError(try WireGuardConfigParser.parse(config))
    }
}
