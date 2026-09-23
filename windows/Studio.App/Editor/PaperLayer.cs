using System.Windows;
using System.Windows.Media;

namespace Lernheft.Studio.App.Editor;

/// <summary>Das Papier: liniert, kariert, gepunktet oder blanko – mit Trennlinien zwischen den Seiten.</summary>
public sealed class PaperLayer : FrameworkElement
{
    public PaperStyle Kind { get; set; } = PaperStyle.Lined;
    public int Pages { get; set; } = 1;
    public double PageWidth { get; set; } = Paper.PageWidth;

    /// <summary>Wie stark die Seite vergrößert ist – Linien bleiben dadurch immer ein Bildpunkt dünn.</summary>
    public double Zoom { get; set; } = 1;

    /// <summary>Für Druck und PDF: immer helles Papier.</summary>
    public bool ForceLight { get; set; }

    public bool ShowSeparators { get; set; } = true;

    public PaperLayer()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    public void Refresh()
    {
        Width = PageWidth;
        Height = Pages * Paper.PageHeight;
        InvalidateVisual();
    }

    public static (Color Paper, Color Line, Color Margin, Color Separator) Colors(bool dark) => dark
        ? (Theme.Hex("#1B1F26"), Theme.Hex("#2D3948"), Theme.Hex("#6E3B38"), Theme.Hex("#46505D"))
        : (Theme.Hex("#FDFDFA"), Theme.Hex("#C6D9E8"), Theme.Hex("#E68A80"), Theme.Hex("#B5C0CE"));

    protected override void OnRender(DrawingContext context)
    {
        Draw(context, Kind, PageWidth, Pages, ForceLight ? false : Theme.IsDark, Zoom, ShowSeparators, 0, Pages * Paper.PageHeight);
    }

    /// <summary>Malt das Papier zwischen <paramref name="top"/> und <paramref name="bottom"/>.</summary>
    public static void Draw(DrawingContext context, PaperStyle style, double width, int pages, bool dark, double zoom,
        bool separators, double top, double bottom)
    {
        var (paper, line, margin, separator) = Colors(dark);
        var height = pages * Paper.PageHeight;
        context.DrawRectangle(Frozen(paper), null, new Rect(0, top, width, bottom - top));
        var thin = Math.Max(0.5, 1 / Math.Max(zoom, 0.1));
        var lineBrush = Frozen(line);
        var spacing = Paper.Spacing;

        switch (style)
        {
            case PaperStyle.Lined:
            case PaperStyle.Grid:
            {
                var pen = new Pen(lineBrush, thin);
                pen.Freeze();
                var first = Math.Max(1, (int)Math.Ceiling(top / spacing));
                for (var k = first; k * spacing < Math.Min(bottom, height); k++)
                {
                    var y = k * spacing;
                    if (separators && Math.Abs(y % Paper.PageHeight) < 0.01) continue;
                    context.DrawLine(pen, new Point(0, y), new Point(width, y));
                }
                if (style == PaperStyle.Grid)
                {
                    for (var x = spacing; x < width; x += spacing)
                    {
                        context.DrawLine(pen, new Point(x, top), new Point(x, Math.Min(bottom, height)));
                    }
                }
                else
                {
                    var marginPen = new Pen(Frozen(margin), Math.Max(thin, 1.2));
                    marginPen.Freeze();
                    context.DrawLine(marginPen, new Point(Paper.MarginX, top), new Point(Paper.MarginX, Math.Min(bottom, height)));
                }
                break;
            }
            case PaperStyle.Dotted:
            {
                var radius = Math.Max(1.1, 1.4 / Math.Max(zoom, 0.1) * 1.2);
                var first = Math.Max(1, (int)Math.Ceiling(top / spacing));
                for (var k = first; k * spacing < Math.Min(bottom, height); k++)
                {
                    var y = k * spacing;
                    for (var x = spacing; x < width; x += spacing)
                    {
                        context.DrawEllipse(lineBrush, null, new Point(x, y), radius, radius);
                    }
                }
                break;
            }
        }

        if (!separators) return;
        var dash = new Pen(Frozen(separator), Math.Max(1, 1.5 / Math.Max(zoom, 0.1)))
        {
            DashStyle = new DashStyle(new double[] { 4, 3 }, 0)
        };
        dash.Freeze();
        for (var page = 1; page < pages; page++)
        {
            var y = page * Paper.PageHeight;
            if (y < top || y > bottom) continue;
            context.DrawLine(dash, new Point(0, y), new Point(width, y));
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
