import Foundation
import XCTest
@testable import IBOXCore

final class ParserTests: XCTestCase {
    func testVlessReality() throws {
        let p = try ShareLinkParser.parse(
            "vless://11111111-2222-3333-4444-555555555555@example.com:443?type=tcp&security=reality&pbk=PUBKEY&sid=ab12&sni=www.apple.com&flow=xtls-rprx-vision#My%20Server")
        XCTAssertEqual(p.proto, .vless)
        XCTAssertEqual(p.server, "example.com")
        XCTAssertEqual(p.port, 443)
        XCTAssertEqual(p.name, "My Server")
        XCTAssertEqual(p.flow, "xtls-rprx-vision")
        XCTAssertEqual(p.tls.realityPublicKey, "PUBKEY")
        XCTAssertEqual(p.tls.fingerprint, "chrome")

        let o = try SingBoxConfig.outbound(p, tag: "proxy")
        XCTAssertEqual(o["type"] as? String, "vless")
        let tls = try XCTUnwrap(o["tls"] as? [String: Any])
        XCTAssertEqual((tls["reality"] as? [String: Any])?["short_id"] as? String, "ab12")
        XCTAssertEqual((tls["utls"] as? [String: Any])?["fingerprint"] as? String, "chrome")
    }

    func testVlessWsEarlyDataInPath() throws {
        let p = try ShareLinkParser.parse("vless://id@[2001:db8::1]:8443?type=ws&path=%2Fws%3Fed%3D2048&host=cdn.example.com&security=tls")
        XCTAssertEqual(p.server, "2001:db8::1")
        XCTAssertEqual(p.port, 8443)
        XCTAssertEqual(p.stream.path, "/ws")
        XCTAssertEqual(p.stream.maxEarlyData, 2048)
        let t = try XCTUnwrap(SingBoxConfig.outbound(p, tag: "proxy")["transport"] as? [String: Any])
        XCTAssertEqual(t["type"] as? String, "ws")
        XCTAssertEqual((t["headers"] as? [String: String])?["Host"], "cdn.example.com")
        XCTAssertEqual(t["max_early_data"] as? Int, 2048)
    }

    func testVmessBase64Json() throws {
        let json = #"{"v":"2","ps":"VM","add":"1.2.3.4","port":"8080","id":"uuid-1","aid":0,"net":"grpc","path":"svc","tls":"tls","sni":"s.example","alpn":"http/1.1"}"#
        let link = "vmess://" + Data(json.utf8).base64EncodedString()
        let p = try ShareLinkParser.parse(link)
        XCTAssertEqual(p.proto, .vmess)
        XCTAssertEqual(p.name, "VM")
        XCTAssertEqual(p.port, 8080)
        XCTAssertEqual(p.stream.type, .grpc)
        XCTAssertEqual(p.stream.serviceName, "svc")
        // gRPC forces h2 over a list without it.
        let tls = try XCTUnwrap(SingBoxConfig.outbound(p, tag: "proxy")["tls"] as? [String: Any])
        XCTAssertEqual(tls["alpn"] as? [String], ["h2"])
    }

    func testShadowsocksSip002AndLegacy() throws {
        let userInfo = Data("aes-256-gcm:pa:ss".utf8).base64EncodedString()
        let a = try ShareLinkParser.parse("ss://\(userInfo)@ss.example.com:8388#SS")
        XCTAssertEqual(a.method, "aes-256-gcm")
        XCTAssertEqual(a.password, "pa:ss")
        XCTAssertEqual(a.port, 8388)

        let legacy = Data("chacha20-ietf-poly1305:pw@10.0.0.1:443".utf8).base64EncodedString()
        let b = try ShareLinkParser.parse("ss://\(legacy)#Old")
        XCTAssertEqual(b.server, "10.0.0.1")
        XCTAssertEqual(b.method, "chacha20-ietf-poly1305")
        XCTAssertEqual(b.name, "Old")
    }

    func testTrojanDefaultsToTls() throws {
        let p = try ShareLinkParser.parse("trojan://secret@t.example.com?sni=t.example.com")
        XCTAssertEqual(p.port, 443)
        XCTAssertTrue(p.tls.enabled)
    }

