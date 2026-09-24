using System.Text.Json;
using Xunit;

namespace Lernheft.Studio.Tests;

public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "studio-test-" + Guid.NewGuid().ToString("N")[..8]);

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); }
        catch (IOException) { }
    }
}

public class MathTests
{
    [Theory]
    [InlineData("x^2", 3, 9)]
    [InlineData("x²-2x+1", 4, 9)]
    [InlineData("0,5x^3", 2, 4)]
    [InlineData("2(x+1)", 3, 8)]
    [InlineData("sin(x)", 0, 0)]
    [InlineData("sqrt(x)", 9, 3)]
    [InlineData("-x^2", 2, -4)]
    [InlineData("1/x", 4, 0.25)]
    [InlineData("3x", 5, 15)]
    [InlineData("2^x", 10, 1024)]
    [InlineData("2^-x", 1, 0.5)]
    [InlineData("f(x) = 2x+1", 2, 5)]
    [InlineData("2xsin(x)", 0, 0)]
    [InlineData("π", 0, Math.PI)]
    public void Evaluates(string term, double x, double expected)
    {
        var function = MathExpression.Compile(term);
        Assert.Equal(expected, function(x)!.Value, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x+")]
    [InlineData("foo(x)")]
    [InlineData("(x+1")]
    public void RejectsNonsense(string term)
    {
        Assert.False(MathExpression.TryCompile(term, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void SqrtOfNegativeIsGap() => Assert.Null(MathExpression.Compile("sqrt(x)")(-4));

    [Fact]
    public void GraphHasAxesLabelsAndCurve()
    {
        var result = GraphBuilder.Build(MathExpression.Compile("x^2"), new GraphBuilder.Settings(), 400, 400);
        Assert.Contains(result.Strokes, s => s.Kind == "pen");
        Assert.True(result.Labels.Count >= 8);
        Assert.All(result.Strokes, s => Assert.False(string.IsNullOrEmpty(s.Id)));
        // Der Scheitel von x² liegt im Nullpunkt.
        var curve = result.Strokes.Where(s => s.Kind == "pen").SelectMany(s => Enumerable.Range(0, s.Count).Select(s.PointAt));
        Assert.Contains(curve, p => Math.Abs(p.X - 400) < 1 && Math.Abs(p.Y - 400) < 1);
    }

    [Fact]
    public void GraphBreaksAtPole()
    {
        var result = GraphBuilder.Build(MathExpression.Compile("1/x"), new GraphBuilder.Settings(DrawAxes: false), 0, 0);
        Assert.True(result.Strokes.Count >= 2);
    }
}

public class InkTests
{
    [Fact]
    public void ApplyAddsRemovesAndReplaces()
    {
        var document = new InkDocument();
        var a = new InkStroke { Id = "a" };
        a.Add(1, 1, 0.5);
        var b = new InkStroke { Id = "b" };
        b.Add(2, 2, 0.5);
        Assert.True(document.Apply(new[] { a, b }, null));
        Assert.Equal(2, document.Strokes.Count);

        var a2 = new InkStroke { Id = "a" };
        a2.Add(5, 5, 0.5);
        document.Apply(new[] { a2 }, null);
        Assert.Equal(2, document.Strokes.Count);
        Assert.Equal(5, document.Strokes.First(s => s.Id == "a").PointAt(0).X);

        Assert.True(document.Apply(null, new[] { "b" }));
        Assert.Single(document.Strokes);
        Assert.False(document.Apply(null, new[] { "zzz" }));
    }

    [Fact]
    public void OldStrokesGetIdsOnLoad()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "strokes.json");
        File.WriteAllText(path, """{"v":1,"device":"iPad","strokes":[{"c":"#000000","w":2,"k":"pen","p":[1,2,0.5,3,4,0.5]},{"c":"#000000","w":2,"k":"pen","p":[]}]}""");
        var document = InkDocument.Load(path);
        Assert.Single(document.Strokes);
        Assert.False(string.IsNullOrEmpty(document.Strokes[0].Id));
    }

    [Fact]
    public void IdsAreUnique()
    {
        var ids = Enumerable.Range(0, 5000).Select(_ => InkIds.New()).ToHashSet();
        Assert.Equal(5000, ids.Count);
    }
}

public class StoreTests
{
    [Fact]
    public void SeedsWelcomeNote()
    {
        using var temp = new TempFolder();
        var store = new LibraryStore(temp.Path);
        Assert.True(store.WasSeeded);
        var note = Assert.Single(store.Library.Notes);
        var content = store.LoadContent(note.Id);
        Assert.Single(content.TextBoxes);
        Assert.Contains("OneNote", content.PlainText);
    }

    [Fact]
    public void UnchangedContentIsNotRewritten()
    {
        using var temp = new TempFolder();
        var store = new LibraryStore(temp.Path);
        var note = store.CreateNote(store.Library.Notebooks[0].Id);
        var content = new NoteContent { TextBoxes = { new NoteTextBox { Text = "Hallo", Xaml = "<x/>" } } };
        Assert.True(store.SaveContent(note.Id, content));
        Assert.False(store.SaveContent(note.Id, content));

        var ink = new InkDocument();
        var stroke = new InkStroke { Id = "s" };
        stroke.Add(1, 2, 0.5);
        ink.Strokes.Add(stroke);
        Assert.True(store.SaveInk(note.Id, ink));
        Assert.False(store.SaveInk(note.Id, ink));
    }

    [Fact]
    public void ReloadsWhatWasSaved()
    {
        using var temp = new TempFolder();
        var store = new LibraryStore(temp.Path);
        var notebook = store.CreateNotebook("Physik", "teal");
        store.CreateNote(notebook.Id, "Optik");
        var again = new LibraryStore(temp.Path);
        Assert.Contains(again.Library.Notes, n => n.Title == "Optik");
        Assert.Equal("grid", again.Library.Notebooks.First(n => n.Name == "Physik").DefaultPaper);
    }

    [Fact]
    public void BrokenLibraryFallsBackToPrevious()
    {
        using var temp = new TempFolder();
        var store = new LibraryStore(temp.Path);
        store.CreateNotebook("Bio", "green");
        _ = new LibraryStore(temp.Path); // legt .previous an
        File.WriteAllText(store.LibraryPath, "{kaputt");
        var recovered = new LibraryStore(temp.Path);
        Assert.Contains(recovered.Library.Notebooks, n => n.Name == "Bio");
    }

    [Fact]
    public void HomeworkIsNotDuplicated()
    {
        using var temp = new TempFolder();
        var store = new LibraryStore(temp.Path);
        var note = store.Library.Notes[0];
        Assert.Equal(1, store.AddHomework(new[] { new Homework { Title = "Buch S. 42 Nr. 3a-c", Subject = "Mathe", NoteId = note.Id } }));
        Assert.Equal(0, store.AddHomework(new[] { new Homework { Title = "Buch S.42 Nr.3a-c", Subject = "Mathe", NoteId = note.Id } }));
        var item = store.Library.Homework[0];
        store.DeleteHomework(item.Id);
        // Einmal aus dieser Notiz übernommen – kommt nicht wieder.
        Assert.Equal(0, store.AddHomework(new[] { new Homework { Title = "Buch S. 42 Nr. 3a-c", Subject = "Mathe", NoteId = note.Id } }));
        Assert.Equal(1, store.AddHomework(new[] { new Homework { Title = "Vokabeln Unit 4 lernen", Subject = "Englisch" } }));
    }

    [Fact]
    public void LeitnerBoxes()
    {
        var card = new Flashcard();
        var now = new DateTime(2026, 9, 1, 12, 0, 0);
        LibraryStore.Review(card, LibraryStore.Rating.Good, now);
        Assert.Equal(2, card.Box);
        Assert.Equal(now.AddDays(2).Date, card.DueAt.Date);
        LibraryStore.Review(card, LibraryStore.Rating.Easy, now);
        Assert.Equal(4, card.Box);
        Assert.Equal(now.AddDays(8).Date, card.DueAt.Date);
        LibraryStore.Review(card, LibraryStore.Rating.Again, now);
        Assert.Equal(1, card.Box);
        for (var i = 0; i < 10; i++) LibraryStore.Review(card, LibraryStore.Rating.Easy, now);
        Assert.Equal(6, card.Box);
    }

    [Fact]
    public void DeletingNotebookRemovesNotes()
    {
        using var temp = new TempFolder();
        var store = new LibraryStore(temp.Path);
        var notebook = store.CreateNotebook("Chemie", "orange");
        var note = store.CreateNote(notebook.Id);
        store.SaveContent(note.Id, new NoteContent { TextBoxes = { new NoteTextBox { Text = "x" } } });
        store.DeleteNotebook(notebook.Id);
        Assert.DoesNotContain(store.Library.Notes, n => n.Id == note.Id);
        Assert.False(Directory.Exists(store.NoteDirectory(note.Id)));
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        using var temp = new TempFolder();
        var settings = new Settings(Path.Combine(temp.Path, "settings.json"))
        {
            Protect = value => "enc:" + value,
            Unprotect = value => value.StartsWith("enc:") ? value[4..] : throw new InvalidDataException()
        };
        settings.SetSecret(Keys.GeminiKey, "  geheim ");
        settings.SetDouble(Keys.BudgetEur, 12.5);
        settings.SetBool(Keys.AutoHomework, true);
        var again = new Settings(Path.Combine(temp.Path, "settings.json")) { Unprotect = settings.Unprotect };
        Assert.Equal("geheim", again.GetSecret(Keys.GeminiKey));
        Assert.Equal(12.5, again.GetDouble(Keys.BudgetEur, 0));
        Assert.True(again.GetBool(Keys.AutoHomework, false));
        Assert.StartsWith("enc:", again.Get(Keys.GeminiKey));
    }
}

public class LegacyImportTests
{
    private static string MakeOldFolder(TempFolder temp, out Guid noteId, out Guid blockNoteId)
    {
        var root = Path.Combine(temp.Path, "alt");
        var notebook = new Notebook { Name = "Deutsch", ColorName = "red" };
        var mathe = new Notebook { Name = "Geschichte", ColorName = "brown" };
        var note = new NoteMeta { NotebookId = notebook.Id, Title = "Gedichtanalyse", PageCount = 2 };
        var wide = new NoteMeta { NotebookId = mathe.Id, Title = "Quer geschrieben", PageCount = 1 };
        noteId = note.Id;
        blockNoteId = wide.Id;
        var library = new Library
        {
            Notebooks = { notebook, mathe },
            Notes = { note, wide },
            Homework = { new Homework { Title = "Gedicht lernen", Subject = "Deutsch" } },
            Decks = { new Deck { Name = "Epochen", Cards = { new Flashcard { Front = "Barock", Back = "1600–1720" } } } },
            Lessons = { new Lesson { Subject = "Deutsch", Weekday = 1 } }
        };
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "library.json"), JsonSerializer.Serialize(library, LibraryStore.Json));

        var first = Path.Combine(root, "notes", note.Id.ToString().ToUpperInvariant());
        Directory.CreateDirectory(first);
        File.WriteAllText(Path.Combine(first, "content.json"),
            """{"typedText":"Getippt am Surface\nZweite Zeile","blocks":[],"backgrounds":[{"id":"7C9E6679-7425-40DE-944B-E07FC1F90AE7","page":0,"file":"bg-0-abc.jpg","x":0,"y":0,"width":400,"height":300}]}""");
        File.WriteAllBytes(Path.Combine(first, "bg-0-abc.jpg"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(first, "strokes.json"),
            """{"v":1,"device":"iPad","strokes":[{"c":"#1A1F2B","w":2.5,"k":"pen","p":[100,100,0.5,200,500,0.5]}]}""");
        File.WriteAllBytes(Path.Combine(first, "drawing.data"), new byte[] { 9, 9 });

        var second = Path.Combine(root, "notes", wide.Id.ToString().ToUpperInvariant());
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "content.json"),
            """{"typedText":"","blocks":[{"id":"5B1F1B7A-1111-2222-3333-444455556666","x":300,"y":200,"width":250,"fontSize":30,"colorHex":"#DC3B3B","text":"Schönschrift"}],"backgrounds":[]}""");
        File.WriteAllText(Path.Combine(second, "strokes.json"),
            """{"v":1,"device":"iPad","strokes":[{"c":"#1A1F2B","w":2.5,"k":"pen","p":[100,100,0.5,1150,300,0.5]}]}""");
        return root;
    }

