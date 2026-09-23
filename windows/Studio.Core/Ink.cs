using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lernheft.Studio;

/// <summary>
/// Ein Strich im gemeinsamen Format von iPad und Surface (strokes.json).
/// Neu gegenüber der alten App ist die Kennung <see cref="Id"/>: Damit können iPad und Surface
/// einzelne Striche hinzufügen und entfernen, statt jedes Mal die ganze Seite zu schicken.
/// </summary>
public class InkStroke
{
    [JsonPropertyName("i")] public string? Id { get; set; }

    /// <summary>Farbe als #RRGGBB, so wie sie auf hellem Papier aussieht.</summary>
    [JsonPropertyName("c")] public string Color { get; set; } = "#1A1F2B";

    /// <summary>Strichstärke in Seiteneinheiten.</summary>
    [JsonPropertyName("w")] public double Width { get; set; } = 2.5;

    /// <summary>pen, pencil, marker oder shape.</summary>
    [JsonPropertyName("k")] public string Kind { get; set; } = "pen";

    /// <summary>Punkte als x, y, Druck (0–1) hintereinander.</summary>
    [JsonPropertyName("p")] public List<double> Points { get; set; } = new();

    [JsonIgnore] public int Count => Points.Count / 3;

    public (double X, double Y, double Pressure) PointAt(int index)
    {
        var offset = index * 3;
        return (Points[offset], Points[offset + 1], Points[offset + 2]);
    }

    public void Add(double x, double y, double pressure)
    {
        Points.Add(Math.Round(x, 2));
        Points.Add(Math.Round(y, 2));
        Points.Add(Math.Round(Math.Clamp(pressure, 0, 1), 2));
    }

    public (double MinX, double MinY, double MaxX, double MaxY) Bounds()
    {
        if (Count == 0) return (0, 0, 0, 0);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (var i = 0; i < Count; i++)
        {
            var (x, y, _) = PointAt(i);
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
        var half = Width / 2;
        return (minX - half, minY - half, maxX + half, maxY + half);
    }

    public InkStroke Clone() => new()
    {
        Id = Id,
        Color = Color,
        Width = Width,
        Kind = Kind,
        Points = new List<double>(Points)
    };

    public InkStroke Moved(double dx, double dy)
    {
        var copy = Clone();
        for (var index = 0; index + 2 < copy.Points.Count; index += 3)
        {
            copy.Points[index] = Math.Round(copy.Points[index] + dx, 2);
            copy.Points[index + 1] = Math.Round(copy.Points[index + 1] + dy, 2);
        }
        return copy;
    }
}

public class InkDocument
{
    [JsonPropertyName("v")] public int Version { get; set; } = 2;
    [JsonPropertyName("device")] public string Device { get; set; } = "Surface";
    [JsonPropertyName("strokes")] public List<InkStroke> Strokes { get; set; } = new();

    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static InkDocument Load(string path)
    {
        if (!File.Exists(path)) return new InkDocument();
        try
        {
            var document = JsonSerializer.Deserialize<InkDocument>(File.ReadAllText(path), Options) ?? new InkDocument();
            document.Strokes.RemoveAll(stroke => stroke.Count == 0);
            document.EnsureIds();
            return document;
        }
        catch (JsonException)
        {
            return new InkDocument();
        }
        catch (IOException)
        {
            return new InkDocument();
        }
    }

    /// <summary>Alte Striche haben noch keine Kennung – die bekommen sie hier, einmalig.</summary>
    public bool EnsureIds()
    {
        var changed = false;
        var seen = new HashSet<string>();
        foreach (var stroke in Strokes)
        {
            if (string.IsNullOrEmpty(stroke.Id) || !seen.Add(stroke.Id))
            {
                stroke.Id = InkIds.New();
                seen.Add(stroke.Id);
                changed = true;
            }
        }
        return changed;
    }

    public double Bottom
    {
        get
        {
            var bottom = 0.0;
            foreach (var stroke in Strokes) bottom = Math.Max(bottom, stroke.Bounds().MaxY);
            return bottom;
        }
    }

    public double Right
    {
        get
        {
            var right = 0.0;
            foreach (var stroke in Strokes) right = Math.Max(right, stroke.Bounds().MaxX);
            return right;
        }
    }

    /// <summary>
    /// Wendet Änderungen an: erst entfernen, dann hinzufügen. Doppelte Kennungen werden ersetzt,
    /// damit ein zweimal geschickter Strich nicht doppelt auf der Seite liegt.
    /// </summary>
    /// <returns>Ob sich etwas geändert hat.</returns>
    public bool Apply(IEnumerable<InkStroke>? add, IEnumerable<string>? remove)
    {
        var changed = false;
        if (remove is not null)
        {
            var gone = remove.ToHashSet();
            if (gone.Count > 0) changed |= Strokes.RemoveAll(stroke => stroke.Id is not null && gone.Contains(stroke.Id)) > 0;
        }
        if (add is not null)
        {
            foreach (var stroke in add)
            {
                if (stroke.Count == 0) continue;
                stroke.Id ??= InkIds.New();
                var existing = Strokes.FindIndex(other => other.Id == stroke.Id);
                if (existing >= 0) Strokes[existing] = stroke;
                else Strokes.Add(stroke);
                changed = true;
            }
        }
        return changed;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

public static class InkIds
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>Kurze, zufällige Kennung – zehn Zeichen reichen für Millionen Striche.</summary>
    public static string New()
    {
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[10];
        for (var index = 0; index < bytes.Length; index++) chars[index] = Alphabet[bytes[index] % Alphabet.Length];
        return new string(chars);
    }
}
