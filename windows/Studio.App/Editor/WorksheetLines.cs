using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lernheft.Studio.App.Editor;

/// <summary>
/// Findet die Linien auf einem eingescannten Arbeitsblatt.
///
/// Warum ohne KI: Gedruckte Linien sind lange dunkle Reihen quer über das Blatt – das sieht man
/// beim Durchzählen der dunklen Bildpunkte je Zeile sofort. Das geht in Millisekunden, kostet
/// nichts und funktioniert auch ohne Netz. Damit kann getippter Text genau auf den Linien des
/// Arbeitsblatts sitzen statt irgendwo daneben.
/// </summary>
public static class WorksheetLines
{
    public record Result(List<double> Lines, double Spacing)
    {
        public bool Found => Lines.Count >= 3;

        /// <summary>Die Linie, die am dichtesten an dieser Stelle liegt.</summary>
        public double Nearest(double y)
        {
            if (Lines.Count == 0) return y;
            var best = Lines[0];
            foreach (var line in Lines)
            {
                if (Math.Abs(line - y) < Math.Abs(best - y)) best = line;
            }
            return best;
        }
    }

    /// <summary>
    /// Sucht waagerechte Linien. Die Rückgabe ist in den Koordinaten des Rechtecks, in dem
    /// das Bild auf der Seite liegt – also direkt verwendbar.
    /// </summary>
    public static Result Detect(BitmapSource image, double top, double height)
    {
        var lines = new List<double>();
        try
        {
            // Kleiner rechnen: für Linien reicht eine grobe Auflösung, und es geht viel schneller.
            var width = Math.Min(700, image.PixelWidth);
            var scale = (double)width / image.PixelWidth;
            var rows = Math.Max(10, (int)(image.PixelHeight * scale));

            var small = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            var gray = new FormatConvertedBitmap(small, PixelFormats.Gray8, null, 0);
            var stride = width;
            var pixels = new byte[stride * rows];
            gray.CopyPixels(pixels, stride, 0);

            // Je Zeile zählen, wie viele Punkte deutlich dunkler sind als das Papier.
            var darkPerRow = new int[rows];
            for (var row = 0; row < rows; row++)
            {
                var dark = 0;
                var offset = row * stride;
                for (var column = 0; column < width; column++)
                {
                    if (pixels[offset + column] < 190) dark++;
                }
                darkPerRow[row] = dark;
            }

            // Eine Linie ist eine Reihe, die über den halben Bogen geht.
            var threshold = (int)(width * 0.45);
            var run = new List<int>();
            for (var row = 0; row < rows; row++)
            {
                if (darkPerRow[row] >= threshold)
                {
                    run.Add(row);
                    continue;
                }
                if (run.Count > 0)
                {
                    // Dicke Balken (Tabellenränder) zählen trotzdem als eine Linie.
                    lines.Add((run[0] + run[^1]) / 2.0 / rows);
                    run.Clear();
                }
            }
            if (run.Count > 0) lines.Add((run[0] + run[^1]) / 2.0 / rows);
        }
        catch (Exception)
        {
            return new Result(new List<double>(), 0);
        }

        // Von Anteilen auf die Stelle im Bild umrechnen.
        var placed = lines.Select(share => top + share * height).ToList();

        // Der übliche Abstand: der Mittelwert der Lücken, Ausreißer fliegen raus.
        var gaps = new List<double>();
        for (var index = 1; index < placed.Count; index++) gaps.Add(placed[index] - placed[index - 1]);
        var spacing = 0.0;
        if (gaps.Count > 0)
        {
            gaps.Sort();
            var middle = gaps[gaps.Count / 2];
            var close = gaps.Where(gap => gap > middle * 0.6 && gap < middle * 1.6).ToList();
            spacing = close.Count > 0 ? close.Average() : middle;
        }
        return new Result(placed, spacing);
    }
}
