import Foundation
import Network
import Observation
import Security
import UIKit

// MARK: - Eine WebSocket-Verbindung

/// Eine Leitung zum Surface – egal, ob das iPad sie aufgebaut hat oder das Surface (Rückweg).
/// Alle Rückrufe kommen auf dem Hauptthread an.
final class Link {
    let connection: NWConnection
    let address: String
    let incoming: Bool
    var onReady: (() -> Void)?
    var onMessage: ((JSON) -> Void)?
    var onClosed: (() -> Void)?
    private(set) var lastHeard = Date()
    private(set) var isClosed = false
    private let queue = DispatchQueue(label: "de.lernheft.pad.link", qos: .userInitiated)

    init(connection: NWConnection, address: String, incoming: Bool) {
        self.connection = connection
        self.address = address
        self.incoming = incoming
    }

    static func webSocketOptions() -> NWProtocolWebSocket.Options {
        let options = NWProtocolWebSocket.Options()
        options.autoReplyPing = true
        options.maximumMessageSize = 256 * 1024 * 1024
        return options
    }

    static func parameters() -> NWParameters {
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        tcp.connectionTimeout = 5
        let parameters = NWParameters(tls: nil, tcp: tcp)
        parameters.includePeerToPeer = false
        parameters.defaultProtocolStack.applicationProtocols.insert(webSocketOptions(), at: 0)
        return parameters
    }

    func start() {
        connection.stateUpdateHandler = { [weak self] state in
            DispatchQueue.main.async {
                guard let self, !self.isClosed else { return }
                switch state {
                case .ready:
                    self.lastHeard = Date()
                    self.onReady?()
                case .failed, .cancelled:
                    self.finish()
                case .waiting:
                    // Kein Weg zum Ziel (anderes Netz, Firewall) – nicht ewig warten.
                    self.close()
                default:
                    break
                }
            }
        }
        connection.start(queue: queue)
        receive()
    }

    private func receive() {
        connection.receiveMessage { [weak self] data, context, _, error in
            guard let self else { return }
            if error != nil {
                DispatchQueue.main.async { self.close() }
                return
            }
            if let metadata = context?.protocolMetadata(definition: NWProtocolWebSocket.definition) as? NWProtocolWebSocket.Metadata,
               metadata.opcode == .close {
                DispatchQueue.main.async { self.close() }
                return
            }
            let message = data.flatMap { PadProtocol.decode($0) }
            DispatchQueue.main.async {
                guard !self.isClosed else { return }
                self.lastHeard = Date()
                if let message { self.onMessage?(message) }
            }
            self.receive()
        }
    }

    /// Schickt eine Nachricht. `completion` meldet, wann sie wirklich raus ist (für große Dateien).
    /// Nur für Bildschirmfotos: tut so, als wäre ein Surface verbunden.
    func startDemo(name: String) {
        serverName = name
        phase = .connected
    }

    func send(_ message: JSON, completion: ((Bool) -> Void)? = nil) {
        guard !isClosed, let data = PadProtocol.encode(message) else {
            completion?(false)
            return
        }
        let metadata = NWProtocolWebSocket.Metadata(opcode: .text)
        let context = NWConnection.ContentContext(identifier: "message", metadata: [metadata])
        connection.send(content: data, contentContext: context, isComplete: true, completion: .contentProcessed { error in
            guard let completion else { return }
            DispatchQueue.main.async { completion(error == nil) }
        })
    }

    func close() {
        guard !isClosed else { return }
        let metadata = NWProtocolWebSocket.Metadata(opcode: .close)
        metadata.closeCode = .protocolCode(.normalClosure)
        let context = NWConnection.ContentContext(identifier: "close", metadata: [metadata])
        connection.send(content: nil, contentContext: context, isComplete: true, completion: .contentProcessed { [connection] _ in
            connection.cancel()
        })
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [connection] in connection.cancel() }
        finish()
    }

    private func finish() {
        guard !isClosed else { return }
        isClosed = true
        connection.stateUpdateHandler = nil
        onClosed?()
        onClosed = nil
        onMessage = nil
        onReady = nil
    }
}

