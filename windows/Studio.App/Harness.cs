using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lernheft.Studio.App.Sheets;

namespace Lernheft.Studio.App;

/// <summary>
/// Automatische Sichtprüfung: startet die App mit Beispieldaten in einem Wegwerf-Ordner und speichert
/// Bildschirmfotos aller Ansichten (hell und dunkel). Aufruf: <c>LernheftStudio.exe --shots &lt;Ordner&gt;</c>.
/// </summary>
public static class Harness
{
    private static string _folder = "";
    private static readonly List<string> Problems = new();

    public static void Run(Application app, string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
        var data = Path.Combine(Path.GetTempPath(), "LernheftStudioShots-" + Guid.NewGuid().ToString("N")[..8]);
        Services.Init(data);
        Services.Settings.Set(Keys.PadPort, "47690");
        Demo.Fill(Services.Store);
        Theme.Apply(Theme.Mode.Light, save: false);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await Shoot(app);
            }
            catch (Exception error)
            {
                Problems.Add("Abbruch: " + error);
            }
            File.WriteAllLines(Path.Combine(folder, "harness.log"), Problems.Count == 0 ? new[] { "ok" } : Problems);
            try { Services.Pad.Dispose(); }
            catch (Exception) { }
            app.Shutdown(Problems.Count == 0 ? 0 : 1);
        });
        app.DispatcherUnhandledException += (_, e) =>
        {
            Problems.Add("Unbehandelt: " + e.Exception);
            e.Handled = true;
        };
    }

    private static async Task Shoot(Application app)
    {
        var main = new MainWindow { Width = 1440, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        app.MainWindow = main;
        main.Show();
        foreach (var dark in new[] { false, true })
        {
            Theme.Apply(dark ? Theme.Mode.Dark : Theme.Mode.Light, save: false);
            var suffix = dark ? "-dark" : "";
            main.CloseNoteForShots();
            await Settle();
            Save(main, "01-home" + suffix);

            main.OpenNote(Demo.MathNote);
            await Settle(900);
            Save(main, "02-note-math" + suffix);

            main.OpenNote(Demo.GermanNote);
            await Settle(900);
            Save(main, "03-note-text" + suffix);
            main.FocusFirstBoxForShots();
            await Settle(500);
            Save(main, "04-note-editing" + suffix);

            main.ResizeForShots(900, 760);
            await Settle(700);
            Save(main, "05-note-narrow" + suffix);
            main.ResizeForShots(1440, 900);
            main.CloseNoteForShots();
            await Settle();

            await Sheet(main, new SettingsWindow(SettingsWindow.Section.Ai), "10-settings-ai" + suffix);
            if (!dark)
            {
                foreach (var section in Enum.GetValues<SettingsWindow.Section>().Skip(1))
                    await Sheet(main, new SettingsWindow(section), $"11-settings-{section.ToString().ToLowerInvariant()}");
            }
            await Sheet(main, new PairingWindow(), "20-pairing" + suffix);
            await Sheet(main, new HomeworkWindow(), "21-homework" + suffix);
            await Sheet(main, new FlashcardsWindow(), "22-flashcards" + suffix);
            await Sheet(main, new TimetableWindow(), "23-timetable" + suffix);
            await Sheet(main, new LegacyImportWindow(), "24-legacy" + suffix);
            await Sheet(main, new FunctionWindow(), "25-function" + suffix);
            await Sheet(main, new AiWindow(new AiContext("Quadratische Funktionen", "f(x) = x² − 2x − 3", new List<GeminiImage>(), false),
                AiWindow.Tab.Math, autoRun: false), "26-ai" + suffix);
            await Sheet(main, new NotebookDialog(null), "27-notebook" + suffix);
        }
        Services.Pad.StopPairing();
        main.Close();
    }

    private static async Task Sheet(Window owner, Window sheet, string name)
    {
        try
        {
            sheet.Owner = owner;
            sheet.Show();
            await Settle(500);
            Save(sheet, name);
            sheet.Close();
            await Settle(150);
        }
        catch (Exception error)
        {
            Problems.Add($"{name}: {error}");
        }
    }

    private static async Task Settle(int milliseconds = 400)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(milliseconds);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static void Save(Window window, string name)
    {
        try
        {
            window.UpdateLayout();
            if (window.Content is not FrameworkElement content) return;
            var dpi = VisualTreeHelper.GetDpi(window);
            var width = content.ActualWidth;
            var height = content.ActualHeight;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
                context.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, new Rect(0, 0, width, height));
            }
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(_folder, name + ".png"));
            encoder.Save(file);
        }
        catch (Exception error)
        {
            Problems.Add($"{name}: {error}");
        }
    }
}

/// <summary>Beispieldaten, die nach echtem Schulalltag aussehen.</summary>
public static class Demo
{
    public static Guid MathNote { get; private set; }
    public static Guid GermanNote { get; private set; }

