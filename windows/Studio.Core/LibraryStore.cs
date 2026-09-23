using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lernheft.Studio;

/// <summary>
/// Alles liegt als Dateien im Datenordner:
/// library.json (Fächer, Notizliste, Hausaufgaben, Karteikarten, Stundenplan) und je Notiz ein
/// Ordner notes/&lt;ID&gt; mit content.json (Textfelder, Bilder), strokes.json (Handschrift) und
/// den Bildern selbst.
/// </summary>
public class LibraryStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public string Root { get; }
    public Library Library { get; private set; } = new();

    /// <summary>Etwas an der Bibliothek hat sich geändert (Liste, Hausaufgaben, Karten …).</summary>
    public event Action? Changed;

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lernheft Studio");

    public LibraryStore(string? root = null, bool seed = true)
    {
        Root = root ?? DefaultRoot;
        Directory.CreateDirectory(Root);
        Load(seed);
    }

    public string LibraryPath => Path.Combine(Root, "library.json");
    public string NotesRoot => Path.Combine(Root, "notes");
    public string NoteDirectory(Guid id) => Path.Combine(NotesRoot, id.ToString().ToUpperInvariant());
    public string ContentPath(Guid id) => Path.Combine(NoteDirectory(id), "content.json");
    public string StrokesPath(Guid id) => Path.Combine(NoteDirectory(id), "strokes.json");
    public string ImagePath(Guid note, string file) => Path.Combine(NoteDirectory(note), file);

    /// <summary>Ob beim Start schon Notizen da waren – sonst gibt es die Willkommensnotiz.</summary>
    public bool WasSeeded { get; private set; }

    private void Load(bool seed)
    {
        if (File.Exists(LibraryPath))
        {
            try
            {
                Library = JsonSerializer.Deserialize<Library>(File.ReadAllText(LibraryPath), Json) ?? new Library();
                // Eine Kopie des letzten guten Stands – falls die Datei je kaputtgeht.
                try { File.Copy(LibraryPath, LibraryPath + ".previous", overwrite: true); }
                catch (IOException) { }
                return;
            }
            catch (JsonException)
            {
                // Lieber die Sicherung nehmen als eine kaputte Datei überschreiben.
                var previous = LibraryPath + ".previous";
                if (File.Exists(previous))
                {
                    try
                    {
                        Library = JsonSerializer.Deserialize<Library>(File.ReadAllText(previous), Json) ?? new Library();
                        return;
                    }
                    catch (JsonException) { }
                }
                File.Copy(LibraryPath, LibraryPath + ".defekt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                Library = new Library();
                return;
            }
        }
        if (seed) Seed();
    }

    public void Reload() => Load(seed: false);

    public void Save()
    {
        var text = JsonSerializer.Serialize(Library, Json);
        var temp = LibraryPath + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, LibraryPath, overwrite: true);
        Changed?.Invoke();
    }

    // MARK: - Fächer

    public Notebook CreateNotebook(string name, string color, bool? scanHomework = null)
    {
        var notebook = new Notebook
        {
            Name = name.Trim(),
            ColorName = color,
            DefaultPaper = name.Contains("mathe", StringComparison.OrdinalIgnoreCase)
                           || name.Contains("physik", StringComparison.OrdinalIgnoreCase) ? "grid" : "lined",
            ScanHomework = scanHomework ?? name.Contains("hausaufgab", StringComparison.OrdinalIgnoreCase)
        };
        Library.Notebooks.Add(notebook);
        Save();
        return notebook;
    }

    public void UpdateNotebook(Guid id, Action<Notebook> change)
    {
        var notebook = Library.Notebooks.FirstOrDefault(n => n.Id == id);
        if (notebook is null) return;
        change(notebook);
        Save();
    }

    public void MoveNotebook(Guid id, int offset)
    {
        var index = Library.Notebooks.FindIndex(n => n.Id == id);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Library.Notebooks.Count) return;
        var notebook = Library.Notebooks[index];
        Library.Notebooks.RemoveAt(index);
        Library.Notebooks.Insert(target, notebook);
        Save();
    }

    public void MoveNotebookTo(Guid id, int target)
    {
        var index = Library.Notebooks.FindIndex(n => n.Id == id);
        if (index < 0) return;
        target = Math.Clamp(target, 0, Library.Notebooks.Count - 1);
        if (index == target) return;
        var notebook = Library.Notebooks[index];
        Library.Notebooks.RemoveAt(index);
        Library.Notebooks.Insert(target, notebook);
        Save();
    }

    public void DeleteNotebook(Guid id)
    {
        foreach (var note in Library.Notes.Where(n => n.NotebookId == id).ToList()) DeleteNoteFiles(note.Id);
        var gone = Library.Notes.Where(n => n.NotebookId == id).Select(n => n.Id).ToHashSet();
        Library.Notes.RemoveAll(n => n.NotebookId == id);
        foreach (var deck in Library.Decks.Where(d => d.NoteId is Guid noteId && gone.Contains(noteId))) deck.NoteId = null;
        Library.Notebooks.RemoveAll(n => n.Id == id);
        foreach (var lesson in Library.Lessons.Where(l => l.NotebookId == id)) lesson.NotebookId = null;
        Save();
    }

    public int NoteCount(Guid notebookId) => Library.Notes.Count(n => n.NotebookId == notebookId);

    // MARK: - Notizen

    public IEnumerable<NoteMeta> NotesIn(Guid notebookId) =>
        Library.Notes.Where(n => n.NotebookId == notebookId).OrderByDescending(n => n.Updated);

    public NoteMeta? Note(Guid id) => Library.Notes.FirstOrDefault(n => n.Id == id);

    public Notebook? NotebookOf(NoteMeta note) => Library.Notebooks.FirstOrDefault(n => n.Id == note.NotebookId);

    public NoteMeta CreateNote(Guid notebookId, string title = "Neue Notiz")
    {
        var notebook = Library.Notebooks.FirstOrDefault(n => n.Id == notebookId);
        var note = new NoteMeta
        {
            NotebookId = notebookId,
            Title = title,
            Paper = notebook?.DefaultPaper ?? "lined",
            PageCount = 1,
            Snippet = ""
        };
        Library.Notes.Add(note);
        Save();
        return note;
    }

    public void UpdateNote(Guid id, Action<NoteMeta> change, bool touch = true)
    {
        var note = Note(id);
        if (note is null) return;
        change(note);
        if (touch) note.Updated = AppleTime.Now;
        Save();
    }

    public void DeleteNote(Guid id)
    {
        DeleteNoteFiles(id);
        Library.Notes.RemoveAll(n => n.Id == id);
        foreach (var deck in Library.Decks.Where(d => d.NoteId == id)) deck.NoteId = null;
        foreach (var item in Library.Homework.Where(h => h.NoteId == id)) item.NoteId = null;
        Save();
    }

    private void DeleteNoteFiles(Guid id)
    {
        var directory = NoteDirectory(id);
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public NoteContent LoadContent(Guid id)
    {
        var path = ContentPath(id);
        if (!File.Exists(path)) return new NoteContent();
        try
        {
            return JsonSerializer.Deserialize<NoteContent>(File.ReadAllText(path), Json) ?? new NoteContent();
        }
        catch (JsonException)
        {
            return new NoteContent();
        }
    }

    /// <summary>Schreibt nur, wenn sich wirklich etwas geändert hat.</summary>
    /// <returns>Ob etwas geschrieben wurde.</returns>
    public bool SaveContent(Guid id, NoteContent content)
    {
        var text = JsonSerializer.Serialize(content, Json);
        var path = ContentPath(id);
        if (File.Exists(path) && File.ReadAllText(path) == text) return false;
        Directory.CreateDirectory(NoteDirectory(id));
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
        return true;
    }

    public InkDocument LoadInk(Guid id)
    {
        var path = StrokesPath(id);
        var document = InkDocument.Load(path);
        return document;
    }

    /// <summary>Wie <see cref="SaveContent"/>: nur schreiben, wenn sich die Striche geändert haben.</summary>
    public bool SaveInk(Guid id, InkDocument ink)
    {
        var path = StrokesPath(id);
        var text = ink.ToJson();
        if (File.Exists(path) && File.ReadAllText(path) == text) return false;
        Directory.CreateDirectory(NoteDirectory(id));
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
        return true;
    }

    /// <summary>Legt ein Bild in den Ordner der Notiz und gibt den Dateinamen zurück.</summary>
    public string SaveImage(Guid note, byte[] data, string extension, string prefix = "bg")
    {
        var name = $"{prefix}-{Guid.NewGuid().ToString("N")[..10]}.{extension.TrimStart('.')}";
        Directory.CreateDirectory(NoteDirectory(note));
        File.WriteAllBytes(ImagePath(note, name), data);
        return name;
    }

    /// <summary>Vorschau-Text, Titel und Zeitstempel der Notiz auffrischen.</summary>
    public void RefreshSnippet(Guid id, NoteContent content, bool hasInk)
    {
        var note = Note(id);
        if (note is null) return;
        var text = content.PlainText.Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (text.Contains("  ")) text = text.Replace("  ", " ");
        var snippet = text.Length switch
        {
            0 => hasInk ? "Handschrift" : content.Backgrounds.Count > 0 ? "Bilder" : "Leer",
            > 140 => text[..140],
            _ => text
        };
        note.Snippet = snippet;
        note.Updated = AppleTime.Now;
        Save();
    }

    // MARK: - Hausaufgaben

    public int OpenHomeworkCount => Library.Homework.Count(h => !h.Done);

    public static DateTime EndOfToday => DateTime.Today.AddDays(1);

    /// <summary>Vereinfachte Schreibweise zum Vergleichen: ohne Groß/klein, Akzente und Zeichen.</summary>
    public static string HomeworkKey(string text)
    {
        var folded = text.Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder();
        foreach (var character in folded)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character)
                == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    /// <summary>Zwei Aufgaben gelten als gleich, wenn sie sich die meisten Wörter teilen.</summary>
    public static bool SimilarHomework(string first, string second)
    {
        static HashSet<string> Words(string text)
        {
            var words = new HashSet<string>();
            var current = new System.Text.StringBuilder();
            foreach (var character in text.ToLowerInvariant() + " ")
            {
                if (char.IsLetterOrDigit(character))
                {
                    current.Append(character);
                    continue;
                }
                if (current.Length > 0) words.Add(current.ToString());
                current.Clear();
            }
            return words;
        }
        var a = Words(first);
        var b = Words(second);
        if (a.Count == 0 || b.Count == 0) return false;
        var shared = (double)a.Intersect(b).Count();
        return shared / Math.Min(a.Count, b.Count) >= 0.75;
    }

    /// <summary>
    /// Neue Hausaufgaben übernehmen, ohne Bekanntes doppelt einzutragen. Was aus einer Notiz schon
    /// einmal übernommen wurde, kommt nie wieder – auch nicht, wenn es erledigt oder gelöscht ist.
    /// </summary>
    public int AddHomework(IEnumerable<Homework> items)
    {
        var added = 0;
        foreach (var item in items)
        {
            item.Title = item.Title.Trim();
            if (item.Title.Length == 0) continue;
            var key = HomeworkKey(item.Title);
            if (item.NoteId is Guid noteId && Note(noteId) is { } note && note.KnownHomework.Contains(key)) continue;

            var already = Library.Homework.Any(existing =>
                existing.Subject == item.Subject
                && (HomeworkKey(existing.Title) == key || SimilarHomework(existing.Title, item.Title)));
            if (!already)
            {
                Library.Homework.Add(item);
                added++;
            }
            if (item.NoteId is Guid source) RememberHomework(key, source);
        }
        Save();
        return added;
    }

    private void RememberHomework(string key, Guid noteId)
    {
        var note = Note(noteId);
        if (note is null || note.KnownHomework.Contains(key)) return;
        note.KnownHomework.Add(key);
        if (note.KnownHomework.Count > 200) note.KnownHomework.RemoveRange(0, note.KnownHomework.Count - 200);
    }

    public void ToggleHomework(Guid id)
    {
        var item = Library.Homework.FirstOrDefault(h => h.Id == id);
        if (item is null) return;
        item.Done = !item.Done;
        if (item.Done && item.NoteId is Guid noteId) RememberHomework(HomeworkKey(item.Title), noteId);
        Save();
    }

    public void UpdateHomework(Guid id, Action<Homework> change)
    {
        var item = Library.Homework.FirstOrDefault(h => h.Id == id);
        if (item is null) return;
        change(item);
        Save();
    }

    public void DeleteHomework(Guid id)
    {
        var item = Library.Homework.FirstOrDefault(h => h.Id == id);
        if (item?.NoteId is Guid noteId) RememberHomework(HomeworkKey(item.Title), noteId);
        Library.Homework.RemoveAll(h => h.Id == id);
        Save();
    }

    public void ClearDoneHomework()
    {
        foreach (var item in Library.Homework.Where(h => h.Done))
        {
            if (item.NoteId is Guid noteId) RememberHomework(HomeworkKey(item.Title), noteId);
        }
        Library.Homework.RemoveAll(h => h.Done);
        Save();
    }

    public IEnumerable<Homework> OpenHomework() => Library.Homework.Where(h => !h.Done)
        .OrderBy(h => h.Due ?? double.MaxValue)
        .ThenByDescending(h => h.Created);

    // MARK: - Karteikarten (Leitner-Boxen)

    public int DueCardCount => Library.Decks.Sum(d => d.Cards.Count(c => c.DueAt <= EndOfToday));

    public IEnumerable<(Deck Deck, Flashcard Card)> DueCards(Guid? deckId = null) => Library.Decks
        .Where(d => deckId is null || d.Id == deckId)
        .SelectMany(d => d.Cards.Where(c => c.DueAt <= EndOfToday).Select(c => (d, c)));

    public void AddCards(IEnumerable<Flashcard> cards, Guid? noteId, string title)
    {
        var deck = noteId is null ? null : Library.Decks.FirstOrDefault(d => d.NoteId == noteId);
        if (deck is null)
        {
            deck = new Deck { Name = title, NoteId = noteId };
            Library.Decks.Add(deck);
        }
        deck.Cards.AddRange(cards);
        Save();
    }

    public void DeleteDeck(Guid id)
    {
        Library.Decks.RemoveAll(d => d.Id == id);
        Save();
    }

    /// <summary>Abstand bis zur nächsten Wiederholung je Box: 1, 2, 4, 8, 16, 32 Tage.</summary>
    public static int IntervalDays(int box) => new[] { 1, 2, 4, 8, 16, 32 }[Math.Clamp(box, 1, 6) - 1];

    public enum Rating { Again, Hard, Good, Easy }

    /// <summary>Leitner-System: Jede richtige Antwort schiebt die Karte eine Box weiter.</summary>
    public static void Review(Flashcard card, Rating rating, DateTime? now = null)
    {
        var time = now ?? DateTime.Now;
        switch (rating)
        {
            case Rating.Again:
                card.Box = 1;
                card.Due = AppleTime.FromDateTime(time);
                break;
            case Rating.Hard:
                card.Box = Math.Max(1, card.Box);
                card.Due = AppleTime.FromDateTime(time.AddDays(1));
                break;
            case Rating.Good:
                card.Box = Math.Min(card.Box + 1, 6);
                card.Due = AppleTime.FromDateTime(time.AddDays(IntervalDays(card.Box)));
                break;
            case Rating.Easy:
                card.Box = Math.Min(card.Box + 2, 6);
                card.Due = AppleTime.FromDateTime(time.AddDays(IntervalDays(card.Box)));
                break;
        }
    }

    // MARK: - Stundenplan

    public IEnumerable<Lesson> LessonsOn(int weekday) =>
        Library.Lessons.Where(l => l.Weekday == weekday).OrderBy(l => l.Start);

    public IEnumerable<DayLesson> EntriesOn(DateTime date)
    {
        var value = DayLesson.DateValue(date);
        return Library.WeekEntries.Where(e => e.Date == value).OrderBy(e => e.Start);
    }

    public void UpsertLesson(Lesson lesson)
    {
        var index = Library.Lessons.FindIndex(l => l.Id == lesson.Id);
        if (index >= 0) Library.Lessons[index] = lesson;
        else Library.Lessons.Add(lesson);
        Save();
    }

    public void DeleteLesson(Guid id)
    {
        Library.Lessons.RemoveAll(l => l.Id == id);
        Save();
    }

    /// <summary>Ersetzt den Plan durch den Import und ordnet die Stunden den passenden Fächern zu.</summary>
    public void ReplaceLessons(List<Lesson> lessons)
    {
        foreach (var lesson in lessons)
        {
            var match = Library.Notebooks.FirstOrDefault(n =>
                n.Name.Contains(lesson.Subject, StringComparison.OrdinalIgnoreCase)
                || lesson.Subject.Contains(n.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null && lesson.Subject.Length > 0) lesson.NotebookId = match.Id;
        }
        Library.Lessons = lessons;
        Save();
    }

    public void ReplaceWeekEntries(List<DayLesson> entries)
    {
        Library.WeekEntries = entries;
        Save();
    }

    /// <summary>Farbe eines Schulfachs, wenn es ein passendes Heft dazu gibt.</summary>
    public Notebook? NotebookForSubject(string subject) => Library.Notebooks.FirstOrDefault(n =>
        subject.Length > 0 && (n.Name.Contains(subject, StringComparison.OrdinalIgnoreCase)
                               || subject.Contains(n.Name, StringComparison.OrdinalIgnoreCase)));

    // MARK: - Erster Start

    private void Seed()
    {
        WasSeeded = true;
        var mathe = new Notebook { Name = "Mathe", ColorName = "blue", DefaultPaper = "grid" };
        var deutsch = new Notebook { Name = "Deutsch", ColorName = "red" };
        var englisch = new Notebook { Name = "Englisch", ColorName = "green" };
        Library = new Library { Notebooks = { mathe, deutsch, englisch } };

        var welcome = new NoteMeta
        {
            NotebookId = deutsch.Id,
            Title = "Willkommen bei Lernheft Studio",
            Paper = "lined",
            PageCount = 1,
            Snippet = "Klick irgendwo auf die Seite und schreib los"
        };
        Library.Notes.Add(welcome);

        var content = new NoteContent();
        content.TextBoxes.Add(new NoteTextBox
        {
            X = 96,
            Y = 34,
            Width = 640,
            Text = WelcomeText,
            Seed = new TextSeed { Text = WelcomeText }
        });
        SaveContent(welcome.Id, content);
        Save();
    }

    private const string WelcomeText =
        "Klick irgendwo auf die Seite und schreib los – genau wie in OneNote.\n" +
        "Ein Textfeld verschiebst du an der Leiste oben, die Breite änderst du am rechten Rand.\n" +
        "Fett, kursiv, Farben und Listen findest du in der Leiste über der Seite.\n" +
        "Zeichnen: Koppel dein iPad über „iPad verbinden“ unten links. Was du dort zeichnest, erscheint sofort hier.\n" +
        "Alte Notizen holst du unter Einstellungen → Alte Notizen übernehmen.";
}
