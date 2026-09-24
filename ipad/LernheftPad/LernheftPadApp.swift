import SwiftUI

@main
struct LernheftPadApp: App {
    @State private var session = PadSession()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(session)
                .onAppear {
                    if let screen = Demo.screen {
                        Demo.start(session, screen: screen)
                    } else {
                        session.connection.resume()
                    }
                }
        }
        .onChange(of: scenePhase) { _, phase in
            guard Demo.screen == nil else { return }
            switch phase {
            case .active: session.connection.resume()
            case .background: session.connection.suspend()
            default: break
            }
        }
    }
}

/// Verbunden (oder kurz unterbrochen, während eine Notiz offen war): Zeichnen. Sonst: Koppeln.
struct RootView: View {
    @Environment(PadSession.self) private var session
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        Group {
            if session.connection.isConnected || (session.connection.phase == .searching && session.note != nil && !session.connection.isPairing) {
                DrawingView()
            } else {
                ConnectView()
            }
        }
        .animation(.easeInOut(duration: 0.25), value: session.connection.isConnected)
        // Aus einer anderen App geteilt (z. B. Vorschau): Datei annehmen und ans Surface geben.
        .onOpenURL { url in session.receiveShared(url) }
        .modifier(SharedFileFlow())
        .onAppear { session.themeChanged(dark: colorScheme == .dark) }
        .onChange(of: colorScheme) { _, scheme in session.themeChanged(dark: scheme == .dark) }
    }
}
