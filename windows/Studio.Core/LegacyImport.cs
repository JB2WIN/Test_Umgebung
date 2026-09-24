using System.Text.Json;

namespace Lernheft.Studio;

/// <summary>
/// Holt die Notizen aus der alten Lernheft-App herüber – egal, ob sie auf diesem Surface liegen,
/// auf dem alten Sync-Server oder vom iPad geschickt werden. Alles landet zuerst in einem Ordner
/// im alten Aufbau (library.json + notes/&lt;ID&gt;/…) und wird von dort übernommen.
///
/// Getippter Text wird zu einem Textfeld, Schönschrift-Blöcke ebenso, Handschrift bleibt Handschrift,
/// eingescannte Seiten bleiben Bilder. Schon übernommene Notizen werden übersprungen – der Import
/// lässt sich also gefahrlos wiederholen.
/// </summary>
public static class LegacyImport
{
    public record Report(int Notes, int Skipped, int Notebooks, int Homework, int Cards, int Lessons,
        int MissingInk, List<string> Problems)
    {
        public string Summary
        {
            get
            {
                if (Notes == 0 && Skipped == 0) return "In der Quelle waren keine Notizen.";
                var parts = new List<string>
                {
                    Notes == 1 ? "1 Notiz übernommen" : $"{Notes} Notizen übernommen"
                };
                if (Skipped > 0) parts.Add($"{Skipped} waren schon da");
                if (Notebooks > 0) parts.Add($"{Notebooks} Fächer");
                if (Homework > 0) parts.Add($"{Homework} Hausaufgaben");
                if (Cards > 0) parts.Add($"{Cards} Karteikarten");
                if (Lessons > 0) parts.Add("Stundenplan");
                var text = string.Join(", ", parts) + ".";
                if (MissingInk > 0)
                {
                    text += $" Bei {MissingInk} Notizen fehlte die Handschrift im gemeinsamen Format – "
                            + "übertrage sie am besten direkt vom iPad (Lernheft Stift → Einstellungen → Alte Notizen übertragen).";
                }
                return text;
            }
        }
    }

    /// <summary>Wo die alte Windows-App ihre Daten abgelegt hat.</summary>
    public static string OldWindowsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lernheft");

    public static bool LooksLikeLegacyFolder(string folder) =>
        File.Exists(Path.Combine(folder, "library.json")) && Directory.Exists(Path.Combine(folder, "notes"));

