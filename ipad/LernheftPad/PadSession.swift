import Foundation
import Observation
import PencilKit
import UIKit

/// Was das iPad über die am Surface offene Notiz weiß – und was es dorthin meldet.
@Observable
final class PadSession {
    struct NoteInfo: Equatable {
        var id: String
        var title: String
        var subject: String
        var color: String
        var paper: String
        var pageCount: Int
        var pageWidth: Double
    }

    struct NoteRef: Identifiable, Equatable {
        var id: String
        var title: String
        var subject: String
        var color: String
    }

    struct NotebookRef: Identifiable, Equatable {
        var id: String
        var name: String
        var color: String
    }

    let connection = PadConnection()
    @ObservationIgnored let canvas = CanvasController()
    let migration = MigrationModel()

    private(set) var note: NoteInfo?
    /// Das Surface hat schon gesagt, was offen ist.
    private(set) var knowsNote = false
    private(set) var recent: [NoteRef] = []
    private(set) var notebooks: [NotebookRef] = []
    private(set) var toast: String?
    private(set) var busy: String?
    private(set) var canUndo = false
    private(set) var canRedo = false
    private(set) var hasImages = false
    private(set) var surfaceTop: Double?

    @ObservationIgnored private var imageCache: [String: (rev: String, image: UIImage)] = [:]
    @ObservationIgnored private var conversions: [String: [String]] = [:]
    @ObservationIgnored private var toastGeneration = 0
    @ObservationIgnored private var lastDark: Bool?

    init() {
        connection.onMessage = { [weak self] message in self?.handle(message) }
        connection.onConnected = { [weak self] in self?.connected() }
        connection.onDisconnected = { [weak self] in self?.disconnected() }
        canvas.send = { [weak self] message in self?.connection.send(message) }
        canvas.onUndoStateChanged = { [weak self] in
            guard let self else { return }
            self.canUndo = self.canvas.canUndo
            self.canRedo = self.canvas.canRedo
        }
        canvas.onNeedsPages = { [weak self] count in
            guard let self, let note = self.note else { return }
            var message = PadProtocol.message(PadProtocol.pages)
            message["noteId"] = note.id
            message["count"] = count
            self.connection.send(message)
        }
        migration.connection = connection
    }

    private func connected() {
        knowsNote = false
        if let dark = lastDark { sendTheme(dark) }
    }

    private func disconnected() {
        busy = nil
        conversions.removeAll()
        migration.connectionLost()
    }

    // MARK: - Nachrichten vom Surface

    /// Für den Demo-Modus: Nachrichten so verarbeiten, als kämen sie vom Surface.
    func receive(_ message: JSON) { handle(message) }

    private func handle(_ message: JSON) {
        switch message.messageType {
        case PadProtocol.note:
            handleNote(message)
        case PadProtocol.ink:
            guard message.string("noteId") == note?.id else { return }
            canvas.load(message.objects("strokes").compactMap { PortableStroke(json: $0) })
        case PadProtocol.ops:
            guard message.string("noteId") == note?.id else { return }
            canvas.applyRemote(add: message.objects("add").compactMap { PortableStroke(json: $0) },
                               remove: message.strings("remove"))
        case PadProtocol.images:
            guard message.string("noteId") == note?.id else { return }
            handleImages(message)
        case PadProtocol.text:
            guard message.string("noteId") == note?.id else { return }
            let page = Int(message.number("page"))
            let data = message.string("data").flatMap { Data(base64Encoded: $0) }
            canvas.setTextLayer(page: page, data: data)
        case PadProtocol.view:
            guard message.string("noteId") == note?.id else { return }
            let top = message.number("top")
            surfaceTop = top
            canvas.setSurfaceView(top: CGFloat(top), height: CGFloat(message.number("height")))
        case PadProtocol.converted:
            handleConverted(message)
        case PadProtocol.toast:
            if let text = message.string("text") { show(text) }
        case PadProtocol.importDone:
            migration.finished(message)
        case PadProtocol.inserted:
            busy = nil
            show(message.string("message") ?? (message.flag("ok") ? "Eingefügt." : "Das Einfügen hat nicht geklappt."))
        default:
            break
        }
    }

