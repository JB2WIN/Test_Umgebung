using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Lernheft.Studio.App.Sheets;

/// <summary>Holt den aktuellen Stand aus WebUntis im Hintergrund – solange die Anmeldung noch gilt.</summary>
public static class TimetableSync
{
    private record StoredCookie(string Name, string Value, string Domain, string Path);

    public static WebUntisReader.Target? Target
    {
        get
        {
            var settings = Services.Settings;
            var host = settings.Get(Keys.UntisHost, "");
            var id = settings.Get(Keys.UntisId, "");
            if (host.Length == 0 || id.Length == 0) return null;
            var type = settings.GetInt(Keys.UntisType, 5);
            return new WebUntisReader.Target(host, type == 0 ? 5 : type, id, DateTime.Today.ToString("yyyy-MM-dd"));
        }
    }

    public static DateTime? LastSync
    {
        get
        {
            var value = Services.Settings.GetDouble(Keys.UntisLastSync, 0);
            return value > 0 ? AppleTime.ToDateTime(value) : null;
        }
    }

    public static void Remember(WebUntisReader.Target target, IEnumerable<CoreWebView2Cookie> cookies)
    {
        var settings = Services.Settings;
        settings.Set(Keys.UntisHost, target.Host);
        settings.SetInt(Keys.UntisType, target.Type);
        settings.Set(Keys.UntisId, target.Id);
        var stored = cookies.Select(c => new StoredCookie(c.Name, c.Value, c.Domain, c.Path)).ToList();
        settings.SetSecret(Keys.UntisCookies, JsonSerializer.Serialize(stored));
    }

    public class ExpiredException() : Exception("Deine WebUntis-Anmeldung ist abgelaufen. Melde dich über „Aus WebUntis laden“ neu an.");

