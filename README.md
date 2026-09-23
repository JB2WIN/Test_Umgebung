# Lernheft Studio & Lernheft Pad

**Lernheft Studio** ist die Hauptapp fürs Surface (Windows 11, WPF/.NET 10): Notizen mit
Textfeldern wie in OneNote, Fächer, Hausaufgaben, Karteikarten, Stundenplan, KI-Helfer und alle
Einstellungen. **Lernheft Pad** macht das iPad bei Bedarf zum Zeichentablett (SwiftUI/PencilKit):
Kopplung per QR-Code oder Code, Striche erscheinen live in der offenen Notiz am Surface.

Bedienung und Einrichtung: [ANLEITUNG.md](ANLEITUNG.md)

## Aufbau

| Ordner | Inhalt |
|---|---|
| `windows/Studio.Core` | Datenmodell, Speicher, Umzug alter Notizen, KI (Gemini), Stundenplan, Verbindung zum iPad (WebSocket-Server) |
| `windows/Studio.App` | die WPF-App (Editor, Fenster, Import/Export, Screenshot-Prüfung mit `--shots <Ordner>`) |
| `windows/Studio.Tests` | Tests für den Kern (`dotnet test windows/Studio.Tests`) |
| `installer/Studio.wxs` | MSI-Paket (WiX 5, pro Benutzer) |
| `ipad` | Lernheft Pad (XcodeGen: `cd ipad && xcodegen generate`) |
| `design` | App-Symbole |

## Bauen

- Windows: `dotnet build windows/LernheftStudio.slnx` – die GitHub-Action baut Tests, Installer
  und Bildschirmfotos (Zweig `ci-shots-windows`).
- iPad: die GitHub-Action baut ein unsigniertes IPA und Simulator-Bildschirmfotos (Zweig `ci-shots-ipad`).
  Demo-Ansichten: App mit `-demo draw|select|picker|function|settings|connect` starten.

## Verbindung

WebSocket im eigenen WLAN, JSON-Nachrichten (`windows/Studio.Core/PadProtocol.cs` ↔
`ipad/LernheftPad/PadProtocol.swift`). Das Surface lauscht auf Port 47650, das iPad auf 47660
(Rückweg, falls die Windows-Firewall sperrt). Gefunden wird das Surface über den QR-Code, Bonjour
(`_lernheft._tcp`) oder einen kurzen Rundruf im /24-Netz.