    /// <summary>Wie viele Notizen im alten Ordner liegen (für die Frage beim ersten Start).</summary>
    public static int CountNotes(string folder)
    {
        try
        {
            var library = ReadLibrary(Path.Combine(folder, "library.json"));
            return library?.Notes.Count ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Wie viele der alten Notizen noch nicht übernommen sind.</summary>
    public static int CountNew(string folder, LibraryStore store)
    {
        var library = ReadLibrary(Path.Combine(folder, "library.json"));
        if (library is null) return 0;
        var known = store.Library.Notes.Select(n => n.Id).ToHashSet();
        return library.Notes.Count(n => !known.Contains(n.Id));
    }

    private static Library? ReadLibrary(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<Library>(File.ReadAllText(path), LibraryStore.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Report ImportFolder(string folder, LibraryStore store, IProgress<string>? progress = null)
    {
        var problems = new List<string>();
        var old = ReadLibrary(Path.Combine(folder, "library.json"))
                  ?? throw new InvalidDataException("In diesem Ordner liegt keine lesbare library.json der alten Lernheft-App.");
        var library = store.Library;

        // Die Willkommensnotiz eines frisch eingerichteten Studios stört nach dem Umzug nur.
        var onlyWelcome = library.Notes.Count == 1 && library.Notes[0].Title.StartsWith("Willkommen bei Lernheft Studio");

        var notebooks = 0;
        foreach (var notebook in old.Notebooks)
        {
            if (library.Notebooks.Any(n => n.Id == notebook.Id)) continue;
            // Gleicher Name wie ein vorhandenes (Beispiel-)Fach: die Notizen dorthin statt doppelt.
            var twin = library.Notebooks.FirstOrDefault(n => string.Equals(n.Name, notebook.Name, StringComparison.OrdinalIgnoreCase));
            if (twin is not null)
            {
                foreach (var note in old.Notes.Where(n => n.NotebookId == notebook.Id)) note.NotebookId = twin.Id;
                twin.ColorName = notebook.ColorName;
                twin.ScanHomework |= notebook.ScanHomework;
                continue;
            }
            library.Notebooks.Add(notebook);
            notebooks++;
        }

        var imported = 0;
        var skipped = 0;
        var missingInk = 0;
        var index = 0;
        foreach (var note in old.Notes)
        {
            index++;
            progress?.Report($"Notiz {index} von {old.Notes.Count}: {note.Title}");
            if (library.Notes.Any(n => n.Id == note.Id))
            {
                skipped++;
                continue;
            }
            try
            {
                if (library.Notebooks.All(n => n.Id != note.NotebookId))
                {
                    var fallback = library.Notebooks.FirstOrDefault(n => n.Name == "Importiert")
                                   ?? AddNotebook(library, "Importiert", "steel");
                    note.NotebookId = fallback.Id;
                }
                ImportNote(folder, note, store, out var nativeInkOnly);
                if (nativeInkOnly) missingInk++;
                library.Notes.Add(note);
                imported++;
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            {
                problems.Add($"„{note.Title}“: {error.Message}");
            }
        }

        var homework = 0;
        foreach (var item in old.Homework)
        {
            if (library.Homework.Any(h => h.Id == item.Id)) continue;
            library.Homework.Add(item);
            homework++;
        }

        var cards = 0;
        foreach (var deck in old.Decks)
        {
            var existing = library.Decks.FirstOrDefault(d => d.Id == deck.Id);
            if (existing is null)
            {
                library.Decks.Add(deck);
                cards += deck.Cards.Count;
                continue;
            }
            foreach (var card in deck.Cards.Where(card => existing.Cards.All(c => c.Id != card.Id)))
            {
                existing.Cards.Add(card);
                cards++;
            }
        }

        var lessons = 0;
        if (library.Lessons.Count == 0 && old.Lessons.Count > 0)
        {
            library.Lessons = old.Lessons;
            lessons = old.Lessons.Count;
        }
        if (library.WeekEntries.Count == 0 && old.WeekEntries.Count > 0) library.WeekEntries = old.WeekEntries;

        if (onlyWelcome && imported > 0)
        {
            var welcome = library.Notes[0];
            library.Notes.Remove(welcome);
            try { Directory.Delete(store.NoteDirectory(welcome.Id), true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        store.Save();
        return new Report(imported, skipped, notebooks, homework, cards, lessons, missingInk, problems);
    }

    private static Notebook AddNotebook(Library library, string name, string color)
    {
        var notebook = new Notebook { Name = name, ColorName = color };
        library.Notebooks.Add(notebook);
        return notebook;
    }

    /// <returns>Ob Handschrift im gemeinsamen Format dabei war.</returns>
    private static bool ImportNote(string folder, NoteMeta note, LibraryStore store, out bool nativeInkOnly)
    {
        var source = FindNoteFolder(folder, note.Id);
        var target = store.NoteDirectory(note.Id);
        Directory.CreateDirectory(target);
        nativeInkOnly = false;

        var content = new NoteContent();
        var hasPortableInk = false;
        InkDocument ink = new();
        if (source is not null)
        {
            var contentPath = Path.Combine(source, "content.json");
            if (File.Exists(contentPath))
            {
                content = JsonSerializer.Deserialize<NoteContent>(File.ReadAllText(contentPath), LibraryStore.Json) ?? new NoteContent();
            }
            var strokesPath = Path.Combine(source, "strokes.json");
            if (File.Exists(strokesPath))
            {
                ink = InkDocument.Load(strokesPath);
                hasPortableInk = ink.Strokes.Count > 0;
            }
            nativeInkOnly = !hasPortableInk && File.Exists(Path.Combine(source, "drawing.data"));

            // Bilder und alle übrigen Dateien mitnehmen (ohne PencilKit-Daten).
            foreach (var file in Directory.GetFiles(source))
            {
                var name = Path.GetFileName(file);
                if (name is "content.json" or "strokes.json" or "drawing.data") continue;
                File.Copy(file, Path.Combine(target, name), overwrite: true);
            }
        }

        // Breite der Seite: Notizen aus dem iPad-Querformat reichen über 800 hinaus.
        var right = Math.Max(ink.Right, content.Backgrounds.Select(b => b.X + b.Width).DefaultIfEmpty(0).Max());
        note.PageWidth = right > Paper.PageWidth + 4 ? Math.Ceiling((right + 24) / Paper.Spacing) * Paper.Spacing : Paper.PageWidth;

        ConvertLegacyText(content, ink, note.PageWidth);

        var bottom = Math.Max(ink.Bottom, content.Backgrounds.Select(b => b.Y + b.Height).DefaultIfEmpty(0).Max());
        bottom = Math.Max(bottom, content.TextBoxes.Select(b => b.Y + EstimateHeight(b)).DefaultIfEmpty(0).Max());
        note.PageCount = Math.Max(Math.Max(1, note.PageCount), (int)Math.Ceiling((bottom + 1) / Paper.PageHeight));
        note.Snippet = Snippet(content, ink.Strokes.Count > 0, note.Snippet);

        store.SaveContent(note.Id, content);
        if (ink.Strokes.Count > 0) store.SaveInk(note.Id, ink);
        return hasPortableInk;
    }

    private static string? FindNoteFolder(string folder, Guid id)
    {
        var notes = Path.Combine(folder, "notes");
        foreach (var name in new[] { id.ToString().ToUpperInvariant(), id.ToString(), id.ToString("N") })
        {
            var path = Path.Combine(notes, name);
            if (Directory.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>
    /// Macht aus dem getippten Text und den Schönschrift-Blöcken der alten App Textfelder –
    /// an derselben Stelle, an der sie vorher auf der Seite standen.
    /// </summary>
    public static void ConvertLegacyText(NoteContent content, InkDocument ink, double pageWidth)
    {
        var typed = (content.TypedText ?? "").Trim('\n', '\r', ' ');
        if (typed.Length > 0)
        {
            var x = content.TypedX is double tx && tx > 8 ? tx : 80;
            double y;
            if (content.TypedY is double ty) y = ty;
            else
            {
                var used = Math.Max(ink.Bottom, content.Backgrounds.Select(b => b.Y + b.Height).DefaultIfEmpty(0).Max());
                y = used <= 1 ? Paper.Spacing : used + Paper.Spacing;
            }
            y = SnapTop(y);
            content.TextBoxes.Add(new NoteTextBox
            {
                X = x,
                Y = y,
                Width = Math.Max(240, pageWidth - x - 48),
                Text = typed,
                Seed = new TextSeed { Text = typed }
            });
        }
        foreach (var block in content.Blocks ?? new List<LegacyTextBlock>())
        {
            if (string.IsNullOrWhiteSpace(block.Text)) continue;
            content.TextBoxes.Add(new NoteTextBox
            {
                Id = block.Id,
                X = block.X,
                Y = block.Y,
                Width = Math.Max(60, block.Width),
                Text = block.Text,
                Seed = new TextSeed
                {
                    Text = block.Text,
                    FontSize = block.FontSize,
                    Color = block.ColorHex,
                    Script = true
                }
            });
        }
        content.TypedText = null;
        content.Blocks = null;
        content.TypedX = null;
        content.TypedY = null;
    }

    /// <summary>Oberkante eines Textfelds so wählen, dass die erste Zeile auf einer Linie sitzt.</summary>
    public static double SnapTop(double y) => Math.Max(0, Math.Floor(y / Paper.Spacing) * Paper.Spacing + 2);

    private static double EstimateHeight(NoteTextBox box)
    {
        var lines = 0;
        var perLine = Math.Max(10, (int)(box.Width / 9));
        foreach (var line in box.Text.Split('\n')) lines += Math.Max(1, (int)Math.Ceiling(line.Length / (double)perLine));
        return lines * Paper.Spacing;
    }

    private static string Snippet(NoteContent content, bool hasInk, string old)
    {
        var text = content.PlainText.Replace('\n', ' ').Trim();
        if (text.Length > 140) text = text[..140];
        if (text.Length > 0) return text;
        if (old.Length > 0 && old != "Leer") return old;
        return hasInk ? "Handschrift" : content.Backgrounds.Count > 0 ? "Bilder" : "Leer";
    }

    // MARK: - Vom alten Sync-Server

    /// <summary>Lädt alles vom alten Lernheft-Server in einen Ordner im alten Aufbau.</summary>
    public static async Task<string> DownloadFromServerAsync(string address, string token, IProgress<string>? progress,
        CancellationToken cancel = default)
    {
        var server = address.Trim().TrimEnd('/');
        if (!server.StartsWith("http", StringComparison.OrdinalIgnoreCase)) server = "http://" + server;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

        HttpRequestMessage Request(string path)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, server + path);
            request.Headers.Add("X-Lernheft-Token", token.Trim());
            request.Headers.Add("ngrok-skip-browser-warning", "true");
            return request;
        }

        progress?.Report("Frage den Server, was er hat …");
        using var manifestResponse = await http.SendAsync(Request("/manifest"), cancel);
        if ((int)manifestResponse.StatusCode == 401) throw new InvalidDataException("Der Zugangsschlüssel passt nicht.");
        manifestResponse.EnsureSuccessStatusCode();
        using var manifest = JsonDocument.Parse(await manifestResponse.Content.ReadAsStringAsync(cancel));

        var folder = Path.Combine(Path.GetTempPath(), "LernheftStudio-Import-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var entries = manifest.RootElement.EnumerateArray()
            .Where(entry => !(entry.TryGetProperty("deleted", out var deleted) && deleted.GetBoolean()))
            .Select(entry => entry.GetProperty("id").GetString() ?? "")
            .Where(id => id.Length > 0)
            .ToList();

        var done = 0;
        foreach (var id in entries)
        {
            cancel.ThrowIfCancellationRequested();
            done++;
            var target = TargetPath(folder, id);
            if (target is null) continue;
            progress?.Report($"Lade {done} von {entries.Count} …");
            using var response = await http.SendAsync(Request("/item/" + Uri.EscapeDataString(id)), cancel);
            if (!response.IsSuccessStatusCode) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, await response.Content.ReadAsByteArrayAsync(cancel), cancel);
        }
        return folder;
    }

    /// <summary>Wohin eine Datei vom Server oder vom iPad im alten Aufbau gehört.</summary>
    public static string? TargetPath(string folder, string id)
    {
        if (id == "library.json") return Path.Combine(folder, "library.json");
        var parts = id.Split(':');
        if (parts.Length < 3 || parts[0] != "note" || !Guid.TryParse(parts[1], out var noteId)) return null;
        var directory = Path.Combine(folder, "notes", noteId.ToString().ToUpperInvariant());
        var kind = string.Join(":", parts.Skip(2));
        if (kind == "content") return Path.Combine(directory, "content.json");
        if (kind == "ink") return Path.Combine(directory, "strokes.json");
        if (kind.StartsWith("img."))
        {
            var name = Path.GetFileName(kind[4..]);
            return name is "" or "." or ".." || name.Contains("..") ? null : Path.Combine(directory, name);
        }
        return null;
    }
}
