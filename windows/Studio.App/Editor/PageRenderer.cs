using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lernheft.Studio.App.Editor;

/// <summary>
/// Malt Seiten unsichtbar im Hintergrund: für das iPad (nur die Textebene), für PDF und Druck
/// (ganze Seiten auf hellem Papier) und für die KI (Bilder der Handschrift).
/// </summary>
public static class PageRenderer
{
    /// <summary>Ein Textfeld, das nur angezeigt wird – ohne Cursor, Rechtschreibprüfung und Rahmen.</summary>
    private static RichTextBox StaticBox(NoteTextBox model, TextEnvironment environment, Color ink)
    {
        var box = new RichTextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            IsReadOnly = true,
            Focusable = false,
            Width = Math.Max(40, model.Width),
            Foreground = new SolidColorBrush(ink),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Document = TextDocs.Create(model, environment)
        };
        var template = new ControlTemplate(typeof(RichTextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(UIElement.ClipToBoundsProperty, false);
        template.VisualTree = host;
        box.Template = template;
        SpellCheck.SetIsEnabled(box, false);
        return box;
    }

    private static Color InkColor(bool dark) => dark ? Theme.Hex("#E9EEF4") : Theme.Hex("#16202C");

    /// <summary>Alle Textfelder der Notiz, fertig vermessen, an ihren Plätzen.</summary>
    private static Canvas TextCanvas(NoteMeta note, NoteContent content, TextEnvironment environment, bool dark, double top, double height)
    {
        var canvas = new Canvas { Width = note.Width, Height = height, ClipToBounds = true };
        foreach (var model in content.TextBoxes)
        {
            if (model.Y > top + height) continue;
            var box = StaticBox(model, environment, InkColor(dark));
            Canvas.SetLeft(box, model.X);
            Canvas.SetTop(box, model.Y - top);
            canvas.Children.Add(box);
        }
        canvas.Measure(new Size(note.Width, height));
        canvas.Arrange(new Rect(0, 0, note.Width, height));
        canvas.UpdateLayout();
        // Felder, die ganz über der Seite enden, sind hier nicht zu sehen.
        return canvas;
    }

    /// <summary>
    /// Die Textebene einer Seite als durchsichtiges PNG für das iPad – oder null, wenn auf der Seite
    /// kein Text steht.
    /// </summary>
    public static byte[]? TextLayer(NoteMeta note, NoteContent content, TextEnvironment environment, int page,
        double scale, bool dark)
    {
        var top = page * Paper.PageHeight;
        var any = false;
        foreach (var model in content.TextBoxes)
        {
            if (string.IsNullOrWhiteSpace(model.Text) && string.IsNullOrWhiteSpace(model.Xaml)) continue;
            if (model.Y > top + Paper.PageHeight) continue;
            // Die genaue Höhe ist erst nach dem Vermessen bekannt – großzügig schätzen.
            var lines = Math.Max(1, model.Text.Split('\n').Length) + model.Text.Length / 40;
            if (model.Y + lines * 64 + 64 < top) continue;
            any = true;
            break;
        }
        if (!any) return null;

        var canvas = TextCanvas(note, content, environment, dark, top, Paper.PageHeight);
        var visible = canvas.Children.OfType<FrameworkElement>().Any(box =>
            Canvas.GetTop(box) + box.ActualHeight > 0 && Canvas.GetTop(box) < Paper.PageHeight);
        if (!visible) return null;

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(note.Width * scale), (int)Math.Ceiling(Paper.PageHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        return ImageTools.Png(bitmap);
    }

    /// <summary>Eine ganze Seite (Papier, Bilder, Text, Handschrift) als Bild.</summary>
    public static BitmapSource Page(NoteMeta note, NoteContent content, InkDocument ink, TextEnvironment environment,
        int page, double scale, bool dark = false, bool paper = true, Rect? region = null)
    {
        var area = region ?? new Rect(0, page * Paper.PageHeight, note.Width, Paper.PageHeight);
        var width = Math.Max(1, (int)Math.Ceiling(area.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(area.Height * scale));

        var root = new Canvas { Width = area.Width, Height = area.Height, ClipToBounds = true };
        if (paper)
        {
            var visual = new DrawingVisualHost(context =>
            {
                context.PushTransform(new TranslateTransform(-area.X, -area.Y));
                PaperLayer.Draw(context, note.PaperStyle, note.Width, Math.Max(1, note.PageCount), dark, scale, false,
                    area.Y, area.Bottom);
                context.Pop();
            }) { Width = area.Width, Height = area.Height };
            root.Children.Add(visual);
        }
        else
        {
            root.Background = dark ? new SolidColorBrush(PaperLayer.Colors(true).Paper) : Brushes.White;
        }

        var seamless = Services.Settings.GetBool(Keys.SeamlessImport, true);
        foreach (var background in content.Backgrounds)
        {
            var source = ImageTools.Load(Services.Store.ImagePath(note.Id, background.File));
            if (source is null) continue;
            var frame = ImageLayer.Frame(background, source, note.Width);
            if (!frame.IntersectsWith(area)) continue;
            var image = new Image
            {
                Source = seamless ? ImageTools.Seamless(source, dark) : source,
                Width = frame.Width,
                Height = frame.Height,
                Stretch = Stretch.Fill
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            Canvas.SetLeft(image, frame.X - area.X);
            Canvas.SetTop(image, frame.Y - area.Y);
            root.Children.Add(image);
        }

        var texts = TextCanvas(note, content, environment, dark, area.Y, area.Height);
        Canvas.SetLeft(texts, -area.X);
        root.Children.Add(texts);

        var strokes = ink.Strokes.Where(s =>
        {
            var (minX, minY, maxX, maxY) = s.Bounds();
            return new Rect(minX, minY, Math.Max(0.1, maxX - minX), Math.Max(0.1, maxY - minY)).IntersectsWith(area);
        }).ToList();
        root.Children.Add(new DrawingVisualHost(context =>
        {
            context.PushTransform(new TranslateTransform(-area.X, -area.Y));
            foreach (var stroke in strokes) InkRenderer.Draw(context, stroke, dark);
            context.Pop();
        }) { Width = area.Width, Height = area.Height });

        root.Measure(new Size(area.Width, area.Height));
        root.Arrange(new Rect(0, 0, area.Width, area.Height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Welche Seiten etwas enthalten.</summary>
    public static List<int> UsedPages(NoteMeta note, NoteContent content, InkDocument ink)
    {
        var used = new SortedSet<int>();
        foreach (var stroke in ink.Strokes) used.Add((int)(Math.Max(0, stroke.Bounds().MinY + stroke.Bounds().MaxY) / 2 / Paper.PageHeight));
        foreach (var box in content.TextBoxes.Where(b => !string.IsNullOrWhiteSpace(b.Text))) used.Add((int)(box.Y / Paper.PageHeight));
        foreach (var background in content.Backgrounds) used.Add(Math.Max(0, (int)((background.Y + 1) / Paper.PageHeight)));
        return used.Where(p => p >= 0 && p < Math.Max(1, note.PageCount) + 1).ToList();
    }

    /// <summary>
    /// Bilder für die KI: je beschriebener Seite eins (höchstens <paramref name="limit"/>). Ohne
    /// Bilder auf der Seite nur der beschriebene Bereich – dann ist die Schrift größer.
    /// </summary>
    public static List<GeminiImage> AiImages(NoteMeta note, NoteContent content, InkDocument ink, TextEnvironment environment,
        int limit = 3, bool inkOnly = true)
    {
        var result = new List<GeminiImage>();
        foreach (var page in UsedPages(note, content, ink))
        {
            if (result.Count >= limit) break;
            var top = page * Paper.PageHeight;
            var pageRect = new Rect(0, top, note.Width, Paper.PageHeight);
            var strokes = ink.Strokes.Where(s =>
            {
                var middle = (s.Bounds().MinY + s.Bounds().MaxY) / 2;
                return middle >= top && middle < top + Paper.PageHeight;
            }).ToList();
            var hasImages = content.Backgrounds.Any(b => ImageLayer.Frame(b, null, note.Width).IntersectsWith(pageRect));
            if (strokes.Count == 0 && !hasImages) continue;
            Rect area;
            if (hasImages)
            {
                area = pageRect;
            }
            else
            {
                var minX = strokes.Min(s => s.Bounds().MinX);
                var minY = strokes.Min(s => s.Bounds().MinY);
                var maxX = strokes.Max(s => s.Bounds().MaxX);
                var maxY = strokes.Max(s => s.Bounds().MaxY);
                area = new Rect(minX - 16, minY - 16, maxX - minX + 32, maxY - minY + 32);
            }
            var scale = Math.Min(2, 1600 / Math.Max(area.Width, area.Height));
            var bitmap = Page(note, inkOnly ? new NoteContent { Backgrounds = content.Backgrounds } : content, ink, environment,
                page, scale, dark: false, paper: false, region: area);
            result.Add(new GeminiImage(ImageTools.Jpeg(bitmap, 85), "image/jpeg"));
        }
        return result;
    }
}

/// <summary>Ein Element, das einfach nur mit einem DrawingContext malt.</summary>
public sealed class DrawingVisualHost(Action<DrawingContext> draw) : FrameworkElement
{
    protected override void OnRender(DrawingContext context) => draw(context);
}