    [Fact]
    public void ImportsNotesTextInkImagesAndLists()
    {
        using var temp = new TempFolder();
        var folder = MakeOldFolder(temp, out var noteId, out var wideId);
        var store = new LibraryStore(Path.Combine(temp.Path, "neu"));
        var report = LegacyImport.ImportFolder(folder, store);

        Assert.Equal(2, report.Notes);
        Assert.Equal(0, report.MissingInk);
        // Die Willkommensnotiz räumt der Umzug weg.
        Assert.Equal(2, store.Library.Notes.Count);
        // „Deutsch" gab es schon als Beispielfach: keine Dopplung.
        Assert.Single(store.Library.Notebooks, n => n.Name == "Deutsch");
        Assert.Contains(store.Library.Notebooks, n => n.Name == "Geschichte");
        Assert.Single(store.Library.Homework);
        Assert.Single(store.Library.Decks);
        Assert.Single(store.Library.Lessons);

        var content = store.LoadContent(noteId);
        var box = Assert.Single(content.TextBoxes);
        Assert.Equal("Getippt am Surface\nZweite Zeile", box.Text);
        Assert.True(box.Y > 500, "Text sitzt unter der Handschrift");
        Assert.Null(content.TypedText);
        Assert.True(File.Exists(store.ImagePath(noteId, "bg-0-abc.jpg")));
        Assert.False(File.Exists(Path.Combine(store.NoteDirectory(noteId), "drawing.data")));
        var ink = store.LoadInk(noteId);
        Assert.Single(ink.Strokes);
        Assert.False(string.IsNullOrEmpty(ink.Strokes[0].Id));

        var wide = store.Note(wideId)!;
        Assert.True(wide.PageWidth > 1150, "breite Notiz vom iPad-Querformat wird breiter");
        var block = Assert.Single(store.LoadContent(wideId).TextBoxes);
        Assert.True(block.Seed!.Script);
        Assert.Equal("#DC3B3B", block.Seed.Color);
        Assert.Equal(30, block.Seed.FontSize);
    }

