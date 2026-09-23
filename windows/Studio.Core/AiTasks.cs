using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lernheft.Studio;

/// <summary>Was die KI über eine Notiz wissen muss: Titel, getippter Text und Bilder der Handschrift.</summary>
public record AiContext(string Title, string TypedText, IReadOnlyList<GeminiImage> Images, bool FromSelection = false)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(TypedText) && Images.Count == 0;

    public string Prompt
    {
        get
        {
            var result = $"Titel der Notiz: {Title}\n";
            var typed = TypedText.Trim();
            if (typed.Length > 0)
            {
                result += $"\nGetippter Text:\n\"\"\"\n{(typed.Length > 30_000 ? typed[..30_000] : typed)}\n\"\"\"\n";
            }
            if (Images.Count > 0)
            {
                result += FromSelection
                    ? "\nDas Bild zeigt einen markierten handschriftlichen Ausschnitt.\n"
                    : "\nDie Bilder zeigen die Seiten der Notiz mit Handschrift (von oben nach unten).\n";
            }
            return result;
        }
    }

    public string Description
    {
        get
        {
            if (IsEmpty) return "Die Notiz ist leer – bei Mathe kannst du die Aufgabe eintippen.";
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(TypedText)) parts.Add("getippter Text");
            if (Images.Count > 0) parts.Add(Images.Count == 1 ? (FromSelection ? "1 Ausschnitt vom iPad" : "1 Seite") : $"{Images.Count} Seiten");
            return (FromSelection ? "Markierter Ausschnitt: " : "Nutzt: ") + string.Join(" · ", parts);
        }
    }
}

/// <summary>Die Aufgaben des KI-Helfers – dieselben Anweisungen wie früher auf dem iPad.</summary>
public static class AiTasks
{
    public const string TranscribePrompt = """
        Das Bild zeigt einen handschriftlichen Ausschnitt aus Schulnotizen.
        Transkribiere ihn genau so, wie er geschrieben ist: gleiche Wörter, gleiche Zeilenumbrüche. Korrigiere keine Fehler.
        Mathematik mit Unicode (x², √, ·, π), kein LaTeX.
        Gib NUR den transkribierten Text zurück – ohne Erklärung, ohne Anführungszeichen, ohne Markdown.
        """;

    public enum MathMode { Solve, Hint, Check }

    public static string MathInstruction(MathMode mode) => mode switch
    {
        MathMode.Hint => "Verrate NICHT die Lösung. Gib nur einen kurzen, hilfreichen Tipp für den nächsten Schritt und stelle eine Frage, die zum Weiterdenken anregt.",
        MathMode.Check => "Prüfe den Rechenweg. Gehe Zeile für Zeile durch, sage, was stimmt, und erkläre jeden Fehler genau (wo, warum, wie richtig). Schreibe am Ende **Fazit:** mit einem Satz.",
        _ => "Löse die Aufgabe Schritt für Schritt. Nummeriere die Schritte, erkläre jeden Schritt in einem Satz, und schreibe am Ende eine Zeile, die mit **Ergebnis:** beginnt."
    };

    public enum SummaryStyle { Short, Bullets, Sheet }

    public static string SummaryInstruction(SummaryStyle style) => style switch
    {
        SummaryStyle.Short => "Fasse die Notiz in 3 bis 4 klaren Sätzen zusammen.",
        SummaryStyle.Sheet => "Erstelle einen Lernzettel für eine Klassenarbeit: Abschnitte mit **fetten** Zwischenzeilen, die wichtigsten Begriffe mit kurzer Erklärung, Formeln, Merksätze und am Ende 2–3 typische Fehler.",
        _ => "Fasse die Notiz in 5 bis 10 knappen Stichpunkten zusammen. Wichtige Begriffe **fett**."
    };

