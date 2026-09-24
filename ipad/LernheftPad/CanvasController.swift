import PencilKit
import SwiftUI
import UIKit
import UIKit.UIGestureRecognizerSubclass

/// Ein eingefügtes Bild (Scan, PDF-Seite) mit seiner Lage auf dem Papier.
struct PlacedImage: Equatable {
    let id: String
    var frame: CGRect
    var image: UIImage

    static func == (lhs: PlacedImage, rhs: PlacedImage) -> Bool {
        lhs.id == rhs.id && lhs.frame == rhs.frame && lhs.image === rhs.image
    }
}

/// Die Zeichenfläche, wie PencilKit sie kennt – plus Rückmeldung bei Größenänderungen.
final class PadCanvasView: PKCanvasView {
    var onLayout: (() -> Void)?
    var onWindow: (() -> Void)?

    override func layoutSubviews() {
        super.layoutSubviews()
        onLayout?()
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        if window != nil { onWindow?() }
    }
}

/// Beobachtet Stift-Berührungen, ohne PencilKit zu stören – für die Live-Übertragung ans Surface.
final class LiveTouchRecognizer: UIGestureRecognizer {
    var onBegan: ((UITouch, UIEvent) -> Bool)?
    var onMoved: ((UITouch, UIEvent) -> Void)?
    var onEnded: ((_ cancelled: Bool) -> Void)?
    private var tracked: UITouch?

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent) {
        if tracked == nil, let touch = touches.first, onBegan?(touch, event) == true {
            tracked = touch
        } else if tracked == nil {
            state = .failed
        }
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent) {
        guard let tracked, touches.contains(tracked) else { return }
        onMoved?(tracked, event)
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent) {
        guard let tracked, touches.contains(tracked) else { return }
        onMoved?(tracked, event)
        self.tracked = nil
        onEnded?(false)
        state = .failed
    }

    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent) {
        guard let tracked, touches.contains(tracked) else { return }
        self.tracked = nil
        onEnded?(true)
        state = .failed
    }

    override func reset() {
        super.reset()
        if tracked != nil {
            tracked = nil
            onEnded?(true)
        }
    }
}

/// Eine Änderung an der Handschrift – für Rückgängig und Wiederholen.
struct InkChange {
    var added: [PortableStroke]
    var removed: [PortableStroke]
}

/// Verwaltet die PencilKit-Zeichenfläche: ordnet jedem Strich eine feste Kennung zu,
/// meldet Änderungen ans Surface, übernimmt Änderungen vom Surface und streamt Striche live.
final class CanvasController: NSObject, PKCanvasViewDelegate, UIGestureRecognizerDelegate {
    static let pageHeight: CGFloat = 1120

    weak var canvas: PadCanvasView?
    let backdrop = BackdropView()
    let toolPicker = PKToolPicker()

    /// Schickt eine Nachricht ans Surface.
    var send: (JSON) -> Void = { _ in }
    /// Rückgängig/Wiederholen haben sich geändert.
    var onUndoStateChanged: (() -> Void)?
    /// Die Notiz braucht eine Seite mehr.
    var onNeedsPages: ((Int) -> Void)?

    private(set) var noteId: String?
    private(set) var pageWidth: CGFloat = 800
    private(set) var pageCount = 1
    private var paper = "lined"

    // Abgleich zwischen PencilKit-Strichen und Kennungen: gleiche Reihenfolge wie drawing.strokes.
    private var strokes: [PKStroke] = []
    private var keys: [String] = []
    private var groups: [[String]] = []
    private var portables: [String: PortableStroke] = [:]
    private var isAdjusting = false

    private var undoStack: [InkChange] = []
    private var redoStack: [InkChange] = []
    var canUndo: Bool { !undoStack.isEmpty }
    var canRedo: Bool { !redoStack.isEmpty }

    // Live-Übertragung
    private var liveSid: String?
    private var liveFinished = false
    private var liveTemplate: JSON = [:]
    private var liveBuffer: [Double] = []
    private var liveSentAt = Date.distantPast
    private var pendingLive: [String] = []
    private var remoteQueue: [(add: [PortableStroke], remove: [String])] = []

