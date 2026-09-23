using System.Text.Json.Serialization;

namespace Lernheft.Studio;

// Die Datenmodelle von Lernheft Studio.
//
// Die JSON-Namen sind absichtlich dieselben wie in der alten Lernheft-App (iPad und Surface):
// So lassen sich alte Notizen ohne Umweg einlesen. Neu sind die Textfelder (wie in OneNote),
// die Breite einer Seite und Kennungen für einzelne Striche.

public enum PaperStyle { Lined, Grid, Dotted, Blank }

public static class Paper
{
    /// <summary>Abstand der Linien und Kästchen in Seiteneinheiten.</summary>
    public const double Spacing = 32;

    /// <summary>Linker Rand (rote Linie) auf liniertem Papier.</summary>
    public const double MarginX = 64;

    /// <summary>Höhe einer Seite – 35 Linien.</summary>
    public const double PageHeight = 1120;

    /// <summary>Normale Seitenbreite. Alte Notizen vom iPad können breiter sein.</summary>
    public const double PageWidth = 800;

    public static string ToJson(PaperStyle style) => style switch
    {
        PaperStyle.Grid => "grid",
        PaperStyle.Dotted => "dotted",
        PaperStyle.Blank => "blank",
        _ => "lined"
    };

    public static PaperStyle FromJson(string? value) => value switch
    {
        "grid" => PaperStyle.Grid,
        "dotted" => PaperStyle.Dotted,
        "blank" => PaperStyle.Blank,
        _ => PaperStyle.Lined
    };

    public static string Label(PaperStyle style) => style switch
    {
        PaperStyle.Grid => "Kariert",
        PaperStyle.Dotted => "Punktiert",
        PaperStyle.Blank => "Blanko",
        _ => "Liniert"
    };
}

public class Notebook
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("name")] public string Name { get; set; } = "Fach";
    [JsonPropertyName("colorName")] public string ColorName { get; set; } = "blue";
    [JsonPropertyName("defaultPaper")] public string DefaultPaper { get; set; } = "lined";

    /// <summary>Nur in diesen Fächern sucht die KI beim Schließen einer Notiz nach Hausaufgaben.</summary>
    [JsonPropertyName("scanHomework")] public bool ScanHomework { get; set; }

    [JsonIgnore] public PaperStyle Paper => Studio.Paper.FromJson(DefaultPaper);
}

public class NoteMeta
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("notebookID")] public Guid NotebookId { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "Neue Notiz";
    [JsonPropertyName("paper")] public string Paper { get; set; } = "lined";
    [JsonPropertyName("created")] public double Created { get; set; } = AppleTime.Now;
    [JsonPropertyName("updated")] public double Updated { get; set; } = AppleTime.Now;
    [JsonPropertyName("pageCount")] public int PageCount { get; set; } = 2;
    [JsonPropertyName("snippet")] public string Snippet { get; set; } = "";
    [JsonPropertyName("lastHomeworkScan")] public double? LastHomeworkScan { get; set; }
    [JsonPropertyName("knownHomework")] public List<string> KnownHomework { get; set; } = new();

    /// <summary>Breite der Seite. Notizen vom iPad im Querformat sind manchmal breiter als 800.</summary>
    [JsonPropertyName("pageWidth")] public double PageWidth { get; set; } = Studio.Paper.PageWidth;

    [JsonIgnore] public DateTime UpdatedAt => AppleTime.ToDateTime(Updated);
    [JsonIgnore] public DateTime CreatedAt => AppleTime.ToDateTime(Created);
    [JsonIgnore] public PaperStyle PaperStyle => Studio.Paper.FromJson(Paper);
    [JsonIgnore] public double Width => PageWidth >= 400 ? PageWidth : Studio.Paper.PageWidth;
}

/// <summary>
/// Ein Textfeld auf der Seite – wie in OneNote: irgendwo hinklicken und lostippen.
/// Der formatierte Inhalt steckt als XAML in <see cref="Xaml"/>, der reine Text in
/// <see cref="Text"/> (für Suche, Vorschau und KI).
/// </summary>
public class NoteTextBox
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("width")] public double Width { get; set; } = 480;
    [JsonPropertyName("xaml")] public string Xaml { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";

    /// <summary>
    /// Eigener Zeilenabstand, z. B. der eines eingescannten Arbeitsblatts. Leer heißt: wie das Papier.
    /// </summary>
    [JsonPropertyName("lineHeight")] public double? LineHeight { get; set; }

    /// <summary>Wird beim ersten Anzeigen aus diesem Text aufgebaut, wenn noch kein XAML da ist.</summary>
    [JsonPropertyName("seed")] public TextSeed? Seed { get; set; }
}