    func testHysteria2PortHopping() throws {
        let p = try ShareLinkParser.parse("hy2://pw@h.example.com:443,20000-30000?obfs=salamander&obfs-password=x&sni=h.example.com")
        XCTAssertEqual(p.port, 443)
        XCTAssertEqual(p.serverPorts, ["20000:30000"])
        let o = try SingBoxConfig.outbound(p, tag: "proxy")
        XCTAssertEqual(o["server_ports"] as? [String], ["20000:30000"])
        XCTAssertEqual((o["obfs"] as? [String: String])?["type"], "salamander")
    }

    func testRejectsBadInput() {
        XCTAssertThrowsError(try ShareLinkParser.parse("vless://@host:443"))
        XCTAssertThrowsError(try ShareLinkParser.parse("vless://id@host:99999"))
        XCTAssertThrowsError(try ShareLinkParser.parse("vless://id@host:443?security=reality"))
        XCTAssertThrowsError(try ShareLinkParser.parse("foo://bar"))
    }

    func testXhttpNeedsXray() throws {
        let p = try ShareLinkParser.parse("vless://id@x.example.com:443?type=xhttp&security=tls")
        XCTAssertFalse(SingBoxConfig.isSupported(p))
        XCTAssertThrowsError(try SingBoxConfig.json(p))
    }

    func testSubscriptionBase64ListDedupes() {
        let body = """
        trojan://a@one.example.com:443#One
        # comment
        trojan://a@one.example.com:443#One
        vless://id@two.example.com:443?security=tls#Two
        unknown://x
        """
        let r = SubscriptionParser.parse(Data(body.utf8).base64EncodedString())
        XCTAssertEqual(r.profiles.map(\.name), ["One", "Two"])
        XCTAssertEqual(r.errors.count, 1)
    }

    func testUserInfoHeader() throws {
        let u = try XCTUnwrap(SubscriptionParser.parseUserInfo("upload=10; download=20; total=100; expire=1700000000"))
        XCTAssertEqual(u.download, 20)
        XCTAssertEqual(u.total, 100)
        XCTAssertEqual(u.expire, Date(timeIntervalSince1970: 1_700_000_000))
    }

    func testFullConfigIsValidJson() throws {
        let p = try ShareLinkParser.parse("trojan://secret@t.example.com:443#T")
        let json = try SingBoxConfig.json(p)
        let obj = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any])
        let inbounds = try XCTUnwrap(obj["inbounds"] as? [[String: Any]])
        XCTAssertEqual(inbounds.first?["type"] as? String, "tun")
        let outbounds = try XCTUnwrap(obj["outbounds"] as? [[String: Any]])
        XCTAssertEqual(outbounds.map { $0["tag"] as? String }, ["proxy", "direct"])
        XCTAssertEqual((obj["route"] as? [String: Any])?["final"] as? String, "proxy")
    }

    func testWireGuardIsEndpoint() throws {
        let p = try ShareLinkParser.parse("wg://cHJpdmF0ZQ==@wg.example.com:51820?publickey=PUB&address=10.0.0.2&reserved=1,2,3")
        let obj = try SingBoxConfig.build(p)
        let endpoints = try XCTUnwrap(obj["endpoints"] as? [[String: Any]])
        XCTAssertEqual(endpoints.first?["address"] as? [String], ["10.0.0.2/32"])
        XCTAssertEqual((obj["outbounds"] as? [[String: Any]])?.count, 1)
    }

    func testStoreRoundTrip() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("ibox-test-\(UUID().uuidString)/servers.json")
        defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
        let store = ServerStore(fileURL: url)
        var state = ServerState()
        let x = try ShareLinkParser.parse("trojan://a@x.example.com:443#X")
        let first = state.addManual([x])
        let second = state.addManual([x])
        XCTAssertEqual(first, 1)
        XCTAssertEqual(second, 0)
        try store.save(state)
        XCTAssertEqual(store.load().selectedProfile?.name, "X")
    }
}
