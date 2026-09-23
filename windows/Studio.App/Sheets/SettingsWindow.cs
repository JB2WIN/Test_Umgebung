using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lernheft.Studio.App.Editor;

namespace Lernheft.Studio.App.Sheets;

/// <summary>
/// Alle Einstellungen an einem Ort – links die Bereiche, rechts die Schalter. Enthält alles, was
/// früher auf dem iPad eingestellt wurde, plus Schreiben, iPad-Kopplung und Umzug.
/// </summary>
public sealed class SettingsWindow : StudioWindow
{
    public enum Section { Ai, Writing, Appearance, Homework, Pad, Backup, Usage, About }

    private static readonly (Section Section, string Title, string Glyph)[] Sections =
    {
        (Section.Ai, "KI", ""),
        (Section.Writing, "Schreiben", Ui.GlyphEdit),
        (Section.Appearance, "Darstellung", ""),
        (Section.Homework, "Hausaufgaben", Ui.GlyphChecklist),
        (Section.Pad, "iPad", Ui.GlyphTablet),
        (Section.Backup, "Sicherung & Umzug", Ui.GlyphSave),
        (Section.Usage, "Guthaben & Verbrauch", ""),
        (Section.About, "Über", Ui.GlyphInfo)
    };

    private readonly ListBox _nav = new();
    private readonly StackPanel _body = new() { Margin = new Thickness(28, 22, 28, 28), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ScrollViewer _scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, PanningMode = PanningMode.VerticalOnly };
    private Section _section;

    /// <summary>Etwas wurde geändert, das die offene Notiz betrifft (Schrift, Rechtschreibung …).</summary>
    public static event Action? Applied;

    public SettingsWindow(Section section = Section.Ai)
    {
        Title = "Einstellungen";
        Width = 980;
        Height = 760;
        MinWidth = 760;
        MinHeight = 520;
        ShowInTaskbar = false;
        _section = section;

        _nav.Style = Ui.Style("PlainList");
        _nav.ItemContainerStyle = Ui.Style("SidebarItem");
        _nav.Padding = new Thickness(10, 8, 10, 8);
        foreach (var (item, title, glyph) in Sections)
        {
            var icon = Ui.Icon(glyph, 14);
            icon.Width = 20;
            icon.Margin = new Thickness(0, 0, 10, 0);
            _nav.Items.Add(new ListBoxItem { Content = Ui.Row(0, icon, new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center }), Tag = item });
        }
        _nav.SelectionChanged += (_, _) =>
        {
            if (_nav.SelectedItem is ListBoxItem { Tag: Section chosen })
            {
                _section = chosen;
                Build();
            }
        };
        var navPanel = new DockPanel { Width = 250 };
        var heading = Ui.Heading("Einstellungen", 20);
        heading.Margin = new Thickness(20, 20, 0, 10);
        DockPanel.SetDock(heading, Dock.Top);
        navPanel.Children.Add(heading);
        navPanel.Children.Add(_nav);
        var navBorder = new Border { Child = navPanel, BorderThickness = new Thickness(0, 0, 1, 0) };
        navBorder.SetResourceReference(Border.BackgroundProperty, "Sidebar");
        navBorder.SetResourceReference(Border.BorderBrushProperty, "Line");

