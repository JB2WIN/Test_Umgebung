import UIKit
import PencilKit

/// Handschrift verschönern: Formen erkennen, Striche glätten, Ausschnitte als Bild rendern.
enum InkTools {

    // MARK: - Punkte

    static func points(of stroke: PKStroke, step: CGFloat = 3) -> [CGPoint] {
        stroke.path.interpolatedPoints(by: .distance(step)).map { $0.location.applying(stroke.transform) }
    }

    private static func distance(_ p: CGPoint, toLineFrom a: CGPoint, to b: CGPoint) -> CGFloat {
        let dx = b.x - a.x, dy = b.y - a.y
        let length = hypot(dx, dy)
        guard length > 0 else { return hypot(p.x - a.x, p.y - a.y) }
        return abs(dy * p.x - dx * p.y + b.x * a.y - b.y * a.x) / length
    }

    // MARK: - Formerkennung

    /// Wird nach jedem Strich aufgerufen: große, eindeutige Linien/Kreise/Rechtecke werden sauber.
    static func snapShape(_ stroke: PKStroke) -> PKStroke? {
        let pts = points(of: stroke)
        guard pts.count >= 6, let shape = recognize(pts, minSize: 60, strict: false) else { return nil }
        return rebuild(stroke, points: shape)
    }

    /// Für die Auswahl-Aktion „Begradigen": kleinere Mindestgröße.
    static func straighten(_ stroke: PKStroke) -> PKStroke {
        let pts = points(of: stroke)
        guard pts.count >= 4, let shape = recognize(pts, minSize: 24, strict: false) else { return stroke }
        return rebuild(stroke, points: shape)
    }

    static func recognize(_ pts: [CGPoint], minSize: CGFloat, strict: Bool) -> [CGPoint]? {
        guard let first = pts.first, let last = pts.last else { return nil }
        var minX = CGFloat.greatestFiniteMagnitude, minY = CGFloat.greatestFiniteMagnitude
        var maxX = -CGFloat.greatestFiniteMagnitude, maxY = -CGFloat.greatestFiniteMagnitude
        for p in pts {
            minX = min(minX, p.x); maxX = max(maxX, p.x)
            minY = min(minY, p.y); maxY = max(maxY, p.y)
        }
        let width = maxX - minX, height = maxY - minY
        let size = max(width, height)
        guard size >= minSize else { return nil }
        let chord = hypot(last.x - first.x, last.y - first.y)

        // Gerade Linie
        if chord > size * 0.85 {
            var maxDeviation: CGFloat = 0
            for p in pts {
                maxDeviation = max(maxDeviation, distance(p, toLineFrom: first, to: last))
            }
            guard maxDeviation < max(3, chord * (strict ? 0.025 : 0.05)) else { return nil }
            var end = last
            let angle = atan2(last.y - first.y, last.x - first.x)
            if abs(sin(angle)) < 0.07 { end.y = first.y }
            if abs(cos(angle)) < 0.07 { end.x = first.x }
            return interpolate([first, end], spacing: 6)
        }

        guard !strict, chord < size * 0.3, min(width, height) > 16 else { return nil }

        let cx = (minX + maxX) / 2, cy = (minY + maxY) / 2
        var rx = width / 2, ry = height / 2
        var ellipseError: CGFloat = 0, rectError: CGFloat = 0
        for p in pts {
            let nx = (p.x - cx) / rx, ny = (p.y - cy) / ry
            ellipseError += abs(hypot(nx, ny) - 1)
            rectError += min(abs(abs(nx) - 1), abs(abs(ny) - 1))
        }
        ellipseError /= CGFloat(pts.count)
        rectError /= CGFloat(pts.count)

        if rectError < 0.07, rectError < ellipseError * 0.7 {
            let corners = [
                CGPoint(x: minX, y: minY), CGPoint(x: maxX, y: minY),
                CGPoint(x: maxX, y: maxY), CGPoint(x: minX, y: maxY),
                CGPoint(x: minX, y: minY)
            ]
            return interpolate(corners, spacing: 6, sharpCorners: true)
        }

        if ellipseError < 0.12 {
            if abs(width - height) < size * 0.12 {
                let r = (rx + ry) / 2
                rx = r
                ry = r
            }
            return (0...72).map { i in
                let a = CGFloat(i) / 72 * 2 * .pi - .pi / 2
                return CGPoint(x: cx + rx * cos(a), y: cy + ry * sin(a))
            }
        }
        return nil
    }