    [Fact]
    public void SecondImportSkipsEverything()
    {
        using var temp = new TempFolder();
        var folder = MakeOldFolder(temp, out _, out _);
        var store = new LibraryStore(Path.Combine(temp.Path, "neu"));
        LegacyImport.ImportFolder(folder, store);
        var again = LegacyImport.ImportFolder(folder, store);
        Assert.Equal(0, again.Notes);
        Assert.Equal(2, again.Skipped);
        Assert.Equal(0, LegacyImport.CountNew(folder, store));
        Assert.Equal(2, store.Library.Notes.Count);
    }

    [Fact]
    public void ReportsMissingPortableInk()
    {
        using var temp = new TempFolder();
        var folder = MakeOldFolder(temp, out var noteId, out _);
        File.Delete(Path.Combine(folder, "notes", noteId.ToString().ToUpperInvariant(), "strokes.json"));
        var store = new LibraryStore(Path.Combine(temp.Path, "neu"));
        var report = LegacyImport.ImportFolder(folder, store);
        Assert.Equal(1, report.MissingInk);
        Assert.Contains("iPad", report.Summary);
    }

    [Fact]
    public void KeepsInkConvertedByThePad()
    {
        using var temp = new TempFolder();
        var folder = MakeOldFolder(temp, out var noteId, out _);
        // So schickt Lernheft Stift die aus PencilKit übersetzte Handschrift: Kennung und Breitenfaktoren.
        File.WriteAllText(Path.Combine(folder, "notes", noteId.ToString().ToUpperInvariant(), "strokes.json"),
            """{"v":2,"device":"iPad","strokes":[{"i":"a1b2c3","c":"#2140C8","w":3.1,"k":"pencil","wf":true,"p":[10,20,0.8,30,40,1.2]}]}""");
        var store = new LibraryStore(Path.Combine(temp.Path, "neu"));
        LegacyImport.ImportFolder(folder, store);
        var stroke = Assert.Single(store.LoadInk(noteId).Strokes);
        Assert.Equal("a1b2c3", stroke.Id);
        Assert.True(stroke.WidthFactors);
        Assert.Equal("pencil", stroke.Kind);
        Assert.Equal(1.2, stroke.PointAt(1).Pressure);
    }