    public record GrammarChange(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] string To,
        [property: JsonPropertyName("why")] string Why);

    public record GrammarResult(
        [property: JsonPropertyName("corrected")] string Corrected,
        [property: JsonPropertyName("changes")] List<GrammarChange> Changes);

    public record CardDraft(
        [property: JsonPropertyName("front")] string Front,
        [property: JsonPropertyName("back")] string Back);

    private static readonly object GrammarSchema = new
    {
        type = "OBJECT",
        properties = new
        {
            corrected = new { type = "STRING" },
            changes = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new { from = new { type = "STRING" }, to = new { type = "STRING" }, why = new { type = "STRING" } },
                    required = new[] { "from", "to", "why" }
                }
            }
        },
        required = new[] { "corrected", "changes" }
    };

    private static readonly object CardSchema = new
    {
        type = "ARRAY",
        items = new
        {
            type = "OBJECT",
            properties = new { front = new { type = "STRING" }, back = new { type = "STRING" } },
            required = new[] { "front", "back" }
        }
    };

    private static readonly JsonSerializerOptions Loose = new() { PropertyNameCaseInsensitive = true };

    public static async Task<string> TranscribeAsync(GeminiClient client, GeminiImage image, CancellationToken cancel = default) =>
        (await client.GenerateAsync(TranscribePrompt, new[] { image }, cancel: cancel)).Trim();

    public static async Task<GrammarResult> GrammarAsync(GeminiClient client, AiContext context, CancellationToken cancel = default)
    {
        var prompt = context.Prompt + """

            Aufgabe: Prüfe Rechtschreibung, Grammatik und Zeichensetzung. Der Text kann Deutsch oder eine Fremdsprache (z. B. Englisch, Französisch, Latein) sein – korrigiere in der Sprache des Textes.
            Ändere keinen Inhalt und keinen Stil, nur echte Fehler. Emojis und Aufzählungszeichen bleiben.
            "corrected": Wenn es getippten Text gibt, ist das der komplette getippte Text mit Korrekturen und exakt denselben Zeilenumbrüchen. Wenn es nur Handschrift gibt, ist es die korrigierte Abschrift der Handschrift.
            "changes": jede einzelne Korrektur mit dem falschen Wort bzw. der Stelle ("from"), der richtigen Form ("to") und einer kurzen Erklärung auf Deutsch ("why", höchstens 12 Wörter). Wenn alles stimmt, ist die Liste leer.
            """;
        var raw = await client.GenerateAsync(prompt, context.Images, json: true, schema: GrammarSchema, cancel: cancel);
        return JsonSerializer.Deserialize<GrammarResult>(raw, Loose) ?? new GrammarResult("", new List<GrammarChange>());
    }

    public static Task<string> MathAsync(GeminiClient client, AiContext context, string typedTask, MathMode mode,
        CancellationToken cancel = default)
    {
        string prompt;
        IReadOnlyList<GeminiImage> images = Array.Empty<GeminiImage>();
        if (string.IsNullOrWhiteSpace(typedTask))
        {
            prompt = context.Prompt + "\nDie Aufgabe steht in der Notiz bzw. im Bild. Wenn es mehrere Aufgaben gibt, bearbeite alle nacheinander.\n";
            images = context.Images;
        }
        else
        {
            prompt = $"Aufgabe:\n{typedTask.Trim()}\n";
        }
        return client.GenerateAsync(prompt + "\n" + MathInstruction(mode), images, cancel: cancel);
    }

    public static async Task<List<Flashcard>> CardsAsync(GeminiClient client, AiContext context, int count,
        CancellationToken cancel = default)
    {
        var prompt = context.Prompt + $"""

            Aufgabe: Erstelle genau {count} Karteikarten zum Lernen für eine Klassenarbeit.
            Vorderseite ("front"): eine kurze, eindeutige Frage oder ein Begriff.
            Rückseite ("back"): eine knappe, richtige Antwort (höchstens 30 Wörter).
            Decke die wichtigsten Begriffe, Formeln, Regeln und Zusammenhänge ab, ohne Wiederholungen.
            Sprache wie in der Notiz (Vokabeln bleiben in der Fremdsprache).
            """;
        var raw = await client.GenerateAsync(prompt, context.Images, json: true, schema: CardSchema, cancel: cancel);
        var drafts = JsonSerializer.Deserialize<List<CardDraft>>(raw, Loose) ?? new List<CardDraft>();
        return drafts
            .Where(draft => !string.IsNullOrWhiteSpace(draft.Front) && !string.IsNullOrWhiteSpace(draft.Back))
            .Select(draft => new Flashcard { Front = draft.Front.Trim(), Back = draft.Back.Trim() })
            .ToList();
    }

    public static Task<string> SummaryAsync(GeminiClient client, AiContext context, SummaryStyle style,
        CancellationToken cancel = default) =>
        client.GenerateAsync(context.Prompt + "\nAufgabe: " + SummaryInstruction(style), context.Images, cancel: cancel);

    public record ChatMessage(bool FromUser, string Text);

    public static Task<string> AskAsync(GeminiClient client, AiContext context, IReadOnlyList<ChatMessage> history,
        CancellationToken cancel = default)
    {
        var turns = new List<GeminiTurn>();
        for (var index = 0; index < history.Count; index++)
        {
            var message = history[index];
            if (index == 0)
            {
                var intro = context.Prompt + "\nBeantworte meine Fragen zu dieser Notiz. Erkläre verständlich und kurz. Wenn ich abgefragt werden möchte, stelle eine Frage nach der anderen.\n\nMeine Frage: " + message.Text;
                turns.Add(new GeminiTurn("user", intro, context.Images));
            }
            else
            {
                turns.Add(new GeminiTurn(message.FromUser ? "user" : "model", message.Text));
            }
        }
        return client.GenerateAsync(turns, cancel: cancel);
    }

    /// <summary>Markdown-Zeichen entfernen, bevor Text in die Notiz kommt.</summary>
    public static string Plain(string text) => text.Replace("**", "").Replace("__", "").Replace("`", "");

    // MARK: - Hausaufgaben

    private record HomeworkDraft(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("due")] string? Due);

    private static readonly object HomeworkSchema = new
    {
        type = "ARRAY",
        items = new
        {
            type = "OBJECT",
            properties = new { title = new { type = "STRING" }, due = new { type = "STRING" } },
            required = new[] { "title" }
        }
    };

    /// <summary>Sucht in einer Notiz nach Hausaufgaben und trägt neue ein.</summary>
    /// <returns>Eine Meldung für die Anzeige.</returns>
    public static async Task<string> FindHomeworkAsync(GeminiClient client, LibraryStore store, NoteMeta note,
        string text, IReadOnlyList<GeminiImage> images, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(text) && images.Count == 0) return "Diese Notiz ist leer.";
        var subject = store.NotebookOf(note)?.Name ?? "";
        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var prompt = $"Fach: {subject}\nTitel der Notiz: {note.Title}\nHeutiges Datum: {today} ({DateTime.Today.ToString("dddd", new CultureInfo("de-DE"))})\n";
        if (!string.IsNullOrWhiteSpace(text))
        {
            var clipped = text.Length > 20_000 ? text[..20_000] : text;
            prompt += $"\nNotiz:\n\"\"\"\n{clipped}\n\"\"\"\n";
        }
        if (images.Count > 0) prompt += "\nDazu Bilder der handschriftlichen Seiten.\n";
        prompt += """

            Aufgabe: Finde alle Hausaufgaben und To-dos, die ich laut dieser Notiz erledigen muss.
            Typische Formulierungen: „HA", „Hausaufgabe", „bis Freitag", „Nr. 3 a-c", „lernen für", „abgeben".
            "title": kurze, klare Aufgabe (höchstens 12 Wörter, z. B. „Buch S. 42 Nr. 3a–c").
            "due": Fälligkeitsdatum als JJJJ-MM-TT, wenn es sich aus der Notiz ergibt (auch aus „bis Freitag" mit dem heutigen Datum berechnen). Sonst weglassen.
            Nimm nur echte Aufgaben auf, keine Lerninhalte und keine Überschriften. Wenn es keine gibt, antworte mit [].
            """;

        var raw = await client.GenerateAsync(prompt, images, json: true, schema: HomeworkSchema, cancel: cancel);
        var drafts = JsonSerializer.Deserialize<List<HomeworkDraft>>(raw, Loose) ?? new List<HomeworkDraft>();
        var items = drafts.Take(12).Select(draft => new Homework
        {
            Title = draft.Title,
            Subject = subject,
            NoteId = note.Id,
            FromAI = true,
            Due = DateTime.TryParseExact(draft.Due ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var due)
                ? AppleTime.FromDateTime(due)
                : null
        }).ToList();

        var added = store.AddHomework(items);
        store.UpdateNote(note.Id, n => n.LastHomeworkScan = AppleTime.Now, touch: false);
        if (added > 0) return added == 1 ? "1 Hausaufgabe übernommen." : $"{added} Hausaufgaben übernommen.";
        return items.Count == 0
            ? $"In „{note.Title}“ habe ich keine Aufgaben gefunden."
            : "Die gefundenen Aufgaben standen schon in der Liste.";
    }
}
