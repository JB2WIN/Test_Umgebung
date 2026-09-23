using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lernheft.Studio;

/// <summary>
/// Eine komplette Sicherung als eine Datei: Fächer, Notizen mit Textfeldern, Handschrift und Bildern,
/// Hausaufgaben, Karteikarten, Stundenplan und Einstellungen. Der API-Schlüssel kommt nur mit,
/// wenn es erlaubt ist – die Datei enthält ihn dann im Klartext.
///
/// Auch Sicherungen der alten iPad-App lassen sich einlesen (ohne Handschrift, weil die dort
/// nur im Apple-Format drinsteckt).
/// </summary>
public static class Backup
{
    public class NoteBundle
    {
        [JsonPropertyName("meta")] public NoteMeta Meta { get; set; } = new();
        [JsonPropertyName("content")] public NoteContent Content { get; set; } = new();
        [JsonPropertyName("ink")] public InkDocument? Ink { get; set; }
        [JsonPropertyName("backgrounds")] public Dictionary<string, byte[]> Images { get; set; } = new();

        /// <summary>Nur in Sicherungen der alten iPad-App: Handschrift im Apple-Format.</summary>
        [JsonPropertyName("drawing")] public byte[]? AppleDrawing { get; set; }
    }

    public class File
    {
        [JsonPropertyName("format")] public string Format { get; set; } = "LernheftStudio";
        [JsonPropertyName("version")] public int Version { get; set; } = 2;
        [JsonPropertyName("created")] public double Created { get; set; } = AppleTime.Now;
        [JsonPropertyName("notebooks")] public List<Notebook> Notebooks { get; set; } = new();
        [JsonPropertyName("notes")] public List<NoteBundle> Notes { get; set; } = new();
        [JsonPropertyName("decks")] public List<Deck> Decks { get; set; } = new();
        [JsonPropertyName("homework")] public List<Homework> Homework { get; set; } = new();
        [JsonPropertyName("lessons")] public List<Lesson> Lessons { get; set; } = new();
        [JsonPropertyName("settings")] public Dictionary<string, string> Settings { get; set; } = new();
        [JsonPropertyName("apiKey")] public string? ApiKey { get; set; }
    }

    /// <summary>Einstellungen, die mit in die Sicherung gehen (keine Geräte-Kopplungen, keine Kennungen).</summary>
    public static readonly string[] SettingKeys =
    {
        Keys.GeminiModel, Keys.KeyLabel, Keys.KeyLabelBackup, Keys.FallbackOnOverload,
        Keys.Appearance, Keys.HideListsWhileWriting, Keys.SeamlessImport, Keys.ImportTextFromPdf, Keys.ZoomLocked,
        Keys.TextFont, Keys.TextSize, Keys.ScriptFont, Keys.SpellCheck, Keys.SnapToLines,
        Keys.AutoHomework, Keys.BackupIncludesKey, Keys.BudgetEur, Keys.PriceInput, Keys.PriceOutput, Keys.UsdToEur,
        Keys.TimetableLink
    };

    public static void Write(string path, LibraryStore store, Settings settings, bool includeKey)
    {
        var file = new File
        {
            Notebooks = store.Library.Notebooks,
            Decks = store.Library.Decks,
            Homework = store.Library.Homework,
            Lessons = store.Library.Lessons
        };
        foreach (var meta in store.Library.Notes)
        {
            var content = store.LoadContent(meta.Id);
            var bundle = new NoteBundle { Meta = meta, Content = content };
            var ink = store.LoadInk(meta.Id);
            if (ink.Strokes.Count > 0) bundle.Ink = ink;
            foreach (var background in content.Backgrounds)
            {
                var image = store.ImagePath(meta.Id, background.File);
                if (System.IO.File.Exists(image)) bundle.Images[background.File] = System.IO.File.ReadAllBytes(image);
            }
            file.Notes.Add(bundle);
        }
        foreach (var key in SettingKeys)
        {
            if (settings.Has(key)) file.Settings[key] = settings.Get(key);
        }
        if (includeKey)
        {
            var main = settings.GetSecret(Keys.GeminiKey);
            if (main.Length > 0) file.ApiKey = main;
            var backup = settings.GetSecret(Keys.GeminiBackupKey);
            if (backup.Length > 0) file.Settings["apiKeyBackup"] = backup;
        }
        var temp = path + ".tmp";
        using (var stream = System.IO.File.Create(temp))
        {
            JsonSerializer.Serialize(stream, file, LibraryStore.Json);
        }
        System.IO.File.Move(temp, path, overwrite: true);
    }