/// <summary>Ausgangsform für ein Textfeld, das noch nie formatiert wurde (Import, KI, iPad).</summary>
public class TextSeed
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("fontSize")] public double? FontSize { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }

    /// <summary>Schönschrift: in der gewählten Handschrift-Schriftart statt der normalen.</summary>
    [JsonPropertyName("script")] public bool Script { get; set; }
}

/// <summary>Alter Schönschrift-Block der iPad-App. Wird beim Import zu einem Textfeld.</summary>
public class LegacyTextBlock
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("width")] public double Width { get; set; } = 200;
    [JsonPropertyName("fontSize")] public double FontSize { get; set; } = 24;
    [JsonPropertyName("colorHex")] public string ColorHex { get; set; } = "#1A1F2B";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

/// <summary>Ein eingefügtes Bild (Scan, PDF-Seite, Foto) mit Platz auf der Seite.</summary>
public class PageBackground
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("height")] public double Height { get; set; }

    /// <summary>Unveränderte Kopie, damit „Original wiederherstellen" nach dem Radieren geht.</summary>
    [JsonPropertyName("originalFile")] public string? OriginalFile { get; set; }
}

public class NoteContent
{
    [JsonPropertyName("textBoxes")] public List<NoteTextBox> TextBoxes { get; set; } = new();
    [JsonPropertyName("backgrounds")] public List<PageBackground> Backgrounds { get; set; } = new();

    // Felder der alten App. Sie werden beim Import in Textfelder umgewandelt und danach leer gelassen.
    [JsonPropertyName("typedText")] public string? TypedText { get; set; }
    [JsonPropertyName("blocks")] public List<LegacyTextBlock>? Blocks { get; set; }
    [JsonPropertyName("typedY")] public double? TypedY { get; set; }
    [JsonPropertyName("typedX")] public double? TypedX { get; set; }

    [JsonIgnore]
    public string PlainText => string.Join("\n\n", TextBoxes
        .OrderBy(box => box.Y).ThenBy(box => box.X)
        .Select(box => box.Text.Trim())
        .Where(text => text.Length > 0));
}

public class Flashcard
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("front")] public string Front { get; set; } = "";
    [JsonPropertyName("back")] public string Back { get; set; } = "";
    [JsonPropertyName("box")] public int Box { get; set; } = 1;
    [JsonPropertyName("due")] public double Due { get; set; } = AppleTime.Now;

    [JsonIgnore] public DateTime DueAt => AppleTime.ToDateTime(Due);
}

public class Deck
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("name")] public string Name { get; set; } = "Stapel";
    [JsonPropertyName("noteID")] public Guid? NoteId { get; set; }
    [JsonPropertyName("cards")] public List<Flashcard> Cards { get; set; } = new();
}

public class Homework
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";
    [JsonPropertyName("noteID")] public Guid? NoteId { get; set; }
    [JsonPropertyName("due")] public double? Due { get; set; }
    [JsonPropertyName("done")] public bool Done { get; set; }
    [JsonPropertyName("created")] public double Created { get; set; } = AppleTime.Now;
    [JsonPropertyName("fromAI")] public bool FromAI { get; set; }

    [JsonIgnore] public DateTime? DueAt => Due is null ? null : AppleTime.ToDateTime(Due.Value);
}

public class Lesson
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>1 = Montag … 7 = Sonntag.</summary>
    [JsonPropertyName("weekday")] public int Weekday { get; set; } = 1;

    /// <summary>Minuten seit Mitternacht.</summary>
    [JsonPropertyName("start")] public int Start { get; set; } = 480;
    [JsonPropertyName("end")] public int End { get; set; } = 525;
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";
    [JsonPropertyName("room")] public string Room { get; set; } = "";
    [JsonPropertyName("notebookID")] public Guid? NotebookId { get; set; }

    [JsonIgnore] public string TimeText => Clock(Start) + "–" + Clock(End);

    public static string Clock(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

    public static readonly string[] WeekdayNames =
        { "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag", "Sonntag" };

    public static int TodayIndex(DateTime? date = null)
    {
        var day = (int)(date ?? DateTime.Today).DayOfWeek; // 0 = Sonntag
        return day == 0 ? 7 : day;
    }
}