// MARK: - Gespeicherte Kopplung

struct SavedPairing: Codable, Equatable {
    var serverId: String
    var serverName: String
    var hosts: [String]
    var port: UInt16
    var pairKey: String = ""

    enum CodingKeys: String, CodingKey { case serverId, serverName, hosts, port }

    private static let defaultsKey = "pairing"
    private static let keychainAccount = "pairKey"

    static func load() -> SavedPairing? {
        guard let data = UserDefaults.standard.data(forKey: defaultsKey),
              var pairing = try? JSONDecoder().decode(SavedPairing.self, from: data),
              let key = Keychain.read(keychainAccount), !key.isEmpty else { return nil }
        pairing.pairKey = key
        return pairing
    }

    func save() {
        if let data = try? JSONEncoder().encode(self) { UserDefaults.standard.set(data, forKey: Self.defaultsKey) }
        Keychain.write(Self.keychainAccount, pairKey)
    }

    static func remove() {
        UserDefaults.standard.removeObject(forKey: defaultsKey)
        Keychain.delete(keychainAccount)
    }
}

enum Keychain {
    private static let service = "de.lernheft.pad"

    static func read(_ account: String) -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func write(_ account: String, _ value: String) {
        delete(account)
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock,
            kSecValueData as String: Data(value.utf8)
        ]
        SecItemAdd(query as CFDictionary, nil)
    }

    static func delete(_ account: String) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
        SecItemDelete(query as CFDictionary)
    }
}

// MARK: - Verbindungsverwaltung

/// Findet das Surface, meldet sich an, hält die Leitung am Leben und baut sie nach Abbrüchen neu auf.
///
/// Drei Wege, damit es auch in schwierigen WLANs klappt:
/// 1. direkt an die Adressen aus dem QR-Code bzw. der letzten Verbindung,
/// 2. Suche im WLAN (Bonjour und ein kurzer Rundruf im eigenen Netz),
/// 3. Rückweg: das iPad lauscht selbst, und das Surface ruft an – falls die Windows-Firewall sperrt.
@Observable
final class PadConnection {
    enum Phase: Equatable {
        case idle          // nichts gekoppelt oder vom Nutzer getrennt
        case searching     // suche das Surface
        case connected
    }

    private(set) var phase: Phase = .idle
    private(set) var pairing: SavedPairing? = SavedPairing.load()
    private(set) var serverName = ""
    private(set) var problem: String?
    private(set) var searchStarted: Date?
    /// Gerade läuft eine neue Kopplung (Code oder QR).
    private(set) var isPairing = false
    private(set) var listenerProblem: String?

    var onMessage: ((JSON) -> Void)?
    var onConnected: (() -> Void)?
    var onDisconnected: (() -> Void)?

    @ObservationIgnored private var link: Link?
    @ObservationIgnored private var candidates: [String: Link] = [:]
    @ObservationIgnored private var offer: PairingOffer?
    @ObservationIgnored private var code: String?
    @ObservationIgnored private var browser: NWBrowser?
    @ObservationIgnored private var bonjourResults: [NWEndpoint] = []
    @ObservationIgnored private var listener: NWListener?
    @ObservationIgnored private var timer: Timer?
    @ObservationIgnored private var tick = 0
    @ObservationIgnored private var scanning = false
    @ObservationIgnored private var lastScan = Date.distantPast
    @ObservationIgnored var isDark: () -> Bool = { false }

    static let deviceId: String = {
        if let id = UserDefaults.standard.string(forKey: "deviceId") { return id }
        let id = UUID().uuidString
        UserDefaults.standard.set(id, forKey: "deviceId")
        return id
    }()

    var isConnected: Bool { phase == .connected }
    var hasPairing: Bool { pairing != nil }

    // MARK: Steuerung