    [Theory]
    [InlineData("library.json", "library.json")]
    [InlineData("note:0f8fad5bd9cb469fa16570867728950e:content", "notes/0F8FAD5B-D9CB-469F-A165-70867728950E/content.json")]
    [InlineData("note:0f8fad5bd9cb469fa16570867728950e:ink", "notes/0F8FAD5B-D9CB-469F-A165-70867728950E/strokes.json")]
    [InlineData("note:0f8fad5bd9cb469fa16570867728950e:img.bg-1.jpg", "notes/0F8FAD5B-D9CB-469F-A165-70867728950E/bg-1.jpg")]
    public void ServerIdsMapToOldLayout(string id, string expected)
    {
        var path = LegacyImport.TargetPath("/x", id)!;
        Assert.Equal(Path.Combine("/x", expected.Replace('/', Path.DirectorySeparatorChar)), path);
    }

    [Fact]
    public void RejectsPathTricks() => Assert.Null(LegacyImport.TargetPath("/x", "note:0f8fad5bd9cb469fa16570867728950e:img.."));
}

public class BackupTests
{
    [Fact]
    public void RoundTripAddsAsCopies()
    {
        using var temp = new TempFolder();
        var store = new LibraryStore(Path.Combine(temp.Path, "a"));
        var settings = new Settings(Path.Combine(temp.Path, "a", "settings.json"));
        settings.SetSecret(Keys.GeminiKey, "key-123");
        settings.Set(Keys.TextFont, "Georgia");
        var note = store.Library.Notes[0];
        var ink = new InkDocument();
        var stroke = new InkStroke { Id = "x1" };
        stroke.Add(10, 10, 0.5);
        ink.Strokes.Add(stroke);
        store.SaveInk(note.Id, ink);

        var file = Path.Combine(temp.Path, "backup.json");
        Backup.Write(file, store, settings, includeKey: true);

        var other = new LibraryStore(Path.Combine(temp.Path, "b"), seed: false);
        var otherSettings = new Settings(Path.Combine(temp.Path, "b", "settings.json"));
        var result = Backup.Restore(file, other, otherSettings, replace: false);
        Assert.Equal(1, result.Notes);
        Assert.Equal("key-123", otherSettings.GetSecret(Keys.GeminiKey));
        Assert.Equal("Georgia", otherSettings.Get(Keys.TextFont));
        Assert.Single(other.LoadInk(other.Library.Notes[0].Id).Strokes);

        // Nochmal hinzufügen: dieselbe Notiz kommt als Kopie.
        Backup.Restore(file, other, otherSettings, replace: false);
        Assert.Equal(2, other.Library.Notes.Count);
        Assert.Contains(other.Library.Notes, n => n.Title.EndsWith("(Import)"));

        Backup.Restore(file, other, otherSettings, replace: true);
        Assert.Single(other.Library.Notes);
    }