    /// <summary>Lädt die aktuelle Woche neu.</summary>
    public static async Task<List<DayLesson>> RefreshAsync()
    {
        var target = Target ?? throw new InvalidOperationException("Es ist noch kein WebUntis-Stundenplan hinterlegt.");
        var handler = new HttpClientHandler { UseCookies = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Get, WebUntisReader.WeeklyDataUrl(target));
        request.Headers.Add("Accept", "application/json");
        try
        {
            var cookies = JsonSerializer.Deserialize<List<StoredCookie>>(Services.Settings.GetSecret(Keys.UntisCookies)) ?? new();
            if (cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")));
        }
        catch (JsonException) { }
        using var response = await http.SendAsync(request);
        if ((int)response.StatusCode is 401 or 403) throw new ExpiredException();
        var text = await response.Content.ReadAsStringAsync();
        if (text.Contains("<html", StringComparison.OrdinalIgnoreCase)) throw new ExpiredException();
        var entries = WebUntisReader.DayLessons(text);
        Services.Store.ReplaceWeekEntries(entries);
        Services.Settings.SetDouble(Keys.UntisLastSync, AppleTime.Now);
        return entries;
    }

    /// <summary>Beim Start: höchstens alle 20 Minuten still nachladen.</summary>
    public static async Task RefreshIfStaleAsync()
    {
        if (Target is null) return;
        if (LastSync is DateTime last && DateTime.Now - last < TimeSpan.FromMinutes(20)) return;
        try
        {
            await RefreshAsync();
        }
        catch (Exception error)
        {
            Services.Log("Stundenplan: " + error.Message);
        }
    }
}

/// <summary>Wochenplan ansehen, selbst eintragen oder aus WebUntis bzw. einem Kalender-Link holen.</summary>
public sealed class TimetableWindow : SheetWindow
{
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 12) };
    private readonly StackPanel _days = new();
    private readonly Button _import;

    public TimetableWindow() : base("Stundenplan", 720, 820)
    {
        _message.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _import = Ui.Button("Importieren", (_, _) => ShowImportMenu(), icon: Ui.GlyphDownload);
        HeaderRight.Children.Add(_import);
        Body.Children.Add(_message);
        Body.Children.Add(_days);
        Rebuild();
    }

    private void Say(string text, string color = "Muted")
    {
        _message.Text = text;
        _message.SetResourceReference(TextBlock.ForegroundProperty, color);
    }

    private void ShowImportMenu()
    {
        var link = Services.Settings.Get(Keys.TimetableLink, "");
        var menu = Ui.Menu(_import);
        menu.Placement = PlacementMode.Bottom;
        menu.Items.Add(Ui.MenuItem("Heute aktualisieren", () => _ = RefreshLiveAsync(), Ui.GlyphRefresh, enabled: TimetableSync.Target is not null));
        menu.Items.Add(Ui.MenuItem("Aus WebUntis laden (Anmeldung) …", Login, Ui.GlyphConnect, enabled: link.Length > 0));
        menu.Items.Add(Ui.MenuItem(link.Length == 0 ? "WebUntis-Adresse eintragen …" : "Adresse ändern …", EditLink, Ui.GlyphLink));
        if (link.Length > 0) menu.Items.Add(Ui.MenuItem("Kalender-Link neu laden", () => _ = LoadLinkAsync(), Ui.GlyphCalendar));
        menu.IsOpen = true;
    }

    private void EditLink()
    {
        var dialog = new TextDialog("Stundenplan laden", "WebUntis-Adresse oder Kalender-Link (.ics, webcal://)",
            Services.Settings.Get(Keys.TimetableLink, ""), "Übernehmen", multiline: true) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Services.Settings.Set(Keys.TimetableLink, dialog.Value);
        if (dialog.Value.Contains(".ics", StringComparison.OrdinalIgnoreCase) || dialog.Value.StartsWith("webcal", StringComparison.OrdinalIgnoreCase))
            _ = LoadLinkAsync();
        else
            Say("Adresse gespeichert. Wähle jetzt „Importieren → Aus WebUntis laden“ und melde dich an.");
    }

    private async Task LoadLinkAsync()
    {
        var link = Services.Settings.Get(Keys.TimetableLink, "");
        if (link.Length == 0)
        {
            EditLink();
            return;
        }
        Say("Lade den Kalender …");
        try
        {
            var lessons = await IcsImporter.LessonsFromLinkAsync(link);
            Services.Store.ReplaceLessons(lessons);
            Say($"{Ui.Plural(lessons.Count, "Stunde", "Stunden")} vom Link übernommen.", "Good");
            Rebuild();
        }
        catch (Exception error)
        {
            Say(error.Message + (error.Message.Contains("Anmeldeseite") ? "" : " Probier „Aus WebUntis laden“."), "Bad");
        }
    }

    private async Task RefreshLiveAsync()
    {
        Say("Frage WebUntis …");
        try
        {
            var entries = await TimetableSync.RefreshAsync();
            var today = entries.Where(e => e.Date == DayLesson.DateValue(DateTime.Today)).ToList();
            var cancelled = today.Count(e => e.IsCancelled);
            Say(cancelled > 0 ? $"Aktualisiert. Heute fallen {Ui.Plural(cancelled, "Stunde", "Stunden")} aus." : "Aktualisiert. Heute fällt nichts aus.", "Good");
            Rebuild();
        }
        catch (Exception error)
        {
            Say(error.Message, "Bad");
        }
    }

    private void Login()
    {
        var login = new WebUntisLoginWindow(Services.Settings.Get(Keys.TimetableLink, "")) { Owner = this };
        if (login.ShowDialog() != true) return;
        if (login.Lessons.Count > 0) Services.Store.ReplaceLessons(login.Lessons);
        if (login.Entries.Count > 0)
        {
            Services.Store.ReplaceWeekEntries(login.Entries);
            Services.Settings.SetDouble(Keys.UntisLastSync, AppleTime.Now);
        }
        Say($"{Ui.Plural(login.Lessons.Count, "Stunde", "Stunden")} aus WebUntis übernommen.", "Good");
        Rebuild();
    }

    private void Rebuild()
    {
        _days.Children.Clear();
        if (_message.Text.Length == 0 && Services.Store.Library.Lessons.Count == 0)
        {
            Say("Trag deine Stunden selbst ein oder hol sie über „Importieren“ aus WebUntis bzw. einem Kalender-Link.");
        }
        var todayEntries = Services.Store.EntriesOn(DateTime.Today).ToList();
        if (todayEntries.Count > 0)
        {
            var today = new StackPanel();
            today.Children.Add(Ui.CardHeader("Heute laut WebUntis", Ui.GlyphCalendar));
            foreach (var entry in todayEntries) today.Children.Add(Home.LessonRow(entry.Subject, entry.TimeText + (entry.Room.Length > 0 ? " · " + entry.Room : ""),
                entry.StatusText ?? "", entry.IsCancelled ? "Bad" : entry.IsExam ? "Warn" : "Accent", entry.IsCancelled, false,
                Services.Store.NotebookForSubject(entry.Subject)?.ColorName));
            if (TimetableSync.LastSync is DateTime last) today.Children.Add(Ui.Hint("Stand: " + last.ToString("dd.MM. HH:mm")));
            _days.Children.Add(Ui.Card(today));
        }
        for (var day = 1; day <= 5; day++)
        {
            var weekday = day;
            var panel = new StackPanel();
            panel.Children.Add(Ui.CardHeader(Lesson.WeekdayNames[day - 1], null, "Stunde hinzufügen", () =>
            {
                var last = Services.Store.LessonsOn(weekday).LastOrDefault();
                Edit(new Lesson { Weekday = weekday, Start = last?.End + 5 ?? 480, End = (last?.End + 5 ?? 480) + 45 }, isNew: true);
            }));
            var lessons = Services.Store.LessonsOn(day).ToList();
            if (lessons.Count == 0) panel.Children.Add(Ui.Text("Frei", 13.5, color: "Faint"));
            foreach (var lesson in lessons)
            {
                var notebook = Services.Store.Library.Notebooks.FirstOrDefault(n => n.Id == lesson.NotebookId);
                var row = Home.LessonRow(lesson.Subject.Length == 0 ? "Ohne Namen" : lesson.Subject,
                    lesson.TimeText + (lesson.Room.Length > 0 ? " · " + lesson.Room : ""), notebook?.Name ?? "", "Faint", false, false,
                    notebook?.ColorName);
                var button = new Button { Style = Ui.Style("Ghost"), Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(-6, 0, -6, 0) };
                button.SetResourceReference(ForegroundProperty, "Text");
                var captured = lesson;
                button.Click += (_, _) => Edit(captured, isNew: false);
                panel.Children.Add(button);
            }
            _days.Children.Add(Ui.Card(panel));
        }
    }

    private void Edit(Lesson lesson, bool isNew)
    {
        var dialog = new LessonDialog(lesson, isNew) { Owner = this };
        var result = dialog.ShowDialog();
        if (dialog.Deleted)
        {
            Services.Store.DeleteLesson(lesson.Id);
        }
        else if (result == true)
        {
            Services.Store.UpsertLesson(dialog.Lesson);
        }
        Rebuild();
    }
}