    public record RestoreResult(int Notes, int WithoutInk);

    /// <param name="replace">Vorher alles löschen – sonst wird hinzugefügt.</param>
    public static RestoreResult Restore(string path, LibraryStore store, Settings settings, bool replace)
    {
        File file;
        using (var stream = System.IO.File.OpenRead(path))
        {
            file = JsonSerializer.Deserialize<File>(stream, LibraryStore.Json)
                   ?? throw new InvalidDataException("Die Datei ist leer.");
        }
        var library = store.Library;
        if (replace)
        {
            foreach (var note in library.Notes.ToList())
            {
                try { Directory.Delete(store.NoteDirectory(note.Id), true); }
                catch (DirectoryNotFoundException) { }
            }
            library.Notebooks.Clear();
            library.Notes.Clear();
            library.Decks.Clear();
            library.Homework.Clear();
        }

        foreach (var notebook in file.Notebooks.Where(n => library.Notebooks.All(existing => existing.Id != n.Id)))
        {
            library.Notebooks.Add(notebook);
        }

        var restored = 0;
        var withoutInk = 0;
        foreach (var bundle in file.Notes)
        {
            var meta = bundle.Meta;
            if (library.Notes.Any(n => n.Id == meta.Id))
            {
                if (replace) continue;
                meta.Id = Guid.NewGuid();
                meta.Title += " (Import)";
            }
            if (library.Notebooks.All(n => n.Id != meta.NotebookId))
            {
                var fallback = library.Notebooks.FirstOrDefault();
                if (fallback is null)
                {
                    fallback = new Notebook { Name = "Import", ColorName = "steel" };
                    library.Notebooks.Add(fallback);
                }
                meta.NotebookId = fallback.Id;
            }
            Directory.CreateDirectory(store.NoteDirectory(meta.Id));
            foreach (var (name, data) in bundle.Images)
            {
                var safe = Path.GetFileName(name);
                if (safe.Length == 0) continue;
                System.IO.File.WriteAllBytes(store.ImagePath(meta.Id, safe), data);
            }
            var content = bundle.Content;
            content.Backgrounds.RemoveAll(b => !System.IO.File.Exists(store.ImagePath(meta.Id, b.File)));
            foreach (var background in content.Backgrounds)
            {
                if (background.OriginalFile is { } original && !System.IO.File.Exists(store.ImagePath(meta.Id, original)))
                    background.OriginalFile = null;
            }
            var ink = bundle.Ink ?? new InkDocument();
            ink.EnsureIds();
            if (content.TypedText is not null || content.Blocks is not null)
                LegacyImport.ConvertLegacyText(content, ink, meta.Width);
            if (bundle.Ink is null && bundle.AppleDrawing is { Length: > 0 }) withoutInk++;
            store.SaveContent(meta.Id, content);
            if (ink.Strokes.Count > 0) store.SaveInk(meta.Id, ink);
            library.Notes.Add(meta);
            restored++;
        }

        foreach (var deck in file.Decks.Where(d => library.Decks.All(existing => existing.Id != d.Id))) library.Decks.Add(deck);
        foreach (var item in file.Homework.Where(h => library.Homework.All(existing => existing.Id != h.Id))) library.Homework.Add(item);
        if (library.Lessons.Count == 0 || replace)
        {
            if (file.Lessons.Count > 0) library.Lessons = file.Lessons;
        }

        foreach (var (key, value) in file.Settings)
        {
            if (key == "apiKeyBackup") settings.SetSecret(Keys.GeminiBackupKey, value);
            else if (SettingKeys.Contains(key)) settings.Set(key, value);
        }
        if (!string.IsNullOrWhiteSpace(file.ApiKey)) settings.SetSecret(Keys.GeminiKey, file.ApiKey);

        store.Save();
        return new RestoreResult(restored, withoutInk);
    }
}
