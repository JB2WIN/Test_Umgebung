import SwiftUI
import PencilKit

/// Funktion eintippen, Vorschau ansehen, als echte Striche in die Notiz setzen.
struct FunctionSheet: View {
    @Environment(\.dismiss) private var dismiss
    let onInsert: (GraphBuilder.Settings, ((Double) -> Double?)?) -> Void

    @AppStorage("graphXRange") private var xRange = 5.0
    @AppStorage("graphYRange") private var yRange = 5.0
    @AppStorage("graphUnit") private var unit = 32.0
    @AppStorage("graphAxes") private var drawAxes = true
    @AppStorage("graphLabels") private var drawLabels = true

    @State private var input = "x^2"
    @State private var colorName = "ink"
    @State private var errorText: String?

    private let examples = ["x^2", "0,5x^2-2x+1", "-x^2+4", "2x+1", "1/x", "sin(x)", "sqrt(x)"]

    private let colors: [(name: String, label: String, color: UIColor)] = [
        ("ink", "Schwarz", UIColor(hex: "#1A1F2B")),
        ("blue", "Blau", UIColor(hex: "#2140C8")),
        ("red", "Rot", UIColor(hex: "#D2413A")),
        ("green", "Grün", UIColor(hex: "#1E8A57"))
    ]

    private var compiled: ((Double) -> Double?)? {
        try? MathExpression.compile(input)
    }

    private var settings: GraphBuilder.Settings {
        var settings = GraphBuilder.Settings()
        settings.xMin = -xRange
        settings.xMax = xRange
        settings.yMin = -yRange
        settings.yMax = yRange
        settings.unit = unit
        settings.drawAxes = drawAxes
        settings.drawLabels = drawLabels
        settings.color = colors.first { $0.name == colorName }?.color ?? UIColor(hex: "#1A1F2B")
        return settings
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    HStack {
                        Text("f(x) =").foregroundStyle(.secondary)
                        TextField("x^2 - 2x + 1", text: $input)
                            .font(.body.monospaced())
                            .textInputAutocapitalization(.never)
                            .autocorrectionDisabled()
                    }
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(spacing: 8) {
                            ForEach(examples, id: \.self) { example in
                                Button(example) { input = example }
                                    .buttonStyle(.bordered)
                                    .font(.footnote.monospaced())
                            }
                        }
                    }
                    if let errorText {
                        Text(errorText).foregroundStyle(.red).font(.footnote)
                    }
                } header: {
                    Text("Funktion")
                } footer: {
                    Text("Erlaubt sind + − · / ^, Klammern, sin, cos, tan, sqrt, abs, ln, log, exp, pi und e. Komma oder Punkt als Dezimaltrennzeichen, „2x“ und „x²“ gehen auch.")
                }

                Section("Vorschau") {
                    GraphPreview(function: compiled, settings: settings)
                        .frame(height: 220)
                        .listRowInsets(EdgeInsets())
                }

                Section("Bereich") {
                    Stepper("x von −\(Int(xRange)) bis \(Int(xRange))", value: $xRange, in: 2...20, step: 1)
                    Stepper("y von −\(Int(yRange)) bis \(Int(yRange))", value: $yRange, in: 2...20, step: 1)
                    Stepper("Ein Kästchen = \(Int(unit)) pt", value: $unit, in: 16...64, step: 8)
                    Toggle("Koordinatensystem zeichnen", isOn: $drawAxes)
                    Toggle("Zahlen an den Achsen", isOn: $drawLabels)
                    Picker("Farbe", selection: $colorName) {
                        ForEach(colors, id: \.name) { entry in
                            Text(entry.label).tag(entry.name)
                        }
                    }
                }
            }
            .navigationTitle("Funktion zeichnen")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Abbrechen") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Einfügen") { insert() }
                }
            }
        }
    }

    private func insert() {
        do {
            let function = try MathExpression.compile(input)
            onInsert(settings, function)
            dismiss()
        } catch {
            errorText = error.localizedDescription
        }
    }
}

/// Zeigt den Graphen sofort an, mit derselben Rechnung wie später auf dem Papier.
struct GraphPreview: View {
    let function: ((Double) -> Double?)?
    let settings: GraphBuilder.Settings

    var body: some View {
        Canvas { context, size in
            let unitX = size.width / CGFloat(settings.xMax - settings.xMin)
            let unitY = size.height / CGFloat(settings.yMax - settings.yMin)
            let originX = size.width * CGFloat(-settings.xMin / (settings.xMax - settings.xMin))
            let originY = size.height * CGFloat(settings.yMax / (settings.yMax - settings.yMin))

            // Kästchen
            var grid = Path()
            var value = ceil(settings.xMin)
            while value <= settings.xMax {
                let x = originX + CGFloat(value) * unitX
                grid.move(to: CGPoint(x: x, y: 0))
                grid.addLine(to: CGPoint(x: x, y: size.height))
                value += 1
            }
            value = ceil(settings.yMin)
            while value <= settings.yMax {
                let y = originY - CGFloat(value) * unitY
                grid.move(to: CGPoint(x: 0, y: y))
                grid.addLine(to: CGPoint(x: size.width, y: y))
                value += 1
            }
            context.stroke(grid, with: .color(.secondary.opacity(0.18)), lineWidth: 0.5)

            if settings.drawAxes {
                var axes = Path()
                axes.move(to: CGPoint(x: 0, y: originY))
                axes.addLine(to: CGPoint(x: size.width, y: originY))
                axes.move(to: CGPoint(x: originX, y: 0))
                axes.addLine(to: CGPoint(x: originX, y: size.height))
                context.stroke(axes, with: .color(.primary.opacity(0.65)), lineWidth: 1.2)
            }

            guard let function else { return }
            var path = Path()
            var started = false
            let steps = Int(size.width)
            for step in 0...max(1, steps) {
                let x = settings.xMin + (settings.xMax - settings.xMin) * Double(step) / Double(max(1, steps))
                guard let y = function(x), y >= settings.yMin - 0.5, y <= settings.yMax + 0.5 else {
                    started = false
                    continue
                }
                let point = CGPoint(x: originX + CGFloat(x) * unitX, y: originY - CGFloat(y) * unitY)
                if started {
                    path.addLine(to: point)
                } else {
                    path.move(to: point)
                    started = true
                }
            }
            let shown = settings.color.hexString == "#1A1F2B" ? Color.primary : Color(uiColor: settings.color)
            context.stroke(path, with: .color(shown), lineWidth: 2)
        }
        .background(Color(uiColor: .secondarySystemBackground))
        .overlay(alignment: .topLeading) {
            if function == nil {
                Text("Term noch nicht vollständig")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
                    .padding(8)
            }
        }
    }
}