    /// Beim Start und beim Zurückkehren in die App: mit dem bekannten Surface verbinden.
    func resume() {
        suspended = false
        guard link == nil else { return }
        if isPairing || pairing != nil, !userDisconnected { beginSearch() }
    }

    /// Die App geht in den Hintergrund: iOS würde die Leitung ohnehin kappen.
    func suspend() {
        suspended = true
        stopSearch()
        if let link {
            var bye = PadProtocol.message(PadProtocol.bye)
            bye["reason"] = "Lernheft Pad ist im Hintergrund."
            link.send(bye)
            link.close()
        }
    }

    @ObservationIgnored private var userDisconnected = false
    @ObservationIgnored private var suspended = false

    /// Mit dem QR-Code vom Surface koppeln.
    func pair(with offer: PairingOffer) {
        closeAll()
        self.offer = offer
        code = nil
        isPairing = true
        userDisconnected = false
        problem = nil
        serverName = offer.serverName
        beginSearch()
    }

    /// Mit dem sechsstelligen Code koppeln – das Surface wird im WLAN gesucht.
    func pair(code: String) {
        closeAll()
        let digits = code.filter(\.isNumber)
        guard digits.count == 6 else {
            problem = "Der Code hat sechs Ziffern."
            return
        }
        self.code = digits
        offer = nil
        isPairing = true
        userDisconnected = false
        problem = nil
        serverName = ""
        beginSearch()
    }

    func cancelPairing() {
        isPairing = false
        offer = nil
        code = nil
        closeCandidates()
        if pairing == nil { stopSearch() } else { resume() }
    }

    /// Vom Nutzer getrennt: nicht von selbst neu verbinden.
    func disconnect() {
        userDisconnected = true
        if let link {
            var bye = PadProtocol.message(PadProtocol.bye)
            bye["reason"] = "Am iPad getrennt."
            link.send(bye)
        }
        closeAll()
        stopSearch()
        phase = .idle
    }

    func reconnect() {
        userDisconnected = false
        problem = nil
        beginSearch()
    }

    /// Kopplung löschen – danach braucht es wieder Code oder QR.
    func forget() {
        disconnect()
        SavedPairing.remove()
        pairing = nil
        serverName = ""
    }

    /// Nur für Bildschirmfotos: tut so, als wäre ein Surface verbunden.
    func startDemo(name: String) {
        serverName = name
        phase = .connected
    }

    func send(_ message: JSON, completion: ((Bool) -> Void)? = nil) {
        guard let link else {
            completion?(false)
            return
        }
        link.send(message, completion: completion)
    }

    // MARK: Suche

