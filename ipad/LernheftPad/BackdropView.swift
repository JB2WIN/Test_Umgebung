import UIKit

/// Alles unter der Handschrift: Papier, eingefügte Bilder, der Text vom Surface und
/// die Markierung, welchen Ausschnitt das Surface gerade zeigt. Scrollt und zoomt mit.
final class BackdropView: UIView {
    static let spacing: CGFloat = 32
    static let marginX: CGFloat = 64
    static let pageHeight: CGFloat = 1120

    private var paper = "lined"
    private var pageCount = 1
    private var pageWidth: CGFloat = 800
    private var zoom: CGFloat = 1

    private let margin = UIView()
    private var separators: [UIView] = []
    private let imageLayer = UIView()
    private let textLayer = UIView()
    private var imageViews: [String: UIImageView] = [:]
    private var images: [PlacedImage] = []
    private var seamless = true
    private var processedGeneration = 0

    private var textData: [Int: Data] = [:]
    private var textViews: [Int: UIImageView] = [:]

    private let viewportBand = UIView()

    /// Welcher Ausschnitt am Surface zu sehen ist (nur y und Höhe zählen).
    var surfaceView: CGRect? {
        didSet { setNeedsLayout() }
    }

    /// Sichtbarer Bereich in Papierkoordinaten – nur dort werden Textebenen entpackt.
    var visibleRect: CGRect = .zero {
        didSet {
            if Int(visibleRect.minY / 400) != Int(oldValue.minY / 400) || Int(visibleRect.maxY / 400) != Int(oldValue.maxY / 400) {
                refreshTextViews()
            }
        }
    }

