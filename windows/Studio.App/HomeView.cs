using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Lernheft.Studio.App;

/// <summary>Die Startseite: Begrüßung, Schnellzugriff, heutiger Stundenplan, Aufgaben, letzte Notizen, Fächer.</summary>
public static class Home
{
    private static readonly CultureInfo German = new("de-DE");

    public record Actions(
        Action<Guid> OpenNote,
        Action<Guid?> NewNote,
        Action Homework,
        Action Cards,
        Action Timetable,
        Action Pair);

    public static void Build(Panel host, double width, Actions actions)
    {
        host.Children.Clear();
        var store = Services.Store;
        var wide = width > 980;

        var greeting = DateTime.Now.Hour switch
        {
            >= 5 and < 11 => "Guten Morgen",
            >= 11 and < 14 => "Mahlzeit",
            >= 14 and < 18 => "Guten Nachmittag",
            >= 18 and < 23 => "Guten Abend",
            _ => "Noch wach?"
        };
        host.Children.Add(Ui.Text(DateTime.Now.ToString("dddd, d. MMMM", German), 13.5, color: "Muted"));
        host.Children.Add(new TextBlock
        {
            Text = greeting,
            FontFamily = Ui.DisplayFont,
            FontSize = 34,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 4)
        });
        var open = store.OpenHomeworkCount;
        var due = store.DueCardCount;
        host.Children.Add(Ui.Text((open, due) switch
        {
            (0, 0) => "Nichts offen. Guter Zeitpunkt für neue Notizen.",
            (0, _) => $"{Ui.Plural(due, "Karteikarte wartet", "Karteikarten warten")} auf dich.",
            (_, 0) => open == 1 ? "1 Hausaufgabe steht noch offen." : $"{open} Hausaufgaben stehen noch offen.",
            _ => $"{Ui.Plural(open, "Hausaufgabe", "Hausaufgaben")} offen, {Ui.Plural(due, "Karteikarte", "Karteikarten")} fällig."
        }, 15, color: "Muted", wrap: true, margin: new Thickness(0, 0, 0, 22)));

        // Schnellzugriff
        var tiles = new UniformGrid { Columns = wide ? 4 : 2, Margin = new Thickness(-6, 0, -6, 10) };
        var last = store.Library.Notes.OrderByDescending(n => n.Updated).FirstOrDefault();
        tiles.Children.Add(Tile(Ui.GlyphEdit, "Neue Notiz", "", "Accent", () => actions.NewNote(null)));
        tiles.Children.Add(Tile(Ui.GlyphChecklist, "Hausaufgaben", open > 0 ? open.ToString() : "", "Warn", actions.Homework));
        tiles.Children.Add(Tile(Ui.GlyphCards, "Karteikarten", due > 0 ? due.ToString() : "", "Good", actions.Cards));
        if (last is not null) tiles.Children.Add(Tile(Ui.GlyphPage, "Weiter schreiben", "", "Accent", () => actions.OpenNote(last.Id), last.Title));
        else tiles.Children.Add(Tile(Ui.GlyphTablet, "iPad verbinden", "", "Accent", actions.Pair));
        host.Children.Add(tiles);