    private func beginSearch() {
        guard link == nil else { return }
        if phase != .searching {
            phase = .searching
            searchStarted = Date()
        }
        startListener()
        startBrowser()
        timer?.invalidate()
        tick = 0
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in self?.onTimer() }
        attemptKnown()
    }

    private func stopSearch() {
        timer?.invalidate()
        timer = nil
        browser?.cancel()
        browser = nil
        bonjourResults = []
        listener?.cancel()
        listener = nil
        closeCandidates()
        if link == nil, phase == .searching { phase = .idle }
    }

    private func onTimer() {
        tick += 1
        if let link {
            // Lebenszeichen alle 4 s; bleibt das Surface 14 s stumm, ist die Leitung tot.
            if Date().timeIntervalSince(link.lastHeard) > 14 {
                link.close()
                return
            }
            if tick % 4 == 0 { link.send(PadProtocol.message(PadProtocol.ping)) }
            return
        }
        guard phase == .searching else { return }
        if tick % 3 == 0 { attemptKnown() }
        if tick % 3 == 1 { bonjourResults.forEach { attempt($0) } }
        if Date().timeIntervalSince(lastScan) > (isPairing ? 8 : 20) { scanNetwork() }
        if listener == nil && tick % 10 == 0 { startListener() }
    }

    private func attemptKnown() {
        if let offer {
            for host in offer.hosts { attempt(host: host, port: offer.port) }
        } else if !isPairing, let pairing {
            for host in pairing.hosts { attempt(host: host, port: pairing.port) }
        }
    }

    private func attempt(host: String, port: UInt16) {
        guard let url = URL(string: "ws://\(host):\(port)/pad") else { return }
        attempt(.url(url), label: "\(host):\(port)")
    }

    private func attempt(_ endpoint: NWEndpoint, label: String? = nil) {
        let key = label ?? "\(endpoint)"
        guard link == nil, candidates[key] == nil, isPairing || pairing != nil else { return }
        let connection = NWConnection(to: endpoint, using: Link.parameters())
        let host = label.map { String($0.split(separator: ":").first ?? "") } ?? ""
        let candidate = Link(connection: connection, address: host, incoming: false)
        adoptCandidate(candidate, key: key)
    }

    private func adoptCandidate(_ candidate: Link, key: String) {
        candidates[key] = candidate
        candidate.onReady = { [weak self, weak candidate] in
            guard let self, let candidate else { return }
            candidate.send(self.hello())
        }
        candidate.onMessage = { [weak self, weak candidate] message in
            guard let self, let candidate else { return }
            self.handshake(candidate, key: key, message: message)
        }
        candidate.onClosed = { [weak self, weak candidate] in
            guard let self else { return }
            if self.candidates[key] === candidate { self.candidates[key] = nil }
        }
        candidate.start()
        DispatchQueue.main.asyncAfter(deadline: .now() + 9) { [weak self, weak candidate] in
            guard let self, let candidate, self.link !== candidate else { return }
            candidate.close()
        }
    }

    private func hello() -> JSON {
        var hello = PadProtocol.message(PadProtocol.hello)
        hello["v"] = PadProtocol.version
        hello["deviceId"] = Self.deviceId
        hello["deviceName"] = UIDevice.current.name
        hello["dark"] = isDark()
        if let offer {
            hello["serverId"] = offer.serverId
            hello["secret"] = offer.secret
            if let pairing, pairing.serverId == offer.serverId { hello["pairKey"] = pairing.pairKey }
        } else if let code {
            hello["code"] = code
        } else if let pairing {
            hello["serverId"] = pairing.serverId
            hello["pairKey"] = pairing.pairKey
        }
        return hello
    }

    private func handshake(_ candidate: Link, key: String, message: JSON) {
        switch message.messageType {
        case PadProtocol.welcome:
            guard link == nil else {
                candidate.close()
                return
            }
            candidates[key] = nil
            welcome(candidate, message: message)
        case PadProtocol.denied:
            let reason = message.string("reason") ?? ""
            if reason != "other", !reason.isEmpty {
                // Beim Code-Koppeln melden sich evtl. mehrere Surfaces – nur echte Probleme zeigen.
                problem = reason
                if !isPairing, pairing != nil, reason.contains("nicht mehr gekoppelt") {
                    SavedPairing.remove()
                    pairing = nil
                }
            }
            candidate.close()
        default:
            break
        }
    }

    private func welcome(_ candidate: Link, message: JSON) {
        let serverId = message.string("serverId") ?? ""
        let name = message.string("serverName") ?? "Surface"
        let pairKey = message.string("pairKey") ?? ""
        var hosts = pairing?.serverId == serverId ? pairing?.hosts ?? [] : []
        if let offer { hosts = offer.hosts + hosts }
        if !candidate.address.isEmpty { hosts.insert(candidate.address, at: 0) }
        var unique: [String] = []
        for host in hosts where !unique.contains(host) && !host.isEmpty { unique.append(host) }
        let port = offer?.port ?? (pairing?.serverId == serverId ? pairing?.port : nil) ?? PadProtocol.studioPort
        let saved = SavedPairing(serverId: serverId, serverName: name, hosts: Array(unique.prefix(6)), port: port, pairKey: pairKey)
        saved.save()
        pairing = saved
        serverName = name
        offer = nil
        code = nil
        isPairing = false
        problem = nil
        closeCandidates()

        link = candidate
        candidate.onMessage = { [weak self] message in self?.received(message) }
        candidate.onClosed = { [weak self, weak candidate] in
            guard let self, self.link === candidate else { return }
            self.lost()
        }
        phase = .connected
        UIApplication.shared.isIdleTimerDisabled = true
        browser?.cancel()
        browser = nil
        onConnected?()
    }

    private func received(_ message: JSON) {
        switch message.messageType {
        case PadProtocol.ping:
            link?.send(PadProtocol.message(PadProtocol.pong))
        case PadProtocol.pong:
            break
        case PadProtocol.bye:
            if let reason = message.string("reason"), !reason.isEmpty { problem = reason }
            if message.string("reason") == "Am Surface getrennt." { userDisconnected = true }
            link?.close()
        default:
            onMessage?(message)
        }
    }

    private func lost() {
        link = nil
        UIApplication.shared.isIdleTimerDisabled = false
        onDisconnected?()
        if userDisconnected {
            stopSearch()
            phase = .idle
        } else if suspended {
            phase = .searching
        } else {
            phase = .searching
            searchStarted = Date()
            beginSearch()
        }
    }

    private func closeCandidates() {
        let all = candidates.values
        candidates = [:]
        all.forEach { $0.close() }
    }

    private func closeAll() {
        closeCandidates()
        if let link {
            self.link = nil
            link.onClosed = nil
            link.close()
            UIApplication.shared.isIdleTimerDisabled = false
            onDisconnected?()
        }
    }

    // MARK: Bonjour

    private func startBrowser() {
        guard browser == nil else { return }
        let browser = NWBrowser(for: .bonjour(type: PadProtocol.studioService, domain: nil), using: .tcp)
        browser.browseResultsChangedHandler = { [weak self] results, _ in
            DispatchQueue.main.async {
                guard let self else { return }
                self.bonjourResults = results.map(\.endpoint)
                if self.link == nil { self.bonjourResults.forEach { self.attempt($0) } }
            }
        }
        browser.start(queue: .main)
        self.browser = browser
    }

    // MARK: Rückweg

    private func startListener() {
        guard listener == nil, let port = NWEndpoint.Port(rawValue: PadProtocol.padPort) else { return }
        let parameters = Link.parameters()
        parameters.allowLocalEndpointReuse = true
        do {
            let listener = try NWListener(using: parameters, on: port)
            listener.newConnectionHandler = { [weak self] connection in
                DispatchQueue.main.async { self?.incoming(connection) }
            }
            listener.stateUpdateHandler = { [weak self] state in
                DispatchQueue.main.async {
                    guard let self else { return }
                    switch state {
                    case .failed(let error):
                        self.listenerProblem = error.localizedDescription
                        self.listener?.cancel()
                        self.listener = nil
                    case .ready:
                        self.listenerProblem = nil
                    default:
                        break
                    }
                }
            }
            listener.start(queue: .main)
            self.listener = listener
        } catch {
            listenerProblem = error.localizedDescription
        }
    }

    private func incoming(_ connection: NWConnection) {
        guard link == nil, isPairing || pairing != nil else {
            connection.cancel()
            return
        }
        var address = ""
        if case .hostPort(let host, _) = connection.endpoint {
            address = "\(host)".components(separatedBy: "%").first ?? ""
            if address.hasPrefix("::ffff:") { address = String(address.dropFirst(7)) }
        }
        let candidate = Link(connection: connection, address: address, incoming: true)
        adoptCandidate(candidate, key: "in:" + address + ":" + UUID().uuidString.prefix(4))
    }

    // MARK: Rundruf im eigenen Netz

    /// Fragt alle Nachbarn im /24-Netz nach „/hello“. Findet das Surface auch dann,
    /// wenn Bonjour im Schul-WLAN gesperrt ist.
    private func scanNetwork() {
        guard !scanning else { return }
        scanning = true
        lastScan = Date()
        let wantedId = isPairing ? offer?.serverId : pairing?.serverId
        let pairingOnly = code != nil
        let ports: [UInt16] = [PadProtocol.studioPort, PadProtocol.studioPort + 1]
        Task.detached(priority: .utility) {
            let found = await NetworkScan.findStudios(ports: ports)
            await MainActor.run {
                self.scanning = false
                guard self.link == nil else { return }
                for studio in found {
                    if let wantedId, studio.id != wantedId { continue }
                    if pairingOnly && !studio.pairing { continue }
                    self.attempt(host: studio.host, port: studio.port)
                }
                if found.isEmpty, self.isPairing, self.code != nil, Date().timeIntervalSince(self.searchStarted ?? Date()) > 12 {
                    self.problem = self.problem ?? "Ich finde kein Surface im WLAN. Sind beide im selben Netz?"
                }
            }
        }
    }
}