/// <summary>Eine Stunde bearbeiten: Fach, Raum, Tag, Beginn, Ende und passendes Heft.</summary>
public sealed class LessonDialog : DialogWindow
{
    public Lesson Lesson { get; }
    public bool Deleted { get; private set; }

    public LessonDialog(Lesson lesson, bool isNew) : base(isNew ? "Neue Stunde" : "Stunde", 470)
    {
        Lesson = new Lesson
        {
            Id = lesson.Id,
            Weekday = lesson.Weekday,
            Start = lesson.Start,
            End = lesson.End,
            Subject = lesson.Subject,
            Room = lesson.Room,
            NotebookId = lesson.NotebookId
        };
        Body.Children.Add(Label("Fach", first: true));
        var subject = Ui.Field(Lesson.Subject, "z. B. Mathematik");
        subject.TextChanged += (_, _) => Lesson.Subject = subject.Text.Trim();
        Body.Children.Add(subject);
        Body.Children.Add(Label("Raum"));
        var room = Ui.Field(Lesson.Room, "z. B. 204");
        room.TextChanged += (_, _) => Lesson.Room = room.Text.Trim();
        Body.Children.Add(room);

        var day = new ComboBox { Width = 180 };
        for (var index = 1; index <= 7; index++) day.Items.Add(new ComboBoxItem { Content = Lesson.WeekdayNames[index - 1], Tag = index });
        day.SelectedIndex = Math.Clamp(Lesson.Weekday - 1, 0, 6);
        day.SelectionChanged += (_, _) => Lesson.Weekday = (int)((ComboBoxItem)day.SelectedItem).Tag;
        Body.Children.Add(Label("Tag"));
        Body.Children.Add(day);

        var start = TimeBox(Lesson.Start, value => Lesson.Start = value);
        var end = TimeBox(Lesson.End, value => Lesson.End = value);
        Body.Children.Add(Label("Zeit"));
        Body.Children.Add(Ui.Row(10, start, Ui.Text("bis", 13.5, color: "Muted"), end));
        ((FrameworkElement)((StackPanel)Body.Children[^1]).Children[1]).VerticalAlignment = VerticalAlignment.Center;

        var notebook = new ComboBox { Width = 220 };
        notebook.Items.Add(new ComboBoxItem { Content = "Kein Heft", Tag = null });
        foreach (var book in Services.Store.Library.Notebooks)
        {
            notebook.Items.Add(new ComboBoxItem
            {
                Content = Ui.Row(8, Ui.Dot(Ui.NotebookBrush(book.ColorName), 9), new TextBlock { Text = book.Name }),
                Tag = book.Id
            });
        }
        notebook.SelectedItem = notebook.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (Guid?)i.Tag == Lesson.NotebookId) ?? notebook.Items[0];
        notebook.SelectionChanged += (_, _) => Lesson.NotebookId = (Guid?)((ComboBoxItem)notebook.SelectedItem).Tag;
        Body.Children.Add(Label("Heft"));
        Body.Children.Add(notebook);

