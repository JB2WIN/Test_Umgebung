using System.Globalization;
using System.Xml;

namespace Lernheft.Studio;

/// <summary>
/// Liest einen SVG-Export (z. B. aus MyScript Nebo) und macht daraus echte Striche.
/// So liegt alte Handschrift direkt auf dem Papier und lässt sich auf dem iPad radieren und verschieben.
/// </summary>
public static class SvgInk
{
    public record Outcome(List<InkStroke> Strokes, double Height, bool FilledShapesOnly);

    public class SvgException(string message) : Exception(message);

    private record Shape(List<List<(double X, double Y)>> Lines, string Color, double Width, bool Stroked);

    public static Outcome Load(string path, double pageWidth)
    {
        var document = new XmlDocument { XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null
            });
            document.Load(reader);
        }
        catch (XmlException)
        {
            throw new SvgException("Diese SVG-Datei konnte ich nicht lesen.");
        }

        var shapes = new List<Shape>();
        (double X, double Y, double W, double H)? viewBox = null;
        Walk(document.DocumentElement, Matrix.Identity, new Dictionary<string, string>(), shapes, ref viewBox);
        if (shapes.Count == 0) throw new SvgException("In der SVG-Datei habe ich keine Striche gefunden. Exportiere notfalls als PDF.");

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var shape in shapes)
        foreach (var line in shape.Lines)
        foreach (var (x, y) in line)
        {
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        var box = viewBox is { W: > 0 } vb ? (vb.X, vb.Y, vb.W, vb.H) : (minX, minY, maxX - minX, maxY - minY);
        if (box.Item3 <= 0) throw new SvgException("In der SVG-Datei habe ich keine Striche gefunden.");
        const double margin = 24;
        var scale = (pageWidth - 2 * margin) / box.Item3;

        var strokes = new List<InkStroke>();
        foreach (var shape in shapes)
        {
            var width = Math.Max(1.2, (shape.Stroked ? shape.Width : 1.4) * scale);
            foreach (var line in shape.Lines.Where(l => l.Count >= 2))
            {
                var stroke = new InkStroke { Id = InkIds.New(), Color = shape.Color, Width = Math.Round(width, 1), Kind = "pen" };
                foreach (var (x, y) in line) stroke.Add((x - box.Item1) * scale + margin, (y - box.Item2) * scale + margin, 0.6);
                strokes.Add(stroke);
            }
        }
        if (strokes.Count == 0) throw new SvgException("In der SVG-Datei habe ich keine Striche gefunden.");
        return new Outcome(strokes, box.Item4 * scale + 2 * margin, shapes.All(s => !s.Stroked));
    }

    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static Matrix Identity => new(1, 0, 0, 1, 0, 0);

        /// <summary>Erst <paramref name="first"/>, dann diese.</summary>
        public Matrix After(Matrix first) => new(
            first.A * A + first.B * C, first.A * B + first.B * D,
            first.C * A + first.D * C, first.C * B + first.D * D,
            first.E * A + first.F * C + E, first.E * B + first.F * D + F);

        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
    }

    private static void Walk(XmlElement? element, Matrix parent, Dictionary<string, string> inherited,
        List<Shape> shapes, ref (double, double, double, double)? viewBox)
    {
        if (element is null) return;
        var style = new Dictionary<string, string>(inherited);
        foreach (XmlAttribute attribute in element.Attributes)
        {
            if (attribute.LocalName == "style") continue;
            style[attribute.LocalName.ToLowerInvariant()] = attribute.Value;
        }
        if (element.GetAttribute("style") is { Length: > 0 } inline)
        {
            foreach (var declaration in inline.Split(';'))
            {
                var parts = declaration.Split(':');
                if (parts.Length == 2) style[parts[0].Trim().ToLowerInvariant()] = parts[1].Trim();
            }
        }
        var matrix = element.GetAttribute("transform") is { Length: > 0 } raw ? parent.After(ParseTransform(raw)) : parent;
        var tag = element.LocalName.ToLowerInvariant();

        if (tag == "svg" && viewBox is null && style.TryGetValue("viewbox", out var box))
        {
            var numbers = Numbers(box);
            if (numbers.Count >= 4) viewBox = (numbers[0], numbers[1], numbers[2], numbers[3]);
        }

        var lines = new List<List<(double X, double Y)>>();
        switch (tag)
        {
            case "path":
                if (style.TryGetValue("d", out var d)) lines = ParsePath(d);
                break;
            case "polyline":
            case "polygon":
                if (style.TryGetValue("points", out var points))
                {
                    var numbers = Numbers(points);
                    var line = new List<(double, double)>();
                    for (var index = 0; index + 1 < numbers.Count; index += 2) line.Add((numbers[index], numbers[index + 1]));
                    if (tag == "polygon" && line.Count > 0) line.Add(line[0]);
                    lines.Add(line);
                }
                break;
            case "line":
                lines.Add(new List<(double, double)>
                {
                    (Number(style, "x1"), Number(style, "y1")), (Number(style, "x2"), Number(style, "y2"))
                });
                break;
            case "rect":
                var (rx, ry, rw, rh) = (Number(style, "x"), Number(style, "y"), Number(style, "width"), Number(style, "height"));
                if (rw > 0 && rh > 0) lines.Add(new List<(double, double)> { (rx, ry), (rx + rw, ry), (rx + rw, ry + rh), (rx, ry + rh), (rx, ry) });
                break;
            case "circle":
            case "ellipse":
                var cx = Number(style, "cx");
                var cy = Number(style, "cy");
                var radiusX = tag == "circle" ? Number(style, "r") : Number(style, "rx");
                var radiusY = tag == "circle" ? Number(style, "r") : Number(style, "ry");
                if (radiusX > 0 && radiusY > 0)
                {
                    var circle = new List<(double, double)>();
                    for (var step = 0; step <= 48; step++)
                    {
                        var angle = step / 48.0 * 2 * Math.PI;
                        circle.Add((cx + radiusX * Math.Cos(angle), cy + radiusY * Math.Sin(angle)));
                    }
                    lines.Add(circle);
                }
                break;
        }

        if (lines.Count > 0)
        {
            var strokeValue = style.TryGetValue("stroke", out var s) ? s.ToLowerInvariant() : "none";
            var fillValue = style.TryGetValue("fill", out var f) ? f.ToLowerInvariant() : "none";
            var stroked = strokeValue != "none" && strokeValue.Length > 0;
            var color = ParseColor(stroked ? strokeValue : fillValue) ?? "#1A1F2B";
            var width = style.TryGetValue("stroke-width", out var w) ? ParseNumber(w, 2) : 2;
            var placed = lines.Select(line => line.Select(point => matrix.Apply(point.X, point.Y)).ToList())
                .Where(line => line.Count >= 2).ToList();
            if (placed.Count > 0) shapes.Add(new Shape(placed, color, width, stroked));
        }

        foreach (XmlNode child in element.ChildNodes)
        {
            if (child is XmlElement childElement) Walk(childElement, matrix, style, shapes, ref viewBox);
        }
    }

    private static double Number(Dictionary<string, string> style, string key) =>
        style.TryGetValue(key, out var value) ? ParseNumber(value, 0) : 0;

    private static double ParseNumber(string raw, double fallback) =>
        double.TryParse(raw.Trim().TrimEnd('x', 'p', 't', 'e', 'm', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    public static List<double> Numbers(string raw)
    {
        var result = new List<double>();
        var token = new System.Text.StringBuilder();
        void Flush()
        {
            if (token.Length > 0 && double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                result.Add(value);
            token.Clear();
        }
        foreach (var character in raw)
        {
            if (char.IsDigit(character) || character == '.' || character is 'e' or 'E')
            {
                if (character == '.' && token.ToString().Contains('.') && !token.ToString().Contains('e')) Flush();
                token.Append(character);
            }
            else if (character is '-' or '+')
            {
                if (token.Length > 0 && token[^1] is not ('e' or 'E')) Flush();
                token.Append(character);
            }
            else Flush();
        }
        Flush();
        return result;
    }

    private static Matrix ParseTransform(string raw)
    {
        var result = Matrix.Identity;
        var index = 0;
        while (index < raw.Length)
        {
            var open = raw.IndexOf('(', index);
            if (open < 0) break;
            var close = raw.IndexOf(')', open);
            if (close < 0) break;
            var name = raw[index..open].Trim(' ', ',', '\t', '\n').ToLowerInvariant();
            var values = Numbers(raw[(open + 1)..close]);
            var step = name switch
            {
                "translate" => new Matrix(1, 0, 0, 1, values.ElementAtOrDefault(0), values.Count > 1 ? values[1] : 0),
                "scale" => new Matrix(values.ElementAtOrDefault(0), 0, 0, values.Count > 1 ? values[1] : values.ElementAtOrDefault(0), 0, 0),
                "matrix" when values.Count >= 6 => new Matrix(values[0], values[1], values[2], values[3], values[4], values[5]),
                "rotate" => Rotation(values.ElementAtOrDefault(0)),
                _ => Matrix.Identity
            };
            // „A B" heißt: erst B, dann A.
            result = result.After(step);
            index = close + 1;
        }
        return result;
    }

    private static Matrix Rotation(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Matrix(Math.Cos(radians), Math.Sin(radians), -Math.Sin(radians), Math.Cos(radians), 0, 0);
    }

    private static string? ParseColor(string raw)
    {
        var value = raw.Trim().ToLowerInvariant();
        if (value.Length == 0 || value == "none") return null;
        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
            return hex.Length == 6 ? "#" + hex.ToUpperInvariant() : null;
        }
        if (value.StartsWith("rgb"))
        {
            var parts = Numbers(value);
            if (parts.Count < 3) return null;
            return $"#{(int)Math.Clamp(parts[0], 0, 255):X2}{(int)Math.Clamp(parts[1], 0, 255):X2}{(int)Math.Clamp(parts[2], 0, 255):X2}";
        }
        return value switch
        {
            "black" => "#1A1F2B",
            "white" => "#FFFFFF",
            "red" => "#DC3B3B",
            "blue" => "#2140C8",
            "green" => "#1E8A57",
            "orange" => "#E9730C",
            "yellow" => "#E5C100",
            "purple" or "violet" => "#8A4FE0",
            "gray" or "grey" => "#7A8591",
            _ => null
        };
    }

    public static List<List<(double X, double Y)>> ParsePath(string d)
    {
        var lines = new List<List<(double X, double Y)>>();
        var line = new List<(double X, double Y)>();
        var point = (X: 0.0, Y: 0.0);
        var command = 'M';
        var values = new List<double>();
        var token = new System.Text.StringBuilder();

        void FlushToken()
        {
            if (token.Length > 0 && double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                values.Add(value);
            token.Clear();
        }

        (double X, double Y) Target(int index, bool relative) =>
            relative ? (point.X + values[index], point.Y + values[index + 1]) : (values[index], values[index + 1]);

        void Apply()
        {
            var relative = char.IsLower(command);
            switch (char.ToLowerInvariant(command))
            {
                case 'm':
                    for (var i = 0; i + 1 < values.Count; i += 2)
                    {
                        var target = Target(i, relative);
                        if (i == 0)
                        {
                            if (line.Count >= 2) lines.Add(line);
                            line = new List<(double, double)> { target };
                        }
                        else line.Add(target);
                        point = target;
                    }
                    break;
                case 'l':
                    for (var i = 0; i + 1 < values.Count; i += 2)
                    {
                        point = Target(i, relative);
                        line.Add(point);
                    }
                    break;
                case 'h':
                    foreach (var value in values)
                    {
                        point = (relative ? point.X + value : value, point.Y);
                        line.Add(point);
                    }
                    break;
                case 'v':
                    foreach (var value in values)
                    {
                        point = (point.X, relative ? point.Y + value : value);
                        line.Add(point);
                    }
                    break;
                case 'c':
                    for (var i = 0; i + 5 < values.Count; i += 6)
                    {
                        var c1 = Target(i, relative);
                        var c2 = Target(i + 2, relative);
                        var end = Target(i + 4, relative);
                        for (var step = 1; step <= 12; step++)
                        {
                            var t = step / 12.0;
                            var mt = 1 - t;
                            line.Add((
                                mt * mt * mt * point.X + 3 * mt * mt * t * c1.X + 3 * mt * t * t * c2.X + t * t * t * end.X,
                                mt * mt * mt * point.Y + 3 * mt * mt * t * c1.Y + 3 * mt * t * t * c2.Y + t * t * t * end.Y));
                        }
                        point = end;
                    }
                    break;
                case 'q':
                    for (var i = 0; i + 3 < values.Count; i += 4)
                    {
                        var control = Target(i, relative);
                        var end = Target(i + 2, relative);
                        for (var step = 1; step <= 10; step++)
                        {
                            var t = step / 10.0;
                            var mt = 1 - t;
                            line.Add((
                                mt * mt * point.X + 2 * mt * t * control.X + t * t * end.X,
                                mt * mt * point.Y + 2 * mt * t * control.Y + t * t * end.Y));
                        }
                        point = end;
                    }
                    break;
                case 'z':
                    if (line.Count > 0)
                    {
                        line.Add(line[0]);
                        point = line[0];
                    }
                    break;
            }
            values.Clear();
        }

        foreach (var character in d)
        {
            if (char.IsLetter(character) && character is not ('e' or 'E'))
            {
                FlushToken();
                Apply();
                command = character;
            }
            else if (char.IsDigit(character) || character == '.' || character is 'e' or 'E')
            {
                if (character == '.' && token.ToString().Contains('.')) FlushToken();
                token.Append(character);
            }
            else if (character is '-' or '+')
            {
                if (token.Length > 0 && token[^1] is not ('e' or 'E')) FlushToken();
                token.Append(character);
            }
            else FlushToken();
        }
        FlushToken();
        Apply();
        if (line.Count >= 2) lines.Add(line);
        return lines;
    }
}
