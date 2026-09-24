import SwiftUI
import UniformTypeIdentifiers

/// Einstellungen fürs Zeichnen, die Verbindung und den Umzug der alten Notizen.
struct SettingsView: View {
    @Environment(PadSession.self) private var session
    @Environment(\.dismiss) private var dismiss

    @AppStorage("autoShapes") private var autoShapes = true
    @AppStorage("fingerDrawing") private var fingerDrawing = false
    @AppStorage("pencilErasesImages") private var pencilErasesImages = true
    @AppStorage("zoomLocked") private var zoomLocked = false

    @State private var confirmForget = false
    @State private var pickFolder = false

    private var connection: PadConnection { session.connection }
    private var migration: MigrationModel { session.migration }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Toggle(isOn: $autoShapes) {
                        Label("Formen automatisch begradigen", systemImage: "square.on.circle")
                    }
                    Toggle(isOn: $fingerDrawing) {
                        Label("Mit dem Finger zeichnen", systemImage: "hand.point.up.left")
                    }
                    Toggle(isOn: $pencilErasesImages) {
                        Label("Radierer wirkt auch auf eingefügte Seiten", systemImage: "eraser")
                    }
                    Toggle(isOn: $zoomLocked) {
                        Label("Zoom sperren", systemImage: "lock")
                    }
                } header: {
                    Text("Zeichnen")
                } footer: {
                    Text("Große Linien, Kreise und Rechtecke werden beim Loslassen sauber. Ohne „Mit dem Finger zeichnen“ scrollt der Finger und nur der Stift schreibt.")
                }

                Section("Surface") {
                    HStack {
                        Label(connection.pairing?.serverName ?? (connection.serverName.isEmpty ? "Nicht gekoppelt" : connection.serverName), systemImage: "laptopcomputer")
                        Spacer()
                        statusBadge
                    }
                    if connection.isConnected {
                        Button("Trennen", role: .destructive) { connection.disconnect() }
                    } else if connection.hasPairing {
                        Button("Verbinden") { connection.reconnect() }
                    }
                    if connection.hasPairing {
                        Button("Kopplung löschen …", role: .destructive) { confirmForget = true }
                    }
                }

                Section {
                    migrationContent
                } header: {
                    Text("Alte Notizen übertragen")
                } footer: {
                    Text("Holt alles aus der bisherigen Lernheft-App: Handschrift, Text, eingescannte Seiten, Hausaufgaben, Karteikarten und Stundenplan. Was am Surface schon da ist, wird übersprungen.")
                }

                Section("Über") {
                    LabeledContent("Version", value: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0")
                    Text("Lernheft Stift ist das Zeichentablett für Lernheft Studio. Notizen, Fächer, Hausaufgaben und KI verwaltest du am Surface.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }
            }
            .navigationTitle("Einstellungen")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Fertig") { dismiss() }
                }
            }
            .confirmationDialog("Kopplung löschen?", isPresented: $confirmForget, titleVisibility: .visible) {
                Button("Löschen", role: .destructive) { connection.forget() }
            } message: {
                Text("Danach verbindest du das iPad wieder mit QR-Code oder Code.")
            }
            .fileImporter(isPresented: $pickFolder, allowedContentTypes: [.folder]) { result in
                if case .success(let url) = result { migration.start(folder: url) }
            }
        }
    }

    @ViewBuilder
    private var statusBadge: some View {
        switch connection.phase {
        case .connected:
            Text("Verbunden").font(.caption.weight(.semibold)).foregroundStyle(.green)
        case .searching:
            HStack(spacing: 6) {
                ProgressView().controlSize(.mini)
                Text("Suche …").font(.caption).foregroundStyle(.secondary)
            }
        case .idle:
            Text("Getrennt").font(.caption).foregroundStyle(.secondary)
        }
    }

    @ViewBuilder
    private var migrationContent: some View {
        switch migration.phase {
        case .idle:
            VStack(alignment: .leading, spacing: 6) {
                Text("1. Mit dem Surface verbinden.")
                Text("2. „Ordner wählen“ tippen und „Auf meinem iPad → Lernheft“ auswählen.")
            }
            .font(.callout)
            Button {
                pickFolder = true
            } label: {
                Label("Ordner wählen …", systemImage: "folder")
            }
            .disabled(!connection.isConnected)
        case .reading:
            HStack {
                ProgressView()
                Text("Lese die alten Notizen …")
            }
        case .sending(let done, let total):
            VStack(alignment: .leading, spacing: 8) {
                Text("Übertrage \(done) von \(total) Dateien …")
                ProgressView(value: Double(done), total: Double(max(total, 1)))
            }
        case .waiting:
            HStack {
                ProgressView()
                Text("Das Surface übernimmt die Notizen …")
            }
        case .finished(let text):
            Label(text, systemImage: "checkmark.circle.fill")
                .foregroundStyle(.green)
            Button("Nochmal übertragen") { migration.reset() }
        case .failed(let text):
            Label(text, systemImage: "exclamationmark.triangle.fill")
                .foregroundStyle(.orange)
            Button("Nochmal versuchen") { migration.reset() }
        }
    }
}