    [Fact]
    public void ReadsOldIpadBackup()
    {
        using var temp = new TempFolder();
        var notebook = Guid.NewGuid();
        var note = Guid.NewGuid();
        var json = $$$"""
            {"version":1,"created":700000000,"notebooks":[{"id":"{{{notebook}}}","name":"Bio","colorName":"green","defaultPaper":"lined","scanHomework":false}],
             "notes":[{"meta":{"id":"{{{note}}}","notebookID":"{{{notebook}}}","title":"Zelle","paper":"lined","created":700000000,"updated":700000000,"pageCount":2,"snippet":"x","knownHomework":[]},
                       "content":{"typedText":"Mitochondrien","blocks":[],"backgrounds":[]},"drawing":"AAEC","backgrounds":{}}],
             "decks":[],"homework":[],"settings":{"geminiModel":"gemini-2.5-flash"},"apiKey":"alt-key"}
            """;
        var file = Path.Combine(temp.Path, "Lernheft-Sicherung.json");
        File.WriteAllText(file, json);
        var store = new LibraryStore(Path.Combine(temp.Path, "s"), seed: false);
        var settings = new Settings(Path.Combine(temp.Path, "s", "settings.json"));
        var result = Backup.Restore(file, store, settings, replace: false);
        Assert.Equal(1, result.Notes);
        Assert.Equal(1, result.WithoutInk);
        Assert.Equal("Mitochondrien", store.LoadContent(note).TextBoxes.Single().Text);
        Assert.Equal("alt-key", settings.GetSecret(Keys.GeminiKey));
        Assert.Equal("gemini-2.5-flash", settings.Get(Keys.GeminiModel));
    }
}