    // Radierer auf Bildern
    private var erasePoints: [CGPoint] = []
    private weak var eraseRecognizer: UIPanGestureRecognizer?

    // Zoom
    private var lastFit: CGFloat = 0
    private var relativeZoom: CGFloat = 1
    private var requestedPagesAt = 0

    private(set) var images: [PlacedImage] = []
    var drawingEnabled = true {
        didSet { updateToolPicker() }
    }

    private var defaults: UserDefaults { .standard }
    private var fingerDrawing: Bool { defaults.bool(forKey: "fingerDrawing") }
    private var autoShapes: Bool { defaults.object(forKey: "autoShapes") as? Bool ?? true }
    private var eraseImages: Bool { defaults.object(forKey: "pencilErasesImages") as? Bool ?? true }

    // MARK: - Zeichenfläche aufbauen

    func makeCanvas() -> PadCanvasView {
        let canvas = PadCanvasView()
        canvas.delegate = self
        canvas.backgroundColor = .clear
        canvas.isOpaque = false
        canvas.drawingPolicy = fingerDrawing ? .anyInput : .pencilOnly
        canvas.alwaysBounceVertical = true
        canvas.bouncesZoom = true
        canvas.contentInsetAdjustmentBehavior = .never
        canvas.showsHorizontalScrollIndicator = false
        canvas.tool = PKInkingTool(.pen, color: .black, width: 3)
        canvas.insertSubview(backdrop, at: 0)
        toolPicker.stateAutosaveName = "LernheftPadTools"
        toolPicker.addObserver(canvas)

        let live = LiveTouchRecognizer()
        live.cancelsTouchesInView = false
        live.delaysTouchesBegan = false
        live.delaysTouchesEnded = false
        live.delegate = self
        live.onBegan = { [weak self] touch, event in self?.liveBegan(touch, event) ?? false }
        live.onMoved = { [weak self] touch, event in self?.liveMoved(touch, event) }
        live.onEnded = { [weak self] cancelled in self?.liveEnded(cancelled: cancelled) }
        canvas.addGestureRecognizer(live)

        let erase = UIPanGestureRecognizer(target: self, action: #selector(handleImageErase(_:)))
        erase.delegate = self
        erase.maximumNumberOfTouches = 1
        erase.cancelsTouchesInView = false
        canvas.addGestureRecognizer(erase)
        eraseRecognizer = erase

        isAdjusting = true
        canvas.drawing = PKDrawing(strokes: strokes)
        isAdjusting = false
        canvas.onLayout = { [weak self] in self?.layout() }
        canvas.onWindow = { [weak self] in self?.updateToolPicker(force: true) }
        self.canvas = canvas
        lastFit = 0
        return canvas
    }

    func dismantle(_ canvas: PadCanvasView) {
        toolPicker.setVisible(false, forFirstResponder: canvas)
        toolPicker.removeObserver(canvas)
        canvas.resignFirstResponder()
        if self.canvas === canvas { self.canvas = nil }
    }

    func applySettings(zoomLocked: Bool, fingerDrawing: Bool) {
        guard let canvas else { return }
        canvas.pinchGestureRecognizer?.isEnabled = !zoomLocked
        let policy: PKCanvasViewDrawingPolicy = fingerDrawing ? .anyInput : .pencilOnly
        if canvas.drawingPolicy != policy { canvas.drawingPolicy = policy }
        eraseRecognizer?.allowedTouchTypes = fingerDrawing
            ? [NSNumber(value: UITouch.TouchType.pencil.rawValue), NSNumber(value: UITouch.TouchType.direct.rawValue)]
            : [NSNumber(value: UITouch.TouchType.pencil.rawValue)]
    }

    private var pickerShown: Bool?

    func updateToolPicker(force: Bool = false) {
        guard let canvas else { return }
        let enabled = drawingEnabled && noteId != nil
        canvas.drawingGestureRecognizer.isEnabled = enabled
        guard force || pickerShown != enabled else { return }
        pickerShown = enabled
        DispatchQueue.main.async { [weak self, weak canvas] in
            guard let self, let canvas, canvas.window != nil else { return }
            self.toolPicker.setVisible(enabled, forFirstResponder: canvas)
            if enabled { canvas.becomeFirstResponder() } else { canvas.resignFirstResponder() }
        }
    }

    // MARK: - Notiz und Seite

    func setNote(id: String?, pageWidth: CGFloat, pageCount: Int, paper: String) {
        let changed = id != noteId
        noteId = id
        if changed {
            load([])
            images = []
            backdrop.clearNote()
            relativeZoom = 1
            lastFit = 0
            requestedPagesAt = 0
        }
        let width = max(400, pageWidth)
        if abs(width - self.pageWidth) > 0.5 { lastFit = 0 }
        self.pageWidth = width
        self.pageCount = max(1, pageCount)
        self.paper = paper
        updateToolPicker()
        canvas?.setNeedsLayout()
        layout()
    }

    func setPageCount(_ count: Int) {
        pageCount = max(pageCount, count)
        layout()
    }

    func setImages(_ images: [PlacedImage], seamless: Bool) {
        self.images = images
        backdrop.setImages(images, seamless: seamless)
    }

    func setTextLayer(page: Int, data: Data?) {
        backdrop.setTextLayer(page: page, data: data)
    }

    func setSurfaceView(top: CGFloat, height: CGFloat) {
        backdrop.surfaceView = CGRect(x: 0, y: top, width: 0, height: height)
    }

    // MARK: - Zoom und Layout

    var zoom: CGFloat { max(canvas?.zoomScale ?? 1, 0.01) }

    private func layout() {
        guard let canvas, canvas.bounds.width > 50 else { return }
        let fit = canvas.bounds.width / pageWidth
        if abs(fit - lastFit) > 0.0001 {
            let topContent = canvas.contentOffset.y / max(canvas.zoomScale, 0.01)
            lastFit = fit
            canvas.minimumZoomScale = fit * 0.6
            canvas.maximumZoomScale = fit * 5
            let target = min(max(fit * relativeZoom, canvas.minimumZoomScale), canvas.maximumZoomScale)
            if abs(canvas.zoomScale - target) > 0.0001 { canvas.zoomScale = target }
            applyContentSize()
            let maxOffset = max(0, canvas.contentSize.height - canvas.bounds.height + canvas.contentInset.bottom)
            canvas.contentOffset = CGPoint(x: -canvas.contentInset.left, y: min(max(0, topContent * canvas.zoomScale), maxOffset))
        }
        applyContentSize()
    }

    private func applyContentSize() {
        guard let canvas else { return }
        let zoom = self.zoom
        let size = CGSize(width: pageWidth * zoom, height: CGFloat(pageCount) * Self.pageHeight * zoom)
        if canvas.contentSize != size { canvas.contentSize = size }
        let insetX = max(0, ((canvas.bounds.width - size.width) / 2).rounded(.down))
        let insets = UIEdgeInsets(top: 0, left: insetX, bottom: 160, right: insetX)
        if canvas.contentInset != insets { canvas.contentInset = insets }
        let frame = CGRect(origin: .zero, size: size)
        if backdrop.frame != frame { backdrop.frame = frame }
        backdrop.configure(paper: paper, pageCount: pageCount, pageWidth: pageWidth, zoom: zoom)
        backdrop.visibleRect = visibleContentRect
    }

    /// Der sichtbare Teil der Seite in Papierkoordinaten.
    var visibleContentRect: CGRect {
        guard let canvas else { return .zero }
        let zoom = self.zoom
        return CGRect(x: canvas.contentOffset.x / zoom, y: canvas.contentOffset.y / zoom,
                      width: canvas.bounds.width / zoom, height: canvas.bounds.height / zoom)
    }

    func resetZoom() {
        relativeZoom = 1
        lastFit = 0
        layout()
    }

    func scroll(toContentY y: CGFloat) {
        guard let canvas else { return }
        let maxOffset = max(0, canvas.contentSize.height - canvas.bounds.height + canvas.contentInset.bottom)
        let target = min(max(0, y * zoom - 12), maxOffset)
        canvas.setContentOffset(CGPoint(x: canvas.contentOffset.x, y: target), animated: true)
    }

    func scrollViewDidZoom(_ scrollView: UIScrollView) {
        if lastFit > 0 { relativeZoom = zoom / lastFit }
        applyContentSize()
    }

    func scrollViewDidScroll(_ scrollView: UIScrollView) {
        backdrop.visibleRect = visibleContentRect
    }

    // Umrechnung zwischen Bildschirm (Rahmen der Zeichenfläche) und Papier.
    func toContent(_ point: CGPoint) -> CGPoint {
        guard let canvas else { return point }
        return CGPoint(x: (point.x + canvas.contentOffset.x) / zoom, y: (point.y + canvas.contentOffset.y) / zoom)
    }

    func toContent(_ rect: CGRect) -> CGRect {
        let origin = toContent(rect.origin)
        return CGRect(x: origin.x, y: origin.y, width: rect.width / zoom, height: rect.height / zoom)
    }

    func toView(_ point: CGPoint) -> CGPoint {
        guard let canvas else { return point }
        return CGPoint(x: point.x * zoom - canvas.contentOffset.x, y: point.y * zoom - canvas.contentOffset.y)
    }

    func toView(_ rect: CGRect) -> CGRect {
        let origin = toView(rect.origin)
        return CGRect(x: origin.x, y: origin.y, width: rect.width * zoom, height: rect.height * zoom)
    }

    // MARK: - Werkzeug

    var toolColor: UIColor { (canvas?.tool as? PKInkingTool)?.color ?? UIColor(hex: "#1A1F2B") }

    /// Sichtbare Breite des gewählten Stifts – passend für gezeichnete Kurven und Kreuze.
    var toolWidth: CGFloat {
        guard let tool = canvas?.tool as? PKInkingTool else { return 2.4 }
        let kind = InkBridge.kind(of: PKInk(tool.inkType, color: tool.color))
        let visible = InkBridge.renderedWidth(size: Double(tool.width), kind: kind == "marker" ? "pen" : kind)
        return CGFloat(min(max(visible, 1.5), 6))
    }

    var drawing: PKDrawing { PKDrawing(strokes: strokes) }

    // MARK: - Vom Surface

    /// Alle Striche neu – beim Öffnen einer Notiz.
    func load(_ portables: [PortableStroke]) {
        remoteQueue.removeAll()
        pendingLive.removeAll()
        self.portables = [:]
        strokes = []
        keys = []
        groups = []
        for portable in portables where self.portables[portable.id] == nil {
            let stroke = InkBridge.stroke(portable)
            self.portables[portable.id] = portable
            strokes.append(stroke)
            keys.append(InkBridge.key(stroke))
            groups.append([portable.id])
        }
        undoStack.removeAll()
        redoStack.removeAll()
        pushDrawing()
        onUndoStateChanged?()
    }

    /// Einzelne Striche hinzu oder weg – z. B. wenn am Surface rückgängig gemacht wurde.
    func applyRemote(add: [PortableStroke], remove: [String]) {
        if liveSid != nil && !liveFinished {
            remoteQueue.append((add, remove))
            return
        }
        apply(add: add, remove: Set(remove))
    }

    private func apply(add: [PortableStroke], remove: Set<String>) {
        var newStrokes: [PKStroke] = []
        var newKeys: [String] = []
        var newGroups: [[String]] = []
        for index in strokes.indices {
            let group = groups[index]
            if group.contains(where: remove.contains) {
                for id in group {
                    if remove.contains(id) {
                        portables[id] = nil
                    } else if let portable = portables[id] {
                        // Ein zerteilter Strich: die übrigen Teile bleiben als eigene Striche.
                        let stroke = InkBridge.stroke(portable)
                        newStrokes.append(stroke)
                        newKeys.append(InkBridge.key(stroke))
                        newGroups.append([id])
                    }
                }
            } else {
                newStrokes.append(strokes[index])
                newKeys.append(keys[index])
                newGroups.append(group)
            }
        }
        for portable in add where portables[portable.id] == nil {
            let stroke = InkBridge.stroke(portable)
            portables[portable.id] = portable
            newStrokes.append(stroke)
            newKeys.append(InkBridge.key(stroke))
            newGroups.append([portable.id])
        }
        strokes = newStrokes
        keys = newKeys
        groups = newGroups
        pushDrawing()
        checkPages()
    }

    private func pushDrawing() {
        guard let canvas else { return }
        isAdjusting = true
        canvas.drawing = PKDrawing(strokes: strokes)
        isAdjusting = false
    }

    // MARK: - Eigene Änderungen

    func canvasViewDrawingDidChange(_ canvasView: PKCanvasView) {
        guard !isAdjusting else { return }
        var drawing = canvasView.drawing

        // Große, eindeutige Linien, Kreise und Rechtecke werden sauber.
        if autoShapes, canvasView.tool is PKInkingTool, drawing.strokes.count == strokes.count + 1,
           let last = drawing.strokes.last, let snapped = InkTools.snapShape(last) {
            drawing.strokes[drawing.strokes.count - 1] = snapped
            isAdjusting = true
            canvasView.drawing = drawing
            isAdjusting = false
        }

        var live = pendingLive
        pendingLive.removeAll()
        if let liveSid, !liveFinished, drawing.strokes.count > strokes.count {
            live.append(liveSid)
            liveFinished = true
            liveBuffer.removeAll()
        }
        commit(drawing, live: live)
    }

    /// Gleicht die neue Zeichnung mit dem bekannten Stand ab und meldet die Unterschiede.
    private func commit(_ drawing: PKDrawing, live: [String] = [], recordUndo: Bool = true) {
        let (added, removed) = diff(drawing)
        guard !added.isEmpty || !removed.isEmpty || !live.isEmpty else { return }
        sendOps(add: added, remove: removed.map(\.id), live: live)
        if recordUndo, !added.isEmpty || !removed.isEmpty {
            undoStack.append(InkChange(added: added, removed: removed))
            if undoStack.count > 100 { undoStack.removeFirst(undoStack.count - 100) }
            redoStack.removeAll()
            onUndoStateChanged?()
        }
        canvas?.undoManager?.removeAllActions()
        checkPages()
    }

    private func diff(_ drawing: PKDrawing) -> (added: [PortableStroke], removed: [PortableStroke]) {
        var pool: [String: [Int]] = [:]
        for (index, key) in keys.enumerated() { pool[key, default: []].append(index) }
        var newKeys: [String] = []
        var newGroups: [[String]] = []
        var added: [PortableStroke] = []
        newKeys.reserveCapacity(drawing.strokes.count)
        newGroups.reserveCapacity(drawing.strokes.count)
        for stroke in drawing.strokes {
            let key = InkBridge.key(stroke)
            if var list = pool[key], !list.isEmpty {
                let index = list.removeFirst()
                pool[key] = list
                newGroups.append(groups[index])
            } else {
                let parts = InkBridge.portable(stroke)
                for part in parts { portables[part.id] = part }
                added.append(contentsOf: parts)
                newGroups.append(parts.map(\.id))
            }
            newKeys.append(key)
        }
        var removed: [PortableStroke] = []
        for indices in pool.values {
            for index in indices {
                for id in groups[index] {
                    if let portable = portables.removeValue(forKey: id) { removed.append(portable) }
                }
            }
        }
        strokes = drawing.strokes
        keys = newKeys
        groups = newGroups
        return (added, removed)
    }

    private func sendOps(add: [PortableStroke], remove: [String], live: [String] = []) {
        guard let noteId else { return }
        var message = PadProtocol.message(PadProtocol.ops)
        message["noteId"] = noteId
        message["add"] = add.map(\.json)
        message["remove"] = remove
        if !live.isEmpty { message["live"] = live }
        send(message)
    }

    /// Ersetzt die Zeichnung durch eine bearbeitete Fassung (Glätten, Graph, Löschen …).
    func replaceDrawing(_ drawing: PKDrawing) {
        if let canvas {
            isAdjusting = true
            canvas.drawing = drawing
            isAdjusting = false
        }
        commit(drawing)
    }

    func addStrokes(_ new: [PKStroke]) {
        guard !new.isEmpty else { return }
        replaceDrawing(PKDrawing(strokes: strokes + new))
    }

    func undo() {
        guard let change = undoStack.popLast() else { return }
        apply(add: change.removed, remove: Set(change.added.map(\.id)))
        sendOps(add: change.removed, remove: change.added.map(\.id))
        redoStack.append(change)
        onUndoStateChanged?()
    }

    func redo() {
        guard let change = redoStack.popLast() else { return }
        apply(add: change.added, remove: Set(change.removed.map(\.id)))
        sendOps(add: change.added, remove: change.removed.map(\.id))
        undoStack.append(change)
        onUndoStateChanged?()
    }

    private func checkPages() {
        guard noteId != nil, !strokes.isEmpty else { return }
        let lowest = PKDrawing(strokes: strokes).bounds.maxY
        let limit = CGFloat(pageCount) * Self.pageHeight - 240
        if lowest > limit, requestedPagesAt != pageCount {
            requestedPagesAt = pageCount
            let wanted = max(pageCount + 1, Int(ceil((lowest + 240) / Self.pageHeight)))
            onNeedsPages?(wanted)
            setPageCount(wanted)
        }
    }

    // MARK: - Auswahl

    func strokeIndices(in rect: CGRect) -> IndexSet {
        InkTools.strokeIndices(in: drawing, rect: rect)
    }

    func ids(at indices: IndexSet) -> [String] {
        indices.flatMap { groups[$0] }
    }

    func strokeList(at indices: IndexSet) -> [PKStroke] {
        indices.map { strokes[$0] }
    }

    func removeStrokes(at indices: IndexSet) {
        guard !indices.isEmpty else { return }
        let kept = strokes.enumerated().filter { !indices.contains($0.offset) }.map(\.element)
        replaceDrawing(PKDrawing(strokes: kept))
    }

    func removeStrokes(withIDs ids: Set<String>) {
        let indices = IndexSet(groups.indices.filter { groups[$0].contains(where: ids.contains) })
        removeStrokes(at: indices)
    }

    func transformStrokes(at indices: IndexSet, _ transform: (PKStroke) -> PKStroke) {
        guard !indices.isEmpty else { return }
        var updated = strokes
        for index in indices { updated[index] = transform(updated[index]) }
        replaceDrawing(PKDrawing(strokes: updated))
    }

    // MARK: - Live-Übertragung

    private func liveBegan(_ touch: UITouch, _ event: UIEvent) -> Bool {
        guard drawingEnabled, noteId != nil, let canvas, let tool = canvas.tool as? PKInkingTool else { return false }
        guard touch.type == .pencil || (fingerDrawing && touch.type == .direct) else { return false }
        flushFinishedLive()
        liveSid = InkIDs.make()
        liveFinished = false
        liveBuffer.removeAll()
        let kind = InkBridge.kind(of: PKInk(tool.inkType, color: tool.color))
        let width = InkBridge.renderedWidth(size: Double(tool.width), kind: kind)
        liveTemplate = ["c": tool.color.hexString, "w": (width * 10).rounded() / 10, "k": kind, "wf": true]
        liveSentAt = .distantPast
        collect(touch, event)
        return true
    }

    private func liveMoved(_ touch: UITouch, _ event: UIEvent) {
        guard liveSid != nil, !liveFinished else { return }
        collect(touch, event)
        if Date().timeIntervalSince(liveSentAt) > 0.012 || liveBuffer.count > 60 { flushLive() }
    }

    private func liveEnded(cancelled: Bool) {
        guard let sid = liveSid else { return }
        if !liveFinished {
            flushLive()
            if cancelled {
                sendLiveCancel(sid)
            } else {
                // Gleich meldet PencilKit den fertigen Strich; bleibt er aus, verschwindet der Live-Strich.
                pendingLive.append(sid)
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in
                    guard let self, let index = self.pendingLive.firstIndex(of: sid) else { return }
                    self.pendingLive.remove(at: index)
                    self.sendLiveCancel(sid)
                }
            }
        }
        liveSid = nil
        liveFinished = false
        liveBuffer.removeAll()
        flushRemoteQueue()
    }