    private static func interpolate(_ corners: [CGPoint], spacing: CGFloat, sharpCorners: Bool = false) -> [CGPoint] {
        guard corners.count > 1 else { return corners }
        var out: [CGPoint] = []
        for i in 0..<(corners.count - 1) {
            let a = corners[i], b = corners[i + 1]
            let steps = max(1, Int(hypot(b.x - a.x, b.y - a.y) / spacing))
            if sharpCorners { out.append(a); out.append(a) }
            for s in 0..<steps {
                let t = CGFloat(s) / CGFloat(steps)
                out.append(CGPoint(x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t))
            }
        }
        if let end = corners.last {
            if sharpCorners { out.append(end); out.append(end) }
            out.append(end)
        }
        return out
    }

    static func rebuild(_ stroke: PKStroke, points: [CGPoint]) -> PKStroke {
        let control = Array(stroke.path)
        let sample = control.isEmpty ? nil : control[control.count / 2]
        let size = sample?.size ?? CGSize(width: 3, height: 3)
        let strokePoints = points.enumerated().map { index, location in
            PKStrokePoint(
                location: location,
                timeOffset: TimeInterval(index) * 0.005,
                size: size,
                opacity: sample?.opacity ?? 1,
                force: sample?.force ?? 1,
                azimuth: sample?.azimuth ?? 0,
                altitude: sample?.altitude ?? .pi / 2
            )
        }
        let path = PKStrokePath(controlPoints: strokePoints, creationDate: InkBridge.uniqueDate())
        return PKStroke(ink: stroke.ink, path: path, transform: .identity, mask: nil)
    }

    // MARK: - Glätten

    /// Zittrige Striche beruhigen und die Strichstärke angleichen.
    static func smooth(_ stroke: PKStroke) -> PKStroke {
        let control = Array(stroke.path)
        guard control.count > 4 else { return stroke }
        let locations = control.map { $0.location.applying(stroke.transform) }
        let meanForce = control.reduce(0) { $0 + $1.force } / CGFloat(control.count)
        let meanWidth = control.reduce(0) { $0 + $1.size.width } / CGFloat(control.count)

        var smoothed: [PKStrokePoint] = []
        smoothed.reserveCapacity(control.count)
        for i in control.indices {
            let lo = max(0, i - 2), hi = min(control.count - 1, i + 2)
            var sx: CGFloat = 0, sy: CGFloat = 0
            for j in lo...hi {
                sx += locations[j].x
                sy += locations[j].y
            }
            let n = CGFloat(hi - lo + 1)
            let isEnd = i == 0 || i == control.count - 1
            let location = isEnd ? locations[i] : CGPoint(x: sx / n, y: sy / n)
            let point = control[i]
            let width = point.size.width * 0.4 + meanWidth * 0.6
            let aspect = point.size.height / max(point.size.width, 0.01)
            smoothed.append(PKStrokePoint(
                location: location,
                timeOffset: point.timeOffset,
                size: CGSize(width: width, height: width * aspect),
                opacity: point.opacity,
                force: point.force * 0.4 + meanForce * 0.6,
                azimuth: point.azimuth,
                altitude: point.altitude
            ))
        }
        let path = PKStrokePath(controlPoints: smoothed, creationDate: InkBridge.uniqueDate())
        return PKStroke(ink: stroke.ink, path: path, transform: .identity, mask: stroke.transform == .identity ? stroke.mask : nil)
    }

    // MARK: - Auswahl