public class TimetableTests
{
    [Fact]
    public void ParsesIcsIntoWeek()
    {
        var monday = DateTime.Today.AddDays(-(int)DateTime.Today.DayOfWeek + 1);
        string Stamp(DateTime d) => d.ToString("yyyyMMdd'T'HHmmss");
        var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART:" + Stamp(monday.AddHours(8)) + "\r\nDTEND:" + Stamp(monday.AddHours(8.75)) +
                  "\r\nSUMMARY:Mathematik (204)\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nDTSTART:" + Stamp(monday.AddDays(7).AddHours(8)) +
                  "\r\nDTEND:" + Stamp(monday.AddDays(7).AddHours(8.75)) + "\r\nSUMMARY:Mathematik (204)\r\nEND:VEVENT\r\n" +
                  "BEGIN:VEVENT\r\nDTSTART:" + Stamp(monday.AddDays(1).AddHours(9)) + "\r\nDTEND:" + Stamp(monday.AddDays(1).AddHours(9.75)) +
                  "\r\nSUMMARY:EN - Meier - 101\r\nLOCATION:\r\nEND:VEVENT\r\nEND:VCALENDAR";
        var lessons = IcsImporter.Lessons(IcsImporter.Parse(ics), monday);
        Assert.Equal(2, lessons.Count);
        Assert.Equal("Mathematik", lessons[0].Subject);
        Assert.Equal("204", lessons[0].Room);
        Assert.Equal(1, lessons[0].Weekday);
        Assert.Equal(480, lessons[0].Start);
        Assert.Equal("EN", lessons[1].Subject);
    }

    private const string UntisJson = """
        {"data":{"result":{"data":{"elements":[{"type":3,"id":7,"name":"M","longName":"Mathematik"},{"type":4,"id":9,"name":"R204"},{"type":2,"id":5,"name":"MEI"}],
         "elementPeriods":{"123":[
           {"date":20260921,"startTime":800,"endTime":845,"cellState":"STANDARD","elements":[{"type":3,"id":7},{"type":4,"id":9},{"type":2,"id":5}]},
           {"date":20260922,"startTime":945,"endTime":1030,"cellState":"CANCEL","elements":[{"type":3,"id":7}]}]}}}}}
        """;

    [Fact]
    public void ReadsWebUntisWeek()
    {
        var entries = WebUntisReader.DayLessons(UntisJson);
        Assert.Equal(2, entries.Count);
        Assert.Equal("Mathematik", entries[0].Subject);
        Assert.Equal("R204", entries[0].Room);
        Assert.Equal("MEI", entries[0].Teacher);
        Assert.True(entries[1].IsCancelled);
        Assert.Equal("entfällt", entries[1].StatusText);

        var lessons = WebUntisReader.Lessons(UntisJson);
        var lesson = Assert.Single(lessons);
        Assert.Equal(1, lesson.Weekday);
    }

    [Fact]
    public void FindsTargetInUrl()
    {
        var target = WebUntisReader.FindTarget("https://schule.webuntis.com/WebUntis/?school=x#/basic/timetable?entityId=4711&date=2026-09-21", "");
        Assert.NotNull(target);
        Assert.Equal("4711", target!.Id);
        Assert.Equal(5, target.Type);
        Assert.Contains("elementId=4711", WebUntisReader.WeeklyDataUrl(target));
        Assert.Equal("99", WebUntisReader.ElementIdFromPageConfig("""{"data":{"elements":[{"id":12},{"id":99,"isOwn":true}]}}"""));
    }

    [Fact]
    public void LoginPageIsNotAWeek() => Assert.Throws<WebUntisReader.ReadException>(() => WebUntisReader.DayLessons("<html>login</html>"));
}

