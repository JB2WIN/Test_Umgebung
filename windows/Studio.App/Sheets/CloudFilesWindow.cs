using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lernheft.Studio.App.Editor;

namespace Lernheft.Studio.App.Sheets;

/// <summary>
/// Dateien aus iCloud Drive (über „iCloud für Windows“): zuletzt geänderte Dateien, Ordner zum
/// Durchklicken und eine Suche. Ein Doppelklick fügt die Datei in die Notiz ein.
/// </summary>
public sealed class CloudFilesWindow : SheetWindow
{
    private readonly string _target;
    private readonly TextBox _search;
    private readonly ListBox _list = new() { Style = Ui.Style("PlainList"), MinHeight = 200 };
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _crumbs = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel _browser = new();
    private readonly Button _insert;
    private readonly DispatcherTimer _searchTimer;
    private string? _root;
    private string? _folder;
    private bool _recentMode = true;
    private int _generation;

    /// <summary>Die gewählte Datei (Pfad im iCloud-Ordner).</summary>
    public string? ChosenPath { get; private set; }

    /// <param name="target">Wohin eingefügt wird – steht unten im Fenster, z. B. der Titel der Notiz.</param>
    public CloudFilesWindow(string target) : base("iCloud Drive", 760, 760, footer: true)
    {
        _target = target;
        var change = Ui.Button("Ordner ändern …", (_, _) => ChooseFolder(), icon: Ui.GlyphFolder);
        HeaderRight.Children.Add(change);

        _search = Ui.Field("", "In iCloud Drive suchen");
        _search.Margin = new Thickness(0, 0, 0, 10);
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            Reload();
        };
        _search.TextChanged += (_, _) =>
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        };

        var modes = Ui.Segments(new[] { "Zuletzt geändert", "Ordner" }, 0, index =>
        {
            _recentMode = index == 0;
            if (!_recentMode) _folder ??= _root;
            _search.Text = "";
            Reload();
        });
        modes.Margin = new Thickness(0, 0, 0, 10);
        modes.HorizontalAlignment = HorizontalAlignment.Left;

        _list.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(_list, source) is ListBoxItem) OpenSelected();
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                OpenSelected();
            }
            else if (e.Key == Key.Back && !_recentMode)
            {
                e.Handled = true;
                GoUp();
            }
        };
        _list.SelectionChanged += (_, _) => UpdateInsert();

        _browser.Children.Add(_search);
        _browser.Children.Add(modes);
        _browser.Children.Add(_crumbs);
        _browser.Children.Add(_status);
        _browser.Children.Add(_list);
        Body.Children.Add(_browser);

        var where = Ui.Text("Einfügen in: " + target, 12.5, color: "Muted");
        where.VerticalAlignment = VerticalAlignment.Center;
        where.MaxWidth = 360;
        DockPanel.SetDock(where, Dock.Left);
        Footer.Children.Add(where);
        _insert = AddFooterButton("Einfügen", OpenSelected, "Primary", isDefault: true);
        AddFooterButton("Abbrechen", () => DialogResult = false, isCancel: true);

        Loaded += (_, _) =>
        {
            Locate();
            _search.Focus();
        };
    }

    // MARK: - Ordner finden

    private void Locate()
    {
        _root = CloudDrive.FindFolder(Services.Settings.Get(Keys.ICloudFolder, ""));
        _folder = _root;
        if (_root is null)
        {
            ShowMissing();
            return;
        }
        _browser.Visibility = Visibility.Visible;
        Reload();
    }

    private void ShowMissing()
    {
        _browser.Visibility = Visibility.Collapsed;
        foreach (var old in Body.Children.OfType<Border>().ToList()) Body.Children.Remove(old);
        var panel = new StackPanel();
        panel.Children.Add(Ui.CardHeader("iCloud Drive ist auf diesem Surface noch nicht da", Ui.GlyphCloud));
        panel.Children.Add(Ui.Text(
            "Lernheft Studio zeigt deine iCloud-Dateien über die kostenlose App „iCloud für Windows“ von Apple. " +
            "Einmal installieren, mit deiner Apple-ID anmelden und „iCloud Drive“ anhaken – danach erscheinen deine " +
            "Dateien hier von selbst. Dateien, die nur in der Cloud liegen, lädt Windows beim Einfügen automatisch.",
            13.5, color: "Muted", wrap: true, margin: new Thickness(0, 0, 0, 14)));
        var row = new WrapPanel();
        var store = Ui.Button("iCloud für Windows holen", (_, _) => OpenStore(), "Primary", Ui.GlyphDownload);
        store.Margin = new Thickness(0, 0, 8, 8);
        var again = Ui.Button("Nochmal suchen", (_, _) => Locate(), icon: Ui.GlyphRefresh);
        again.Margin = new Thickness(0, 0, 8, 8);
        var pick = Ui.Button("Ordner selbst wählen …", (_, _) => ChooseFolder(), icon: Ui.GlyphFolder);
        pick.Margin = new Thickness(0, 0, 8, 8);
        row.Children.Add(store);
        row.Children.Add(again);
        row.Children.Add(pick);
        panel.Children.Add(row);
        Body.Children.Add(Ui.Card(panel));
        UpdateInsert();
    }

    private static void OpenStore()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-windows-store://pdp/?productid=9PKTQ5699M62") { UseShellExecute = true });
        }
        catch (Exception)
        {
            Process.Start(new ProcessStartInfo("https://apps.microsoft.com/detail/9pktq5699m62") { UseShellExecute = true });
        }
    }

    private void ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Ordner von iCloud Drive wählen" };
        if (_root is not null) dialog.InitialDirectory = _root;
        if (dialog.ShowDialog(this) != true) return;
        Services.Settings.Set(Keys.ICloudFolder, dialog.FolderName);
        foreach (var old in Body.Children.OfType<Border>().ToList()) Body.Children.Remove(old);
        Locate();
    }

    // MARK: - Liste

    private void Reload()
    {
        if (_root is null) return;
        var generation = ++_generation;
        var root = _root;
        var folder = _folder ?? root;
        var query = _search.Text.Trim();
        var recent = _recentMode;
        UpdateCrumbs(query.Length == 0 && !recent);
        Say(query.Length > 0 ? "Suche …" : recent ? "Suche die neuesten Dateien …" : "", "Muted");
        _list.Items.Clear();
        UpdateInsert();
        Task.Run(() => query.Length > 0 ? CloudDrive.Search(root, query)
                : recent ? CloudDrive.Recent(root)
                : CloudDrive.List(folder))
            .ContinueWith(task => Dispatcher.BeginInvoke(() =>
            {
                if (generation != _generation) return;
                if (task.IsFaulted)
                {
                    Say("iCloud Drive ließ sich nicht lesen: " + task.Exception?.GetBaseException().Message, "Bad");
                    return;
                }
                Show(task.Result, showFolder: query.Length > 0 || recent);
                if (task.Result.Count == 0)
                {
                    Say(query.Length > 0 ? $"Nichts gefunden für „{query}“."
                        : recent ? "In iCloud Drive liegen noch keine PDFs, Bilder oder Texte."
                        : "Dieser Ordner enthält nichts, was sich einfügen lässt.", "Muted");
                }
                else Say("", "Muted");
            }));
    }

    private void Show(List<CloudDrive.Entry> entries, bool showFolder)
    {
        _list.Items.Clear();
        foreach (var entry in entries) _list.Items.Add(Row(entry, showFolder));
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private ListBoxItem Row(CloudDrive.Entry entry, bool showFolder)
    {
        var icon = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 12, 0),
            Child = Ui.Icon(Glyph(entry.Kind), 18, entry.Kind == CloudDrive.Kind.Folder ? "Accent" : "Muted"),
            ClipToBounds = true
        };
        icon.SetResourceReference(Border.BackgroundProperty, entry.Kind == CloudDrive.Kind.Folder ? "AccentSoft" : "SurfaceAlt");
        if (entry.Kind == CloudDrive.Kind.Image && !entry.OnlyInCloud && entry.Size < 30_000_000)
        {
            // Vorschau nur für Dateien, die schon auf dem Surface liegen – sonst würde Windows alles herunterladen.
            var path = entry.Path;
            Dispatcher.BeginInvoke(() =>
            {
                var preview = ImageTools.Load(path, 96);
                if (preview is not null) icon.Child = new Image { Source = preview, Stretch = Stretch.UniformToFill };
            }, DispatcherPriority.Background);
        }

        var name = Ui.Text(entry.Name, 13.5, FontWeights.SemiBold);
        var details = new List<string>();
        if (showFolder)
        {
            var folder = Path.GetDirectoryName(CloudDrive.Relative(_root!, entry.Path)) ?? "";
            details.Add(folder.Length == 0 ? "iCloud Drive" : folder.Replace(Path.DirectorySeparatorChar, '›').Replace("›", " › "));
        }
        details.Add(When(entry.Modified));
        if (entry.Kind != CloudDrive.Kind.Folder) details.Add(Size(entry.Size));
        var detail = Ui.Text(string.Join("  ·  ", details), 12, color: "Faint", margin: new Thickness(0, 2, 0, 0));

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(detail);

        var row = new DockPanel { Margin = new Thickness(4, 5, 4, 5) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        if (entry.OnlyInCloud)
        {
            var cloud = Ui.Icon(Ui.GlyphCloud, 14, "Faint");
            cloud.ToolTip = "Liegt nur in iCloud – wird beim Einfügen geladen";
            cloud.Margin = new Thickness(10, 0, 4, 0);
            DockPanel.SetDock(cloud, Dock.Right);
            row.Children.Add(cloud);
        }
        else if (entry.Kind == CloudDrive.Kind.Folder)
        {
            var chevron = Ui.Icon(Ui.GlyphChevronRight, 11, "Faint");
            chevron.Margin = new Thickness(10, 0, 4, 0);
            DockPanel.SetDock(chevron, Dock.Right);
            row.Children.Add(chevron);
        }
        row.Children.Add(text);
        return new ListBoxItem { Content = row, Tag = entry };
    }

    private static string Glyph(CloudDrive.Kind kind) => kind switch
    {
        CloudDrive.Kind.Folder => Ui.GlyphFolder,
        CloudDrive.Kind.Pdf => Ui.GlyphDocument,
        CloudDrive.Kind.Image => Ui.GlyphPicture,
        CloudDrive.Kind.Drawing => Ui.GlyphEdit,
        _ => Ui.GlyphPage
    };

    private static string When(DateTime time)
    {
        var german = new CultureInfo("de-DE");
        if (time.Date == DateTime.Today) return "heute, " + time.ToString("HH:mm", german);
        if (time.Date == DateTime.Today.AddDays(-1)) return "gestern, " + time.ToString("HH:mm", german);
        return time.Year == DateTime.Today.Year ? time.ToString("d. MMM", german) : time.ToString("d. MMM yyyy", german);
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Byte",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB"
    };

    private void UpdateCrumbs(bool visible)
    {
        _crumbs.Children.Clear();
        _crumbs.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible || _root is null) return;
        var parts = new List<(string Name, string Path)> { ("iCloud Drive", _root) };
        var relative = CloudDrive.Relative(_root, _folder ?? _root);
        var current = _root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            parts.Add((part, current));
        }
        for (var i = 0; i < parts.Count; i++)
        {
            var (name, path) = parts[i];
            if (i > 0)
            {
                var separator = Ui.Icon(Ui.GlyphChevronRight, 9, "Faint");
                separator.Margin = new Thickness(6, 0, 6, 0);
                _crumbs.Children.Add(separator);
            }
            var last = i == parts.Count - 1;
            var crumb = Ui.Button(name, (_, _) =>
            {
                _folder = path;
                Reload();
            }, last ? "Soft" : "Ghost");
            crumb.IsEnabled = !last;
            _crumbs.Children.Add(crumb);
        }
    }

    private void GoUp()
    {
        if (_root is null || _folder is null || _folder == _root) return;
        _folder = Path.GetDirectoryName(_folder) ?? _root;
        Reload();
    }

    private void OpenSelected()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: CloudDrive.Entry entry }) return;
        if (entry.Kind == CloudDrive.Kind.Folder)
        {
            _recentMode = false;
            _search.Text = "";
            _folder = entry.Path;
            SelectFolderMode();
            Reload();
            return;
        }
        ChosenPath = entry.Path;
        DialogResult = true;
    }

    private void SelectFolderMode()
    {
        // Umschalter auf „Ordner“ stellen, ohne erneut zu laden.
        if (_browser.Children.OfType<Border>().FirstOrDefault() is { } segments)
        {
            foreach (var radio in FindRadios(segments)) radio.IsChecked = (radio.Content as string ?? (radio.Content as TextBlock)?.Text) == "Ordner";
        }
    }

    private static IEnumerable<RadioButton> FindRadios(DependencyObject parent)
    {
        if (parent is RadioButton radio) yield return radio;
        if (parent is Panel panel)
        {
            foreach (UIElement child in panel.Children)
                foreach (var found in FindRadios(child)) yield return found;
        }
        else if (parent is Decorator { Child: { } inner })
        {
            foreach (var found in FindRadios(inner)) yield return found;
        }
    }

    private void UpdateInsert()
    {
        var entry = (_list.SelectedItem as ListBoxItem)?.Tag as CloudDrive.Entry;
        _insert.IsEnabled = entry is not null;
        _insert.Content = entry?.Kind == CloudDrive.Kind.Folder ? "Öffnen" : "Einfügen";
    }

    private void Say(string text, string color)
    {
        _status.Text = text;
        _status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _status.Margin = new Thickness(0, 0, 0, text.Length == 0 ? 0 : 8);
        _status.SetResourceReference(TextBlock.ForegroundProperty, color);
    }
}