    static func strokeIndices(in drawing: PKDrawing, rect: CGRect) -> IndexSet {
        var result = IndexSet()
        for (index, stroke) in drawing.strokes.enumerated() {
            let bounds = stroke.renderBounds
            let center = CGPoint(x: bounds.midX, y: bounds.midY)
            if rect.contains(center) {
                result.insert(index)
            } else if rect.intersects(bounds) {
                let overlap = rect.intersection(bounds)
                if overlap.width * overlap.height > 0.6 * bounds.width * bounds.height {
                    result.insert(index)
                }
            }
        }
        return result
    }

    static func bounds(of strokes: [PKStroke]) -> CGRect {
        strokes.reduce(CGRect.null) { $0.union($1.renderBounds) }
    }

    /// Findet handgezeichnete Kreuze und Punkte im gewählten Feld.
    /// Ein Kreuz sind zwei kurze Striche, die sich schneiden; ein Punkt ist ein einzelner, winziger Strich.
    /// Lange Striche wie Achsen oder Schrift bleiben außen vor.
    /// `color` grenzt auf eine Stiftfarbe ein – so zählen schwarze Achsen und Beschriftungen nicht mit.
    static func markerCenters(
        in drawing: PKDrawing,
        rect: CGRect,
        color: UIColor? = nil,
        maxSize: CGFloat = 46
    ) -> [CGPoint] {
        struct Candidate {
            var bounds: CGRect
            var points: [CGPoint]
        }

        var candidates: [Candidate] = []
        for stroke in drawing.strokes {
            let bounds = stroke.renderBounds
            guard rect.contains(CGPoint(x: bounds.midX, y: bounds.midY)) else { continue }
            guard max(bounds.width, bounds.height) <= maxSize else { continue }
            if let color, !colorsMatch(stroke.ink.color, color) { continue }
            let pts = points(of: stroke, step: 2)
            guard pts.count >= 2 else {
                if let single = pts.first { candidates.append(Candidate(bounds: bounds, points: [single, single])) }
                continue
            }
            candidates.append(Candidate(bounds: bounds, points: pts))
        }
        guard !candidates.isEmpty else { return [] }

        // Striche, die dicht beieinander liegen, gehören zum gleichen Zeichen.
        // Klein gehalten, damit zwei benachbarte Kreuze nicht zu einem verschmelzen.
        let reachInset: CGFloat = 4
        var groups: [[Candidate]] = []
        for candidate in candidates {
            let reach = candidate.bounds.insetBy(dx: -reachInset, dy: -reachInset)
            if let index = groups.firstIndex(where: { group in
                group.contains { reach.intersects($0.bounds.insetBy(dx: -reachInset, dy: -reachInset)) }
            }) {
                groups[index].append(candidate)
            } else {
                groups.append([candidate])
            }
        }

        var centers: [CGPoint] = []
        for group in groups {
            let bounds = group.reduce(CGRect.null) { $0.union($1.bounds) }
            let size = max(bounds.width, bounds.height)
            let center = CGPoint(x: bounds.midX, y: bounds.midY)
            guard size <= maxSize else { continue }

            if group.count == 1 {
                // Einzelner kurzer Strich = Punkt oder ein von Hand in einem Zug gemaltes Kreuz
                let length = strokeLength(group[0].points)
                if size <= 16 || length <= 20 { centers.append(center) }
                continue
            }
            // Kreuz: zwei Striche, die sich wirklich schneiden – und beide müssen kurz sein
            let allShort = group.allSatisfy { max($0.bounds.width, $0.bounds.height) <= maxSize }
            var crossing = false
            outer: for first in 0..<(group.count - 1) {
                for second in (first + 1)..<group.count where segmentsCross(group[first].points, group[second].points) {
                    crossing = true
                    break outer
                }
            }
            if crossing, allShort, group.count <= 3 { centers.append(center) }
        }
        return centers.sorted { $0.x < $1.x }
    }

