import UIKit
import PencilKit

/// Rechnet Funktionsterme aus, die man mit der Tastatur eintippt: „x^2", „0,5x²-2x+1", „sin(x)".
/// Erlaubt sind + - * / ^, Klammern, sin cos tan sqrt abs ln log exp, pi und e.
enum MathExpression {
    enum ParseError: LocalizedError {
        case empty
        case unexpected(String)

        var errorDescription: String? {
            switch self {
            case .empty: return "Tipp einen Funktionsterm ein, z. B. x^2 - 2x + 1"
            case .unexpected(let part): return "Das versteh ich nicht: „\(part)“"
            }
        }
    }

    /// Macht aus dem eingetippten Text eine Funktion, die man beliebig auswerten kann.
    static func compile(_ text: String) throws -> (Double) -> Double? {
        var cleaned = text.lowercased()
        // Schreibweisen, die man auf dem iPad tippt oder aus dem Buch abschreibt
        for (from, to) in [
            ("f(x)=", ""), ("y=", ""), ("f(x) =", ""), ("y =", ""),
            ("²", "^2"), ("³", "^3"), ("·", "*"), ("×", "*"), ("÷", "/"),
            ("−", "-"), ("–", "-"), ("√", "sqrt"), ("π", "pi"), (",", ".")
        ] {
            cleaned = cleaned.replacingOccurrences(of: from, with: to)
        }
        cleaned = cleaned.replacingOccurrences(of: " ", with: "")
        guard !cleaned.isEmpty else { throw ParseError.empty }

        let tokens = try tokenize(cleaned)
        var parser = Parser(tokens: tokens)
        let node = try parser.parseExpression()
        guard parser.isAtEnd else { throw ParseError.unexpected(parser.rest) }
        return { x in
            let value = node.value(x)
            return value.isFinite ? value : nil
        }
    }

    // MARK: - Zerlegen

    private enum Token: Equatable {
        case number(Double)
        case variable
        case identifier(String)
        case symbol(Character)
    }

    private static func tokenize(_ text: String) throws -> [Token] {
        var tokens: [Token] = []
        let characters = Array(text)
        var index = 0
        while index < characters.count {
            let character = characters[index]
            if character.isNumber || character == "." {
                var literal = ""
                while index < characters.count, characters[index].isNumber || characters[index] == "." {
                    literal.append(characters[index])
                    index += 1
                }
                guard let value = Double(literal) else { throw ParseError.unexpected(literal) }
                tokens.append(.number(value))
                continue
            }
            if character.isLetter {
                var name = ""
                while index < characters.count, characters[index].isLetter {
                    name.append(characters[index])
                    index += 1
                }
                if name == "x" {
                    tokens.append(.variable)
                } else {
                    tokens.append(.identifier(name))
                }
                continue
            }
            if "+-*/^()".contains(character) {
                tokens.append(.symbol(character))
                index += 1
                continue
            }
            throw ParseError.unexpected(String(character))
        }
        return tokens
    }

    // MARK: - Aufbauen

    private indirect enum Node {
        case constant(Double)
        case variable
        case unary(String, Node)
        case binary(Character, Node, Node)

        func value(_ x: Double) -> Double {
            switch self {
            case .constant(let value):
                return value
            case .variable:
                return x
            case .unary(let name, let inner):
                let v = inner.value(x)
                switch name {
                case "neg": return -v
                case "sin": return sin(v)
                case "cos": return cos(v)
                case "tan": return tan(v)
                case "sqrt": return v < 0 ? Double.nan : sqrt(v)
                case "abs": return abs(v)
                case "ln": return v <= 0 ? Double.nan : log(v)
                case "log": return v <= 0 ? Double.nan : log10(v)
                case "exp": return exp(v)
                default: return Double.nan
                }
            case .binary(let operation, let lhs, let rhs):
                let a = lhs.value(x), b = rhs.value(x)
                switch operation {
                case "+": return a + b
                case "-": return a - b
                case "*": return a * b
                case "/": return b == 0 ? Double.nan : a / b
                case "^":
                    if a < 0, b != b.rounded() { return Double.nan }
                    return pow(a, b)
                default: return Double.nan
                }
            }
        }
    }

