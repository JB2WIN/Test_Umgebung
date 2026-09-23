import Foundation
import PencilKit
import UIKit

/// Ein Strich im gemeinsamen Format von iPad und Surface.
/// Punkte liegen als x, y und ein dritter Wert hintereinander. Bei Strichen von Lernheft Pad ist
/// der dritte Wert die Breite im Verhältnis zu `width` („wf“), bei alten Strichen der Druck (0–1).
struct PortableStroke {
    var id: String
    var color: String = "#1A1F2B"
    var width: Double = 2.5
    var kind: String = "pen"
    var points: [Double] = []
    var widthFactors: Bool = false

    var count: Int { points.count / 3 }

    init(id: String, color: String, width: Double, kind: String, points: [Double], widthFactors: Bool) {
        self.id = id
        self.color = color
        self.width = width
        self.kind = kind
        self.points = points
        self.widthFactors = widthFactors
    }

    init?(json: JSON) {
        let points = json.numbers("p")
        guard points.count >= 3 else { return nil }
        id = json.string("i") ?? InkIDs.make()
        color = json.string("c") ?? "#1A1F2B"
        width = json.number("w", 2.5)
        kind = json.string("k") ?? "pen"
        self.points = points
        widthFactors = json.flag("wf")
    }

    var json: JSON {
        var result: JSON = ["i": id, "c": color, "w": width, "k": kind, "p": points]
        if widthFactors { result["wf"] = true }
        return result
    }

    var bounds: CGRect {
        guard count > 0 else { return .null }
        var minX = Double.greatestFiniteMagnitude, minY = Double.greatestFiniteMagnitude
        var maxX = -Double.greatestFiniteMagnitude, maxY = -Double.greatestFiniteMagnitude
        for index in 0..<count {
            let x = points[index * 3], y = points[index * 3 + 1]
            minX = min(minX, x); maxX = max(maxX, x)
            minY = min(minY, y); maxY = max(maxY, y)
        }
        let half = width / 2
        return CGRect(x: minX - half, y: minY - half, width: maxX - minX + width, height: maxY - minY + width)
    }
}

enum InkIDs {
    /// Kurze, eindeutige Kennung für einen Strich.
    static func make() -> String {
        String(UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(12)).lowercased()
    }
}

/// Übersetzt zwischen PencilKit und dem gemeinsamen Format.
enum InkBridge {
    /// Abstand der Stützpunkte: fein genug für schöne Kurven, klein genug für schnelle Übertragung.
    static let sampleDistance: CGFloat = 2.5

    private static var dateCounter: Double = 0

    /// Jede selbst gebaute Linie bekommt einen eigenen Zeitstempel – daran erkennt der
    /// Abgleich später, welcher PencilKit-Strich zu welcher Kennung gehört.
    static func uniqueDate() -> Date {
        dateCounter += 1
        return Date(timeIntervalSinceReferenceDate: Date().timeIntervalSinceReferenceDate + dateCounter * 0.000_01)
    }

    static func kind(of ink: PKInk) -> String {
        let type = ink.inkType
        if type == .marker || type == .watercolor { return "marker" }
        if type == .pencil || type == .crayon { return "pencil" }
        return "pen"
    }

    static func inkType(for kind: String) -> PKInk.InkType {
        switch kind {
        case "marker": return .marker
        case "pencil": return .pencil
        default: return .pen
        }
    }

    /// Ein PencilKit-Strich wird zu einem oder – wenn der Radierer ihn zerteilt hat – mehreren Strichen.
    static func portable(_ stroke: PKStroke, ids: [String]? = nil) -> [PortableStroke] {
        let transform = stroke.transform
        let scale = sqrt(abs(transform.a * transform.d - transform.b * transform.c))
        let ranges: [ClosedRange<CGFloat>?] = stroke.mask == nil ? [nil] : stroke.maskedPathRanges.map { Optional($0) }
        let color = hex(stroke.ink.color)
        let inkKind = kind(of: stroke.ink)
        var result: [PortableStroke] = []
        for (index, range) in ranges.enumerated() {
            var locations: [CGPoint] = []
            var widths: [Double] = []
            for point in stroke.path.interpolatedPoints(in: range, by: .distance(sampleDistance)) {
                locations.append(point.location.applying(transform))
                widths.append(Double(max(point.size.width * scale, 0.2)))
            }
            guard !locations.isEmpty else { continue }
            let mean = widths.reduce(0, +) / Double(widths.count)
            var numbers: [Double] = []
            numbers.reserveCapacity(locations.count * 3)
            for (location, width) in zip(locations, widths) {
                numbers.append(round2(Double(location.x)))
                numbers.append(round2(Double(location.y)))
                numbers.append(round2(min(4, width / max(mean, 0.01))))
            }
            let id = ids.flatMap { index < $0.count ? $0[index] : nil } ?? InkIDs.make()
            result.append(PortableStroke(id: id, color: color, width: round2(mean), kind: inkKind, points: numbers, widthFactors: true))
        }
        return result
    }