        if (!isNew)
        {
            var delete = AddButton("Löschen", () =>
            {
                Deleted = true;
                DialogResult = false;
            }, "Danger");
            delete.Margin = new Thickness(0, 0, 0, 0);
            delete.HorizontalAlignment = HorizontalAlignment.Left;
        }
        AddButton("Abbrechen", () => DialogResult = false, isCancel: true);
        AddButton("Sichern", () =>
        {
            if (Lesson.End <= Lesson.Start) Lesson.End = Lesson.Start + 45;
            DialogResult = true;
        }, "Primary", isDefault: true);
        Loaded += (_, _) => subject.Focus();
    }

    private static ComboBox TimeBox(int minutes, Action<int> changed)
    {
        var box = new ComboBox { Width = 110, MaxDropDownHeight = 320 };
        for (var value = 6 * 60; value <= 20 * 60; value += 5) box.Items.Add(new ComboBoxItem { Content = Lesson.Clock(value), Tag = value });
        var rounded = Math.Clamp((int)Math.Round(minutes / 5.0) * 5, 6 * 60, 20 * 60);
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(i => (int)i.Tag == rounded);
        box.SelectionChanged += (_, _) => changed((int)((ComboBoxItem)box.SelectedItem).Tag);
        return box;
    }
}

/// <summary>
/// Anmeldefenster für WebUntis (auch mit Schul-Login). Nach der Anmeldung liest die App den
/// Wochenplan direkt aus der Seite, auf der man gerade ist.
/// </summary>
public sealed class WebUntisLoginWindow : StudioWindow
{
    private readonly WebView2 _web = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _read;
    private readonly string _link;

    public List<Lesson> Lessons { get; private set; } = new();
    public List<DayLesson> Entries { get; private set; } = new();

    public WebUntisLoginWindow(string link)
    {
        _link = link.Trim();
        Title = "WebUntis";
        Width = 1000;
        Height = 760;
        ShowInTaskbar = true;
        CloseOnEscape = false;
        _status.Text = "Melde dich an wie im Browser und öffne deinen Stundenplan.";
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _read = Ui.Button("Stundenplan lesen", (_, _) => _ = ReadAsync(), "Primary", Ui.GlyphDownload);
        var cancel = Ui.Button("Abbrechen", (_, _) => DialogResult = false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var bar = new DockPanel { Margin = new Thickness(16, 10, 16, 10) };
        var buttons = Ui.Row(0, cancel, _read);
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(_status);
        var top = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
        top.SetResourceReference(Border.BorderBrushProperty, "Line");
        top.SetResourceReference(Border.BackgroundProperty, "Surface");
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_web);
        Content = root;
        Loaded += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Services.DataRoot, "webview"));
            await _web.EnsureCoreWebView2Async(environment);
            var address = IcsImporter.Normalize(_link);
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) throw new InvalidOperationException("Die Adresse ist ungültig.");
            _web.Source = uri;
        }
        catch (Exception error)
        {
            _status.Text = "WebUntis ließ sich nicht öffnen: " + error.Message + " (Fehlt die WebView2-Laufzeit von Microsoft?)";
            _status.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
            _read.IsEnabled = false;
        }
    }

    private async Task<string> FetchAsync(string url)
    {
        var expression = "(async () => { const r = await fetch(" + JsonSerializer.Serialize(url) +
                         ", { headers: { 'Accept': 'application/json' }, credentials: 'include' }); return await r.text(); })()";
        var arguments = JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true });
        var raw = await _web.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", arguments);
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.TryGetProperty("result", out var result) && result.TryGetProperty("value", out var value))
            return value.GetString() ?? "";
        return "";
    }

    private async Task ReadAsync()
    {
        if (_web.CoreWebView2 is null) return;
        _read.IsEnabled = false;
        _status.Text = "Lese den Stundenplan …";
        try
        {
            var page = _web.Source?.ToString() ?? _link;
            var target = WebUntisReader.FindTarget(page, _link)
                         ?? throw new InvalidOperationException("Aus dieser Adresse werde ich nicht schlau. Öffne deinen Stundenplan in WebUntis.");
            if (target.Id.Length == 0)
            {
                var config = await FetchAsync(WebUntisReader.PageConfigUrl(target));
                var id = WebUntisReader.ElementIdFromPageConfig(config)
                         ?? throw new InvalidOperationException("Ich konnte deine Kennung nicht finden. Öffne in WebUntis deinen eigenen Stundenplan.");
                target = target with { Id = id };
            }
            var json = await FetchAsync(WebUntisReader.WeeklyDataUrl(target));
            Lessons = WebUntisReader.Lessons(json);
            try { Entries = WebUntisReader.DayLessons(json); }
            catch (WebUntisReader.ReadException) { Entries = new List<DayLesson>(); }
            var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync("https://" + target.Host);
            TimetableSync.Remember(target, cookies);
            DialogResult = true;
        }
        catch (Exception error)
        {
            _status.Text = error.Message;
            _status.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
            _read.IsEnabled = true;
        }
    }
}