    private func handleNote(_ message: JSON) {
        knowsNote = true
        guard let id = message.string("id") else {
            note = nil
            recent = message.objects("recent").compactMap { item in
                guard let id = item.string("id") else { return nil }
                return NoteRef(id: id, title: item.string("title") ?? "Notiz", subject: item.string("subject") ?? "",
                               color: item.string("color") ?? "#2F5BEA")
            }
            notebooks = message.objects("notebooks").compactMap { item in
                guard let id = item.string("id") else { return nil }
                return NotebookRef(id: id, name: item.string("name") ?? "Fach", color: item.string("color") ?? "#2F5BEA")
            }
            imageCache.removeAll()
            hasImages = false
            surfaceTop = nil
            canvas.setNote(id: nil, pageWidth: 800, pageCount: 1, paper: "lined")
            return
        }
        let info = NoteInfo(
            id: id,
            title: message.string("title") ?? "Notiz",
            subject: message.string("subject") ?? "",
            color: message.string("color") ?? "#2F5BEA",
            paper: message.string("paper") ?? "lined",
            pageCount: max(1, Int(message.number("pageCount", 1))),
            pageWidth: message.number("pageWidth", 800)
        )
        if note?.id != id {
            imageCache.removeAll()
            hasImages = false
            surfaceTop = nil
        }
        note = info
        canvas.setNote(id: id, pageWidth: CGFloat(info.pageWidth), pageCount: info.pageCount, paper: info.paper)
    }

    private func handleImages(_ message: JSON) {
        var placed: [PlacedImage] = []
        var keep = Set<String>()
        for item in message.objects("items") {
            guard let id = item.string("id") else { continue }
            let rev = item.string("rev") ?? ""
            var image = imageCache[id]?.rev == rev ? imageCache[id]?.image : nil
            if let data = item.string("data").flatMap({ Data(base64Encoded: $0) }), let decoded = UIImage(data: data) {
                image = decoded
                imageCache[id] = (rev, decoded)
            }
            guard let image else { continue }
            keep.insert(id)
            let frame = CGRect(x: item.number("x"), y: item.number("y"), width: item.number("w"), height: item.number("h"))
            placed.append(PlacedImage(id: id, frame: frame, image: image))
        }
        for id in imageCache.keys where !keep.contains(id) { imageCache[id] = nil }
        hasImages = !placed.isEmpty
        canvas.setImages(placed, seamless: message["seamless"] == nil ? true : message.flag("seamless"))
    }

    // MARK: - Wünsche ans Surface

    func open(_ noteId: String) {
        var message = PadProtocol.message(PadProtocol.open)
        message["noteId"] = noteId
        connection.send(message)
    }

    func newNote(in notebookId: String?) {
        var message = PadProtocol.message(PadProtocol.newNote)
        if let notebookId { message["notebookId"] = notebookId }
        connection.send(message)
    }

    func addPage() {
        guard let note else { return }
        var message = PadProtocol.message(PadProtocol.pages)
        message["noteId"] = note.id
        message["count"] = note.pageCount + 1
        connection.send(message)
        canvas.setPageCount(note.pageCount + 1)
        show("Seite \(note.pageCount + 1) angelegt")
    }

    func themeChanged(dark: Bool) {
        guard lastDark != dark else { return }
        lastDark = dark
        connection.isDark = { dark }
        sendTheme(dark)
    }

    private func sendTheme(_ dark: Bool) {
        var message = PadProtocol.message(PadProtocol.theme)
        message["dark"] = dark
        connection.send(message)
    }

    /// Achsenzahlen und Beschriftungen setzt das Surface als kleine Textfelder.
    func addLabels(_ labels: [GraphLabel]) {
        guard let note, !labels.isEmpty else { return }
        var message = PadProtocol.message(PadProtocol.addText)
        message["noteId"] = note.id
        message["items"] = labels.map { label -> JSON in
            ["x": Double(label.x), "y": Double(label.y), "width": Double(label.width), "fontSize": 15,
             "color": "#5A6774", "text": label.text, "free": true]
        }
        connection.send(message)
    }

    func deleteText(in rect: CGRect) {
        guard let note else { return }
        var message = PadProtocol.message(PadProtocol.deleteIn)
        message["noteId"] = note.id
        message["x"] = Double(rect.minX)
        message["y"] = Double(rect.minY)
        message["w"] = Double(rect.width)
        message["h"] = Double(rect.height)
        connection.send(message)
    }

