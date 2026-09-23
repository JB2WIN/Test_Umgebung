import PencilKit
import UIKit

/// Beispielinhalte für automatische Bildschirmfotos im Simulator.
/// Start mit dem Argument `-demo draw` (oder connect, picker, settings, function).
enum Demo {
    static let screen: String? = UserDefaults.standard.string(forKey: "demo")

    static func start(_ session: PadSession, screen: String) {
        guard screen != "connect" else { return }
        session.connection.startDemo(name: "Surface von Jonas")
        if screen == "picker" {
            session.receive([
                "t": PadProtocol.note, "id": NSNull(),
                "recent": [
                    ["id": UUID().uuidString, "title": "Quadratische Funktionen", "subject": "Mathe", "color": "#2F5BEA"],
                    ["id": UUID().uuidString, "title": "Erörterung – Aufbau", "subject": "Deutsch", "color": "#E0457B"],
                    ["id": UUID().uuidString, "title": "Simple Past vs. Present Perfect", "subject": "Englisch", "color": "#1E8A57"]
                ],
                "notebooks": [
                    ["id": UUID().uuidString, "name": "Mathe", "color": "#2F5BEA"],
                    ["id": UUID().uuidString, "name": "Deutsch", "color": "#E0457B"],
                    ["id": UUID().uuidString, "name": "Englisch", "color": "#1E8A57"]
                ]
            ])
            return
        }
        let noteId = UUID().uuidString
        if screen == "inktest" {
            inkTest(session, noteId: noteId)
            return
        }
        session.receive(["t": PadProtocol.note, "id": noteId, "title": "Quadratische Funktionen", "subject": "Mathe",
                         "color": "#2F5BEA", "paper": "grid", "pageCount": 2, "pageWidth": 800])
        var strokes: [JSON] = []
        if let function = try? MathExpression.compile("x^2 - 2x - 3") {
            var settings = GraphBuilder.Settings()
            settings.xMin = -3; settings.xMax = 5; settings.yMin = -5; settings.yMax = 6
            settings.color = UIColor(hex: "#2D4BE0")
            let graph = GraphBuilder.build(function: function, settings: settings, origin: CGPoint(x: 416, y: 544))
            strokes += graph.strokes.flatMap { InkBridge.portable($0) }.map(\.json)
        }
        strokes += scribble(x: 96, y: 150, width: 300).map(\.json)
        session.receive(["t": PadProtocol.ink, "noteId": noteId, "strokes": strokes])
        if let text = textLayer() {
            session.receive(["t": PadProtocol.text, "noteId": noteId, "page": 0, "scale": 3, "data": text.base64EncodedString()])
        }
        session.receive(["t": PadProtocol.view, "noteId": noteId, "top": 0, "height": 700])
    }

