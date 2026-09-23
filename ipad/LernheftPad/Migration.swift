import Foundation
import Observation
import PencilKit

/// Holt die Notizen aus der alten Lernheft-App und schickt sie ans Surface.
///
/// Die alte App legt alles unter „Auf meinem iPad → Lernheft“ ab: library.json und je Notiz einen
/// Ordner mit drawing.data (PencilKit), strokes.json, content.json und eingefügten Bildern.
/// Die Handschrift steckt im Apple-Format – nur das iPad kann sie lesen. Deshalb wird sie hier in
/// das gemeinsame Strichformat übersetzt, bevor sie hinübergeht.
@Observable
final class MigrationModel {
    enum Phase: Equatable {
        case idle
        case reading
        case sending(done: Int, total: Int)
        case waiting
        case finished(String)
        case failed(String)
    }

    private(set) var phase: Phase = .idle
    @ObservationIgnored weak var connection: PadConnection?
    @ObservationIgnored private var task: Task<Void, Never>?

    var isRunning: Bool {
        switch phase {
        case .reading, .sending, .waiting: return true
        default: return false
        }
    }

    struct Item {
        var id: String
        var load: () throws -> Data
    }

    func start(folder: URL) {
        guard !isRunning else { return }
        phase = .reading
        task = Task { @MainActor in
            let access = folder.startAccessingSecurityScopedResource()
            defer { if access { folder.stopAccessingSecurityScopedResource() } }
            do {
                let root = try Self.findRoot(folder)
                let items = try Self.collect(root)
                try await send(items)
            } catch is CancellationError {
                if case .failed = phase { return }
                phase = .failed("Die Übertragung wurde abgebrochen.")
            } catch {
                phase = .failed(error.localizedDescription)
            }
        }
    }

    func reset() {
        if !isRunning { phase = .idle }
    }

    func connectionLost() {
        if isRunning {
            task?.cancel()
            phase = .failed("Die Verbindung zum Surface ist abgebrochen. Starte die Übertragung neu – schon Übertragenes wird übersprungen.")
        }
    }

    func finished(_ message: JSON) {
        let text = message.string("message") ?? ""
        phase = message.flag("ok") ? .finished(text.isEmpty ? "Fertig." : text) : .failed(text.isEmpty ? "Das Surface konnte die Notizen nicht übernehmen." : text)
    }

    // MARK: Lesen

    enum MigrationError: LocalizedError {
        case notFound, empty

        var errorDescription: String? {
            switch self {
            case .notFound:
                return "In diesem Ordner liegt keine library.json. Wähle „Auf meinem iPad → Lernheft“."
            case .empty:
                return "Im Ordner habe ich keine Notizen gefunden."
            }
        }
    }

    /// Der gewählte Ordner selbst oder ein „Lernheft“-Ordner darin.
    static func findRoot(_ folder: URL) throws -> URL {
        let manager = FileManager.default
        if manager.fileExists(atPath: folder.appendingPathComponent("library.json").path) { return folder }
        let nested = folder.appendingPathComponent("Lernheft", isDirectory: true)
        if manager.fileExists(atPath: nested.appendingPathComponent("library.json").path) { return nested }
        throw MigrationError.notFound
    }

    static func collect(_ root: URL) throws -> [Item] {
        let manager = FileManager.default
        let library = root.appendingPathComponent("library.json")
        var items: [Item] = [Item(id: "library.json", load: { try Data(contentsOf: library) })]

        let data = try Data(contentsOf: library)
        let json = PadProtocol.decode(data) ?? [:]
        let ids = json.objects("notes").compactMap { $0.string("id") }.compactMap { UUID(uuidString: $0) }
        guard !ids.isEmpty else { throw MigrationError.empty }

        let notesFolder = root.appendingPathComponent("notes", isDirectory: true)
        for id in ids {
            let folder = notesFolder.appendingPathComponent(id.uuidString, isDirectory: true)
            guard manager.fileExists(atPath: folder.path) else { continue }
            let key = id.uuidString.replacingOccurrences(of: "-", with: "").lowercased()
            let files = (try? manager.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.contentModificationDateKey, .isRegularFileKey])) ?? []

            let content = folder.appendingPathComponent("content.json")
            if manager.fileExists(atPath: content.path) {
                items.append(Item(id: "note:\(key):content", load: { try Data(contentsOf: content) }))
            }

            let native = folder.appendingPathComponent("drawing.data")
            let portable = folder.appendingPathComponent("strokes.json")
            let nativeDate = modified(native)
            let portableDate = modified(portable)
            if let nativeDate, portableDate.map({ $0 <= nativeDate.addingTimeInterval(1) }) ?? true {
                items.append(Item(id: "note:\(key):ink", load: { try convertDrawing(native) }))
            } else if portableDate != nil {
                items.append(Item(id: "note:\(key):ink", load: { try Data(contentsOf: portable) }))
            }

            for file in files {
                let name = file.lastPathComponent
                guard !name.hasPrefix("."), !["content.json", "drawing.data", "strokes.json"].contains(name),
                      (try? file.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true else { continue }
                items.append(Item(id: "note:\(key):img.\(name)", load: { try Data(contentsOf: file) }))
            }
        }
        return items
    }

    private static func modified(_ url: URL) -> Date? {
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        return (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? .distantPast
    }

    /// PencilKit-Zeichnung → gemeinsames Strichformat (strokes.json, Version 2).
    static func convertDrawing(_ url: URL) throws -> Data {
        let drawing = try PKDrawing(data: Data(contentsOf: url))
        let strokes = drawing.strokes.flatMap { InkBridge.portable($0) }
        let document: JSON = ["v": 2, "device": "iPad", "strokes": strokes.map(\.json)]
        return try JSONSerialization.data(withJSONObject: document)
    }

    // MARK: Senden

    @MainActor
    private func send(_ items: [Item]) async throws {
        guard let connection, connection.isConnected else {
            phase = .failed("Verbinde zuerst mit dem Surface.")
            return
        }
        phase = .sending(done: 0, total: items.count)
        guard await sendAndWait(connection, PadProtocol.message(PadProtocol.importBegin)) else { throw CancellationError() }
        for (index, item) in items.enumerated() {
            try Task.checkCancellation()
            let data: Data
            do {
                data = try item.load()
            } catch {
                continue
            }
            var message = PadProtocol.message(PadProtocol.importFile)
            message["id"] = item.id
            message["data"] = data.base64EncodedString()
            guard await sendAndWait(connection, message) else { throw CancellationError() }
            phase = .sending(done: index + 1, total: items.count)
        }
        guard await sendAndWait(connection, PadProtocol.message(PadProtocol.importEnd)) else { throw CancellationError() }
        phase = .waiting
    }

    @MainActor
    private func sendAndWait(_ connection: PadConnection, _ message: JSON) async -> Bool {
        await withCheckedContinuation { continuation in
            connection.send(message) { ok in continuation.resume(returning: ok) }
        }
    }
}
