import Foundation

/// Die Sprache zwischen Lernheft Pad (iPad) und Lernheft Studio (Surface).
/// Jede Nachricht ist ein JSON-Objekt mit dem Feld „t“ für die Art. Dieselben Namen stehen
/// in Lernheft Studio (PadProtocol.cs).
enum PadProtocol {
    static let version = 1
    static let studioPort: UInt16 = 47650
    static let padPort: UInt16 = 47660
    static let scheme = "lernheftpad"
    static let studioService = "_lernheft._tcp"
    static let padService = "_lernheftpad._tcp"

    // Anmeldung
    static let hello = "hello"
    static let welcome = "welcome"
    static let denied = "denied"
    static let ping = "ping"
    static let pong = "pong"
    static let bye = "bye"

    // Die offene Notiz (Surface → iPad)
    static let note = "note"
    static let ink = "ink"
    static let images = "images"
    static let text = "text"
    static let view = "view"

    // Zeichnen
    static let live = "live"
    static let ops = "ops"

    // Wünsche vom iPad
    static let pages = "pages"
    static let eraseImage = "eraseImage"
    static let addText = "addText"
    static let deleteIn = "deleteIn"
    static let convert = "convert"
    static let converted = "converted"
    static let ai = "ai"
    static let open = "open"
    static let newNote = "newNote"
    static let theme = "theme"
    static let toast = "toast"

    // Umzug der alten Notizen
    static let importBegin = "importBegin"
    static let importFile = "importFile"
    static let importEnd = "importEnd"
    static let importDone = "importDone"

    static func message(_ type: String) -> JSON { ["t": type] }

    static func encode(_ message: JSON) -> Data? {
        guard JSONSerialization.isValidJSONObject(message) else { return nil }
        return try? JSONSerialization.data(withJSONObject: message)
    }

    static func decode(_ data: Data) -> JSON? {
        (try? JSONSerialization.jsonObject(with: data)) as? JSON
    }
}

typealias JSON = [String: Any]

extension Dictionary where Key == String, Value == Any {
    var messageType: String { self["t"] as? String ?? "" }

    func string(_ key: String) -> String? { self[key] as? String }

    func number(_ key: String, _ fallback: Double = 0) -> Double {
        (self[key] as? NSNumber)?.doubleValue ?? fallback
    }

    func flag(_ key: String) -> Bool {
        if let value = self[key] as? Bool { return value }
        return (self[key] as? NSNumber)?.boolValue ?? false
    }

    func objects(_ key: String) -> [JSON] { self[key] as? [JSON] ?? [] }

    func strings(_ key: String) -> [String] { self[key] as? [String] ?? [] }

    func numbers(_ key: String) -> [Double] {
        (self[key] as? [Any])?.compactMap { ($0 as? NSNumber)?.doubleValue } ?? []
    }
}

/// Was im QR-Code am Surface steht: lernheftpad://pair?v=1&id=…&n=…&h=ip1,ip2&p=47650&s=…
struct PairingOffer: Equatable {
    var serverId: String
    var serverName: String
    var hosts: [String]
    var port: UInt16
    var secret: String

    init?(_ text: String) {
        guard let components = URLComponents(string: text.trimmingCharacters(in: .whitespacesAndNewlines)),
              components.scheme == PadProtocol.scheme else { return nil }
        var values: [String: String] = [:]
        for item in components.queryItems ?? [] { values[item.name] = item.value ?? "" }
        guard let id = values["id"], !id.isEmpty, let secret = values["s"], !secret.isEmpty else { return nil }
        serverId = id
        serverName = values["n"] ?? "Surface"
        hosts = (values["h"] ?? "").split(separator: ",").map { String($0) }.filter { !$0.isEmpty }
        port = UInt16(values["p"] ?? "") ?? PadProtocol.studioPort
        self.secret = secret
    }
}