    public static void Fill(LibraryStore store)
    {
        var library = store.Library;
        var mathe = library.Notebooks.First(n => n.Name == "Mathe");
        var deutsch = library.Notebooks.First(n => n.Name == "Deutsch");
        var englisch = library.Notebooks.First(n => n.Name == "Englisch");
        var bio = store.CreateNotebook("Biologie", "teal");
        store.CreateNotebook("Geschichte", "orange");

        // Mathe: Text, Funktionsgraph und Handschrift.
        var math = store.CreateNote(mathe.Id, "Quadratische Funktionen");
        store.UpdateNote(math.Id, n => n.Paper = "grid");
        var content = new NoteContent();
        const string mathText = "Scheitelpunktform\nf(x) = a(x − d)² + e\nDer Scheitelpunkt liegt bei S(d | e).";
        content.TextBoxes.Add(new NoteTextBox { X = 64, Y = 32, Width = 520, Text = mathText, Seed = new TextSeed { Text = mathText } });
        store.SaveContent(math.Id, content);
        var ink = new InkDocument();
        var graph = GraphBuilder.Build(MathExpression.Compile("x^2 - 2x - 3"),
            new GraphBuilder.Settings(-3, 5, -5, 6, 32, Color: "#2D4BE0"), 400, 520);
        ink.Strokes.AddRange(graph.Strokes);
        ink.Strokes.Add(Scribble(96, 360, 260, "#C62D2D"));
        ink.Strokes.Add(Underline(64, 136, 300));
        store.SaveInk(math.Id, ink);
        store.RefreshSnippet(math.Id, content, true);
        MathNote = math.Id;

        // Deutsch: mehrere Absätze Text.
        var german = store.CreateNote(deutsch.Id, "Erörterung – Aufbau");
        var textContent = new NoteContent();
        const string germanText = "Einleitung\nHinführung zum Thema, Fragestellung nennen.\n\nHauptteil\n" +
                                  "Argumente vom schwächsten zum stärksten ordnen. Jedes Argument: Behauptung, Begründung, Beispiel.\n\n" +
                                  "Schluss\nEigene Meinung, Ausblick.";
        textContent.TextBoxes.Add(new NoteTextBox { X = 96, Y = 32, Width = 600, Text = germanText, Seed = new TextSeed { Text = germanText } });
        store.SaveContent(german.Id, textContent);
        store.RefreshSnippet(german.Id, textContent, false);
        GermanNote = german.Id;

        foreach (var (notebook, title) in new[]
                 {
                     (englisch, "Simple Past vs. Present Perfect"), (englisch, "Vocabulary Unit 4"),
                     (bio, "Zellaufbau"), (bio, "Fotosynthese"), (mathe, "Binomische Formeln")
                 })
        {
            var note = store.CreateNote(notebook.Id, title);
            var box = new NoteContent();
            box.TextBoxes.Add(new NoteTextBox { X = 96, Y = 32, Width = 560, Text = title, Seed = new TextSeed { Text = title } });
            store.SaveContent(note.Id, box);
            store.RefreshSnippet(note.Id, box, false);
        }

        store.AddHomework(new[]
        {
            new Homework { Title = "S. 84 Nr. 3a–d", Subject = "Mathe", Due = AppleTime.FromDateTime(DateTime.Today.AddDays(1)) },
            new Homework { Title = "Erörterung Einleitung schreiben", Subject = "Deutsch", Due = AppleTime.FromDateTime(DateTime.Today.AddDays(3)) },
            new Homework { Title = "Vokabeln Unit 4 lernen", Subject = "Englisch", Done = true }
        });
        store.AddCards(new[]
        {
            new Flashcard { Front = "Scheitelpunktform", Back = "f(x) = a(x − d)² + e" },
            new Flashcard { Front = "Nullstellen von x² − 2x − 3", Back = "x = −1 und x = 3" },
            new Flashcard { Front = "Diskriminante", Back = "D = b² − 4ac" }
        }, math.Id, "Quadratische Funktionen");

        var lessons = new List<Lesson>();
        var subjects = new[] { "Mathe", "Deutsch", "Englisch", "Biologie", "Geschichte" };
        for (var day = 1; day <= 5; day++)
        {
            for (var hour = 0; hour < 5; hour++)
            {
                var start = 480 + hour * 50 + (hour >= 2 ? 20 : 0);
                lessons.Add(new Lesson
                {
                    Weekday = day, Start = start, End = start + 45, Subject = subjects[(day + hour) % subjects.Length],
                    Room = $"R{100 + hour * 3 + day}"
                });
            }
        }
        store.ReplaceLessons(lessons);
        store.Save();
    }

    private static InkStroke Scribble(double x, double y, double width, string color)
    {
        var stroke = new InkStroke { Color = color, Width = 2.6, Kind = "pen" };
        for (var i = 0; i <= 120; i++)
        {
            var t = i / 120.0;
            stroke.Add(x + t * width + Math.Sin(t * 38) * 7, y + Math.Cos(t * 38) * 11 - Math.Sin(t * 5) * 3, 0.5 + 0.3 * Math.Sin(t * 9));
        }
        return stroke;
    }

    private static InkStroke Underline(double x, double y, double width)
    {
        var stroke = new InkStroke { Color = "#F2B705", Width = 14, Kind = "marker" };
        for (var i = 0; i <= 20; i++) stroke.Add(x + width * i / 20.0, y + Math.Sin(i / 3.0), 0.5);
        return stroke;
    }
}