    private struct Parser {
        let tokens: [Token]
        var index = 0

        var isAtEnd: Bool { index >= tokens.count }

        var rest: String {
            guard !isAtEnd else { return "" }
            switch tokens[index] {
            case .number(let value): return "\(value)"
            case .variable: return "x"
            case .identifier(let name): return name
            case .symbol(let character): return String(character)
            }
        }

        private func peek() -> Token? { isAtEnd ? nil : tokens[index] }

        mutating func parseExpression() throws -> Node {
            var node = try parseTerm()
            while case .symbol(let operation)? = peek(), operation == "+" || operation == "-" {
                index += 1
                node = .binary(operation, node, try parseTerm())
            }
            return node
        }

        mutating func parseTerm() throws -> Node {
            var node = try parseUnary()
            while let token = peek() {
                if case .symbol(let operation) = token, operation == "*" || operation == "/" {
                    index += 1
                    node = .binary(operation, node, try parseUnary())
                    continue
                }
                // Verstecktes Malzeichen: 2x, 3(x+1), 2sin(x)
                let startsValue: Bool
                switch token {
                case .number, .variable, .identifier: startsValue = true
                case .symbol(let character): startsValue = character == "("
                }
                guard startsValue else { break }
                node = .binary("*", node, try parsePower())
            }
            return node
        }

        /// Hochzahlen binden stärker als das Minus davor: −x² ist −(x²), 2^−x geht auch.
        mutating func parsePower() throws -> Node {
            let base = try parsePrimary()
            if case .symbol("^")? = peek() {
                index += 1
                return .binary("^", base, try parseUnary())
            }
            return base
        }

        mutating func parseUnary() throws -> Node {
            if case .symbol("-")? = peek() {
                index += 1
                return .unary("neg", try parseUnary())
            }
            if case .symbol("+")? = peek() {
                index += 1
                return try parseUnary()
            }
            return try parsePower()
        }

        mutating func parsePrimary() throws -> Node {
            guard let token = peek() else { throw ParseError.empty }
            switch token {
            case .number(let value):
                index += 1
                return .constant(value)
            case .variable:
                index += 1
                return .variable
            case .identifier(let name):
                index += 1
                if name == "pi" { return .constant(Double.pi) }
                if name == "e" { return .constant(M_E) }
                let known = ["sin", "cos", "tan", "sqrt", "abs", "ln", "log", "exp"]
                guard known.contains(name) else { throw ParseError.unexpected(name) }
                let argument = try parsePrimary()
                return .unary(name, argument)
            case .symbol("("):
                index += 1
                let inner = try parseExpression()
                guard case .symbol(")")? = peek() else { throw ParseError.unexpected(")") }
                index += 1
                return inner
            case .symbol(let character):
                throw ParseError.unexpected(String(character))
            }
        }
    }
}

/// Achsenzahl eines Graphen – wird am Surface als kleines Textfeld gesetzt.
struct GraphLabel {
    var x: CGFloat
    var y: CGFloat
    var width: CGFloat
    var text: String
}

/// Baut aus einer Funktion echte Pencil-Striche: Koordinatensystem, Graph und Achsenzahlen.
enum GraphBuilder {
    struct Settings {
        var xMin: Double = -5
        var xMax: Double = 5
        var yMin: Double = -5
        var yMax: Double = 5
        /// Punkte pro Einheit – 32 entspricht genau einem Kästchen im karierten Papier.
        var unit: CGFloat = 32
        var drawAxes = true
        var drawLabels = true
        var color: UIColor = UIColor(hex: "#1A1F2B")
        var lineWidth: CGFloat = 2.4
    }

    struct Result {
        var strokes: [PKStroke] = []
        var labels: [GraphLabel] = []
        var size: CGSize = .zero
    }