enum NetworkScan {
    struct Studio {
        var host: String
        var port: UInt16
        var id: String
        var name: String
        var pairing: Bool
    }

    /// Eigene IPv4-Adressen (WLAN, Hotspot, Kabel) – ohne Loopback und ohne „169.254“.
    static func localAddresses() -> [String] {
        var result: [String] = []
        var pointer: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&pointer) == 0, let first = pointer else { return [] }
        defer { freeifaddrs(pointer) }
        var current: UnsafeMutablePointer<ifaddrs>? = first
        while let entry = current {
            defer { current = entry.pointee.ifa_next }
            let flags = Int32(entry.pointee.ifa_flags)
            guard (flags & IFF_UP) != 0, (flags & IFF_LOOPBACK) == 0,
                  let address = entry.pointee.ifa_addr, address.pointee.sa_family == UInt8(AF_INET) else { continue }
            let name = String(cString: entry.pointee.ifa_name)
            guard name.hasPrefix("en") || name.hasPrefix("bridge") || name.hasPrefix("ap") else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            if getnameinfo(address, socklen_t(address.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 {
                let text = String(cString: host)
                if !text.hasPrefix("169.254"), !result.contains(text) { result.append(text) }
            }
        }
        return result
    }

    static func findStudios(ports: [UInt16]) async -> [Studio] {
        var hosts: [String] = []
        for own in localAddresses() {
            let parts = own.split(separator: ".")
            guard parts.count == 4 else { continue }
            let prefix = parts[0...2].joined(separator: ".")
            for index in 1...254 where "\(prefix).\(index)" != own {
                hosts.append("\(prefix).\(index)")
            }
        }
        guard !hosts.isEmpty else { return [] }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 1.2
        configuration.timeoutIntervalForResource = 2
        configuration.httpMaximumConnectionsPerHost = 1
        configuration.waitsForConnectivity = false
        let session = URLSession(configuration: configuration)
        defer { session.invalidateAndCancel() }

        var targets: [(String, UInt16)] = []
        for port in ports { for host in hosts { targets.append((host, port)) } }
        var found: [Studio] = []
        // In Portionen, damit das WLAN nicht überrannt wird.
        let batch = 64
        var start = 0
        while start < targets.count {
            let slice = targets[start..<min(start + batch, targets.count)]
            start += batch
            await withTaskGroup(of: Studio?.self) { group in
                for (host, port) in slice {
                    group.addTask { await probe(session: session, host: host, port: port) }
                }
                for await studio in group {
                    if let studio { found.append(studio) }
                }
            }
            if !found.isEmpty && start >= hosts.count { break }
        }
        return found
    }

    private static func probe(session: URLSession, host: String, port: UInt16) async -> Studio? {
        guard let url = URL(string: "http://\(host):\(port)/hello") else { return nil }
        do {
            let (data, response) = try await session.data(from: url)
            guard (response as? HTTPURLResponse)?.statusCode == 200,
                  let json = PadProtocol.decode(data), json.string("app") == "LernheftStudio" else { return nil }
            return Studio(host: host, port: port, id: json.string("id") ?? "", name: json.string("name") ?? "Surface",
                          pairing: json.flag("pairing"))
        } catch {
            return nil
        }
    }
}