    private func flushFinishedLive() {
        if liveSid != nil { liveSid = nil }
        flushRemoteQueue()
    }

    private func flushRemoteQueue() {
        guard !remoteQueue.isEmpty else { return }
        let queued = remoteQueue
        remoteQueue.removeAll()
        for entry in queued { apply(add: entry.add, remove: Set(entry.remove)) }
    }

    private func collect(_ touch: UITouch, _ event: UIEvent) {
        guard let canvas else { return }
        let zoom = self.zoom
        let touches = event.coalescedTouches(for: touch) ?? [touch]
        for sample in touches {
            let location = sample.preciseLocation(in: canvas)
            var factor = 1.0
            if sample.type == .pencil, sample.maximumPossibleForce > 0 {
                let force = Double(sample.force / sample.maximumPossibleForce)
                factor = min(1.6, max(0.35, 0.55 + force * 0.9))
            }
            liveBuffer.append(((Double(location.x / zoom)) * 100).rounded() / 100)
            liveBuffer.append(((Double(location.y / zoom)) * 100).rounded() / 100)
            liveBuffer.append((factor * 100).rounded() / 100)
        }
    }

    private func flushLive() {
        guard let sid = liveSid, let noteId, !liveBuffer.isEmpty else { return }
        var message = liveTemplate
        message["t"] = PadProtocol.live
        message["noteId"] = noteId
        message["sid"] = sid
        message["p"] = liveBuffer
        send(message)
        liveBuffer.removeAll()
        liveSentAt = Date()
    }