    /// Handschrift lesen lassen: „script“ = Schönschrift an derselben Stelle, „text“ = Textfeld.
    func convert(rect: CGRect, mode: String) {
        guard let note else { return }
        let indices = canvas.strokeIndices(in: rect)
        let strokes = canvas.strokeList(at: indices)
        guard !strokes.isEmpty else {
            show("Im Rahmen ist keine Handschrift.")
            return
        }
        let bounds = InkTools.bounds(of: strokes)
        let image = InkTools.image(of: canvas.drawing, rect: bounds, images: canvas.images)
        let requestId = UUID().uuidString
        let ids = canvas.ids(at: indices)
        conversions[requestId] = ids
        var message = PadProtocol.message(PadProtocol.convert)
        message["noteId"] = note.id
        message["requestId"] = requestId
        message["mode"] = mode
        message["x"] = Double(bounds.minX)
        message["y"] = Double(bounds.minY)
        message["w"] = Double(bounds.width)
        message["h"] = Double(bounds.height)
        message["color"] = InkTools.dominantColorHex(strokes)
        message["strokes"] = ids
        message["image"] = image.pngData()?.base64EncodedString() ?? ""
        connection.send(message)
        busy = mode == "script" ? "Wird zu Schönschrift …" : "Wird zu Text …"
    }

    private func handleConverted(_ message: JSON) {
        busy = nil
        guard let requestId = message.string("requestId"), let ids = conversions.removeValue(forKey: requestId) else { return }
        if message.flag("ok") {
            canvas.removeStrokes(withIDs: Set(ids))
            show("Umgewandelt")
        } else {
            show(message.string("message") ?? "Das hat nicht geklappt.")
        }
    }

    /// Ausschnitt an den KI-Helfer am Surface: „math“ rechnet sofort, „ask“ öffnet den Chat.
    func askAI(rect: CGRect, mode: String) {
        guard let note else { return }
        let indices = canvas.strokeIndices(in: rect)
        let strokes = canvas.strokeList(at: indices)
        let area = strokes.isEmpty ? rect : InkTools.bounds(of: strokes)
        guard !strokes.isEmpty || canvas.images.contains(where: { $0.frame.intersects(rect) }) else {
            show("Im Rahmen ist nichts.")
            return
        }
        let image = InkTools.image(of: canvas.drawing, rect: area, images: canvas.images)
        var message = PadProtocol.message(PadProtocol.ai)
        message["noteId"] = note.id
        message["mode"] = mode
        message["image"] = image.pngData()?.base64EncodedString() ?? ""
        connection.send(message)
        show(mode == "math" ? "Die Lösung erscheint am Surface." : "Der KI-Helfer ist am Surface offen.")
    }

    /// PDFs, Fotos, Scans oder Texte vom iPad in die Notiz am Surface.
    /// „pages“ hängt sie als neue Seiten an, „here“ setzt sie an die Stelle, die das iPad zeigt.
    func insertFiles(_ files: [OutgoingFile], placement: String, newNote: Bool) {
        guard connection.isConnected else {
            show("Nicht mit dem Surface verbunden.")
            return
        }
        var message = PadProtocol.message(PadProtocol.insertFile)
        message["requestId"] = UUID().uuidString
        if !newNote, let note { message["noteId"] = note.id }
        message["placement"] = placement
        message["width"] = 1.0
        if placement == "here" { message["top"] = Double(max(0, canvas.visibleContentRect.minY)) }
        message["files"] = files.map { file -> JSON in ["name": file.name, "data": file.data.base64EncodedString()] }
        let megabytes = Double(files.reduce(0) { $0 + $1.data.count }) / 1_048_576
        busy = megabytes > 2 ? String(format: "Wird ans Surface geschickt (%.1f MB) …", megabytes) : "Wird ans Surface geschickt …"
        connection.send(message) { [weak self] ok in
            guard let self else { return }
            if ok {
                if self.busy != nil { self.busy = "Das Surface fügt ein …" }
            } else {
                self.busy = nil
                self.show("Die Übertragung ist abgebrochen.")
            }
        }
    }

    // MARK: - Meldungen

    func show(_ text: String) {
        toastGeneration += 1
        let generation = toastGeneration
        toast = text
        DispatchQueue.main.asyncAfter(deadline: .now() + 3.2) { [weak self] in
            guard let self, self.toastGeneration == generation else { return }
            self.toast = nil
        }
    }

    func cancelBusy() {
        busy = nil
        conversions.removeAll()
    }
}
