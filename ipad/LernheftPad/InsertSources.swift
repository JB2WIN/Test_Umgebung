import PhotosUI
import SwiftUI
import UIKit
import UniformTypeIdentifiers
import VisionKit

/// Eine Datei, die vom iPad in die Notiz am Surface soll.
struct OutgoingFile {
    var name: String
    var data: Data
}

/// Bereitet Dateien, Fotos und Scans fürs Surface auf.
enum InsertSources {
    /// Was die Dateien-App anbieten soll (inkl. iCloud Drive).
    static var fileTypes: [UTType] {
        var types: [UTType] = [.pdf, .image, .svg, .plainText, .rtf]
        if let markdown = UTType(filenameExtension: "md") { types.append(markdown) }
        return types
    }

    /// Größte Datenmenge pro Übertragung – darüber wird es im WLAN zäh.
    static let maxBytes = 80 * 1024 * 1024

    /// Liest eine Datei aus der Dateien-App. Fotos (auch HEIC) werden zu JPEG, damit Windows sie sicher lesen kann.
    static func load(_ url: URL) throws -> OutgoingFile {
        let access = url.startAccessingSecurityScopedResource()
        defer { if access { url.stopAccessingSecurityScopedResource() } }
        var data = Data()
        var coordinateError: NSError?
        var readError: Error?
        NSFileCoordinator().coordinate(readingItemAt: url, options: [], error: &coordinateError) { readable in
            do {
                data = try Data(contentsOf: readable)
            } catch {
                readError = error
            }
        }
        if let error = coordinateError ?? readError { throw error }
        let name = url.lastPathComponent
        let type = UTType(filenameExtension: url.pathExtension.lowercased())
        if let type, type.conforms(to: .image), !type.conforms(to: .svg), !type.conforms(to: .png) {
            return image(data, name: name)
        }
        return OutgoingFile(name: name, data: data)
    }

    /// Foto als JPEG (PNG bleibt PNG, damit durchsichtige Stellen erhalten bleiben).
    static func image(_ data: Data, name: String) -> OutgoingFile {
        let base = (name as NSString).deletingPathExtension
        guard let image = UIImage(data: data) else { return OutgoingFile(name: name, data: data) }
        let upright = normalized(image)
        if let jpeg = upright.jpegData(compressionQuality: 0.88) {
            return OutgoingFile(name: (base.isEmpty ? "Foto" : base) + ".jpg", data: jpeg)
        }
        return OutgoingFile(name: name, data: data)
    }

    /// Eingescannte Seiten als ein PDF – am Surface wird daraus je Seite ein Blatt.
    static func pdf(from pages: [UIImage], name: String) -> OutgoingFile {
        let renderer = UIGraphicsPDFRenderer(bounds: CGRect(x: 0, y: 0, width: 595, height: 842))
        let data = renderer.pdfData { context in
            for page in pages {
                let size = page.size
                let bounds = CGRect(x: 0, y: 0, width: 595, height: 595 * size.height / max(size.width, 1))
                context.beginPage(withBounds: bounds, pageInfo: [:])
                page.draw(in: bounds)
            }
        }
        return OutgoingFile(name: name, data: data)
    }

    /// Kamerabilder drehen, damit Windows sie aufrecht zeigt.
    private static func normalized(_ image: UIImage) -> UIImage {
        guard image.imageOrientation != .up else { return image }
        let format = UIGraphicsImageRendererFormat()
        format.scale = image.scale
        return UIGraphicsImageRenderer(size: image.size, format: format).image { _ in
            image.draw(in: CGRect(origin: .zero, size: image.size))
        }
    }

    static var scannerAvailable: Bool { VNDocumentCameraViewController.isSupported }
}

/// Der Dokumentenscanner von iOS: erkennt Blattkanten, entzerrt und schärft.
struct DocumentScanner: UIViewControllerRepresentable {
    var onFinish: ([UIImage]) -> Void

    func makeCoordinator() -> Coordinator { Coordinator(onFinish: onFinish) }

    func makeUIViewController(context: Context) -> VNDocumentCameraViewController {
        let controller = VNDocumentCameraViewController()
        controller.delegate = context.coordinator
        return controller
    }

    func updateUIViewController(_ controller: VNDocumentCameraViewController, context: Context) {}

    final class Coordinator: NSObject, VNDocumentCameraViewControllerDelegate {
        let onFinish: ([UIImage]) -> Void

        init(onFinish: @escaping ([UIImage]) -> Void) {
            self.onFinish = onFinish
        }

        func documentCameraViewController(_ controller: VNDocumentCameraViewController, didFinishWith scan: VNDocumentCameraScan) {
            onFinish((0..<scan.pageCount).map { scan.imageOfPage(at: $0) })
        }