    /// Vergleicht zwei Tintenfarben grob – kleine Unterschiede beim Zeichnen sind erlaubt.
    static func colorsMatch(_ first: UIColor, _ second: UIColor, tolerance: CGFloat = 0.34) -> Bool {
        let light = UITraitCollection(userInterfaceStyle: .light)
        let a = first.resolvedColor(with: light), b = second.resolvedColor(with: light)
        var r1: CGFloat = 0, g1: CGFloat = 0, b1: CGFloat = 0, a1: CGFloat = 0
        var r2: CGFloat = 0, g2: CGFloat = 0, b2: CGFloat = 0, a2: CGFloat = 0
        a.getRed(&r1, green: &g1, blue: &b1, alpha: &a1)
        b.getRed(&r2, green: &g2, blue: &b2, alpha: &a2)
        return sqrt(pow(r1 - r2, 2) + pow(g1 - g2, 2) + pow(b1 - b2, 2)) <= tolerance
    }

    /// Häufigste Farbe unter kurzen Strichen im Feld – nützlich, wenn die Kreuze farbig sind.
    static func dominantMarkerColor(in drawing: PKDrawing, rect: CGRect, maxSize: CGFloat = 46) -> UIColor? {
        var buckets: [String: (color: UIColor, count: Int)] = [:]
        for stroke in drawing.strokes {
            let bounds = stroke.renderBounds
            guard rect.contains(CGPoint(x: bounds.midX, y: bounds.midY)) else { continue }
            guard max(bounds.width, bounds.height) <= maxSize else { continue }
            let key = stroke.ink.color.hexString
            let entry = buckets[key] ?? (stroke.ink.color, 0)
            buckets[key] = (entry.color, entry.count + 1)
        }
        return buckets.values.max { $0.count < $1.count }?.color
    }

    private static func strokeLength(_ points: [CGPoint]) -> CGFloat {
        guard points.count > 1 else { return 0 }
        var length: CGFloat = 0
        for index in 1..<points.count {
            length += hypot(points[index].x - points[index - 1].x, points[index].y - points[index - 1].y)
        }
        return length
    }

    private static func segmentsCross(_ first: [CGPoint], _ second: [CGPoint]) -> Bool {
        guard first.count >= 2, second.count >= 2 else { return false }
        func orientation(_ a: CGPoint, _ b: CGPoint, _ c: CGPoint) -> CGFloat {
            (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)
        }
        // Grob abtasten reicht: Kreuze bestehen aus wenigen Segmenten.
        let a = stride(from: 0, to: first.count - 1, by: max(1, first.count / 8))
        let b = stride(from: 0, to: second.count - 1, by: max(1, second.count / 8))
        for i in a {
            let p1 = first[i], p2 = first[min(i + max(1, first.count / 8), first.count - 1)]
            for j in b {
                let q1 = second[j], q2 = second[min(j + max(1, second.count / 8), second.count - 1)]
                let d1 = orientation(p1, p2, q1)
                let d2 = orientation(p1, p2, q2)
                let d3 = orientation(q1, q2, p1)
                let d4 = orientation(q1, q2, p2)
                if ((d1 > 0) != (d2 > 0)) && ((d3 > 0) != (d4 > 0)) { return true }
            }
        }
        return false
    }

    static func dominantColorHex(_ strokes: [PKStroke]) -> String {
        var counts: [String: Int] = [:]
        for stroke in strokes where stroke.ink.inkType != .marker {
            counts[stroke.ink.color.hexString, default: 0] += 1
        }
        return counts.max { $0.value < $1.value }?.key ?? "#1A1F2B"
    }

    // MARK: - Bilder (für Texterkennung und KI)

