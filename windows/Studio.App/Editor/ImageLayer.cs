using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lernheft.Studio.App.Editor;

/// <summary>Bilder laden, nahtlos einblenden (Weiß wird durchsichtig) und radieren.</summary>
public static class ImageTools
{
    public static BitmapSource? Load(string path, int decodeWidth = 0)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static BitmapSource? FromBytes(byte[] data)
    {
        try
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(data);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Macht den weißen Hintergrund eines Scans durchsichtig, sodass nur die Schrift übrig bleibt
    /// und das Papier durchscheint. Im Dunkeln wird graue bis schwarze Tinte hell.
    /// </summary>
    public static BitmapSource Seamless(BitmapSource source, bool dark)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        for (var index = 0; index < pixels.Length; index += 4)
        {
            double blue = pixels[index], green = pixels[index + 1], red = pixels[index + 2];
            var sourceAlpha = pixels[index + 3] / 255.0;
            var luminance = (0.299 * red + 0.587 * green + 0.114 * blue) / 255;
            var alpha = Math.Min(1, (1 - luminance) * 1.2) * sourceAlpha;
            if (alpha < 0.07)
            {
                pixels[index] = pixels[index + 1] = pixels[index + 2] = pixels[index + 3] = 0;
                continue;
            }
            var spread = Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue));
            if (dark && spread < 45)
            {
                red = 255 - red;
                green = 255 - green;
                blue = 255 - blue;
            }
            // Vormultipliziert ablegen, damit die Kanten nicht grau ausfransen.
            pixels[index] = (byte)(blue * alpha);
            pixels[index + 1] = (byte)(green * alpha);
            pixels[index + 2] = (byte)(red * alpha);
            pixels[index + 3] = (byte)(alpha * 255);
        }
        var result = BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Pbgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    /// <summary>Radiert Kreise aus einem Bild heraus. Punkte und Radius sind in Bildpunkten.</summary>
    public static BitmapSource Erase(BitmapSource source, IReadOnlyList<Point> points, double radius)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var bitmap = new WriteableBitmap(converted);
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        var r2 = radius * radius;
        // Zwischen den Punkten auffüllen, damit schnelle Bewegungen keine Lücken lassen.
        var filled = new List<Point>();
        for (var i = 0; i < points.Count; i++)
        {
            filled.Add(points[i]);
            if (i == 0) continue;
            var distance = (points[i] - points[i - 1]).Length;
            var steps = (int)(distance / Math.Max(1, radius / 2));
            for (var step = 1; step < steps; step++)
            {
                filled.Add(points[i - 1] + (points[i] - points[i - 1]) * (step / (double)steps));
            }
        }
        foreach (var point in filled)
        {
            var minX = Math.Max(0, (int)(point.X - radius));
            var maxX = Math.Min(width - 1, (int)(point.X + radius));
            var minY = Math.Max(0, (int)(point.Y - radius));
            var maxY = Math.Min(height - 1, (int)(point.Y + radius));
            for (var y = minY; y <= maxY; y++)
            {
                var dy = y - point.Y;
                for (var x = minX; x <= maxX; x++)
                {
                    var dx = x - point.X;
                    if (dx * dx + dy * dy > r2) continue;
                    var offset = y * stride + x * 4;
                    pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = pixels[offset + 3] = 0;
                }
            }
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    public static byte[] Png(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static byte[] Jpeg(BitmapSource source, int quality = 88)
    {
        // JPEG kennt keine Durchsichtigkeit: auf Weiß legen.
        var flattened = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var area = new Rect(0, 0, source.PixelWidth, source.PixelHeight);
            context.DrawRectangle(Brushes.White, null, area);
            context.DrawImage(source, area);
        }
        flattened.Render(visual);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(flattened));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Große Fotos auf eine vernünftige Größe bringen, bevor sie in die Notiz kommen.</summary>
    public static BitmapSource Shrink(BitmapSource source, int maxSide = 2400)
    {
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= maxSide) return source;
        var factor = (double)maxSide / longest;
        var scaled = new TransformedBitmap(source, new ScaleTransform(factor, factor));
        scaled.Freeze();
        return scaled;
    }
}

/// <summary>Die eingefügten Bilder einer Notiz, unter der Schrift und der Handschrift.</summary>
public sealed class ImageLayer : Canvas
{
    private readonly Dictionary<Guid, Image> _views = new();
    private readonly Dictionary<string, BitmapSource> _processed = new();

    public ImageLayer()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;
    }

    public Guid NoteId { get; set; }
    public bool Seamless { get; set; } = true;
    public bool Dark { get; set; }

    /// <summary>Die Rohbilder, damit Radieren und Linien-Erkennung darauf arbeiten können.</summary>
    public Dictionary<Guid, BitmapSource> Originals { get; } = new();

    public void SetAll(IEnumerable<PageBackground> backgrounds, LibraryStore store, double pageWidth)
    {
        Children.Clear();
        _views.Clear();
        Originals.Clear();
        foreach (var background in backgrounds) Place(background, store, pageWidth);
    }

    public void Place(PageBackground background, LibraryStore store, double pageWidth, BitmapSource? replace = null)
    {
        var source = replace ?? ImageTools.Load(store.ImagePath(NoteId, background.File));
        if (source is null) return;
        Originals[background.Id] = source;
        var frame = Frame(background, source, pageWidth);
        if (!_views.TryGetValue(background.Id, out var view))
        {
            view = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = false };
            RenderOptions.SetBitmapScalingMode(view, BitmapScalingMode.HighQuality);
            _views[background.Id] = view;
            Children.Add(view);
        }
        view.Source = Display(background, source);
        view.Width = frame.Width;
        view.Height = frame.Height;
        SetLeft(view, frame.X);
        SetTop(view, frame.Y);
    }

    public void Move(PageBackground background, double pageWidth)
    {
        if (!_views.TryGetValue(background.Id, out var view) || !Originals.TryGetValue(background.Id, out var source)) return;
        var frame = Frame(background, source, pageWidth);
        view.Width = frame.Width;
        view.Height = frame.Height;
        SetLeft(view, frame.X);
        SetTop(view, frame.Y);
    }

    public void Remove(Guid id)
    {
        if (!_views.Remove(id, out var view)) return;
        Children.Remove(view);
        Originals.Remove(id);
    }

    private BitmapSource Display(PageBackground background, BitmapSource source)
    {
        if (!Seamless) return source;
        var key = $"{background.Id}|{background.File}|{Dark}|{source.GetHashCode()}";
        if (_processed.TryGetValue(key, out var done)) return done;
        var processed = ImageTools.Seamless(source, Dark);
        _processed[key] = processed;
        return processed;
    }

    /// <summary>Wo das Bild auf der Seite liegt. Alte Bilder ohne Maße füllen ihre Seite.</summary>
    public static Rect Frame(PageBackground background, BitmapSource? source, double pageWidth)
    {
        if (background.Width > 1 && background.Height > 1)
            return new Rect(background.X, background.Y, background.Width, background.Height);
        var page = new Rect(0, background.Page * Paper.PageHeight, pageWidth, Paper.PageHeight);
        if (source is null || source.PixelWidth == 0) return page;
        var factor = Math.Min(page.Width / source.PixelWidth, page.Height / source.PixelHeight);
        var size = new Size(source.PixelWidth * factor, source.PixelHeight * factor);
        return new Rect(page.X + (page.Width - size.Width) / 2, page.Y, size.Width, size.Height);
    }
}