        func documentCameraViewControllerDidCancel(_ controller: VNDocumentCameraViewController) {
            onFinish([])
        }

        func documentCameraViewController(_ controller: VNDocumentCameraViewController, didFailWithError error: Error) {
            onFinish([])
        }
    }
}

/// Alles rund ums Einfügen: Dateien-App, Fotos, Scanner und die Frage „wohin?“.
struct InsertFlow: ViewModifier {
    @Environment(PadSession.self) private var session
    @Binding var source: Source?
    /// Ohne offene Notiz entsteht am Surface eine neue.
    var newNote: Bool

    enum Source: Identifiable {
        case files, photos, scanner
        var id: Int { hashValue }
    }

    @State private var showFiles = false
    @State private var showPhotos = false
    @State private var showScanner = false
    @State private var photoItems: [PhotosPickerItem] = []
    @State private var pending: [OutgoingFile] = []
    @State private var askPlacement = false
    @State private var preparing = false

    func body(content: Content) -> some View {
        content
            .onChange(of: source) { _, value in
                guard let value else { return }
                source = nil
                switch value {
                case .files: showFiles = true
                case .photos: showPhotos = true
                case .scanner: showScanner = true
                }
            }
            .fileImporter(isPresented: $showFiles, allowedContentTypes: InsertSources.fileTypes, allowsMultipleSelection: true) { result in
                guard case .success(let urls) = result, !urls.isEmpty else { return }
                prepare {
                    try urls.map { try InsertSources.load($0) }
                }
            }
            .photosPicker(isPresented: $showPhotos, selection: $photoItems, maxSelectionCount: 20, matching: .images)
            .onChange(of: photoItems) { _, items in
                guard !items.isEmpty else { return }
                photoItems = []
                preparing = true
                Task { @MainActor in
                    var files: [OutgoingFile] = []
                    for (index, item) in items.enumerated() {
                        if let data = try? await item.loadTransferable(type: Data.self) {
                            files.append(InsertSources.image(data, name: items.count == 1 ? "Foto" : "Foto \(index + 1)"))
                        }
                    }
                    preparing = false
                    offer(files)
                }
            }
            .fullScreenCover(isPresented: $showScanner) {
                DocumentScanner { pages in
                    showScanner = false
                    guard !pages.isEmpty else { return }
                    let formatter = DateFormatter()
                    formatter.dateFormat = "d.M.yyyy HH.mm"
                    offer([InsertSources.pdf(from: pages, name: "Scan \(formatter.string(from: Date())).pdf")])
                }
                .ignoresSafeArea()
            }
            .confirmationDialog(placementTitle, isPresented: $askPlacement, titleVisibility: .visible) {
                Button("Als neue Seiten") { send(placement: "pages") }
                if !newNote {
                    Button("Hier auf der Seite") { send(placement: "here") }
                }
                Button("Abbrechen", role: .cancel) { pending = [] }
            } message: {
                Text(newNote ? "Am Surface entsteht dafür eine neue Notiz." : "„Hier“ setzt die Datei an die Stelle, die du gerade siehst.")
            }
            .overlay {
                if preparing {
                    ProgressView("Wird vorbereitet …")
                        .padding(24)
                        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
                }
            }
    }

    private var placementTitle: String {
        pending.count == 1 ? "„\(pending[0].name)“ einfügen" : "\(pending.count) Dateien einfügen"
    }

    private func prepare(_ work: @escaping () throws -> [OutgoingFile]) {
        preparing = true
        DispatchQueue.global(qos: .userInitiated).async {
            let result = Result { try work() }
            DispatchQueue.main.async {
                preparing = false
                switch result {
                case .success(let files): offer(files)
                case .failure(let error): session.show("Die Datei ließ sich nicht lesen: \(error.localizedDescription)")
                }
            }
        }
    }

    private func offer(_ files: [OutgoingFile]) {
        guard !files.isEmpty else { return }
        let total = files.reduce(0) { $0 + $1.data.count }
        guard total <= InsertSources.maxBytes else {
            session.show("Das sind \(total / 1_048_576) MB – zu viel auf einmal. Bitte weniger oder kleinere Dateien wählen.")
            return
        }
        pending = files
        if newNote {
            send(placement: "pages")
        } else {
            askPlacement = true
        }
    }

    private func send(placement: String) {
        let files = pending
        pending = []
        session.insertFiles(files, placement: placement, newNote: newNote)
    }
}

extension View {
    func insertFlow(source: Binding<InsertFlow.Source?>, newNote: Bool = false) -> some View {
        modifier(InsertFlow(source: source, newNote: newNote))
    }
}