        _scroller.Content = _body;
        var root = new DockPanel();
        DockPanel.SetDock(navBorder, Dock.Left);
        root.Children.Add(navBorder);
        root.Children.Add(_scroller);
        Content = root;
        _nav.SelectedIndex = Array.FindIndex(Sections, s => s.Section == section);
    }

    private static Settings S => Services.Settings;

    private void Build()
    {
        _body.Children.Clear();
        _scroller.ScrollToTop();
        var title = Sections.First(s => s.Section == _section).Title;
        var heading = Ui.Heading(title, 24);
        heading.Margin = new Thickness(0, 0, 0, 16);
        _body.Children.Add(heading);
        switch (_section)
        {
            case Section.Ai: BuildAi(); break;
            case Section.Writing: BuildWriting(); break;
            case Section.Appearance: BuildAppearance(); break;
            case Section.Homework: BuildHomework(); break;
            case Section.Pad: BuildPad(); break;
            case Section.Backup: BuildBackup(); break;
            case Section.Usage: BuildUsage(); break;
            case Section.About: BuildAbout(); break;
        }
    }

    private StackPanel Group(string title, string? footer = null)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Style = Ui.Style("CardTitle") });
        var card = Ui.Card(panel);
        _body.Children.Add(card);
        if (footer is not null) _body.Children.Add(Ui.Hint(footer, new Thickness(4, -6, 4, 16)));
        return panel;
    }

    private static TextBlock FieldLabel(string text, bool first = false) =>
        Ui.Text(text, 12, FontWeights.SemiBold, "Muted", margin: new Thickness(0, first ? 0 : 14, 0, 6));

    private static CheckBox Toggle(string label, string key, bool fallback, Action<bool>? after = null) =>
        Ui.Switch(label, S.GetBool(key, fallback), value =>
        {
            S.SetBool(key, value);
            after?.Invoke(value);
            Applied?.Invoke();
        });

    private static TextBlock Status() => new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 10, 0, 0) };

    private static void Say(TextBlock status, string text, string color)
    {
        status.Text = text;
        status.SetResourceReference(TextBlock.ForegroundProperty, color);
    }

    // MARK: - KI

    private void BuildAi()
    {
        var main = Group(S.Get(Keys.KeyLabel, "").Length == 0 ? "Gemini – Hauptschlüssel" : "Gemini – " + S.Get(Keys.KeyLabel, ""),
            "Der Schlüssel liegt verschlüsselt auf diesem Surface und lässt sich hier jederzeit wieder anzeigen und kopieren. " +
            "Laut Google-Bedingungen muss der Inhaber des Google-Kontos mindestens 18 Jahre alt sein.");
        var key = new PasswordBox { Password = S.GetSecret(Keys.GeminiKey) };
        var visible = Ui.Field(key.Password);
        visible.Visibility = Visibility.Collapsed;
        visible.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        key.PasswordChanged += (_, _) =>
        {
            S.SetSecret(Keys.GeminiKey, key.Password);
            if (visible.Text != key.Password) visible.Text = key.Password;
        };
        visible.TextChanged += (_, _) =>
        {
            if (key.Password != visible.Text) key.Password = visible.Text;
        };
        main.Children.Add(FieldLabel("API-Schlüssel", first: true));
        main.Children.Add(key);
        main.Children.Add(visible);
        var label = Ui.Field(S.Get(Keys.KeyLabel, ""), "Name, z. B. bezahlt (Projekt School-Paid)");
        label.TextChanged += (_, _) => S.Set(Keys.KeyLabel, label.Text.Trim());
        main.Children.Add(FieldLabel("Name (nur zur Übersicht)"));
        main.Children.Add(label);
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var show = Ui.Button("Anzeigen", null, icon: "");
        show.Click += (_, _) =>
        {
            var showing = visible.Visibility == Visibility.Visible;
            visible.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
            key.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
            show.Content = Ui.Row(8, Ui.Icon(showing ? "" : "", 14), new TextBlock { Text = showing ? "Anzeigen" : "Verbergen" });
        };
        show.Margin = new Thickness(0, 0, 8, 8);
        var copy = Ui.Button("Kopieren", (_, _) => Clipboard.SetText(S.GetSecret(Keys.GeminiKey)), icon: Ui.GlyphCopy);
        copy.Margin = new Thickness(0, 0, 8, 8);
        var studio = Ui.Button("Schlüssel in Google AI Studio erstellen", (_, _) => OpenUrl("https://aistudio.google.com/apikey"), icon: Ui.GlyphLink);
        studio.Margin = new Thickness(0, 0, 8, 8);
        row.Children.Add(show);
        row.Children.Add(copy);
        row.Children.Add(studio);
        main.Children.Add(row);

        var backup = Group(S.Get(Keys.KeyLabelBackup, "").Length == 0 ? "Ersatzschlüssel" : "Ersatz – " + S.Get(Keys.KeyLabelBackup, ""),
            "Lernheft fragt immer zuerst den Hauptschlüssel. Bei „Limit erreicht“ (429) hilft der Ersatzschlüssel immer, weil Limits pro " +
            "Schlüssel gelten. Bei „überlastet“ (503) lohnt er sich nur, wenn der Ersatz die bessere Stufe ist.");
        var backupKey = new PasswordBox { Password = S.GetSecret(Keys.GeminiBackupKey) };
        backupKey.PasswordChanged += (_, _) => S.SetSecret(Keys.GeminiBackupKey, backupKey.Password);
        backup.Children.Add(FieldLabel("Ersatz-Schlüssel (freiwillig)", first: true));
        backup.Children.Add(backupKey);
        var backupLabel = Ui.Field(S.Get(Keys.KeyLabelBackup, ""), "Name, z. B. kostenlos (Projekt School)");
        backupLabel.TextChanged += (_, _) => S.Set(Keys.KeyLabelBackup, backupLabel.Text.Trim());
        backup.Children.Add(FieldLabel("Name"));
        backup.Children.Add(backupLabel);
        var fallback = Toggle("Auch bei Überlastung (503) ausweichen", Keys.FallbackOnOverload, false);
        fallback.Margin = new Thickness(0, 12, 0, 0);
        backup.Children.Add(fallback);
        backup.Children.Add(Ui.Hint($"Bisher ausgewichen: {Services.Usage.FallbackRequests}×"));

        var model = Group("Modell", $"„{GeminiClient.DefaultModel}“ zeigt immer auf das aktuelle Flash-Modell. Flash-Lite ist schneller, aber ungenauer beim Lesen von Handschrift.");
        var modelBox = Ui.Field(S.Get(Keys.GeminiModel, GeminiClient.DefaultModel));
        modelBox.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        modelBox.MaxWidth = 420;
        modelBox.HorizontalAlignment = HorizontalAlignment.Left;
        modelBox.MinWidth = 320;
        modelBox.TextChanged += (_, _) =>
        {
            var value = modelBox.Text.Trim();
            if (value.Length > 0) S.Set(Keys.GeminiModel, value);
        };
        var available = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        available.SelectionChanged += (_, _) =>
        {
            if (available.SelectedItem is string value) modelBox.Text = value;
        };
        model.Children.Add(modelBox);
        model.Children.Add(available);
        var status = Status();
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var list = Ui.Button("Verfügbare Modelle laden", null, icon: Ui.GlyphBullets);
        list.Margin = new Thickness(0, 0, 8, 8);
        list.Click += async (_, _) =>
        {
            var client = Services.Gemini();
            if (client is null)
            {
                Say(status, "Trag zuerst einen Schlüssel ein.", "Bad");
                return;
            }
            list.IsEnabled = false;
            Say(status, "Frage Google …", "Muted");
            try
            {
                var models = await client.ListModelsAsync();
                available.Items.Clear();
                foreach (var name in models) available.Items.Add(name);
                available.SelectedItem = models.Contains(modelBox.Text.Trim()) ? modelBox.Text.Trim() : null;
                available.Visibility = models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                Say(status, models.Count == 0 ? "Keine passenden Modelle gefunden." : $"{models.Count} Modelle gefunden – in der Liste auswählen.",
                    models.Count == 0 ? "Bad" : "Good");
            }
            catch (Exception error)
            {
                Say(status, GeminiClient.Describe(error), "Bad");
            }
            list.IsEnabled = true;
        };
        var test = Ui.Button("Verbindung testen", null, icon: Ui.GlyphConnect);
        test.Margin = new Thickness(0, 0, 8, 8);
        test.Click += async (_, _) =>
        {
            var client = Services.Gemini();
            if (client is null)
            {
                Say(status, "Trag zuerst einen Schlüssel ein.", "Bad");
                return;
            }
            test.IsEnabled = false;
            Say(status, "Teste …", "Muted");
            try
            {
                await client.GenerateAsync("Antworte nur mit dem Wort: OK");
                Say(status, "Verbindung klappt.", "Good");
            }
            catch (Exception error)
            {
                Say(status, GeminiClient.Describe(error), "Bad");
            }
            test.IsEnabled = true;
        };
        buttons.Children.Add(list);
        buttons.Children.Add(test);
        model.Children.Add(buttons);
        model.Children.Add(status);
    }

    // MARK: - Schreiben

    private void BuildWriting()
    {
        var text = Group("Text", "Gilt für neue Textfelder. Einzelne Stellen formatierst du über die Leiste über der Seite.");
        var font = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var name in TextDocs.InstalledFonts())
        {
            font.Items.Add(new ComboBoxItem { Content = new TextBlock { Text = name, FontFamily = TextDocs.Family(name), FontSize = 14 }, Tag = name });
        }
        font.SelectedItem = font.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == S.Get(Keys.TextFont, TextDocs.DefaultFont)) ?? font.Items[0];
        font.SelectionChanged += (_, _) =>
        {
            S.Set(Keys.TextFont, (string)((ComboBoxItem)font.SelectedItem).Tag);
            Applied?.Invoke();
        };
        var size = new ComboBox { Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var value in new[] { 14.0, 16, 17, 18, 20, 22 }) size.Items.Add(new ComboBoxItem { Content = value.ToString("0"), Tag = value });
        size.SelectedItem = size.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Math.Abs((double)i.Tag - S.GetDouble(Keys.TextSize, 18)) < 0.1) ?? size.Items[3];
        size.SelectionChanged += (_, _) =>
        {
            S.SetDouble(Keys.TextSize, (double)((ComboBoxItem)size.SelectedItem).Tag);
            Applied?.Invoke();
        };
        text.Children.Add(FieldLabel("Schriftart", first: true));
        text.Children.Add(font);
        text.Children.Add(FieldLabel("Schriftgröße"));
        text.Children.Add(size);
        var snap = Toggle("Text sitzt auf den Linien des Papiers", Keys.SnapToLines, true);
        snap.Margin = new Thickness(0, 16, 0, 0);
        text.Children.Add(snap);
        text.Children.Add(Toggle("Rechtschreibung prüfen (rote Wellenlinie)", Keys.SpellCheck, true));

        var script = Group("Schönschrift", "Wenn du auf dem iPad Handschrift markierst und „Schönschrift“ wählst, wird sie in dieser Schrift an dieselbe Stelle gesetzt.");
        var scriptFont = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var name in new[] { "Ink Free", "Segoe Print", "Segoe Script", "Comic Sans MS", "Segoe UI" }.Where(TextDocs.IsInstalled))
        {
            scriptFont.Items.Add(new ComboBoxItem { Content = new TextBlock { Text = name, FontFamily = TextDocs.Family(name), FontSize = 16 }, Tag = name });
        }
        if (scriptFont.Items.Count == 0) scriptFont.Items.Add(new ComboBoxItem { Content = "Segoe UI", Tag = "Segoe UI" });
        scriptFont.SelectedItem = scriptFont.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == S.Get(Keys.ScriptFont, TextDocs.DefaultScriptFont)) ?? scriptFont.Items[0];
        scriptFont.SelectionChanged += (_, _) => S.Set(Keys.ScriptFont, (string)((ComboBoxItem)scriptFont.SelectedItem).Tag);
        script.Children.Add(scriptFont);

        var pdf = Group("Einfügen");
        pdf.Children.Add(Toggle("Eingefügte Seiten nahtlos einblenden (Weiß wird durchsichtig)", Keys.SeamlessImport, true));
        pdf.Children.Add(Toggle("Text aus PDFs zusätzlich als Textfeld übernehmen", Keys.ImportTextFromPdf, false));
    }

    // MARK: - Darstellung

    private void BuildAppearance()
    {
        var look = Group("Erscheinungsbild");
        var modes = Enum.GetValues<Theme.Mode>();
        look.Children.Add(Ui.Segments(modes.Select(Theme.Label), Array.IndexOf(modes, Theme.Current), index =>
        {
            Theme.Apply(modes[index]);
            Applied?.Invoke();
        }));
        var layout = Group("Beim Schreiben");
        layout.Children.Add(Toggle("Listen ausblenden, sobald eine Notiz offen ist", Keys.HideListsWhileWriting, false));
        layout.Children.Add(Toggle("Zoom sperren (kein Vergrößern mit zwei Fingern oder Strg+Mausrad)", Keys.ZoomLocked, false));
        layout.Children.Add(Ui.Hint("Mit F11 blendest du die Listen jederzeit ein und aus."));
    }

    // MARK: - Hausaufgaben

    private void BuildHomework()
    {
        var group = Group("Automatisch erkennen",
            "Die KI schaut nur in den angehakten Fächern nach Aufgaben, und zwar beim Schließen einer Notiz. Pro Notiz kostet das eine Gemini-Anfrage.");
        var list = new StackPanel { Margin = new Thickness(18, 6, 0, 0) };
        void Fill()
        {
            list.Children.Clear();
            list.IsEnabled = S.GetBool(Keys.AutoHomework, false);
            list.Opacity = list.IsEnabled ? 1 : 0.5;
            if (Services.Store.Library.Notebooks.Count == 0) list.Children.Add(Ui.Text("Noch keine Fächer angelegt.", 13, color: "Faint"));
            foreach (var notebook in Services.Store.Library.Notebooks)
            {
                var check = new CheckBox { IsChecked = notebook.ScanHomework, Margin = new Thickness(0, 5, 0, 5) };
                check.Content = Ui.Row(8, Ui.Dot(Ui.NotebookBrush(notebook.ColorName), 9), new TextBlock { Text = notebook.Name });
                var id = notebook.Id;
                check.Click += (_, _) => Services.Store.UpdateNotebook(id, n => n.ScanHomework = check.IsChecked == true);
                list.Children.Add(check);
            }
        }
        group.Children.Add(Toggle("Hausaufgaben automatisch erkennen", Keys.AutoHomework, false, _ => Fill()));
        group.Children.Add(list);
        Fill();
    }

    // MARK: - iPad

    private void BuildPad()
    {
        var group = Group("Lernheft Pad", "Das iPad dient nur zum Zeichnen. Notizen, Einstellungen und KI liegen hier auf dem Surface.");
        var connected = Services.Bridge.Connected;
        group.Children.Add(Ui.Text(connected ? $"Verbunden mit {Services.Bridge.DeviceName}." : "Gerade ist kein iPad verbunden.",
            14, FontWeights.SemiBold, connected ? "Good" : "Muted"));
        var pair = Ui.Button("iPad verbinden (QR-Code / Code) …", (_, _) =>
        {
            new PairingWindow { Owner = this }.ShowDialog();
            Build();
        }, "Primary", Ui.GlyphQr);
        pair.HorizontalAlignment = HorizontalAlignment.Left;
        pair.Margin = new Thickness(0, 12, 0, 0);
        group.Children.Add(pair);

        var devices = Group("Gekoppelte iPads");
        var paired = Services.Pad.PairedDevices;
        if (paired.Count == 0) devices.Children.Add(Ui.Text("Noch keins.", 13, color: "Faint"));
        foreach (var device in paired)
        {
            var remove = Ui.Button("Entfernen", (_, _) =>
            {
                Services.Pad.Forget(device.DeviceId);
                Build();
            });
            var dock = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            DockPanel.SetDock(remove, Dock.Right);
            dock.Children.Add(remove);
            var info = new StackPanel();
            info.Children.Add(Ui.Text(device.Name, 14, FontWeights.SemiBold));
            info.Children.Add(Ui.Text("zuletzt " + AppleTime.ToDateTime(device.LastSeen).ToString("dd.MM.yyyy HH:mm"), 12, color: "Faint"));
            dock.Children.Add(info);
            devices.Children.Add(dock);
        }

        var network = Group("Netzwerk", "Ohne Freigabe verbindet sich das Surface von sich aus mit dem iPad. Mit Freigabe geht es etwas schneller.");
        var name = Ui.Field(Services.Pad.ServerName);
        name.TextChanged += (_, _) =>
        {
            var value = name.Text.Trim();
            if (value.Length == 0) return;
            S.Set(Keys.ServerName, value);
            Services.Pad.ServerName = value;
        };
        network.Children.Add(FieldLabel("Name dieses Surface (so erscheint es auf dem iPad)", first: true));
        network.Children.Add(name);
        var addresses = PadServer.LocalAddresses();
        network.Children.Add(Ui.Hint((addresses.Count == 0 ? "Gerade in keinem Netz." : "Adressen: " + string.Join(", ", addresses)) +
                                     $" · Port {Services.Pad.Port}" + (Services.Pad.Listening ? "" : " – belegt, das Surface wählt sich selbst ein")));
        var status = Status();
        var firewall = Ui.Button(Firewall.RuleExists() ? "Firewall-Freigabe ist eingerichtet" : "Firewall-Freigabe einrichten", (_, _) =>
        {
            Say(status, Firewall.AddRule() ? "Freigabe eingerichtet." : "Windows hat die Freigabe nicht erlaubt – das ist nicht schlimm.", "Muted");
        }, icon: Ui.GlyphConnect);
        firewall.HorizontalAlignment = HorizontalAlignment.Left;
        firewall.Margin = new Thickness(0, 12, 0, 0);
        network.Children.Add(firewall);
        network.Children.Add(status);
    }

    // MARK: - Sicherung

    private void BuildBackup()
    {
        var backup = Group("Sicherung",
            "Die Sicherung enthält Fächer, Notizen mit Text, Handschrift und Bildern, Karteikarten, Hausaufgaben, Stundenplan und Einstellungen. " +
            "Mit eingeschaltetem Schalter steht auch dein API-Schlüssel im Klartext darin – gib die Datei dann niemandem weiter.");
        backup.Children.Add(Toggle("API-Schlüssel mitsichern", Keys.BackupIncludesKey, true));
        var status = Status();
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var create = Ui.Button("Sicherung erstellen …", (_, _) =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Sicherung speichern",
                FileName = $"Lernheft-Studio-Sicherung-{DateTime.Today:yyyy-MM-dd}.json",
                Filter = "Lernheft-Sicherung|*.json",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                Backup.Write(dialog.FileName, Services.Store, S, S.GetBool(Keys.BackupIncludesKey, true));
                Say(status, "Sicherung gespeichert: " + Path.GetFileName(dialog.FileName), "Good");
            }
            catch (Exception error)
            {
                Say(status, "Sicherung fehlgeschlagen: " + error.Message, "Bad");
            }
        }, icon: Ui.GlyphUpload);
        create.Margin = new Thickness(0, 0, 8, 8);
        var restore = Ui.Button("Sicherung einlesen …", (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Sicherung einlesen", Filter = "Lernheft-Sicherung|*.json" };
            if (dialog.ShowDialog(this) != true) return;
            var choice = Dialogs.Choose(this, "Sicherung einlesen",
                "Zu den vorhandenen Notizen hinzufügen – oder alles hier durch die Sicherung ersetzen?", "Hinzufügen", "Alles ersetzen", true);
            if (choice == 0) return;
            try
            {
                var result = Backup.Restore(dialog.FileName, Services.Store, S, replace: choice == 2);
                Say(status, $"{Ui.Plural(result.Notes, "Notiz", "Notizen")} wiederhergestellt." +
                            (result.WithoutInk > 0 ? $" Bei {result.WithoutInk} fehlt die Handschrift (altes iPad-Format) – übertrage sie direkt vom iPad." : ""), "Good");
                MainWindow.Current?.RefreshAll();
            }
            catch (Exception error)
            {
                Say(status, "Die Datei konnte nicht gelesen werden: " + error.Message, "Bad");
            }
        }, icon: Ui.GlyphDownload);
        restore.Margin = new Thickness(0, 0, 8, 8);
        var pdfs = Ui.Button("Alle Notizen als PDF …", async (_, _) =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Alle Notizen als PDF",
                FileName = $"Lernheft-{DateTime.Today:yyyy-MM-dd}.pdf",
                Filter = "PDF|*.pdf"
            };
            if (dialog.ShowDialog(this) != true) return;
            Say(status, "Erstelle das PDF …", "Muted");
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            try
            {
                var notes = Services.Store.Library.Notebooks
                    .SelectMany(book => Services.Store.NotesIn(book.Id).OrderBy(n => n.Created))
                    .ToList();
                PdfExport.Write(dialog.FileName, notes);
                Say(status, $"{Ui.Plural(notes.Count, "Notiz", "Notizen")} im PDF: " + Path.GetFileName(dialog.FileName), "Good");
            }
            catch (Exception error)
            {
                Say(status, "PDF fehlgeschlagen: " + error.Message, "Bad");
            }
        }, icon: Ui.GlyphDocument);
        pdfs.Margin = new Thickness(0, 0, 8, 8);
        row.Children.Add(create);
        row.Children.Add(restore);
        row.Children.Add(pdfs);
        backup.Children.Add(row);
        backup.Children.Add(status);

        var move = Group("Alte Notizen übernehmen",
            "Holt alles aus der bisherigen Lernheft-App herüber: Fächer, Notizen, Handschrift, eingescannte Seiten, Hausaufgaben, " +
            "Karteikarten und Stundenplan. Schon übernommene Notizen werden übersprungen.");
        var open = Ui.Button("Umzug starten …", (_, _) => new LegacyImportWindow { Owner = this }.ShowDialog(), "Primary", Ui.GlyphDownload);
        open.HorizontalAlignment = HorizontalAlignment.Left;
        move.Children.Add(open);

        var folder = Group("Datenordner");
        folder.Children.Add(Ui.Text(Services.DataRoot, 12.5, color: "Muted", wrap: true));
        var reveal = Ui.Button("Im Explorer zeigen", (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Services.DataRoot}\"") { UseShellExecute = true }),
            icon: Ui.GlyphFolder);
        reveal.HorizontalAlignment = HorizontalAlignment.Left;
        reveal.Margin = new Thickness(0, 10, 0, 0);
        folder.Children.Add(reveal);
    }

    // MARK: - Verbrauch

    private void BuildUsage()
    {
        var usage = Services.Usage;
        var budget = S.GetDouble(Keys.BudgetEur, 10);
        var spent = usage.EstimatedCostEur;
        var left = Math.Max(0, budget - spent);
        var money = new CultureInfo("de-DE");

        var credit = Group("Guthaben",
            "Das ist eine Schätzung aus deinen Token und den Preisen der Modelle – Google gibt den echten Kontostand nicht an Apps heraus." +
            (usage.HasGuessedPrice ? " Für mindestens ein benutztes Modell kenne ich den Preis nicht genau; trag ihn unten selbst ein." : ""));
        var bar = new ProgressBar { Maximum = Math.Max(budget, 0.01), Value = Math.Min(spent, budget), Height = 8, BorderThickness = new Thickness(0), Margin = new Thickness(0, 4, 0, 8) };
        bar.SetResourceReference(ForegroundProperty, left < budget * 0.1 ? "Bad" : "Good");
        bar.SetResourceReference(BackgroundProperty, "SurfaceAlt");
        var labels = new DockPanel();
        var leftText = Ui.Text(left.ToString("C", money) + " übrig", 13, color: left < budget * 0.1 ? "Bad" : "Muted");
        DockPanel.SetDock(leftText, Dock.Right);
        labels.Children.Add(leftText);
        labels.Children.Add(Ui.Text(spent.ToString("C", money) + " verbraucht", 13));
        credit.Children.Add(labels);
        credit.Children.Add(bar);
        if (usage.RemainingDays(budget) is int days && days > 0)
            credit.Children.Add(Ui.Text("Reicht beim bisherigen Tempo noch " + (days > 900 ? "über 2 Jahre" : $"{days} Tage"), 13, color: "Muted"));
        credit.Children.Add(FieldLabel("Guthaben in Euro"));
        var budgetBox = NumberField(budget, value => S.SetDouble(Keys.BudgetEur, Math.Clamp(value, 1, 500)));
        credit.Children.Add(budgetBox);
        var billing = Ui.Button("Echte Abrechnung bei Google öffnen", (_, _) => OpenUrl("https://console.cloud.google.com/billing"), icon: Ui.GlyphLink);
        billing.HorizontalAlignment = HorizontalAlignment.Left;
        billing.Margin = new Thickness(0, 12, 0, 0);
        credit.Children.Add(billing);

        var counts = Group("Verbrauch", "Im kostenlosen Kontingent kosten diese Token nichts, es gibt nur Grenzen pro Minute und pro Tag. " +
                                        "Die Zahlen helfen dir zu entscheiden, ob sich das bezahlte Kontingent lohnt.");
        counts.Children.Add(Pair("Anfragen", usage.Requests.ToString("N0", money)));
        counts.Children.Add(Pair("Token hinein", Tokens(usage.InputTokens)));
        counts.Children.Add(Pair("Token hinaus", Tokens(usage.OutputTokens)));
        if (usage.ProjectedYearTokens is long projected) counts.Children.Add(Pair("Hochrechnung Schuljahr", Tokens(projected)));
        foreach (var (model, values) in usage.ByModel.OrderBy(e => e.Key))
        {
            counts.Children.Add(Pair(model, $"{(values.Length > 2 ? values[2] : 0)}×, {Tokens((values.Length > 0 ? values[0] : 0) + (values.Length > 1 ? values[1] : 0))}", small: true));
        }
        if (usage.Since is DateTime since) counts.Children.Add(Ui.Hint("gezählt seit " + since.ToString("dd.MM.yyyy HH:mm")));
        var reset = Ui.Button("Zähler zurücksetzen", (_, _) =>
        {
            if (!Dialogs.Confirm(this, "Zähler zurücksetzen?", "Alle gezählten Anfragen und Token werden auf null gesetzt.", "Zurücksetzen")) return;
            usage.Reset();
            Build();
        });
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Margin = new Thickness(0, 12, 0, 0);
        counts.Children.Add(reset);

        var prices = Group("Preise selbst eintragen", "0 bedeutet: Preis automatisch nach Modell.");
        prices.Children.Add(FieldLabel("US-Dollar je 1 Mio. Token hinein", first: true));
        prices.Children.Add(NumberField(S.GetDouble(Keys.PriceInput, 0), value => S.SetDouble(Keys.PriceInput, Math.Max(0, value))));
        prices.Children.Add(FieldLabel("US-Dollar je 1 Mio. Token hinaus"));
        prices.Children.Add(NumberField(S.GetDouble(Keys.PriceOutput, 0), value => S.SetDouble(Keys.PriceOutput, Math.Max(0, value))));
        prices.Children.Add(FieldLabel("1 US-Dollar in Euro"));
        prices.Children.Add(NumberField(S.GetDouble(Keys.UsdToEur, 0.92), value => S.SetDouble(Keys.UsdToEur, value > 0 ? value : 0.92)));
    }

    private static TextBox NumberField(double value, Action<double> changed)
    {
        var box = Ui.Field(value.ToString("0.##", new CultureInfo("de-DE")));
        box.Width = 140;
        box.HorizontalAlignment = HorizontalAlignment.Left;
        box.TextChanged += (_, _) =>
        {
            if (double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) changed(number);
        };
        return box;
    }

    private static UIElement Pair(string label, string value, bool small = false)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var right = Ui.Text(value, small ? 12.5 : 13.5, color: "Muted");
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(Ui.Text(label, small ? 12.5 : 13.5, color: small ? "Muted" : "Text"));
        return dock;
    }

    private static string Tokens(long value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000.0).ToString("0.0", new CultureInfo("de-DE")) + " Mio.",
        >= 1_000 => (value / 1_000.0).ToString("0", new CultureInfo("de-DE")) + " Tsd.",
        _ => value.ToString(CultureInfo.InvariantCulture)
    };

    // MARK: - Über

    private void BuildAbout()
    {
        var about = Group("Lernheft Studio");
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        about.Children.Add(Pair("Version", $"{version?.Major}.{version?.Minor}.{version?.Build}"));
        about.Children.Add(Pair("Notizen", Services.Store.Library.Notes.Count.ToString()));
        about.Children.Add(Pair("Fächer", Services.Store.Library.Notebooks.Count.ToString()));
        about.Children.Add(Ui.Hint("Deine Notizen liegen nur auf diesem Surface. Mach ab und zu eine Sicherung (Sicherung & Umzug)."));
        var log = Ui.Button("Protokoll öffnen", (_, _) =>
        {
            if (File.Exists(Services.LogPath)) Process.Start(new ProcessStartInfo(Services.LogPath) { UseShellExecute = true });
        }, icon: Ui.GlyphDocument);
        log.HorizontalAlignment = HorizontalAlignment.Left;
        log.Margin = new Thickness(0, 12, 0, 0);
        log.IsEnabled = File.Exists(Services.LogPath);
        about.Children.Add(log);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception) { }
    }
}