    /// Messbild: wie breit zeichnet PencilKit Striche bei welcher Punktgröße und welchem Druck?
    private static func inkTest(_ session: PadSession, noteId: String) {
        session.receive(["t": PadProtocol.note, "id": noteId, "title": "Strichtest", "subject": "",
                         "color": "#2F5BEA", "paper": "blank", "pageCount": 1, "pageWidth": 800])
        session.receive(["t": PadProtocol.ink, "noteId": noteId, "strokes": [JSON]()])
        var strokes: [PKStroke] = []
        let widths: [CGFloat] = [1, 2, 3, 5, 8]
        let rows: [(label: String, dense: Bool, force: CGFloat, ink: PKInk.InkType)] = [
            ("2 Punkte, Druck 1", false, 1, .pen),
            ("dicht, Druck 1", true, 1, .pen),
            ("dicht, Druck 0,5", true, 0.5, .pen),
            ("dicht, Druck 2", true, 2, .pen),
            ("dicht, Druck 4", true, 4, .pen),
            ("Monoline, Druck 1", true, 1, .monoline),
            ("Bleistift, Druck 1", true, 1, .pencil),
            ("Marker, Druck 1", true, 1, .marker)
        ]
        for (row, entry) in rows.enumerated() {
            let y = 80 + CGFloat(row) * 60
            for (column, width) in widths.enumerated() {
                let x = 260 + CGFloat(column) * 105
                var locations = [CGPoint(x: x, y: y), CGPoint(x: x + 90, y: y)]
                if entry.dense { locations = InkBridge.densify(locations) }
                var time: TimeInterval = 0
                var points: [PKStrokePoint] = []
                for (index, location) in locations.enumerated() {
                    if index > 0 { time += Double(hypot(location.x - locations[index - 1].x, 0)) / 350 }
                    points.append(PKStrokePoint(location: location, timeOffset: time, size: CGSize(width: width, height: width),
                                                opacity: 1, force: entry.force, azimuth: 0, altitude: .pi / 2))
                }
                let path = PKStrokePath(controlPoints: points, creationDate: InkBridge.uniqueDate())
                strokes.append(PKStroke(ink: PKInk(entry.ink, color: UIColor(hex: "#1A1F2B")), path: path))
            }
        }
        // Vergleich: So breit zeichnet das Surface dieselben Werte (Rechtecke genau in Punktgröße).
        let size = CGSize(width: 800, height: 1120)
        let format = UIGraphicsImageRendererFormat()
        format.scale = 3
        format.opaque = false
        let image = UIGraphicsImageRenderer(size: size, format: format).image { context in
            let attributes: [NSAttributedString.Key: Any] = [.font: UIFont.systemFont(ofSize: 15), .foregroundColor: UIColor.systemGray]
            for (row, entry) in rows.enumerated() {
                (entry.label as NSString).draw(at: CGPoint(x: 24, y: 70 + CGFloat(row) * 60), withAttributes: attributes)
            }
            let y = 80 + CGFloat(rows.count) * 60
            ("Soll-Breite" as NSString).draw(at: CGPoint(x: 24, y: y - 10), withAttributes: attributes)
            UIColor.systemRed.setFill()
            for (column, width) in widths.enumerated() {
                let x = 260 + CGFloat(column) * 105
                context.fill(CGRect(x: x, y: y - width / 2, width: 90, height: width))
                ("\(Int(width)) pt" as NSString).draw(at: CGPoint(x: x + 30, y: 40), withAttributes: attributes)
            }
        }
        if let data = image.pngData() {
            session.receive(["t": PadProtocol.text, "noteId": noteId, "page": 0, "scale": 3, "data": data.base64EncodedString()])
        }
        session.canvas.addStrokes(strokes)
    }

    private static func scribble(x: CGFloat, y: CGFloat, width: CGFloat) -> [PortableStroke] {
        var points: [Double] = []
        for index in 0...140 {
            let t = Double(index) / 140
            let px: Double = Double(x) + t * Double(width) + sin(t * 40) * 7
            let py: Double = Double(y) + cos(t * 40) * 11 - sin(t * 5) * 3
            let factor: Double = 1 + 0.25 * sin(t * 9)
            points.append(px)
            points.append(py)
            points.append(factor)
        }
        return [PortableStroke(id: InkIDs.make(), color: "#C62D2D", width: 2.6, kind: "pen", points: points, widthFactors: true)]
    }

    /// So ungefähr sieht die Textebene aus, die das Surface schickt.
    private static func textLayer() -> Data? {
        let size = CGSize(width: 800, height: 1120)
        let format = UIGraphicsImageRendererFormat()
        format.scale = 3
        format.opaque = false
        // Wie das Surface: im Dunkelmodus kommt der Text hell.
        let dark = UIScreen.main.traitCollection.userInterfaceStyle == .dark
        let ink = UIColor(hex: dark ? "#E9EEF4" : "#16202C")
        let image = UIGraphicsImageRenderer(size: size, format: format).image { _ in
            let title: [NSAttributedString.Key: Any] = [.font: UIFont.systemFont(ofSize: 22, weight: .semibold), .foregroundColor: ink]
            let body: [NSAttributedString.Key: Any] = [.font: UIFont.systemFont(ofSize: 17), .foregroundColor: ink]
            ("Scheitelpunktform" as NSString).draw(at: CGPoint(x: 64, y: 38), withAttributes: title)
            ("f(x) = a(x − d)² + e" as NSString).draw(at: CGPoint(x: 64, y: 72), withAttributes: body)
            ("Der Scheitelpunkt liegt bei S(d | e)." as NSString).draw(at: CGPoint(x: 64, y: 104), withAttributes: body)
        }
        return image.pngData()
    }
}
