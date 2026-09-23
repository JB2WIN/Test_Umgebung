using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Lernheft.Studio.App.Sheets;

/// <summary>Hausaufgaben: eintragen, abhaken, von der KI in den Notizen finden lassen.</summary>
public sealed class HomeworkWindow : SheetWindow
{
    private static readonly CultureInfo German = new("de-DE");
    private readonly StackPanel _lists = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Visibility = Visibility.Collapsed };
    private readonly TextBox _title;
    private readonly ComboBox _subject = new() { Width = 170 };
    private readonly ComboBox _due = new() { Width = 170 };

    public HomeworkWindow() : base("Hausaufgaben", 660, 780)
    {
        ShowInTaskbar = false;
        var add = new StackPanel();
        add.Children.Add(Ui.CardHeader("Neue Aufgabe", Ui.GlyphAdd));
        _title = Ui.Field("", "z. B. Buch S. 42 Nr. 3a–c");
        _title.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Add();
        };
        add.Children.Add(_title);
        _subject.Items.Add(new ComboBoxItem { Content = "Ohne Fach", Tag = "" });
        foreach (var notebook in Services.Store.Library.Notebooks)
        {
            _subject.Items.Add(new ComboBoxItem
            {
                Content = Ui.Row(8, Ui.Dot(Ui.NotebookBrush(notebook.ColorName), 9), new TextBlock { Text = notebook.Name }),
                Tag = notebook.Name
            });
        }
        _subject.SelectedIndex = 0;
        _due.Items.Add(new ComboBoxItem { Content = "Ohne Datum", Tag = null });
        for (var day = 1; day <= 14; day++)
        {
            var date = DateTime.Today.AddDays(day);
            var label = day switch
            {
                1 => "Morgen",
                2 => "Übermorgen",
                _ => date.ToString("ddd, d. MMM", German)
            };
            _due.Items.Add(new ComboBoxItem { Content = label, Tag = date });
        }
        _due.SelectedIndex = 0;
        var addButton = Ui.Button("Hinzufügen", (_, _) => Add(), "Primary", Ui.GlyphAdd);
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        _subject.Margin = new Thickness(0, 0, 8, 0);
        _due.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(_subject);
        row.Children.Add(_due);
        row.Children.Add(addButton);
        add.Children.Add(row);
        Body.Children.Add(Ui.Card(add));

        var ai = new StackPanel();
        ai.Children.Add(Ui.CardHeader("Von der KI finden lassen", Ui.GlyphSearch));
        ai.Children.Add(Ui.Text(ScanDescription(), 13, color: "Muted", wrap: true, margin: new Thickness(0, 0, 0, 10)));
        var scan = Ui.Button("In den Notizen suchen", null, icon: Ui.GlyphSearch);
        scan.HorizontalAlignment = HorizontalAlignment.Left;
        scan.Click += async (_, _) =>
        {
            scan.IsEnabled = false;
            _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _status.Text = "Lese deine Notizen …";
            _status.Visibility = Visibility.Visible;
            _status.Text = await ScanNotesAsync(progress => _status.Text = progress);
            scan.IsEnabled = true;
            Rebuild();
        };
        ai.Children.Add(scan);
        _status.Margin = new Thickness(0, 10, 0, 0);
        ai.Children.Add(_status);
        Body.Children.Add(Ui.Card(ai));
        Body.Children.Add(_lists);
        Rebuild();
        Loaded += (_, _) => _title.Focus();
    }

    private static string ScanDescription()
    {
        var watched = Services.Store.Library.Notebooks.Where(n => n.ScanHomework).Select(n => n.Name).ToList();
        return watched.Count == 0
            ? "Die KI liest deine fünf zuletzt bearbeiteten Notizen – Text und Handschrift – und trägt gefundene Aufgaben ein."
            : $"Die KI liest die neuesten Notizen in {string.Join(", ", watched)} und trägt gefundene Aufgaben ein.";
    }

    private void Add()
    {
        var title = _title.Text.Trim();
        if (title.Length == 0)
        {
            _title.Focus();
            return;
        }
        var due = (_due.SelectedItem as ComboBoxItem)?.Tag as DateTime?;
        Services.Store.AddHomework(new[]
        {
            new Homework
            {
                Title = title,
                Subject = (_subject.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
                Due = due is DateTime date ? AppleTime.FromDateTime(date) : null
            }
        });
        _title.Text = "";
        _due.SelectedIndex = 0;
        Rebuild();
        _title.Focus();
    }

    private async Task<string> ScanNotesAsync(Action<string> progress)
    {
        var client = Services.Gemini();
        if (client is null) return "Dafür brauchst du einen Gemini-Schlüssel (Einstellungen → KI).";
        var watched = Services.Store.Library.Notebooks.Where(n => n.ScanHomework).Select(n => n.Id).ToHashSet();
        var notes = Services.Store.Library.Notes
            .Where(n => watched.Count == 0 || watched.Contains(n.NotebookId))
            .OrderByDescending(n => n.Updated)
            .Take(watched.Count == 0 ? 5 : 8)
            .ToList();
        var before = Services.Store.Library.Homework.Count;
        var index = 0;
        foreach (var note in notes)
        {
            index++;
            progress($"Lese „{note.Title}“ ({index} von {notes.Count}) …");
            try
            {
                var content = Services.Store.LoadContent(note.Id);
                var ink = Services.Store.LoadInk(note.Id);
                var images = Editor.PageRenderer.AiImages(note, content, ink, Editor.TextEnvironment.FromSettings(note.PaperStyle), limit: 2);
                await AiTasks.FindHomeworkAsync(client, Services.Store, note, content.PlainText, images);
            }
            catch (Exception error)
            {
                return "Die KI hat nicht geantwortet: " + GeminiClient.Describe(error);
            }
        }
        var added = Services.Store.Library.Homework.Count - before;
        return added switch
        {
            0 => $"{Ui.Plural(notes.Count, "Notiz", "Notizen")} durchgesehen, nichts Neues gefunden.",
            1 => "1 neue Hausaufgabe eingetragen.",
            _ => $"{added} neue Hausaufgaben eingetragen."
        };
    }

    private void Rebuild()
    {
        _lists.Children.Clear();
        var open = Services.Store.OpenHomework().ToList();
        var done = Services.Store.Library.Homework.Where(h => h.Done).OrderByDescending(h => h.Created).ToList();

        var openPanel = new StackPanel();
        openPanel.Children.Add(Ui.CardHeader($"Offen ({open.Count})", Ui.GlyphChecklist));
        if (open.Count == 0) openPanel.Children.Add(Ui.Text("Nichts offen – gut gemacht!", 13.5, color: "Muted"));
        foreach (var item in open) openPanel.Children.Add(Row(item));
        _lists.Children.Add(Ui.Card(openPanel));

        if (done.Count == 0) return;
        var donePanel = new StackPanel();
        donePanel.Children.Add(Ui.CardHeader($"Erledigt ({done.Count})", Ui.GlyphCheck, "Erledigte entfernen", () =>
        {
            Services.Store.ClearDoneHomework();
            Rebuild();
        }));
        foreach (var item in done) donePanel.Children.Add(Row(item));
        _lists.Children.Add(Ui.Card(donePanel));
    }

    private UIElement Row(Homework item)
    {
        var check = new CheckBox { IsChecked = item.Done, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        check.Click += (_, _) =>
        {
            Services.Store.ToggleHomework(item.Id);
            Rebuild();
        };
        var text = new StackPanel();
        var title = Ui.Text(item.Title, 14, wrap: true, color: item.Done ? "Faint" : "Text");
        if (item.Done) title.TextDecorations = TextDecorations.Strikethrough;
        text.Children.Add(title);
        var details = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        if (item.Subject.Length > 0)
        {
            var notebook = Services.Store.NotebookForSubject(item.Subject);
            if (notebook is not null) details.Children.Add(new Border { Child = Ui.Dot(Ui.NotebookBrush(notebook.ColorName), 7), Margin = new Thickness(0, 0, 5, 0) });
            details.Children.Add(Ui.Text(item.Subject, 12, color: "Faint"));
        }
        if (item.DueAt is DateTime due)
        {
            if (details.Children.Count > 0) details.Children.Add(Ui.Text("  ·  ", 12, color: "Faint"));
            var color = item.Done ? "Faint" : due < LibraryStore.EndOfToday ? "Bad" : due < LibraryStore.EndOfToday.AddDays(1) ? "Warn" : "Faint";
            var label = due.Date == DateTime.Today ? "heute" : due.Date == DateTime.Today.AddDays(1) ? "morgen" : due.ToString("ddd, d. MMM", German);
            details.Children.Add(Ui.Text(label, 12, due < LibraryStore.EndOfToday && !item.Done ? FontWeights.SemiBold : FontWeights.Normal, color));
        }
        if (item.FromAI)
        {
            if (details.Children.Count > 0) details.Children.Add(Ui.Text("  ·  ", 12, color: "Faint"));
            details.Children.Add(Ui.Text("von der KI erkannt", 12, color: "Faint"));
        }
        if (details.Children.Count > 0) text.Children.Add(details);
        var delete = Ui.IconButton(Ui.GlyphDelete, "Löschen", (_, _) =>
        {
            Services.Store.DeleteHomework(item.Id);
            Rebuild();
        }, 12);
        delete.Width = delete.Height = 28;
        delete.VerticalAlignment = VerticalAlignment.Top;
        delete.Opacity = 0.6;
        var dock = new DockPanel { Margin = new Thickness(0, 6, 0, 6), Background = Brushes.Transparent };
        dock.MouseEnter += (_, _) => delete.Opacity = 1;
        dock.MouseLeave += (_, _) => delete.Opacity = 0.6;
        DockPanel.SetDock(check, Dock.Left);
        DockPanel.SetDock(delete, Dock.Right);
        dock.Children.Add(check);
        dock.Children.Add(delete);
        dock.Children.Add(text);
        return dock;
    }
}
