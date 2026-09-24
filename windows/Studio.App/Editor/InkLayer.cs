using System.Windows;
using System.Windows.Media;

namespace Lernheft.Studio.App.Editor;

/// <summary>
/// Malt Striche: glatt durch die Punkte, bei Stiften mit wechselnder Breite, Marker halb durchsichtig.
/// Dieselbe Rechnung wird für Bildschirm, Druck, PDF und KI-Bilder benutzt.
/// </summary>
public static class InkRenderer
{
    /// <summary>Wie deckend eine Tintenart ist.</summary>
    public static double Opacity(string kind) => kind switch
    {
        "marker" => 0.38,
        "pencil" => 0.82,
        "watercolor" => 0.55,
        "crayon" => 0.8,
        _ => 1
    };

    public static void Draw(DrawingContext context, InkStroke stroke, bool dark)
    {
        var count = stroke.Count;
        if (count == 0) return;
        var color = Theme.AdaptInk(Theme.Hex(stroke.Color), dark);
        var opacity = Opacity(stroke.Kind);
        if (dark && stroke.Kind == "marker") opacity = 0.5;
        var brush = new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), color.R, color.G, color.B));
        brush.Freeze();
        var width = Math.Max(0.6, stroke.Width);

        if (count == 1)
        {
            var (x, y, _) = stroke.PointAt(0);
            context.DrawEllipse(brush, null, new Point(x, y), width / 2, width / 2);
            return;
        }

        if (stroke.WidthFactors && stroke.Kind is not "marker" && DrawVariable(context, stroke, width, color, opacity)) return;

        var pen = new Pen(brush, width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        context.DrawGeometry(null, pen, Centerline(stroke));
    }

    /// <summary>Weiche Linie: quadratische Kurven durch die Mittelpunkte der Abschnitte.</summary>
    public static StreamGeometry Centerline(InkStroke stroke)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var count = stroke.Count;
            var (x0, y0, _) = stroke.PointAt(0);
            context.BeginFigure(new Point(x0, y0), false, false);
            if (count == 2 || stroke.Kind == "shape")
            {
                for (var index = 1; index < count; index++)
                {
                    var (x, y, _) = stroke.PointAt(index);
                    context.LineTo(new Point(x, y), true, true);
                }
            }
            else
            {
                for (var index = 1; index < count - 1; index++)
                {
                    var (x, y, _) = stroke.PointAt(index);
                    var (nx, ny, _) = stroke.PointAt(index + 1);
                    context.QuadraticBezierTo(new Point(x, y), new Point((x + nx) / 2, (y + ny) / 2), true, true);
                }
                var (lx, ly, _) = stroke.PointAt(count - 1);
                context.LineTo(new Point(lx, ly), true, true);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Strich mit wechselnder Breite (wie ein Füller): in Abschnitte gleicher Breite geteilt, jeder als
    /// runde, geglättete Linie. Anders als ein einziger Umriss kann sich das in engen Kurven der
    /// Handschrift nicht verdrehen – keine Löcher, keine „Perlenkette“.
    /// </summary>
    private static bool DrawVariable(DrawingContext context, InkStroke stroke, double baseWidth, Color color, double opacity)
    {
        var count = stroke.Count;
        var points = new List<Point>(count);
        var raw = new List<double>(count);
        for (var index = 0; index < count; index++)
        {
            var (x, y, factor) = stroke.PointAt(index);
            var point = new Point(x, y);
            if (points.Count > 0 && (points[^1] - point).Length < 0.35) continue;
            points.Add(point);
            raw.Add(baseWidth * Math.Clamp(factor <= 0 ? 1 : factor, 0.35, 2.5));
        }
        if (points.Count < 2) return false;

        // Breiten glätten: Druckschwankungen einzelner Punkte sollen den Strich nicht zittern lassen.
        var widths = raw.ToArray();
        for (var pass = 0; pass < 3; pass++)
        {
            var copy = (double[])widths.Clone();
            for (var i = 1; i < widths.Length - 1; i++) widths[i] = (copy[i - 1] + 2 * copy[i] + copy[i + 1]) / 4;
        }
        // In feinen Stufen, damit gleich breite Stücke zusammen gezeichnet werden können.
        var step = Math.Max(0.15, baseWidth * 0.08);
        double Level(int segment) => Math.Max(0.5, Math.Round((widths[segment] + widths[segment + 1]) / 2 / step) * step);

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        // Deckkraft für den ganzen Strich auf einmal – überlappende Stücke werden so nicht dunkler.
        if (opacity < 1) context.PushOpacity(opacity);
        var start = 0;
        while (start < points.Count - 1)
        {
            var level = Level(start);
            var end = start + 1;
            while (end < points.Count - 1 && Math.Abs(Level(end) - level) < step / 2) end++;
            var pen = new Pen(brush, level)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();
            context.DrawGeometry(null, pen, SmoothRun(points, start, end));
            start = end;
        }
        if (opacity < 1) context.Pop();
        return true;
    }

    /// <summary>Weiche Linie durch die Punkte <paramref name="from"/> bis <paramref name="to"/>.</summary>
    private static StreamGeometry SmoothRun(List<Point> points, int from, int to)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            // Beginnt und endet auf den Mittelpunkten der Nachbarabschnitte, damit Stücke nahtlos aneinanderpassen.
            var first = from == 0 ? points[0] : Mid(points[from - 1], points[from]);
            context.BeginFigure(first, false, false);
            for (var i = from; i < to; i++)
            {
                var mid = Mid(points[i], points[i + 1]);
                context.QuadraticBezierTo(points[i], mid, true, true);
            }
            if (to == points.Count - 1) context.LineTo(points[to], true, true);
            else context.QuadraticBezierTo(points[to], Mid(points[to], points[to + 1]), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
}

/// <summary>
/// Die Ebene mit der Handschrift. Jeder Strich ist ein eigenes Bild, damit ein neuer Strich vom
/// iPad nicht die ganze Seite neu zeichnen lässt. Striche, die gerade auf dem iPad entstehen,
/// liegen darüber und wachsen mit jedem Paket.
/// </summary>
public sealed class InkLayer : FrameworkElement
{
    private readonly VisualCollection _visuals;
    private readonly Dictionary<string, DrawingVisual> _strokes = new();
    private readonly Dictionary<string, (DrawingVisual Visual, InkStroke Stroke)> _live = new();
    private bool _dark;

    public InkLayer()
    {
        _visuals = new VisualCollection(this);
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    public void SetAll(IEnumerable<InkStroke> strokes, bool dark)
    {
        _dark = dark;
        _visuals.Clear();
        _strokes.Clear();
        _live.Clear();
        foreach (var stroke in strokes) Add(stroke);
    }

    public void Add(InkStroke stroke)
    {
        if (stroke.Id is null) return;
        if (_strokes.TryGetValue(stroke.Id, out var old)) _visuals.Remove(old);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) InkRenderer.Draw(context, stroke, _dark);
        _strokes[stroke.Id] = visual;
        // Unter die Striche, die gerade noch entstehen.
        _visuals.Insert(Math.Max(0, _visuals.Count - _live.Count), visual);
    }

    public void Remove(string id)
    {
        if (!_strokes.Remove(id, out var visual)) return;
        _visuals.Remove(visual);
    }

    /// <summary>Hängt Punkte an einen entstehenden Strich an (oder legt ihn an).</summary>
    public void UpdateLive(string sid, InkStroke template, IReadOnlyList<double> newPoints)
    {
        if (!_live.TryGetValue(sid, out var entry))
        {
            var fresh = new InkStroke { Id = sid, Color = template.Color, Width = template.Width, Kind = template.Kind, WidthFactors = template.WidthFactors };
            entry = (new DrawingVisual(), fresh);
            _live[sid] = entry;
            _visuals.Add(entry.Visual);
        }
        entry.Stroke.Points.AddRange(newPoints);
        using var context = entry.Visual.RenderOpen();
        InkRenderer.Draw(context, entry.Stroke, _dark);
    }

    public void RemoveLive(string sid)
    {
        if (!_live.Remove(sid, out var entry)) return;
        _visuals.Remove(entry.Visual);
    }

    public void ClearLive()
    {
        foreach (var (visual, _) in _live.Values) _visuals.Remove(visual);
        _live.Clear();
    }

    public IEnumerable<string> LiveIds => _live.Keys.ToList();
}