    private func sendLiveCancel(_ sid: String) {
        guard let noteId else { return }
        var message = PadProtocol.message(PadProtocol.live)
        message["noteId"] = noteId
        message["sid"] = sid
        message["cancel"] = true
        send(message)
    }

    // MARK: - Radierer auf eingefügten Bildern

    @objc private func handleImageErase(_ recognizer: UIPanGestureRecognizer) {
        guard let canvas else { return }
        let point = recognizer.location(in: canvas)
        let onPaper = CGPoint(x: point.x / zoom, y: point.y / zoom)
        switch recognizer.state {
        case .began:
            erasePoints = [onPaper]
        case .changed:
            if let last = erasePoints.last, hypot(onPaper.x - last.x, onPaper.y - last.y) < 5 { return }
            erasePoints.append(onPaper)
        case .ended, .cancelled, .failed:
            let points = erasePoints
            erasePoints = []
            guard !points.isEmpty, let noteId else { return }
            let radius = max(8, ((canvas.tool as? PKEraserTool)?.width ?? 32) / 2)
            var message = PadProtocol.message(PadProtocol.eraseImage)
            message["noteId"] = noteId
            message["points"] = points.flatMap { [Double($0.x), Double($0.y)] }
            message["radius"] = Double(radius)
            send(message)
        default:
            break
        }
    }