    /// `origin` ist die Position des Nullpunkts auf der Seite (Inhaltskoordinaten).
    static func build(function: ((Double) -> Double?)?, settings: Settings, origin: CGPoint) -> Result {
        var result = Result()
        let unit = settings.unit
        let width = CGFloat(settings.xMax - settings.xMin) * unit
        let height = CGFloat(settings.yMax - settings.yMin) * unit
        result.size = CGSize(width: width, height: height)

        func point(_ x: Double, _ y: Double) -> CGPoint {
            CGPoint(x: origin.x + CGFloat(x) * unit, y: origin.y - CGFloat(y) * unit)
        }

        let axisColor = UIColor(hex: "#1A1F2B")
        if settings.drawAxes {
            // x-Achse mit Pfeilspitze
            if let stroke = InkBridge.line(
                points: [point(settings.xMin, 0), point(settings.xMax, 0)],
                width: 1.8,
                color: axisColor
            ) { result.strokes.append(stroke) }
            let xTip = point(settings.xMax, 0)
            for direction in [CGFloat(-1), CGFloat(1)] {
                if let stroke = InkBridge.line(
                    points: [CGPoint(x: xTip.x - 9, y: xTip.y + direction * 6), xTip],
                    width: 1.8,
                    color: axisColor
                ) { result.strokes.append(stroke) }
            }

            // y-Achse mit Pfeilspitze
            if let stroke = InkBridge.line(
                points: [point(0, settings.yMin), point(0, settings.yMax)],
                width: 1.8,
                color: axisColor
            ) { result.strokes.append(stroke) }
            let yTip = point(0, settings.yMax)
            for direction in [CGFloat(-1), CGFloat(1)] {
                if let stroke = InkBridge.line(
                    points: [CGPoint(x: yTip.x + direction * 6, y: yTip.y + 9), yTip],
                    width: 1.8,
                    color: axisColor
                ) { result.strokes.append(stroke) }
            }

            // Striche an den ganzen Zahlen
            let step = max(1, Int((max(settings.xMax - settings.xMin, settings.yMax - settings.yMin) / 12).rounded()))
            for value in stride(from: Int(settings.xMin.rounded()), through: Int(settings.xMax.rounded()), by: step) where value != 0 {
                let base = point(Double(value), 0)
                if let stroke = InkBridge.line(
                    points: [CGPoint(x: base.x, y: base.y - 4), CGPoint(x: base.x, y: base.y + 4)],
                    width: 1.5,
                    color: axisColor
                ) { result.strokes.append(stroke) }
                if settings.drawLabels {
                    result.labels.append(GraphLabel(x: base.x - 10, y: base.y + 6, width: 34, text: "\(value)"))
                }
            }
            for value in stride(from: Int(settings.yMin.rounded()), through: Int(settings.yMax.rounded()), by: step) where value != 0 {
                let base = point(0, Double(value))
                if let stroke = InkBridge.line(
                    points: [CGPoint(x: base.x - 4, y: base.y), CGPoint(x: base.x + 4, y: base.y)],
                    width: 1.5,
                    color: axisColor
                ) { result.strokes.append(stroke) }
                if settings.drawLabels {
                    result.labels.append(GraphLabel(x: base.x - 30, y: base.y - 9, width: 26, text: "\(value)"))
                }
            }
        }

        // Der Graph selbst: in Bildschirmschritten abtasten und bei Sprüngen unterbrechen
        if let function {
            var segment: [CGPoint] = []
            let steps = Int(width)
            for step in 0...max(1, steps) {
                let x = settings.xMin + (settings.xMax - settings.xMin) * Double(step) / Double(max(1, steps))
                if let y = function(x), y >= settings.yMin - 0.5, y <= settings.yMax + 0.5 {
                    segment.append(point(x, y))
                } else {
                    if segment.count >= 2, let stroke = InkBridge.line(points: segment, width: settings.lineWidth, color: settings.color) {
                        result.strokes.append(stroke)
                    }
                    segment = []
                }
            }
            if segment.count >= 2, let stroke = InkBridge.line(points: segment, width: settings.lineWidth, color: settings.color) {
                result.strokes.append(stroke)
            }
        }
        return result
    }