    override init(frame: CGRect) {
        super.init(frame: frame)
        isUserInteractionEnabled = false
        imageLayer.isUserInteractionEnabled = false
        textLayer.isUserInteractionEnabled = false
        addSubview(margin)
        addSubview(imageLayer)
        addSubview(textLayer)
        viewportBand.backgroundColor = UIColor.tintColor.withAlphaComponent(0.55)
        viewportBand.layer.cornerRadius = 2
        viewportBand.isHidden = true
        addSubview(viewportBand)
        registerForTraitChanges([UITraitUserInterfaceStyle.self]) { (view: BackdropView, _: UITraitCollection) in
            view.refreshPaper()
            view.applyImageStyle()
        }
        refreshPaper()
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) wird nicht verwendet")
    }

    private var isDark: Bool { traitCollection.userInterfaceStyle == .dark }

    func configure(paper: String, pageCount: Int, pageWidth: CGFloat, zoom: CGFloat) {
        var needsPaper = false
        if paper != self.paper { self.paper = paper; needsPaper = true }
        if abs(zoom - self.zoom) > 0.0005 { self.zoom = zoom; needsPaper = true }
        if pageCount != self.pageCount || abs(pageWidth - self.pageWidth) > 0.5 {
            self.pageCount = pageCount
            self.pageWidth = pageWidth
            setNeedsLayout()
            refreshTextViews()
        }
        if needsPaper {
            refreshPaper()
            setNeedsLayout()
        }
    }

    func clearNote() {
        textData.removeAll()
        textViews.values.forEach { $0.removeFromSuperview() }
        textViews.removeAll()
        setImages([], seamless: seamless)
        surfaceView = nil
    }

    // MARK: Papier

    static func paperColor(dark: Bool) -> UIColor {
        dark ? UIColor(red: 0.106, green: 0.122, blue: 0.149, alpha: 1)
             : UIColor(red: 0.992, green: 0.992, blue: 0.980, alpha: 1)
    }

    static func lineColor(dark: Bool) -> UIColor {
        dark ? UIColor(red: 0.176, green: 0.224, blue: 0.282, alpha: 1)
             : UIColor(red: 0.776, green: 0.851, blue: 0.910, alpha: 1)
    }

    static func marginColor(dark: Bool) -> UIColor {
        dark ? UIColor(red: 0.431, green: 0.231, blue: 0.220, alpha: 1)
             : UIColor(red: 0.902, green: 0.541, blue: 0.502, alpha: 1)
    }

    private func refreshPaper() {
        backgroundColor = UIColor(patternImage: Self.pattern(style: paper, dark: isDark, zoom: zoom))
        margin.backgroundColor = Self.marginColor(dark: isDark)
        margin.isHidden = paper != "lined"
        let separatorColor = isDark ? UIColor(white: 1, alpha: 0.16) : UIColor(white: 0, alpha: 0.14)
        separators.forEach { $0.backgroundColor = separatorColor }
    }

    static func pattern(style: String, dark: Bool, zoom: CGFloat) -> UIImage {
        let step = spacing * max(zoom, 0.1)
        let size = CGSize(width: step, height: step)
        let format = UIGraphicsImageRendererFormat()
        format.opaque = true
        return UIGraphicsImageRenderer(size: size, format: format).image { context in
            paperColor(dark: dark).setFill()
            // Bei krummen Zoomstufen ist die Kachel keine ganze Pixelzahl breit – etwas größer füllen,
            // sonst bleibt am Rand eine halbe, dunkle Pixelspalte stehen (sieht aus wie Kästchen).
            context.fill(CGRect(x: 0, y: 0, width: size.width + 2, height: size.height + 2))
            lineColor(dark: dark).setFill()
            let line = max(1, zoom.rounded(.down))
            switch style {
            case "lined":
                context.fill(CGRect(x: 0, y: step - line, width: step, height: line))
            case "grid":
                context.fill(CGRect(x: 0, y: step - line, width: step, height: line))
                context.fill(CGRect(x: step - line, y: 0, width: line, height: step))
            case "dotted":
                let dot = 3 * max(1, zoom * 0.8)
                UIBezierPath(ovalIn: CGRect(x: step - dot / 2 - 0.5, y: step - dot / 2 - 0.5, width: dot, height: dot)).fill()
            default:
                break
            }
        }
    }

    // MARK: Bilder

    func setImages(_ images: [PlacedImage], seamless: Bool) {
        let styleChanged = seamless != self.seamless
        self.seamless = seamless
        let ids = Set(images.map(\.id))
        for (id, view) in imageViews where !ids.contains(id) {
            view.removeFromSuperview()
            imageViews[id] = nil
        }
        let changed = images.filter { placed in !self.images.contains(placed) }.map(\.id)
        for placed in images where imageViews[placed.id] == nil {
            let view = UIImageView()
            view.contentMode = .scaleToFill
            view.clipsToBounds = true
            imageLayer.addSubview(view)
            imageViews[placed.id] = view
        }
        self.images = images
        if styleChanged { applyImageStyle() } else { applyImageStyle(only: Set(changed)) }
        setNeedsLayout()
    }

    /// Seiten nahtlos einblenden: Weiß wird durchsichtig, im Dunkelmodus wird Tinte hell.
    private func applyImageStyle(only: Set<String>? = nil) {
        let dark = isDark
        processedGeneration += 1
        let generation = processedGeneration
        for placed in images where only == nil || only!.contains(placed.id) {
            guard let view = imageViews[placed.id] else { continue }
            guard seamless else {
                view.image = placed.image
                continue
            }
            if view.image == nil { view.image = placed.image }
            let original = placed.image
            DispatchQueue.global(qos: .userInitiated).async { [weak self, weak view] in
                let processed = InkTools.inkLayer(from: original, forDarkMode: dark)
                DispatchQueue.main.async {
                    guard let self, let view, self.processedGeneration >= generation,
                          self.images.contains(where: { $0.id == placed.id && $0.image === original }) else { return }
                    view.image = processed ?? original
                }
            }
        }
    }

    // MARK: Text vom Surface

    func setTextLayer(page: Int, data: Data?) {
        textData[page] = data
        textViews[page]?.removeFromSuperview()
        textViews[page] = nil
        refreshTextViews()
    }

    /// Entpackt nur die Textebenen der sichtbaren Seiten – eine Seite braucht entpackt über 30 MB.
    private func refreshTextViews() {
        let wanted = visibleRect.insetBy(dx: 0, dy: -500)
        for page in Array(textViews.keys) {
            let top = CGFloat(page) * Self.pageHeight
            let area = CGRect(x: 0, y: top, width: pageWidth, height: Self.pageHeight)
            if !area.intersects(wanted) || page >= pageCount {
                textViews[page]?.removeFromSuperview()
                textViews[page] = nil
            }
        }
        for (page, data) in textData where textViews[page] == nil && page < pageCount {
            let top = CGFloat(page) * Self.pageHeight
            let area = CGRect(x: 0, y: top, width: pageWidth, height: Self.pageHeight)
            guard area.intersects(wanted), let image = UIImage(data: data, scale: 3) else { continue }
            let view = UIImageView(image: image)
            view.contentMode = .scaleToFill
            textLayer.addSubview(view)
            textViews[page] = view
        }
        setNeedsLayout()
    }

    // MARK: Anordnung

    override func layoutSubviews() {
        super.layoutSubviews()
        margin.frame = CGRect(x: Self.marginX * zoom, y: 0, width: max(1, 1.5 * zoom), height: bounds.height)
        imageLayer.frame = bounds
        textLayer.frame = bounds

        let wanted = max(0, pageCount - 1)
        while separators.count < wanted {
            let view = UIView()
            view.backgroundColor = isDark ? UIColor(white: 1, alpha: 0.16) : UIColor(white: 0, alpha: 0.14)
            insertSubview(view, aboveSubview: margin)
            separators.append(view)
        }
        while separators.count > wanted {
            separators.removeLast().removeFromSuperview()
        }
        for (index, view) in separators.enumerated() {
            view.frame = CGRect(x: 0, y: CGFloat(index + 1) * Self.pageHeight * zoom - 1.5, width: bounds.width, height: 3)
        }
        for placed in images {
            imageViews[placed.id]?.frame = CGRect(x: placed.frame.minX * zoom, y: placed.frame.minY * zoom,
                                                  width: placed.frame.width * zoom, height: placed.frame.height * zoom)
        }
        for (page, view) in textViews {
            view.frame = CGRect(x: 0, y: CGFloat(page) * Self.pageHeight * zoom,
                                width: pageWidth * zoom, height: Self.pageHeight * zoom)
        }
        if let surfaceView, surfaceView.height > 0 {
            viewportBand.isHidden = false
            bringSubviewToFront(viewportBand)
            viewportBand.frame = CGRect(x: 2, y: surfaceView.minY * zoom, width: 4, height: surfaceView.height * zoom)
        } else {
            viewportBand.isHidden = true
        }
    }
}