    /// Rendert einen Ausschnitt immer hell auf Weiß – so lesen Vision und Gemini am besten.
    /// `backgrounds` sind importierte Seiten (z. B. aus einem PDF); sie kommen mit ins Bild,
    /// sonst sieht die KI von einer importierten Notiz nichts.
    static func image(
        of drawing: PKDrawing,
        rect: CGRect,
        images: [PlacedImage] = [],
        maxSide: CGFloat = 1600
    ) -> UIImage {
        let area = rect.insetBy(dx: -16, dy: -16)
        let scale = min(2, maxSide / max(area.width, area.height, 1))
        var ink = UIImage()
        UITraitCollection(userInterfaceStyle: .light).performAsCurrent {
            ink = drawing.image(from: area, scale: scale)
        }
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1
        format.opaque = true
        let size = CGSize(width: area.width * scale, height: area.height * scale)
        return UIGraphicsImageRenderer(size: size, format: format).image { context in
            UIColor.white.setFill()
            context.fill(CGRect(origin: .zero, size: size))
            let cg = context.cgContext
            cg.saveGState()
            cg.scaleBy(x: scale, y: scale)
            cg.translateBy(x: -area.minX, y: -area.minY)
            for placed in images where placed.frame.intersects(area) {
                placed.image.draw(in: placed.frame)
            }
            cg.restoreGState()
            ink.draw(in: CGRect(origin: .zero, size: size))
        }
    }

    /// Macht den weißen Hintergrund eines importierten Scans durchsichtig, sodass nur die
    /// Schrift übrig bleibt und das Papier der Notiz durchscheint.
    /// `forDarkMode` hellt grauschwarze Tinte auf, farbige Stellen bleiben farbig – wie bei PencilKit.
    static func inkLayer(from image: UIImage, forDarkMode: Bool) -> UIImage? {
        guard let source = image.cgImage else { return nil }
        let width = source.width, height = source.height
        guard width > 0, height > 0 else { return nil }

        guard let context = CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ), let raw = context.data else { return nil }
        context.draw(source, in: CGRect(x: 0, y: 0, width: width, height: height))
        let pixels = raw.bindMemory(to: UInt8.self, capacity: context.bytesPerRow * height)
        let rowBytes = context.bytesPerRow

        for row in 0..<height {
            for column in 0..<width {
                let index = row * rowBytes + column * 4
                let red = Double(pixels[index])
                let green = Double(pixels[index + 1])
                let blue = Double(pixels[index + 2])
                let luminance = (0.299 * red + 0.587 * green + 0.114 * blue) / 255

                // Je heller die Stelle, desto durchsichtiger. Weiß verschwindet ganz.
                let alpha = min(1, (1 - luminance) * 1.2)
                if alpha < 0.07 {
                    pixels[index] = 0; pixels[index + 1] = 0; pixels[index + 2] = 0; pixels[index + 3] = 0
                    continue
                }

                var outRed = red, outGreen = green, outBlue = blue
                let spread = max(red, max(green, blue)) - min(red, min(green, blue))
                if forDarkMode && spread < 45 {
                    // Graue bis schwarze Tinte wird hell, damit sie auf dunklem Papier lesbar ist.
                    outRed = 255 - red; outGreen = 255 - green; outBlue = 255 - blue
                }

                pixels[index] = UInt8(outRed * alpha)
                pixels[index + 1] = UInt8(outGreen * alpha)
                pixels[index + 2] = UInt8(outBlue * alpha)
                pixels[index + 3] = UInt8(alpha * 255)
            }
        }

        guard let output = context.makeImage() else { return nil }
        return UIImage(cgImage: output, scale: image.scale, orientation: .up)
    }

    /// Radiert Kreise aus einem importierten Bild heraus – die Stellen werden durchsichtig.
    /// `points` und `radius` liegen im Koordinatensystem des Bildes.
    static func erasing(_ image: UIImage, at points: [CGPoint], radius: CGFloat) -> UIImage {
        guard !points.isEmpty else { return image }
        let format = UIGraphicsImageRendererFormat()
        format.scale = image.scale
        format.opaque = false
        return UIGraphicsImageRenderer(size: image.size, format: format).image { context in
            image.draw(at: .zero)
            context.cgContext.setBlendMode(.clear)
            for point in points {
                context.cgContext.fillEllipse(in: CGRect(
                    x: point.x - radius,
                    y: point.y - radius,
                    width: radius * 2,
                    height: radius * 2
                ))
            }
        }
    }

}
