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

        if (stroke.WidthFactors && stroke.Kind is not "marker")
        {
            var outline = Outline(stroke, width);
            if (outline is not null)
            {
                context.DrawGeometry(brush, null, outline);
                return;
            }
        }

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

    /// <summary>Umriss eines Strichs mit wechselnder Breite (wie ein Füller).</summary>
    private static Geometry? Outline(InkStroke stroke, double baseWidth)
    {
        var count = stroke.Count;
        var points = new List<(Point P, double W)>(count);
        for (var index = 0; index < count; index++)
        {
            var (x, y, factor) = stroke.PointAt(index);
            var w = baseWidth * Math.Clamp(factor <= 0 ? 1 : factor, 0.25, 2.5);
            var point = new Point(x, y);
            if (points.Count > 0 && (points[^1].P - point).Length < 0.35) continue;
            points.Add((point, w));
        }
        if (points.Count < 2) return null;

        // Breiten leicht glätten, damit der Rand nicht zittert.
        var widths = points.Select(p => p.W).ToArray();
        for (var pass = 0; pass < 2; pass++)
        {
            var copy = (double[])widths.Clone();
            for (var i = 1; i < widths.Length - 1; i++) widths[i] = (copy[i - 1] + 2 * copy[i] + copy[i + 1]) / 4;
        }

        var left = new List<Point>(points.Count);
        var right = new List<Point>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var previous = points[Math.Max(0, i - 1)].P;
            var next = points[Math.Min(points.Count - 1, i + 1)].P;
            var direction = next - previous;
            if (direction.Length < 0.0001) direction = new Vector(1, 0);
            direction.Normalize();
            var normal = new Vector(-direction.Y, direction.X) * (widths[i] / 2);
            left.Add(points[i].P + normal);
            right.Add(points[i].P - normal);
        }

        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            context.BeginFigure(left[0], true, true);
            AddSmooth(context, left);
            var endRadius = widths[^1] / 2;
            context.ArcTo(right[^1], new Size(endRadius, endRadius), 0, false, SweepDirection.Clockwise, true, true);
            right.Reverse();
            AddSmooth(context, right);
            var startRadius = widths[0] / 2;
            context.ArcTo(left[0], new Size(startRadius, startRadius), 0, false, SweepDirection.Clockwise, true, true);
        }
        geometry.Freeze();

        // Zusätzlich die Mittellinie mit der kleinsten Breite: schließt Lücken an spitzen Kehren.
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        group.Children.Add(geometry);
        var thin = widths.Min();
        var core = new Pen(Brushes.Black, thin) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var line = new StreamGeometry();
        using (var context = line.Open())
        {
            context.BeginFigure(points[0].P, false, false);
            for (var i = 1; i < points.Count; i++) context.LineTo(points[i].P, true, true);
        }
        group.Children.Add(line.GetWidenedPathGeometry(core));
        group.Freeze();
        return group;
    }

    private static void AddSmooth(StreamGeometryContext context, List<Point> points)
    {
        for (var i = 1; i < points.Count - 1; i++)
        {
            var mid = new Point((points[i].X + points[i + 1].X) / 2, (points[i].Y + points[i + 1].Y) / 2);
            context.QuadraticBezierTo(points[i], mid, true, true);
        }
        context.LineTo(points[^1], true, true);
    }
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