    /// Kurve wie ein Funktionsgraph: läuft durch alle Punkte, ohne Ecken und ohne Überschwingen.
    /// Nutzt ein monotones Spline (Fritsch–Carlson), wenn die Punkte von links nach rechts verlaufen.
    static func functionCurve(through points: [CGPoint], color: UIColor, width: CGFloat) -> PKStroke? {
        let sorted = points.sorted { $0.x < $1.x }
        guard sorted.count >= 2 else { return nil }
        // Zwei Punkte übereinander (gleiches x) verträgt ein Funktionsgraph nicht – dann weiche Kurve.
        for index in 1..<sorted.count where sorted[index].x - sorted[index - 1].x < 0.5 {
            return curve(through: points, smooth: true, color: color, width: width)
        }

        let count = sorted.count
        var slopes: [CGFloat] = []
        for index in 0..<(count - 1) {
            let dx = sorted[index + 1].x - sorted[index].x
            slopes.append((sorted[index + 1].y - sorted[index].y) / dx)
        }
        var tangents = [CGFloat](repeating: 0, count: count)
        tangents[0] = slopes[0]
        tangents[count - 1] = slopes[count - 2]
        for index in 1..<(count - 1) {
            if slopes[index - 1] * slopes[index] <= 0 {
                tangents[index] = 0                     // Hoch- oder Tiefpunkt: waagerechte Tangente
            } else {
                tangents[index] = (slopes[index - 1] + slopes[index]) / 2
            }
        }
        // Tangenten begrenzen, damit die Kurve nicht über die Punkte hinausschießt
        for index in 0..<(count - 1) where slopes[index] != 0 {
            let alpha = tangents[index] / slopes[index]
            let beta = tangents[index + 1] / slopes[index]
            let distance = hypot(alpha, beta)
            if distance > 3 {
                tangents[index] = 3 / distance * alpha * slopes[index]
                tangents[index + 1] = 3 / distance * beta * slopes[index]
            }
        }

        var curvePoints: [CGPoint] = []
        for index in 0..<(count - 1) {
            let p0 = sorted[index], p1 = sorted[index + 1]
            let dx = p1.x - p0.x
            let steps = max(6, Int(dx / 3))
            for step in 0..<steps {
                let t = CGFloat(step) / CGFloat(steps)
                let t2 = t * t, t3 = t2 * t
                let h00 = 2 * t3 - 3 * t2 + 1
                let h10 = t3 - 2 * t2 + t
                let h01 = -2 * t3 + 3 * t2
                let h11 = t3 - t2
                let y = h00 * p0.y + h10 * dx * tangents[index] + h01 * p1.y + h11 * dx * tangents[index + 1]
                curvePoints.append(CGPoint(x: p0.x + t * dx, y: y))
            }
        }
        curvePoints.append(sorted[count - 1])
        return InkBridge.line(points: curvePoints, width: width, color: color)
    }

    /// Glatte Kurve durch angetippte Punkte (Catmull-Rom) oder gerade Verbindungen.
    static func curve(through points: [CGPoint], smooth: Bool, color: UIColor, width: CGFloat) -> PKStroke? {
        guard points.count >= 2 else { return nil }
        guard smooth, points.count >= 3 else {
            return InkBridge.line(points: points, width: width, color: color)
        }
        var curvePoints: [CGPoint] = []
        let extended = [points[0]] + points + [points[points.count - 1]]
        for index in 1..<(extended.count - 2) {
            let p0 = extended[index - 1], p1 = extended[index]
            let p2 = extended[index + 1], p3 = extended[index + 2]
            let steps = max(6, Int(hypot(p2.x - p1.x, p2.y - p1.y) / 4))
            for step in 0..<steps {
                let t = CGFloat(step) / CGFloat(steps)
                let t2 = t * t, t3 = t2 * t
                let x = 0.5 * ((2 * p1.x) + (-p0.x + p2.x) * t + (2 * p0.x - 5 * p1.x + 4 * p2.x - p3.x) * t2 + (-p0.x + 3 * p1.x - 3 * p2.x + p3.x) * t3)
                let y = 0.5 * ((2 * p1.y) + (-p0.y + p2.y) * t + (2 * p0.y - 5 * p1.y + 4 * p2.y - p3.y) * t2 + (-p0.y + 3 * p1.y - 3 * p2.y + p3.y) * t3)
                curvePoints.append(CGPoint(x: x, y: y))
            }
        }
        curvePoints.append(points[points.count - 1])
        return InkBridge.line(points: curvePoints, width: width, color: color)
    }
}