        // Zeichnen mit dem iPad – nur solange noch keins gekoppelt ist.
        if (!Services.Bridge.Connected && Services.Pad.PairedDevices.Count == 0)
        {
            var pad = new DockPanel();
            var button = Ui.Button("iPad verbinden", (_, _) => actions.Pair(), "Primary", Ui.GlyphQr);
            button.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(button, Dock.Right);
            pad.Children.Add(button);
            var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            text.Children.Add(Ui.Text("Zeichnen mit dem iPad", 16, FontWeights.SemiBold));
            text.Children.Add(Ui.Text("Koppel dein iPad per QR-Code. Was du dort mit dem Pencil zeichnest, erscheint sofort in der offenen Notiz.",
                13.5, color: "Muted", wrap: true, margin: new Thickness(0, 4, 0, 0)));
            pad.Children.Add(text);
            var icon = Ui.Icon(Ui.GlyphTablet, 26, "Accent");
            icon.Margin = new Thickness(0, 0, 16, 0);
            DockPanel.SetDock(icon, Dock.Left);
            pad.Children.Insert(0, icon);
            host.Children.Add(Ui.Card(pad));
        }

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (wide)
        {
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var left = new StackPanel();
        var right = wide ? new StackPanel() : left;
        Grid.SetColumn(left, 0);
        columns.Children.Add(left);
        if (wide)
        {
            Grid.SetColumn(right, 2);
            columns.Children.Add(right);
        }
        host.Children.Add(columns);

        left.Children.Add(TimetableCard(actions));
        var homework = store.OpenHomework().Take(5).ToList();
        if (homework.Count > 0) right.Children.Add(HomeworkCard(host, width, actions, homework));

        var recent = store.Library.Notes.OrderByDescending(n => n.Updated).Take(wide ? 6 : 4).ToList();
        if (recent.Count > 0)
        {
            var panel = new StackPanel();
            panel.Children.Add(Ui.CardHeader("Zuletzt bearbeitet", Ui.GlyphPage));
            var grid = new UniformGrid { Columns = wide ? 3 : 2, Margin = new Thickness(-4, 0, -4, 0) };
            foreach (var note in recent)
            {
                var notebook = store.NotebookOf(note);
                var content = new StackPanel();
                content.Children.Add(Ui.Row(7, Ui.Dot(Ui.NotebookBrush(notebook?.ColorName), 8), Ui.Text(notebook?.Name ?? "", 11.5, color: "Muted")));
                content.Children.Add(Ui.Text(note.Title, 13.5, FontWeights.SemiBold, margin: new Thickness(0, 6, 0, 2)));
                content.Children.Add(Ui.Text(Relative(note.UpdatedAt), 11.5, color: "Faint"));
                var button = new Button
                {
                    Style = Ui.Style("Soft"),
                    Content = content,
                    Margin = new Thickness(4),
                    Padding = new Thickness(12, 10, 12, 10),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                var id = note.Id;
                button.Click += (_, _) => actions.OpenNote(id);
                grid.Children.Add(button);
            }
            panel.Children.Add(grid);
            host.Children.Add(Ui.Card(panel));
        }

        var subjects = new StackPanel();
        subjects.Children.Add(Ui.CardHeader("Deine Fächer", Ui.GlyphFolder));
        var subjectGrid = new UniformGrid { Columns = wide ? 4 : 2, Margin = new Thickness(-4, 0, -4, 0) };
        foreach (var notebook in store.Library.Notebooks)
        {
            var content = new StackPanel();
            content.Children.Add(Ui.Row(9, Ui.Dot(Ui.NotebookBrush(notebook.ColorName), 10), Ui.Text(notebook.Name, 13.5, FontWeights.SemiBold)));
            content.Children.Add(Ui.Text(Ui.Plural(store.NoteCount(notebook.Id), "Notiz", "Notizen"), 11.5, color: "Faint", margin: new Thickness(19, 2, 0, 0)));
            var button = new Button
            {
                Style = Ui.Style("Soft"),
                Content = content,
                Margin = new Thickness(4),
                Padding = new Thickness(12, 10, 12, 10),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ToolTip = "Neue Notiz in " + notebook.Name
            };
            var id = notebook.Id;
            button.Click += (_, _) => actions.NewNote(id);
            subjectGrid.Children.Add(button);
        }
        subjects.Children.Add(subjectGrid);
        subjects.Children.Add(Ui.Hint("Ein Fach anklicken legt dort eine neue Notiz an."));
        host.Children.Add(Ui.Card(subjects));
    }

    public static string Relative(DateTime time)
    {
        var today = DateTime.Today;
        if (time.Date == today) return "heute, " + time.ToString("HH:mm");
        if (time.Date == today.AddDays(-1)) return "gestern, " + time.ToString("HH:mm");
        if (time > today.AddDays(-6)) return time.ToString("dddd, HH:mm", German);
        return time.ToString("d. MMM yyyy", German);
    }

    private static Button Tile(string glyph, string title, string badge, string color, Action action, string? detail = null)
    {
        var panel = new StackPanel();
        var top = new DockPanel();
        if (badge.Length > 0)
        {
            var chip = Ui.Badge(badge, color, color + "Soft");
            if (color == "Accent") chip.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            DockPanel.SetDock(chip, Dock.Right);
            top.Children.Add(chip);
        }
        var icon = Ui.Icon(glyph, 20, color);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        top.Children.Add(icon);
        panel.Children.Add(top);
        panel.Children.Add(Ui.Text(title, 14.5, FontWeights.SemiBold, margin: new Thickness(0, 14, 0, 0)));
        if (detail is not null) panel.Children.Add(Ui.Text(detail, 12, color: "Faint", margin: new Thickness(0, 2, 0, 0)));
        var button = new Button
        {
            Style = Ui.Style("Soft"),
            Content = panel,
            MinHeight = 98,
            Margin = new Thickness(6),
            Padding = new Thickness(16, 14, 16, 14),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static UIElement TimetableCard(Actions actions)
    {
        var store = Services.Store;
        var panel = new StackPanel();
        panel.Children.Add(Ui.CardHeader("Heute im Stundenplan", Ui.GlyphCalendar, "Alle", actions.Timetable));
        var minutes = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
        var entries = store.EntriesOn(DateTime.Today).ToList();
        if (entries.Count > 0)
        {
            foreach (var entry in entries)
            {
                var status = entry.StatusText ?? (entry.Start <= minutes && entry.End >= minutes ? "jetzt" : "");
                var color = entry.IsCancelled ? "Bad" : entry.IsExam ? "Warn" : entry.IsChanged ? "Accent" : "Accent";
                panel.Children.Add(LessonRow(entry.Subject, entry.TimeText + (entry.Room.Length > 0 ? " · " + entry.Room : ""), status, color,
                    entry.IsCancelled, entry.End < minutes, store.NotebookForSubject(entry.Subject)?.ColorName));
            }
            if (Sheets.TimetableSync.LastSync is DateTime last) panel.Children.Add(Ui.Hint("Stand: " + last.ToString("HH:mm")));
        }
        else
        {
            var today = store.LessonsOn(Lesson.TodayIndex()).ToList();
            if (today.Count == 0)
            {
                panel.Children.Add(Ui.Text(store.Library.Lessons.Count == 0
                    ? "Noch kein Stundenplan. Trag ihn ein oder hol ihn aus WebUntis."
                    : "Heute ist frei.", 13.5, color: "Muted", wrap: true));
            }
            foreach (var lesson in today)
            {
                var notebook = store.Library.Notebooks.FirstOrDefault(n => n.Id == lesson.NotebookId);
                panel.Children.Add(LessonRow(lesson.Subject, lesson.TimeText + (lesson.Room.Length > 0 ? " · " + lesson.Room : ""),
                    lesson.Start <= minutes && lesson.End >= minutes ? "jetzt" : "", "Accent", false, lesson.End < minutes, notebook?.ColorName));
            }
        }
        return Ui.Card(panel);
    }

    private static UIElement HomeworkCard(Panel host, double width, Actions actions, List<Homework> items)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.CardHeader("Als Nächstes dran", Ui.GlyphChecklist, "Alle", actions.Homework));
        foreach (var item in items)
        {
            var check = new CheckBox { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            var id = item.Id;
            check.Click += (_, _) =>
            {
                Services.Store.ToggleHomework(id);
                Build(host, width, actions);
            };
            var text = new StackPanel();
            text.Children.Add(Ui.Text(item.Title, 14, wrap: true));
            var detail = item.Subject;
            var late = false;
            if (item.DueAt is DateTime due)
            {
                late = due < LibraryStore.EndOfToday;
                var label = due.Date == DateTime.Today ? "heute" : due.Date == DateTime.Today.AddDays(1) ? "morgen" : due.ToString("ddd, d. MMM", German);
                detail += (detail.Length > 0 ? " · " : "") + label;
            }
            if (detail.Length > 0) text.Children.Add(Ui.Text(detail, 12, late ? FontWeights.SemiBold : FontWeights.Normal, late ? "Bad" : "Faint"));
            var dock = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
            DockPanel.SetDock(check, Dock.Left);
            dock.Children.Add(check);
            dock.Children.Add(text);
            panel.Children.Add(dock);
        }
        return Ui.Card(panel);
    }

    public static UIElement LessonRow(string subject, string detail, string status, string color, bool cancelled, bool past, string? notebookColor)
    {
        var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5), Opacity = past ? 0.5 : 1 };
        var bar = new Border { Width = 4, Height = 32, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 12, 0) };
        if (cancelled) bar.SetResourceReference(Border.BackgroundProperty, "Line");
        else if (notebookColor is not null) bar.Background = Ui.NotebookBrush(notebookColor);
        else bar.SetResourceReference(Border.BackgroundProperty, "LineStrong");
        DockPanel.SetDock(bar, Dock.Left);
        row.Children.Add(bar);
        if (status.Length > 0)
        {
            var badge = color == "Faint"
                ? Ui.Badge(status, "Muted", "SurfaceAlt")
                : Ui.Badge(status, color, color == "Accent" ? "AccentSoft" : color + "Soft");
            DockPanel.SetDock(badge, Dock.Right);
            row.Children.Add(badge);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Ui.Text(subject, 14, color: cancelled ? "Faint" : "Text");
        if (cancelled) title.TextDecorations = TextDecorations.Strikethrough;
        text.Children.Add(title);
        text.Children.Add(Ui.Text(detail, 12, color: "Faint"));
        row.Children.Add(text);
        return row;
    }
}
