import SwiftUI

/// Koppeln und Verbinden: QR-Code scannen oder Code abtippen. Mehr kann das iPad ohne Surface nicht.
struct ConnectView: View {
    @Environment(PadSession.self) private var session
    @State private var method: Method = .qr
    @State private var code = ""
    @State private var choosingNew = false
    @State private var showSettings = false
    @FocusState private var codeFocused: Bool

    enum Method: String, CaseIterable, Identifiable {
        case qr = "QR-Code scannen"
        case code = "Code eingeben"
        var id: String { rawValue }
    }

    private var connection: PadConnection { session.connection }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 22) {
                    header
                    if connection.isPairing {
                        pairingProgress
                    } else if connection.hasPairing && !choosingNew {
                        knownSurface
                    } else {
                        pairingCard
                    }
                    if let problem = connection.problem {
                        problemCard(problem)
                    }
                    helpCard
                }
                .padding(.horizontal, 24)
                .padding(.vertical, 32)
                .frame(maxWidth: 640)
                .frame(maxWidth: .infinity)
            }
            .background(background)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button {
                        showSettings = true
                    } label: {
                        Label("Einstellungen", systemImage: "gearshape")
                    }
                }
            }
            .sheet(isPresented: $showSettings) { SettingsView() }
        }
    }

    private var background: some View {
        LinearGradient(
            colors: [Color(uiColor: .systemGroupedBackground), Color.accentColor.opacity(0.08)],
            startPoint: .top, endPoint: .bottom
        )
        .ignoresSafeArea()
    }

    private var header: some View {
        VStack(spacing: 10) {
            ZStack {
                RoundedRectangle(cornerRadius: 22, style: .continuous)
                    .fill(LinearGradient(colors: [Color(red: 0.23, green: 0.40, blue: 0.95), Color(red: 0.15, green: 0.25, blue: 0.78)],
                                         startPoint: .topLeading, endPoint: .bottomTrailing))
                    .frame(width: 84, height: 84)
                    .shadow(color: .black.opacity(0.18), radius: 10, y: 4)
                Image(systemName: "pencil.and.scribble")
                    .font(.system(size: 38, weight: .semibold))
                    .foregroundStyle(.white)
            }
            Text("Lernheft Pad")
                .font(.largeTitle.weight(.bold))
            Text("Dein iPad als Zeichentablett für Lernheft Studio. Was du hier schreibst, erscheint sofort auf dem Surface.")
                .font(.body)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.bottom, 4)
    }

    // MARK: Bekanntes Surface

    private var knownSurface: some View {
        card {
            HStack(spacing: 14) {
                Image(systemName: "laptopcomputer")
                    .font(.title)
                    .foregroundStyle(Color.accentColor)
                    .frame(width: 44)
                VStack(alignment: .leading, spacing: 3) {
                    Text(connection.pairing?.serverName ?? "Surface")
                        .font(.title3.weight(.semibold))
                    if connection.phase == .searching {
                        HStack(spacing: 8) {
                            ProgressView().controlSize(.small)
                            Text("Verbinde …").foregroundStyle(.secondary)
                        }
                    } else {
                        Text("Getrennt").foregroundStyle(.secondary)
                    }
                }
                Spacer()
            }
            if connection.phase == .searching {
                Text("Lernheft Studio muss auf dem Surface laufen, und beide Geräte müssen im selben WLAN sein.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            HStack(spacing: 12) {
                if connection.phase != .searching {
                    Button {
                        connection.reconnect()
                    } label: {
                        Label("Verbinden", systemImage: "link")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                }
                Button {
                    connection.disconnect()
                    choosingNew = true
                } label: {
                    Label("Anderes Surface koppeln", systemImage: "qrcode")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(.bordered)
                .controlSize(.large)
            }
        }
    }

    // MARK: Neu koppeln

    private var pairingCard: some View {
        card {
            Text("Am Surface in Lernheft Studio auf „iPad verbinden“ klicken. Dann den QR-Code scannen oder den sechsstelligen Code eingeben.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
                .fixedSize(horizontal: false, vertical: true)
            Picker("Weg", selection: $method) {
                ForEach(Method.allCases) { Text($0.rawValue).tag($0) }
            }
            .pickerStyle(.segmented)

            switch method {
            case .qr:
                QRScannerView { text in
                    if let offer = PairingOffer(text) {
                        connection.pair(with: offer)
                    } else {
                        session.show("Das ist kein QR-Code von Lernheft Studio.")
                    }
                }
                .frame(height: 300)
                .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
                .overlay {
                    RoundedRectangle(cornerRadius: 16, style: .continuous)
                        .strokeBorder(Color.white.opacity(0.7), style: StrokeStyle(lineWidth: 3, dash: [18, 12]))
                        .padding(40)
                        .allowsHitTesting(false)
                }
            case .code:
                codeEntry
            }

            if connection.hasPairing && choosingNew {
                Button("Zurück zu \(connection.pairing?.serverName ?? "Surface")") {
                    choosingNew = false
                    connection.reconnect()
                }
                .font(.callout)
            }
        }
        .overlay(alignment: .bottom) { toastView }
    }

    private var codeEntry: some View {
        VStack(spacing: 16) {
            ZStack {
                TextField("", text: $code)
                    .keyboardType(.numberPad)
                    .textContentType(.oneTimeCode)
                    .focused($codeFocused)
                    .opacity(0.02)
                    .frame(height: 64)
                    .onChange(of: code) { _, value in
                        let digits = String(value.filter(\.isNumber).prefix(6))
                        if digits != value { code = digits }
                        if digits.count == 6 { connect() }
                    }
                HStack(spacing: 10) {
                    ForEach(0..<6, id: \.self) { index in
                        let characters = Array(code)
                        Text(index < characters.count ? String(characters[index]) : "")
                            .font(.system(size: 32, weight: .semibold, design: .rounded))
                            .monospacedDigit()
                            .frame(width: 48, height: 60)
                            .background(Color(uiColor: .tertiarySystemFill), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                            .overlay {
                                RoundedRectangle(cornerRadius: 12, style: .continuous)
                                    .strokeBorder(index == characters.count && codeFocused ? Color.accentColor : .clear, lineWidth: 2)
                            }
                        if index == 2 { Spacer().frame(width: 8) }
                    }
                }
                .allowsHitTesting(false)
            }
            .contentShape(Rectangle())
            .onTapGesture { codeFocused = true }

            Button {
                connect()
            } label: {
                Label("Verbinden", systemImage: "link")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(code.count != 6)
        }
        .onAppear { codeFocused = true }
    }

    private func connect() {
        guard code.count == 6 else { return }
        codeFocused = false
        connection.pair(code: code)
    }

    private var pairingProgress: some View {
        card {
            HStack(spacing: 14) {
                ProgressView().controlSize(.large)
                VStack(alignment: .leading, spacing: 4) {
                    Text(connection.serverName.isEmpty ? "Suche das Surface …" : "Verbinde mit \(connection.serverName) …")
                        .font(.title3.weight(.semibold))
                    Text("Das dauert meist nur ein paar Sekunden. Beim ersten Mal fragt iOS, ob Lernheft Pad im lokalen Netzwerk suchen darf – bitte erlauben.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 0)
            }
            Button("Abbrechen", role: .cancel) {
                connection.cancelPairing()
                code = ""
            }
            .buttonStyle(.bordered)
            .frame(maxWidth: .infinity, alignment: .trailing)
        }
    }

    private func problemCard(_ text: String) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(.orange)
            Text(text)
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .padding(16)
        .background(Color.orange.opacity(0.12), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
    }

    private var helpCard: some View {
        DisclosureGroup {
            VStack(alignment: .leading, spacing: 10) {
                tip("wifi", "iPad und Surface müssen im selben WLAN sein.")
                tip("personalhotspot", "Im Schul-WLAN sehen sich Geräte oft nicht. Dann am Surface einen mobilen Hotspot einschalten (Einstellungen → Netzwerk → Mobiler Hotspot) und das iPad damit verbinden.")
                tip("lock.shield", "iPad-Einstellungen → Datenschutz → Lokales Netzwerk: Lernheft Pad muss eingeschaltet sein.")
                tip("shield.lefthalf.filled", "Fragt Windows nach der Firewall, dort „Zulassen“ wählen. Ohne Freigabe ruft das Surface das iPad selbst an – das klappt meistens trotzdem.")
            }
            .padding(.top, 10)
        } label: {
            Label("Klappt nicht?", systemImage: "questionmark.circle")
                .font(.callout.weight(.medium))
        }
        .padding(16)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
    }

    private func tip(_ symbol: String, _ text: String) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: symbol)
                .foregroundStyle(Color.accentColor)
                .frame(width: 24)
            Text(text)
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    @ViewBuilder
    private var toastView: some View {
        if let toast = session.toast {
            Text(toast)
                .font(.callout.weight(.medium))
                .padding(.horizontal, 16)
                .padding(.vertical, 10)
                .background(.thickMaterial, in: Capsule())
                .padding(.bottom, 12)
                .transition(.opacity)
        }
    }

    private func card<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 16) {
            content()
        }
        .padding(20)
        .background(Color(uiColor: .secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 20, style: .continuous))
        .shadow(color: .black.opacity(0.06), radius: 12, y: 4)
    }
}