    func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
        guard gestureRecognizer === eraseRecognizer else { return true }
        guard eraseImages, !images.isEmpty, drawingEnabled, let canvas else { return false }
        return canvas.tool is PKEraserTool
    }

    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer,
                           shouldRecognizeSimultaneouslyWith other: UIGestureRecognizer) -> Bool {
        true
    }

    func canvasViewDidBeginUsingTool(_ canvasView: PKCanvasView) {
        if !canvasView.isFirstResponder, drawingEnabled {
            toolPicker.setVisible(true, forFirstResponder: canvasView)
            canvasView.becomeFirstResponder()
        }
    }
}

// MARK: - SwiftUI

struct PadCanvas: UIViewRepresentable {
    let controller: CanvasController
    var drawingEnabled: Bool
    var zoomLocked: Bool
    var fingerDrawing: Bool

    func makeCoordinator() -> CanvasController { controller }

    func makeUIView(context: Context) -> PadCanvasView {
        let canvas = controller.makeCanvas()
        controller.applySettings(zoomLocked: zoomLocked, fingerDrawing: fingerDrawing)
        controller.drawingEnabled = drawingEnabled
        return canvas
    }

    func updateUIView(_ canvas: PadCanvasView, context: Context) {
        controller.applySettings(zoomLocked: zoomLocked, fingerDrawing: fingerDrawing)
        if controller.drawingEnabled != drawingEnabled { controller.drawingEnabled = drawingEnabled }
    }

    static func dismantleUIView(_ canvas: PadCanvasView, coordinator: CanvasController) {
        coordinator.dismantle(canvas)
    }
}