    static func stroke(_ portable: PortableStroke) -> PKStroke {
        let count = portable.count
        var points: [PKStrokePoint] = []
        points.reserveCapacity(max(count, 2))
        for index in 0..<count {
            let x = portable.points[index * 3]
            let y = portable.points[index * 3 + 1]
            let third = portable.points[index * 3 + 2]
            let width = portable.widthFactors ? portable.width * max(third, 0.05) : portable.width
            let force = portable.widthFactors ? 1 : max(third, 0.05)
            points.append(PKStrokePoint(
                location: CGPoint(x: x, y: y),
                timeOffset: Double(index) * 0.008,
                size: CGSize(width: width, height: width),
                opacity: 1,
                force: CGFloat(force),
                azimuth: 0,
                altitude: .pi / 2
            ))
        }
        // Ein einzelner Punkt ergibt keinen Pfad – dann wird ein winziger Strich daraus.
        if points.count == 1, let only = points.first {
            points.append(PKStrokePoint(
                location: CGPoint(x: only.location.x + 0.6, y: only.location.y),
                timeOffset: 0.01,
                size: only.size,
                opacity: 1,
                force: only.force,
                azimuth: 0,
                altitude: .pi / 2
            ))
        }
        let path = PKStrokePath(controlPoints: points, creationDate: uniqueDate())
        let ink = PKInk(inkType(for: portable.kind), color: color(hex: portable.color))
        return PKStroke(ink: ink, path: path)
    }

    /// Wiedererkennungsmerkmal eines PencilKit-Strichs: ändert sich, sobald der Strich bewegt,
    /// umgefärbt oder angeradiert wird.
    static func key(_ stroke: PKStroke) -> String {
        let path = stroke.path
        let first = path.first?.location ?? .zero
        let t = stroke.transform
        var key = "\(path.creationDate.timeIntervalSinceReferenceDate)|\(path.count)|\(first.x),\(first.y)"
        key += "|\(t.a),\(t.b),\(t.c),\(t.d),\(t.tx),\(t.ty)|\(stroke.ink.inkType.rawValue)|\(hex(stroke.ink.color))"
        if stroke.mask != nil {
            key += "|" + stroke.maskedPathRanges.map { "\($0.lowerBound)-\($0.upperBound)" }.joined(separator: ",")
        }
        return key
    }

    private static func round2(_ value: Double) -> Double { (value * 100).rounded() / 100 }

    static func hex(_ color: UIColor) -> String {
        let light = color.resolvedColor(with: UITraitCollection(userInterfaceStyle: .light))
        var red: CGFloat = 0, green: CGFloat = 0, blue: CGFloat = 0, alpha: CGFloat = 0
        light.getRed(&red, green: &green, blue: &blue, alpha: &alpha)
        func byte(_ value: CGFloat) -> Int { Int(max(0, min(255, (value * 255).rounded()))) }
        return String(format: "#%02X%02X%02X", byte(red), byte(green), byte(blue))
    }

    static func color(hex: String) -> UIColor {
        var text = hex.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.hasPrefix("#") { text.removeFirst() }
        guard text.count == 6, let value = UInt32(text, radix: 16) else {
            return UIColor(red: 0.10, green: 0.12, blue: 0.17, alpha: 1)
        }
        return UIColor(
            red: CGFloat((value >> 16) & 0xFF) / 255,
            green: CGFloat((value >> 8) & 0xFF) / 255,
            blue: CGFloat(value & 0xFF) / 255,
            alpha: 1
        )
    }

    /// Einfacher Strich durch die angegebenen Punkte – für Graphen, Kreuze und Formen.
    static func line(points: [CGPoint], width: CGFloat, color: UIColor) -> PKStroke? {
        guard points.count >= 2 else { return nil }
        let size = CGSize(width: width, height: width)
        let strokePoints = points.enumerated().map { index, location in
            PKStrokePoint(location: location, timeOffset: TimeInterval(index) * 0.004, size: size,
                          opacity: 1, force: 1, azimuth: 0, altitude: .pi / 2)
        }
        let path = PKStrokePath(controlPoints: strokePoints, creationDate: uniqueDate())
        return PKStroke(ink: PKInk(.pen, color: color), path: path, transform: .identity, mask: nil)
    }
}

extension UIColor {
    convenience init(hex: String) {
        self.init(cgColor: InkBridge.color(hex: hex).cgColor)
    }

    var hexString: String { InkBridge.hex(self) }
}