public class SvgTests
{
    [Fact]
    public void ReadsPathsWithGroupStyleAndTransform()
    {
        using var temp = new TempFolder();
        var file = Path.Combine(temp.Path, "x.svg");
        File.WriteAllText(file, """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 50">
              <g stroke="#ff0000" stroke-width="2" fill="none" transform="translate(10,0)">
                <path d="M0 0 L10 10 l10 0 C 20 20, 30 20, 30 30"/>
                <line x1="0" y1="40" x2="50" y2="40"/>
              </g>
            </svg>
            """);
        var outcome = SvgInk.Load(file, 800);
        Assert.Equal(2, outcome.Strokes.Count);
        Assert.All(outcome.Strokes, s => Assert.Equal("#FF0000", s.Color));
        var first = outcome.Strokes[0].PointAt(0);
        // (0+10) * (800-48)/100 + 24
        Assert.Equal(10 * 7.52 + 24, first.X, 1);
        Assert.False(outcome.FilledShapesOnly);
    }

    [Fact]
    public void NumbersWithSignsAndExponents() =>
        Assert.Equal(new[] { 1.5, -2, 3e2, -0.5, 0.25 }, SvgInk.Numbers("1.5-2 3e2-.5.25"));
}

public class CloudDriveTests
{
    [Fact]
    public void FindsICloudFolderInProfile()
    {
        using var temp = new TempFolder();
        Assert.Null(CloudDrive.FindFolder(null, temp.Path));
        var folder = Path.Combine(temp.Path, "iCloudDrive");
        Directory.CreateDirectory(folder);
        Assert.Equal(folder, CloudDrive.FindFolder(null, temp.Path));
        var chosen = Path.Combine(temp.Path, "Eigene Wahl");
        Directory.CreateDirectory(chosen);
        Assert.Equal(chosen, CloudDrive.FindFolder(chosen, temp.Path));
        Assert.Equal(folder, CloudDrive.FindFolder(Path.Combine(temp.Path, "gibt es nicht"), temp.Path));
    }

    [Fact]
    public void ListsFoldersFirstAndOnlyInsertableFiles()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(Path.Combine(temp.Path, "Mathe"));
        Directory.CreateDirectory(Path.Combine(temp.Path, ".versteckt"));
        File.WriteAllText(Path.Combine(temp.Path, "Blatt.pdf"), "x");
        File.WriteAllText(Path.Combine(temp.Path, "foto.HEIC"), "x");
        File.WriteAllText(Path.Combine(temp.Path, "tabelle.xlsx"), "x");
        File.WriteAllText(Path.Combine(temp.Path, ".DS_Store"), "x");
        var entries = CloudDrive.List(temp.Path);
        Assert.Equal(new[] { "Mathe", "Blatt.pdf", "foto.HEIC" }, entries.Select(e => e.Name));
        Assert.Equal(CloudDrive.Kind.Folder, entries[0].Kind);
        Assert.Equal(CloudDrive.Kind.Pdf, entries[1].Kind);
        Assert.Equal(CloudDrive.Kind.Image, entries[2].Kind);
    }

    [Fact]
    public void RecentAndSearchLookIntoSubfolders()
    {
        using var temp = new TempFolder();
        var deep = Path.Combine(temp.Path, "Schule", "Bio");
        Directory.CreateDirectory(deep);
        var old = Path.Combine(temp.Path, "alt.png");
        var fresh = Path.Combine(deep, "Zellaufbau Arbeitsblatt.pdf");
        File.WriteAllText(old, "x");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-3));
        var recent = CloudDrive.Recent(temp.Path);
        Assert.Equal(fresh, recent[0].Path);
        Assert.Equal(2, recent.Count);
        var hits = CloudDrive.Search(temp.Path, "zell blatt");
        Assert.Equal(fresh, Assert.Single(hits).Path);
        Assert.Equal(Path.Combine("Schule", "Bio", "Zellaufbau Arbeitsblatt.pdf"), CloudDrive.Relative(temp.Path, fresh));
    }

    [Fact]
    public async Task CopiesFileForImport()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "Notiz.txt");
        File.WriteAllText(source, "Hallo");
        var copy = await CloudDrive.MakeLocalCopyAsync(source);
        Assert.Equal("Notiz.txt", Path.GetFileName(copy));
        Assert.Equal("Hallo", File.ReadAllText(copy));
    }
}
