using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Lernheft.Studio.App.Sheets;

/// <summary>
/// Umzug aus der alten Lernheft-App. Vier Wege, je nachdem, wo die Notizen liegen: auf diesem
/// Surface, auf dem iPad, auf dem alten Sync-Server oder in einem Ordner bzw. einer Sicherung.
/// </summary>
public sealed class LegacyImportWindow : SheetWindow
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly Border _statusCard;

    public LegacyImportWindow() : base("Alte Notizen übernehmen", 720, 820)
    {
        Body.Children.Add(Ui.Text("Alles aus der bisherigen Lernheft-App kommt herüber: Fächer, Notizen, Handschrift, eingescannte Seiten, " +
                                  "Hausaufgaben, Karteikarten und Stundenplan. Getippter Text wird zu Textfeldern. Was schon da ist, " +
                                  "wird übersprungen – du kannst den Umzug also gefahrlos wiederholen.", 13.5, color: "Muted", wrap: true,
            margin: new Thickness(0, 0, 0, 16)));

        _statusCard = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 14), Child = _status, Visibility = Visibility.Collapsed };
        Body.Children.Add(_statusCard);

        Body.Children.Add(SurfaceCard());
        Body.Children.Add(PadCard());
        Body.Children.Add(ServerCard());
        Body.Children.Add(FolderCard());
    }

    private void Say(string text, string background = "AccentSoft", string foreground = "Text")
    {
        _statusCard.Visibility = Visibility.Visible;
        _statusCard.SetResourceReference(Border.BackgroundProperty, background);
        _status.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        _status.Text = text;
        Scroller.ScrollToTop();
    }

    private void Done(LegacyImport.Report report)
    {
        var text = report.Summary;
        if (report.Problems.Count > 0) text += "\n\nProbleme:\n" + string.Join("\n", report.Problems.Take(8));
        Say(text, report.Notes > 0 ? "GoodSoft" : "AccentSoft");
        MainWindow.Current?.RefreshAll();
        Services.Bridge.LibraryUpdated();
    }

    private UIElement SurfaceCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.CardHeader("Von diesem Surface", Ui.GlyphFolder));
        var folder = LegacyImport.OldWindowsFolder;
        var exists = LegacyImport.LooksLikeLegacyFolder(folder);
        var fresh = exists ? LegacyImport.CountNew(folder, Services.Store) : 0;
        panel.Children.Add(Ui.Text(exists
                ? $"Die alte Windows-App hat hier {Ui.Plural(LegacyImport.CountNotes(folder), "Notiz", "Notizen")} gespeichert" +
                  (fresh == 0 ? " – alle sind schon übernommen." : $", davon {fresh} noch nicht übernommen.") +
                  " Wenn du vorher mit dem iPad abgeglichen hast, ist die Handschrift vom iPad mit dabei."
                : "Auf diesem Surface habe ich keine Daten der alten Lernheft-App gefunden.",
            13.5, color: "Muted", wrap: true, margin: new Thickness(0, 0, 0, 12)));
        var button = Ui.Button("Übernehmen", (_, _) => Run(() => LegacyImport.ImportFolder(folder, Services.Store)), "Primary", Ui.GlyphDownload);
        button.IsEnabled = exists && fresh > 0;
        button.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(button);
        return Ui.Card(panel);
    }

    private UIElement PadCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.CardHeader("Vom iPad (empfohlen für Handschrift)", Ui.GlyphTablet));
        panel.Children.Add(Ui.Text("Auf dem iPad liegen die Originale deiner Handschrift. Lernheft Pad liest sie aus der alten App und schickt alles hierher:",
            13.5, color: "Muted", wrap: true, margin: new Thickness(0, 0, 0, 8)));
        panel.Children.Add(Ui.Text("1.  iPad mit diesem Surface verbinden.\n2.  In Lernheft Pad: Einstellungen → „Alte Notizen übertragen“.\n" +
                                   "3.  Den Ordner „Lernheft“ auswählen (Auf meinem iPad → Lernheft) – fertig.", 13.5, wrap: true,
            margin: new Thickness(0, 0, 0, 12)));
        var connected = Services.Bridge.Connected;
        panel.Children.Add(Ui.Text(connected ? $"Verbunden mit {Services.Bridge.DeviceName} – du kannst auf dem iPad loslegen." : "Gerade ist kein iPad verbunden.",
            13, FontWeights.SemiBold, connected ? "Good" : "Faint", margin: new Thickness(0, 0, 0, 10)));
        if (!connected)
        {
            var pair = Ui.Button("iPad verbinden …", (_, _) =>
            {
                new PairingWindow { Owner = this }.ShowDialog();
            }, icon: Ui.GlyphQr);
            pair.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(pair);
        }
        return Ui.Card(panel);
    }

    private UIElement ServerCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.CardHeader("Vom alten Lernheft-Server", Ui.GlyphSync));
        panel.Children.Add(Ui.Text("Falls der Sync-Server auf dem PC noch läuft: Adresse und Zugangsschlüssel wie früher eintragen.",
            13.5, color: "Muted", wrap: true, margin: new Thickness(0, 0, 0, 8)));
        var address = Ui.Field("", "z. B. http://192.168.1.31:5199 oder https://…ngrok-free.app");
        var token = new PasswordBox();
        panel.Children.Add(Ui.Text("Adresse", 12, FontWeights.SemiBold, "Muted", margin: new Thickness(0, 4, 0, 6)));
        panel.Children.Add(address);
        panel.Children.Add(Ui.Text("Zugangsschlüssel", 12, FontWeights.SemiBold, "Muted", margin: new Thickness(0, 12, 0, 6)));
        panel.Children.Add(token);
        var button = Ui.Button("Laden und übernehmen", null, "Primary", Ui.GlyphDownload);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(0, 12, 0, 0);
        button.Click += async (_, _) =>
        {
            if (address.Text.Trim().Length == 0 || token.Password.Trim().Length == 0)
            {
                Say("Trag Adresse und Zugangsschlüssel ein.", "WarnSoft");
                return;
            }
            button.IsEnabled = false;
            try
            {
                var progress = new Progress<string>(text => Say(text));
                var folder = await LegacyImport.DownloadFromServerAsync(address.Text, token.Password, progress);
                Run(() => LegacyImport.ImportFolder(folder, Services.Store));
                try { Directory.Delete(folder, true); }
                catch (IOException) { }
            }
            catch (Exception error)
            {
                Say("Der Server ist nicht erreichbar: " + error.Message, "BadSoft");
            }
            button.IsEnabled = true;
        };
        panel.Children.Add(button);
        return Ui.Card(panel);
    }

    private UIElement FolderCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.CardHeader("Aus einem Ordner oder einer Sicherung", Ui.GlyphDocument));
        panel.Children.Add(Ui.Text("Ein kopierter „Lernheft“-Ordner (mit library.json) oder eine Sicherungsdatei der alten App. " +
                                   "In Sicherungen vom iPad steckt die Handschrift nur im Apple-Format – dafür besser den Weg über das iPad nehmen.",
            13.5, color: "Muted", wrap: true, margin: new Thickness(0, 0, 0, 12)));
        var row = new WrapPanel();
        var folder = Ui.Button("Ordner wählen …", (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Ordner der alten Lernheft-App" };
            if (dialog.ShowDialog(this) != true) return;
            if (!LegacyImport.LooksLikeLegacyFolder(dialog.FolderName))
            {
                Say("In diesem Ordner liegt keine library.json mit Notizen-Ordner daneben.", "WarnSoft");
                return;
            }
            Run(() => LegacyImport.ImportFolder(dialog.FolderName, Services.Store));
        }, icon: Ui.GlyphFolder);
        folder.Margin = new Thickness(0, 0, 8, 8);
        var file = Ui.Button("Sicherung wählen …", (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Sicherung der alten App", Filter = "Lernheft-Sicherung|*.json" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var result = Backup.Restore(dialog.FileName, Services.Store, Services.Settings, replace: false);
                Say($"{Ui.Plural(result.Notes, "Notiz", "Notizen")} übernommen." +
                    (result.WithoutInk > 0 ? $" Bei {result.WithoutInk} fehlt die Handschrift – übertrage sie direkt vom iPad." : ""), "GoodSoft");
                MainWindow.Current?.RefreshAll();
            }
            catch (Exception error)
            {
                Say("Die Datei konnte nicht gelesen werden: " + error.Message, "BadSoft");
            }
        }, icon: Ui.GlyphDocument);
        file.Margin = new Thickness(0, 0, 8, 8);
        row.Children.Add(folder);
        row.Children.Add(file);
        panel.Children.Add(row);
        return Ui.Card(panel);
    }

    private void Run(Func<LegacyImport.Report> work)
    {
        try
        {
            Say("Übernehme …");
            Done(work());
        }
        catch (Exception error)
        {
            Say("Das hat nicht geklappt: " + error.Message, "BadSoft");
        }
    }
}
