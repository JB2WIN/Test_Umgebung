import PencilKit
import SwiftUI

/// Der Zeichenbildschirm: Papier vom Surface, Stift, Auswahl-Aktionen und Werkzeuge.
struct DrawingView: View {
    @Environment(PadSession.self) private var session
    @Environment(\.colorScheme) private var colorScheme

    @AppStorage("zoomLocked") private var zoomLocked = false
    @AppStorage("fingerDrawing") private var fingerDrawing = false
    @AppStorage("connectSmooth") private var connectSmooth = true
    @AppStorage("connectCrosses") private var drawCrosses = true

    @State private var mode: Mode = .draw
    @State private var dragRect: CGRect?
    @State private var selection: CGRect?
    @State private var points: [CGPoint] = []
    @State private var showFunction = false
    @State private var showSettings = false
    @State private var showNotes = false
    @State private var alert: String?
    @State private var insertSource: InsertFlow.Source?

    enum Mode { case draw, select, points }

    private var canvas: CanvasController { session.canvas }
    private var connected: Bool { session.connection.isConnected }

    var body: some View {
        VStack(spacing: 0) {
            topBar
            Divider()
            if mode == .select {
                selectionBar
                Divider()
            }
            if mode == .points {
                pointBar
                Divider()
            }
            ZStack {
                desk.ignoresSafeArea(edges: .bottom)
                PadCanvas(
                    controller: canvas,
                    drawingEnabled: mode == .draw && connected && session.note != nil && !showFunction && !showSettings && !showNotes,
                    zoomLocked: zoomLocked,
                    fingerDrawing: fingerDrawing
                )
                .ignoresSafeArea(edges: .bottom)
                .opacity(session.note == nil ? 0 : 1)

                if mode == .select { selectionOverlay }
                if mode == .points { pointOverlay }

                if session.note == nil && session.knowsNote {
                    NotePickerView(inline: true)
                        .transition(.opacity)
                } else if session.note == nil {
                    ProgressView("Warte auf das Surface …")
                }

                if !connected { reconnectBanner }
                busyOverlay
                toastView
            }
        }
        .sheet(isPresented: $showFunction) {
            FunctionSheet { settings, function in insertGraph(settings: settings, function: function) }
        }
        .sheet(isPresented: $showSettings) { SettingsView() }
        .insertFlow(source: $insertSource)
        .sheet(isPresented: $showNotes) {
            NavigationStack {
                NotePickerView(inline: false)
                    .navigationTitle("Notizen")
                    .navigationBarTitleDisplayMode(.inline)
                    .toolbar {
                        ToolbarItem(placement: .confirmationAction) { Button("Fertig") { showNotes = false } }
                    }
            }
            .presentationDetents([.medium, .large])
        }
        .alert("Hinweis", isPresented: Binding(get: { alert != nil }, set: { if !$0 { alert = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(alert ?? "")
        }
        .onAppear {
            switch Demo.screen {
            case "settings": showSettings = true
            case "function": showFunction = true
            case "select":
                mode = .select
                selection = CGRect(x: 80, y: 120, width: 340, height: 70)
            default: break
            }
        }
        .onChange(of: session.note?.id) { _, _ in
            mode = .draw
            selection = nil
            points = []
        }
    }

    private var desk: Color {
        colorScheme == .dark ? Color(red: 0.051, green: 0.063, blue: 0.078) : Color(red: 0.890, green: 0.906, blue: 0.933)
    }

    // MARK: - Obere Leiste

    private var topBar: some View {
        HStack(spacing: 6) {
            connectionChip
            if let note = session.note {
                Button {
                    showNotes = true
                } label: {
                    HStack(spacing: 8) {
                        Circle().fill(Color(uiColor: UIColor(hex: note.color))).frame(width: 10, height: 10)
                        VStack(alignment: .leading, spacing: 0) {
                            Text(note.title).font(.headline).lineLimit(1)
                            if !note.subject.isEmpty {
                                Text(note.subject).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                            }
                        }
                        Image(systemName: "chevron.down").font(.caption2.weight(.bold)).foregroundStyle(.secondary)
                    }
                    .padding(.horizontal, 10)
                    .padding(.vertical, 5)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Notiz wechseln")
            }
            Spacer(minLength: 8)
            if session.note != nil {
                barButton("arrow.uturn.backward", "Rückgängig", enabled: session.canUndo) { canvas.undo() }
                barButton("arrow.uturn.forward", "Wiederholen", enabled: session.canRedo) { canvas.redo() }
                Divider().frame(height: 22).padding(.horizontal, 4)
                barButton("lasso", "Auswählen", active: mode == .select) {
                    mode = mode == .select ? .draw : .select
                    selection = nil
                    dragRect = nil
                }
                toolsMenu
                barButton(zoomLocked ? "lock.fill" : "lock.open", zoomLocked ? "Zoom ist gesperrt" : "Zoom sperren",
                          active: zoomLocked, tint: .orange) {
                    zoomLocked.toggle()
                }
            }
            barButton("gearshape", "Einstellungen") { showSettings = true }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .background(.bar)
    }

    private var connectionChip: some View {
        Menu {
            Section(connected ? "Verbunden mit \(session.connection.serverName)" : "Verbindung wird gesucht") {
                Button(role: .destructive) {
                    session.connection.disconnect()
                } label: {
                    Label("Trennen", systemImage: "xmark.circle")
                }
            }
        } label: {
            HStack(spacing: 6) {
                Circle()
                    .fill(connected ? Color.green : Color.orange)
                    .frame(width: 8, height: 8)
                Image(systemName: "laptopcomputer")
                    .font(.subheadline)
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 7)
            .background(Color(uiColor: .tertiarySystemFill), in: Capsule())
        }
        .accessibilityLabel(connected ? "Verbunden mit \(session.connection.serverName)" : "Nicht verbunden")
    }

    private var toolsMenu: some View {
        Menu {
            Section("Einfügen") {
                Button { insertSource = .files } label: { Label("Datei einfügen (PDF, Bild, Text) …", systemImage: "doc.badge.plus") }
                Button { insertSource = .photos } label: { Label("Foto einfügen …", systemImage: "photo.on.rectangle") }
                if InsertSources.scannerAvailable {
                    Button { insertSource = .scanner } label: { Label("Seiten scannen …", systemImage: "doc.viewfinder") }
                }
            }
            Button { showFunction = true } label: { Label("Funktion zeichnen", systemImage: "chart.xyaxis.line") }
            Button {
                mode = .points
                points = []
            } label: { Label("Punkte verbinden", systemImage: "point.topleft.down.curvedto.point.bottomright.up") }
            Divider()
            Button { session.addPage() } label: { Label("Seite hinzufügen", systemImage: "doc.badge.plus") }
            Button { canvas.resetZoom() } label: { Label("Seitenbreite", systemImage: "arrow.left.and.right") }
            if let top = session.surfaceTop {
                Button { canvas.scroll(toContentY: CGFloat(top)) } label: {
                    Label("Zum Ausschnitt am Surface", systemImage: "rectangle.dashed")
                }
            }
        } label: {
            Image(systemName: "square.grid.2x2")
                .font(.system(size: 18, weight: .regular))
                .frame(width: 40, height: 36)
                .contentShape(Rectangle())
        }
        .accessibilityLabel("Werkzeuge")
    }

    private func barButton(_ symbol: String, _ label: String, enabled: Bool = true, active: Bool = false,
                           tint: Color = .accentColor, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 18, weight: .regular))
                .frame(width: 40, height: 36)
                .background(active ? tint.opacity(0.18) : .clear, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                .foregroundStyle(active ? tint : (enabled ? Color.primary : Color.secondary.opacity(0.5)))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .accessibilityLabel(label)
    }

    // MARK: - Auswahl

    private var selectionBar: some View {
        BarScroll {
            HStack(spacing: 8) {
                if selection == nil {
                    Label("Zieh einen Rahmen um deine Handschrift", systemImage: "hand.draw")
                        .foregroundStyle(.secondary)
                        .padding(.horizontal, 8)
                } else {
                    action("Kreuze verbinden", "point.3.connected.trianglepath.dotted") { connectMarkers() }
                    action("Schönschrift", "wand.and.stars") { convert("script") }
                    action("In Text", "text.viewfinder") { convert("text") }
                    action("Mathe lösen", "function") { askAI("math") }
                    action("KI fragen", "sparkles") { askAI("ask") }
                    action("Glätten", "scribble.variable") { transform(InkTools.smooth) }
                    action("Begradigen", "line.diagonal") { transform(InkTools.straighten) }
                    action("Löschen", "trash", role: .destructive) { deleteSelection() }
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 8)
        }
        .modifier(TrailingDone(prominent: true) {
            mode = .draw
            selection = nil
        })
    }

    private func action(_ title: String, _ symbol: String, role: ButtonRole? = nil, perform: @escaping () -> Void) -> some View {
        Button(role: role, action: perform) {
            Label(title, systemImage: symbol)
        }
        .buttonStyle(.bordered)
        .disabled(session.busy != nil || !connected)
    }

    private var selectionOverlay: some View {
        let shown = dragRect ?? selection.map { canvas.toView($0) }
        return ZStack(alignment: .topLeading) {
            Color.black.opacity(0.001)
            if let shown {
                Path { $0.addRect(shown) }
                    .fill(Color.accentColor.opacity(0.08))
                Path { $0.addRect(shown) }
                    .stroke(Color.accentColor, style: StrokeStyle(lineWidth: 2, dash: [7, 5]))
            }
        }
        .contentShape(Rectangle())
        .gesture(
            DragGesture(minimumDistance: 0, coordinateSpace: .local)
                .onChanged { value in
                    let start = value.startLocation, current = value.location
                    guard hypot(current.x - start.x, current.y - start.y) > 6 else { return }
                    dragRect = CGRect(x: min(start.x, current.x), y: min(start.y, current.y),
                                      width: abs(current.x - start.x), height: abs(current.y - start.y))
                }
                .onEnded { _ in
                    if let rect = dragRect, rect.width > 12, rect.height > 12 {
                        selection = canvas.toContent(rect)
                    } else {
                        selection = nil
                    }
                    dragRect = nil
                }
        )
    }

    private func connectMarkers() {
        guard let rect = selection else { return }
        let drawing = canvas.drawing
        // Zuerst nur die Farbe des aktuellen Stifts – so bleiben schwarze Achsen und Zahlen außen vor.
        var centers = InkTools.markerCenters(in: drawing, rect: rect, color: canvas.toolColor)
        if centers.count < 2, let dominant = InkTools.dominantMarkerColor(in: drawing, rect: rect) {
            centers = InkTools.markerCenters(in: drawing, rect: rect, color: dominant)
        }
        if centers.count < 2 {
            centers = InkTools.markerCenters(in: drawing, rect: rect)
        }
        guard centers.count >= 2 else {
            alert = centers.isEmpty
                ? "Im Rahmen habe ich keine Kreuze gefunden. Zieh den Rahmen etwas größer oder tippe die Punkte mit „Punkte verbinden“ selbst an."
                : "Ich habe nur ein Kreuz gefunden. Für eine Kurve brauche ich mindestens zwei."
            return
        }
        // Erst zeigen, dann zeichnen: falsch erkannte Punkte kannst du wegtippen.
        points = centers
        drawCrosses = false
        selection = nil
        mode = .points
        session.show("\(centers.count) Kreuze gefunden – falsche wegtippen, fehlende dazutippen, dann „Zeichnen“.")
    }

    private func convert(_ modeName: String) {
        guard let rect = selection else { return }
        session.convert(rect: rect, mode: modeName)
        selection = nil
    }

    private func askAI(_ modeName: String) {
        guard let rect = selection else { return }
        session.askAI(rect: rect, mode: modeName)
    }

    private func transform(_ change: (PKStroke) -> PKStroke) {
        guard let rect = selection else { return }
        let indices = canvas.strokeIndices(in: rect)
        guard !indices.isEmpty else {
            session.show("Im Rahmen ist keine Handschrift.")
            return
        }
        canvas.transformStrokes(at: indices, change)
    }

    private func deleteSelection() {
        guard let rect = selection else { return }
        canvas.removeStrokes(at: canvas.strokeIndices(in: rect))
        session.deleteText(in: rect)
        selection = nil
    }

    // MARK: - Punkte verbinden

    private var pointBar: some View {
        BarScroll {
            HStack(spacing: 10) {
                if points.isEmpty {
                    Label("Punkte antippen – nochmal tippen entfernt sie", systemImage: "hand.tap")
                        .foregroundStyle(.secondary)
                } else {
                    Text("\(points.count) Punkt\(points.count == 1 ? "" : "e")")
                        .font(.subheadline.weight(.semibold))
                        .monospacedDigit()
                }
                Picker("Form", selection: $connectSmooth) {
                    Text("Glatte Kurve").tag(true)
                    Text("Geraden").tag(false)
                }
                .pickerStyle(.segmented)
                .frame(width: 210)

                Toggle(isOn: $drawCrosses) {
                    Label("Kreuze zeichnen", systemImage: "xmark")
                }
                .toggleStyle(.button)
                .buttonStyle(.bordered)

                Button {
                    if !points.isEmpty { points.removeLast() }
                } label: {
                    Label("Zurück", systemImage: "arrow.uturn.backward")
                }
                .buttonStyle(.bordered)
                .disabled(points.isEmpty)

                Button {
                    insertConnectedPoints()
                } label: {
                    Label("Zeichnen", systemImage: "scribble.variable")
                }
                .buttonStyle(.borderedProminent)
                .disabled(points.count < 2 || !connected)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 8)
        }
        .modifier(TrailingDone(prominent: false) {
            mode = .draw
            points = []
        })
    }

    private var pointOverlay: some View {
        ZStack(alignment: .topLeading) {
            Color.black.opacity(0.001)
            ForEach(Array(points.enumerated()), id: \.offset) { index, point in
                let shown = canvas.toView(point)
                ZStack {
                    Path { path in
                        path.move(to: CGPoint(x: 2, y: 2))
                        path.addLine(to: CGPoint(x: 18, y: 18))
                        path.move(to: CGPoint(x: 18, y: 2))
                        path.addLine(to: CGPoint(x: 2, y: 18))
                    }
                    .stroke(Color.accentColor, style: StrokeStyle(lineWidth: 2.5, lineCap: .round))
                    .frame(width: 20, height: 20)
                    Text("\(index + 1)")
                        .font(.caption2.weight(.bold))
                        .foregroundStyle(Color.accentColor)
                        .offset(x: 14, y: -12)
                }
                .position(x: shown.x, y: shown.y)
            }
        }
        .contentShape(Rectangle())
        .gesture(
            DragGesture(minimumDistance: 0, coordinateSpace: .local)
                .onEnded { value in
                    let moved = hypot(value.location.x - value.startLocation.x, value.location.y - value.startLocation.y)
                    guard moved < 12 else { return }
                    let tapped = canvas.toContent(value.location)
                    // Auf einen vorhandenen Punkt tippen heißt: wieder weg damit.
                    if let index = points.firstIndex(where: { hypot($0.x - tapped.x, $0.y - tapped.y) < 24 / canvas.zoom }) {
                        points.remove(at: index)
                    } else {
                        points.append(tapped)
                    }
                }
        )
    }

    private func insertConnectedPoints() {
        guard points.count >= 2 else { return }
        let color = canvas.toolColor
        let width = canvas.toolWidth
        var strokes: [PKStroke] = []
        if drawCrosses {
            let arm: CGFloat = 7
            for point in points {
                if let stroke = InkBridge.line(points: [CGPoint(x: point.x - arm, y: point.y - arm), CGPoint(x: point.x + arm, y: point.y + arm)],
                                               width: width, color: color) { strokes.append(stroke) }
                if let stroke = InkBridge.line(points: [CGPoint(x: point.x + arm, y: point.y - arm), CGPoint(x: point.x - arm, y: point.y + arm)],
                                               width: width, color: color) { strokes.append(stroke) }
            }
        }
        let curve = connectSmooth
            ? GraphBuilder.functionCurve(through: points, color: color, width: width)
            : GraphBuilder.curve(through: points, smooth: false, color: color, width: width)
        if let curve { strokes.append(curve) }
        canvas.addStrokes(strokes)
        points = []
    }

    // MARK: - Funktion

    private func insertGraph(settings: GraphBuilder.Settings, function: ((Double) -> Double?)?) {
        // Nullpunkt in die Mitte des sichtbaren Bereichs, aufs Kästchenraster gerundet
        let visible = canvas.visibleContentRect
        let width = CGFloat(settings.xMax - settings.xMin) * settings.unit
        let height = CGFloat(settings.yMax - settings.yMin) * settings.unit
        var left = visible.midX - width / 2
        left = min(max(left, 16), max(16, canvas.pageWidth - width - 16))
        var top = visible.midY - height / 2
        top = max(top, 16)
        let origin = CGPoint(
            x: ((left - CGFloat(settings.xMin) * settings.unit) / 32).rounded() * 32,
            y: ((top + CGFloat(settings.yMax) * settings.unit) / 32).rounded() * 32
        )
        let result = GraphBuilder.build(function: function, settings: settings, origin: origin)
        canvas.addStrokes(result.strokes)
        session.addLabels(result.labels)
    }

    // MARK: - Hinweise

    private var reconnectBanner: some View {
        VStack {
            HStack(spacing: 12) {
                ProgressView()
                VStack(alignment: .leading, spacing: 2) {
                    Text("Verbindung zum Surface unterbrochen").font(.callout.weight(.semibold))
                    Text("Ich verbinde neu – bis dahin ist das Zeichnen pausiert.").font(.footnote).foregroundStyle(.secondary)
                }
            }
            .padding(.horizontal, 18)
            .padding(.vertical, 12)
            .background(.thickMaterial, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
            .shadow(color: .black.opacity(0.12), radius: 10, y: 3)
            .padding(.top, 14)
            Spacer()
        }
    }

    @ViewBuilder
    private var busyOverlay: some View {
        if let busy = session.busy {
            VStack(spacing: 14) {
                Text(busy).font(.callout)
                ProgressView().progressViewStyle(.linear).frame(width: 220)
                Button("Abbrechen") { session.cancelBusy() }
                    .buttonStyle(.bordered)
            }
            .padding(24)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
            .shadow(radius: 12, y: 4)
        }
    }

    @ViewBuilder
    private var toastView: some View {
        if let toast = session.toast {
            VStack {
                Spacer()
                Text(toast)
                    .font(.callout.weight(.medium))
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 18)
                    .padding(.vertical, 11)
                    .background(.thickMaterial, in: Capsule())
                    .shadow(color: .black.opacity(0.12), radius: 8, y: 2)
                    .padding(.bottom, 96)
                    .padding(.horizontal, 24)
            }
            .allowsHitTesting(false)
            .transition(.opacity)
        }
    }
}

/// Liste der Notizen vom Surface: öffnen oder eine neue anlegen.
struct NotePickerView: View {
    @Environment(PadSession.self) private var session
    @Environment(\.dismiss) private var dismiss
    let inline: Bool
    @State private var insertSource: InsertFlow.Source?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                if inline {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Welche Notiz?")
                            .font(.title.weight(.bold))
                        Text("Am Surface ist gerade keine Notiz offen. Wähle eine aus – sie öffnet sich auf beiden Geräten.")
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                if !session.recent.isEmpty {
                    section("Zuletzt bearbeitet") {
                        ForEach(session.recent) { note in
                            row(color: note.color, title: note.title, detail: note.subject, symbol: "doc.text") {
                                session.open(note.id)
                                if !inline { dismiss() }
                            }
                        }
                    }
                }
                if inline {
                    section("Datei vom iPad") {
                        row(color: "#6E7A8A", title: "PDF oder Bild als neue Notiz …", detail: "Aus Dateien oder iCloud Drive", symbol: "doc.badge.plus") {
                            insertSource = .files
                        }
                        if InsertSources.scannerAvailable {
                            row(color: "#6E7A8A", title: "Seiten scannen …", detail: "Mit der Kamera – wird eine neue Notiz", symbol: "doc.viewfinder") {
                                insertSource = .scanner
                            }
                        }
                    }
                }
                if !session.notebooks.isEmpty {
                    section("Neue Notiz in …") {
                        ForEach(session.notebooks) { notebook in
                            row(color: notebook.color, title: notebook.name, detail: nil, symbol: "plus") {
                                session.newNote(in: notebook.id)
                                if !inline { dismiss() }
                            }
                        }
                    }
                }
                if session.recent.isEmpty && session.notebooks.isEmpty {
                    Text("Öffne am Surface eine Notiz – sie erscheint dann hier.")
                        .foregroundStyle(.secondary)
                }
            }
            .padding(24)
            .frame(maxWidth: 620, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .background(inline ? Color(uiColor: .systemGroupedBackground) : Color.clear)
        .insertFlow(source: $insertSource, newNote: true)
    }

    private func section<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title.uppercased())
                .font(.caption.weight(.semibold))
                .tracking(0.8)
                .foregroundStyle(.secondary)
            VStack(spacing: 0) { content() }
                .background(Color(uiColor: .secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        }
    }

    private func row(color: String, title: String, detail: String?, symbol: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 12) {
                RoundedRectangle(cornerRadius: 3).fill(Color(uiColor: UIColor(hex: color))).frame(width: 6, height: 30)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).font(.body.weight(.medium)).foregroundStyle(.primary).lineLimit(1)
                    if let detail, !detail.isEmpty {
                        Text(detail).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                }
                Spacer()
                Image(systemName: symbol).foregroundStyle(Color.accentColor)
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 11)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

/// Waagrecht scrollende Leiste – bleibt links vom festen „Fertig“-Knopf.
struct BarScroll<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) { content }
    }
}

/// Setzt „Fertig“ fest an den rechten Rand der Leiste, neben den scrollbaren Teil.
struct TrailingDone: ViewModifier {
    let prominent: Bool
    let action: () -> Void

    func body(content: Content) -> some View {
        HStack(spacing: 0) {
            content
            Divider().frame(height: 28)
            Group {
                if prominent {
                    Button("Fertig", action: action).buttonStyle(.borderedProminent)
                } else {
                    Button("Fertig", action: action).buttonStyle(.bordered)
                }
            }
            .padding(.horizontal, 12)
        }
        .background(.bar)
    }
}