/// <summary>Der tatsächliche Stand eines Schultags aus WebUntis – mit Entfall und Vertretung.</summary>
public class DayLesson
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Datum als 20260921.</summary>
    [JsonPropertyName("date")] public int Date { get; set; }
    [JsonPropertyName("start")] public int Start { get; set; }
    [JsonPropertyName("end")] public int End { get; set; }
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";
    [JsonPropertyName("room")] public string Room { get; set; } = "";
    [JsonPropertyName("teacher")] public string Teacher { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "";

    [JsonIgnore] public bool IsCancelled => State.Contains("CANCEL", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsExam => State.Contains("EXAM", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsChanged => State.Contains("SUBSTITUTION", StringComparison.OrdinalIgnoreCase)
                             || State.Contains("SHIFT", StringComparison.OrdinalIgnoreCase)
                             || State.Contains("ADDITIONAL", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string? StatusText => IsCancelled ? "entfällt" : IsExam ? "Prüfung" : IsChanged ? "Vertretung" : null;

    [JsonIgnore] public string TimeText => Lesson.Clock(Start) + "–" + Lesson.Clock(End);

    public static int DateValue(DateTime date) => date.Year * 10_000 + date.Month * 100 + date.Day;
}

public class Library
{
    [JsonPropertyName("notebooks")] public List<Notebook> Notebooks { get; set; } = new();
    [JsonPropertyName("notes")] public List<NoteMeta> Notes { get; set; } = new();
    [JsonPropertyName("decks")] public List<Deck> Decks { get; set; } = new();
    [JsonPropertyName("homework")] public List<Homework> Homework { get; set; } = new();
    [JsonPropertyName("lessons")] public List<Lesson> Lessons { get; set; } = new();
    [JsonPropertyName("weekEntries")] public List<DayLesson> WeekEntries { get; set; } = new();
}

/// <summary>
/// Apple speichert Datumsangaben als Sekunden seit dem 1.1.2001. Die alten Dateien nutzen das,
/// deshalb bleibt es dabei – so lassen sie sich unverändert einlesen.
/// </summary>
public static class AppleTime
{
    private static readonly DateTime Epoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static double Now => (DateTime.UtcNow - Epoch).TotalSeconds;

    public static DateTime ToDateTime(double value) => Epoch.AddSeconds(value).ToLocalTime();

    public static double FromDateTime(DateTime value) => (value.ToUniversalTime() - Epoch).TotalSeconds;
}

/// <summary>Die 20 Fachfarben – gleiche Werte wie in der alten App.</summary>
public static class NotebookColors
{
    public static readonly (string Name, string Hex, string Label)[] All =
    {
        ("blue", "#2F5BEA", "Blau"), ("indigo", "#4338CA", "Indigo"), ("violet", "#6D4AE0", "Violett"),
        ("purple", "#8A4FE0", "Lila"), ("magenta", "#B72FA8", "Magenta"), ("pink", "#E0457B", "Pink"),
        ("red", "#DC3B3B", "Rot"), ("coral", "#F0603C", "Koralle"), ("orange", "#E9730C", "Orange"),
        ("amber", "#C98A00", "Bernstein"), ("yellow", "#B8A200", "Gelb"), ("olive", "#7E8B1F", "Oliv"),
        ("lime", "#5A9E23", "Hellgrün"), ("green", "#1F9D5C", "Grün"), ("emerald", "#0E9488", "Smaragd"),
        ("teal", "#138F99", "Petrol"), ("cyan", "#0E86C4", "Türkis"), ("steel", "#4A6C8C", "Stahlblau"),
        ("brown", "#8A5A3B", "Braun"), ("graphite", "#5C6672", "Graphit")
    };

    public static string Hex(string? name)
    {
        foreach (var entry in All)
        {
            if (entry.Name == name) return entry.Hex;
        }
        return All[0].Hex;
    }

    public static string Label(string? name)
    {
        foreach (var entry in All)
        {
            if (entry.Name == name) return entry.Label;
        }
        return All[0].Label;
    }
}
