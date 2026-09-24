using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lernheft.Studio.App.Editor;
using Lernheft.Studio.App.Sheets;

namespace Lernheft.Studio.App;

/// <summary>
/// Das Hauptfenster: links Orte und Fächer, daneben die Notizen, rechts Übersicht oder Notiz.
/// </summary>
public sealed class MainWindow : Window
{
    public static MainWindow? Current { get; private set; }

    public static ImageSource? AppIcon { get; } = LoadIcon();

    private static ImageSource? LoadIcon()
    {
        try
        {
            return BitmapFrame.Create(new Uri("pack://application:,,,/app.ico", UriKind.Absolute));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private readonly ColumnDefinition _sidebarColumn = new() { Width = new GridLength(250) };
    private readonly ColumnDefinition _listColumn = new() { Width = new GridLength(300) };
    private readonly ListBox _places = new();
    private readonly ListBox _notebooks = new();
    private readonly ListBox _notes = new();
    private readonly TextBlock _listTitle = new();
    private readonly TextBox _search;
    private readonly TextBlock _emptyList = new();
    private readonly Border _padCard = new();
    private readonly ScrollViewer _homeScroller = new();
    private readonly StackPanel _homePanel = new() { Margin = new Thickness(34, 30, 34, 40), MaxWidth = 1040, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly NoteEditor _editor = new();
    private readonly Border _banner = new();
    private readonly TextBlock _bannerText = new();
    private readonly StackPanel _bannerButtons = new() { Orientation = Orientation.Horizontal };
    private readonly Grid _content = new();

    private Guid? _notebookId;
    private Guid? _openNoteId;
    private bool _listsHidden;
    private UIElement _sidebar = null!;
    private UIElement _list = null!;
    /// Im schmalen Fenster klappen die Listen beim Schreiben von selbst weg – außer man holt sie bewusst zurück.
    private bool _listsForced;

    private bool ListsCollapsed => _listsHidden || (!_listsForced && _openNoteId is not null && ActualWidth < 1120);
    private bool _updating;
    private BonjourAdvertiser? _bonjour;

    private record Place(string Key, string Glyph, string Title);

    public MainWindow()
    {
        Current = this;
        Title = "Lernheft Studio";
        Icon = AppIcon;
        Width = 1440;
        Height = 900;
        MinWidth = 820;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Ui.UiFont;
        FontSize = 13.5;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        SetResourceReference(BackgroundProperty, "Chrome");
        SetResourceReference(ForegroundProperty, "Text");
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);
        _search = Ui.Field("", "Alle Notizen durchsuchen");

        var root = new Grid();
        root.ColumnDefinitions.Add(_sidebarColumn);
        root.ColumnDefinitions.Add(_listColumn);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sidebar = BuildSidebar();
        _sidebar = sidebar;
        root.Children.Add(sidebar);
        var list = BuildList();
        _list = list;
        Grid.SetColumn(list, 1);
        root.Children.Add(list);
        BuildContent();
        Grid.SetColumn(_content, 2);
        root.Children.Add(_content);
        Content = root;

        _editor.ToggleLists += ToggleLists;
        _editor.MetaChanged += () =>
        {
            ReloadNotebooks();
            ReloadNotes();
        };
        _editor.Deleted += () =>
        {
            _openNoteId = null;
            ReloadNotebooks();
            ReloadNotes();
            ShowHome();
        };
        Services.Bridge.StatusChanged += UpdatePadCard;
        Services.Bridge.OpenRequested += id =>
        {
            Activate();
            OpenNote(id);
        };
        Services.Bridge.NewNoteRequested += id => NewNote(id);
        Services.Bridge.NewNoteForFileRequested += title =>
        {
            NewNote(null);
            if (_openNoteId is Guid created)
            {
                Services.Store.UpdateNote(created, n => n.Title = title);
                _editor.Open(created);
                ReloadNotes();
            }
        };
        Services.Bridge.LibraryChanged += RefreshAll;
        Services.Pad.PairingChanged += () => Dispatcher.BeginInvoke(UpdatePadCard);
        Services.Store.Changed += () =>
        {
            if (!IsLoaded || _openNoteId is not null) return;
            Dispatcher.BeginInvoke(RebuildHome, System.Windows.Threading.DispatcherPriority.Background);
        };
        Theme.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            ReloadNotebooks();
            ReloadNotes();
            if (_openNoteId is null) RebuildHome();
        });
        SettingsWindow.Applied += () => _editor.RefreshEnvironment();

        InputBindings.Add(new KeyBinding(new Command(() => NewNote(null)), Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Command(() => { if (ListsCollapsed) ToggleLists(); _search.Focus(); _search.SelectAll(); }), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Command(ToggleLists), Key.F11, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new Command(() => OpenSettings()), Key.OemComma, ModifierKeys.Control));

        SizeChanged += (_, _) => FitColumns();
        Loaded += (_, _) => Start();
        Closing += (_, _) =>
        {
            _editor.Leave();
            _bonjour?.Dispose();
        };

        ReloadPlaces();
        ReloadNotebooks();
        _places.SelectedIndex = 0;
        ShowHome();
    }

    private void Start()
    {
        Services.Pad.Start(Services.Settings.GetInt(Keys.PadPort, PadProtocol.StudioPort));
        _bonjour = new BonjourAdvertiser();
        if (Services.Pad.Listening) _bonjour.Start(Services.Pad.ServerName, Services.Pad.Port);
        UpdatePadCard();
        _ = TimetableSync.RefreshIfStaleAsync().ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            if (_openNoteId is null) RebuildHome();
        }));
        OfferLegacyImport();
    }

    // MARK: - Seitenleiste

    private UIElement BuildSidebar()
    {
        var border = new Border { BorderThickness = new Thickness(0, 0, 1, 0) };
        border.SetResourceReference(Border.BackgroundProperty, "Sidebar");
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        var dock = new DockPanel();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 18, 12, 14) };
        header.Children.Add(new Image { Source = AppIcon, Width = 30, Height = 30, Margin = new Thickness(0, 0, 11, 0) });
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = "Lernheft Studio", FontFamily = Ui.DisplayFont, FontSize = 17, FontWeight = FontWeights.SemiBold });
        header.Children.Add(name);
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);

        var bottom = new StackPanel { Margin = new Thickness(12, 8, 12, 14) };
        _padCard.CornerRadius = new CornerRadius(10);
        _padCard.Padding = new Thickness(12, 10, 12, 10);
        _padCard.Margin = new Thickness(0, 0, 0, 8);
        _padCard.Cursor = Cursors.Hand;
        _padCard.BorderThickness = new Thickness(1);
        _padCard.SetResourceReference(Border.BackgroundProperty, "Surface");
        _padCard.SetResourceReference(Border.BorderBrushProperty, "Line");
        _padCard.MouseLeftButtonUp += (_, _) =>
        {
            if (Services.Bridge.Connected) ShowPadMenu();
            else new PairingWindow { Owner = this }.ShowDialog();
        };
        bottom.Children.Add(_padCard);
        var settings = Ui.Button("Einstellungen", (_, _) => OpenSettings(), icon: Ui.GlyphSettings);
        settings.HorizontalAlignment = HorizontalAlignment.Stretch;
        settings.HorizontalContentAlignment = HorizontalAlignment.Left;
        settings.SetResourceReference(BackgroundProperty, "Sidebar");
        settings.BorderBrush = Brushes.Transparent;
        bottom.Children.Add(settings);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);

        var scroll = new StackPanel();
        _places.Style = Ui.Style("StaticList");
        _places.ItemContainerStyle = Ui.Style("SidebarItem");
        _places.Padding = new Thickness(10, 0, 10, 0);
        _places.SelectionChanged += PlaceChosen;
        scroll.Children.Add(_places);

        var section = new DockPanel { Margin = new Thickness(20, 18, 14, 4) };
        var add = Ui.IconButton(Ui.GlyphAdd, "Neues Fach", (_, _) => NewNotebook(), 12);
        add.Width = add.Height = 26;
        DockPanel.SetDock(add, Dock.Right);
        section.Children.Add(add);
        var label = Ui.Text("FÄCHER", 11, FontWeights.SemiBold, "Faint");
        label.VerticalAlignment = VerticalAlignment.Center;
        section.Children.Add(label);
        scroll.Children.Add(section);

        _notebooks.Style = Ui.Style("StaticList");
        _notebooks.ItemContainerStyle = Ui.Style("SidebarItem");
        _notebooks.Padding = new Thickness(10, 0, 10, 8);
        _notebooks.SelectionChanged += NotebookChosen;
        scroll.Children.Add(_notebooks);

        dock.Children.Add(new ScrollViewer { Content = scroll, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        border.Child = dock;
        return border;
    }

    private void ReloadPlaces()
    {
        _updating = true;
        var selected = (_places.SelectedItem as ListBoxItem)?.Tag as string;
        _places.Items.Clear();
        foreach (var place in new[]
                 {
                     new Place("home", Ui.GlyphHome, "Übersicht"),
                     new Place("timetable", Ui.GlyphCalendar, "Stundenplan"),
                     new Place("homework", Ui.GlyphChecklist, "Hausaufgaben"),
                     new Place("cards", Ui.GlyphCards, "Karteikarten"),
                     new Place("icloud", Ui.GlyphCloud, "iCloud Drive")
                 })
        {
            var count = place.Key switch
            {
                "homework" => Services.Store.OpenHomeworkCount,
                "cards" => Services.Store.DueCardCount,
                _ => 0
            };
            var row = new DockPanel();
            if (count > 0)
            {
                var badge = Ui.Badge(count.ToString(), place.Key == "homework" ? "Warn" : "Good", place.Key == "homework" ? "WarnSoft" : "GoodSoft");
                DockPanel.SetDock(badge, Dock.Right);
                row.Children.Add(badge);
            }
            var icon = Ui.Icon(place.Glyph, 15, "Muted");
            icon.Width = 20;
            icon.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = place.Title, VerticalAlignment = VerticalAlignment.Center });
            _places.Items.Add(new ListBoxItem { Content = row, Tag = place.Key });
        }
        if (selected is not null) _places.SelectedItem = _places.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == selected);
        _updating = false;
    }

    private void ReloadNotebooks()
    {
        _updating = true;
        _notebooks.Items.Clear();
        foreach (var notebook in Services.Store.Library.Notebooks)
        {
            var row = new DockPanel();
            var count = Ui.Text(Services.Store.NoteCount(notebook.Id).ToString(), 12, color: "Faint");
            count.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(count);
            var dot = Ui.Dot(Ui.NotebookBrush(notebook.ColorName), 10);
            dot.Margin = new Thickness(5, 0, 15, 0);
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(new TextBlock { Text = notebook.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var item = new ListBoxItem { Content = row, Tag = notebook.Id, ContextMenu = NotebookMenu(notebook) };
            _notebooks.Items.Add(item);
        }
        if (_notebookId is Guid id) _notebooks.SelectedItem = _notebooks.Items.OfType<ListBoxItem>().FirstOrDefault(i => (Guid)i.Tag == id);
        _updating = false;
        ReloadPlaces();
    }

    private ContextMenu NotebookMenu(Notebook notebook)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Ui.MenuItem("Neue Notiz", () => NewNote(notebook.Id), Ui.GlyphEdit));
        menu.Items.Add(Ui.MenuItem("Bearbeiten …", () => EditNotebook(notebook), Ui.GlyphSettings));
        menu.Items.Add(new Separator());
        var index = Services.Store.Library.Notebooks.IndexOf(notebook);
        menu.Items.Add(Ui.MenuItem("Nach oben", () => { Services.Store.MoveNotebook(notebook.Id, -1); ReloadNotebooks(); }, Ui.GlyphChevronUp, enabled: index > 0));
        menu.Items.Add(Ui.MenuItem("Nach unten", () => { Services.Store.MoveNotebook(notebook.Id, 1); ReloadNotebooks(); }, Ui.GlyphChevronDown,
            enabled: index < Services.Store.Library.Notebooks.Count - 1));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.MenuItem("Löschen …", () => DeleteNotebook(notebook), Ui.GlyphDelete));
        return menu;
    }

    private void PlaceChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _places.SelectedItem is not ListBoxItem { Tag: string key }) return;
        _updating = true;
        _notebooks.SelectedItem = null;
        _updating = false;
        switch (key)
        {
            case "home":
                _notebookId = null;
                _search.Text = "";
                ReloadNotes();
                CloseNote();
                ShowHome();
                break;
            case "timetable":
                new TimetableWindow { Owner = this }.ShowDialog();
                AfterSheet();
                break;
            case "homework":
                new HomeworkWindow { Owner = this }.ShowDialog();
                AfterSheet();
                break;
            case "cards":
                new FlashcardsWindow { Owner = this }.ShowDialog();
                AfterSheet();
                break;
            case "icloud":
                _ = OpenCloudFileAsync();
                break;
        }
    }

    /// <summary>
    /// iCloud Drive aus der Seitenleiste: Ist eine Notiz offen, kommt die Datei dort hinein,
    /// sonst entsteht eine neue Notiz mit dem Dateinamen als Titel.
    /// </summary>
    private async Task OpenCloudFileAsync()
    {
        var open = _openNoteId is Guid id ? Services.Store.Note(id) : null;
        var target = open is not null ? $"„{open.Title}“" : "eine neue Notiz";
        var window = new CloudFilesWindow(target) { Owner = this };
        var chosen = window.ShowDialog() == true ? window.ChosenPath : null;
        AfterSheet();
        if (chosen is null) return;
        if (open is null)
        {
            NewNote(null);
            if (_openNoteId is Guid created)
            {
                Services.Store.UpdateNote(created, n => n.Title = System.IO.Path.GetFileNameWithoutExtension(chosen));
                _editor.Open(created);
                ReloadNotes();
            }
        }
        await _editor.InsertCloudFileAsync(chosen);
    }

    /// <summary>Nach einem Fenster wie „Hausaufgaben": wieder „Übersicht“ oder die Notiz markieren.</summary>
    private void AfterSheet()
    {
        _updating = true;
        _places.SelectedIndex = _openNoteId is null && _notebookId is null ? 0 : -1;
        if (_notebookId is Guid id) _notebooks.SelectedItem = _notebooks.Items.OfType<ListBoxItem>().FirstOrDefault(i => (Guid)i.Tag == id);
        _updating = false;
        ReloadPlaces();
        if (_openNoteId is null) RebuildHome();
    }

    private void NotebookChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _notebooks.SelectedItem is not ListBoxItem { Tag: Guid id }) return;
        _updating = true;
        _places.SelectedItem = null;
        _updating = false;
        _notebookId = id;
        _search.Text = "";
        ReloadNotes();
    }

    private void NewNotebook()
    {
        var dialog = new NotebookDialog(null) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var notebook = Services.Store.CreateNotebook(dialog.NotebookName, dialog.ColorName, dialog.ScanHomework);
        _notebookId = notebook.Id;
        ReloadNotebooks();
        ReloadNotes();
    }

    private void EditNotebook(Notebook notebook)
    {
        var dialog = new NotebookDialog(notebook) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Services.Store.UpdateNotebook(notebook.Id, n =>
        {
            n.Name = dialog.NotebookName;
            n.ColorName = dialog.ColorName;
            n.ScanHomework = dialog.ScanHomework;
        });
        ReloadNotebooks();
        ReloadNotes();
    }

    private void DeleteNotebook(Notebook notebook)
    {
        var count = Services.Store.NoteCount(notebook.Id);
        if (!Dialogs.Confirm(this, "Fach löschen?", $"„{notebook.Name}“ wird mit {Ui.Plural(count, "Notiz", "Notizen")} endgültig gelöscht.",
                "Löschen", danger: true)) return;
        if (_openNoteId is Guid open && Services.Store.Note(open)?.NotebookId == notebook.Id) CloseNote();
        Services.Store.DeleteNotebook(notebook.Id);
        if (_notebookId == notebook.Id) _notebookId = null;
        ReloadNotebooks();
        ReloadNotes();
        if (_openNoteId is null) ShowHome();
    }

    // MARK: - iPad

    private void UpdatePadCard()
    {
        var connected = Services.Bridge.Connected;
        var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        dot.SetResourceReference(Border.BackgroundProperty, connected ? "Good" : "LineStrong");
        var text = new StackPanel();
        text.Children.Add(Ui.Text(connected ? "iPad verbunden" : "iPad verbinden", 13, FontWeights.SemiBold));
        var paired = Services.Pad.PairedDevices.Count;
        text.Children.Add(Ui.Text(connected ? Services.Bridge.DeviceName : paired > 0 ? "wartet auf das iPad …" : "zum Zeichnen koppeln", 11.5, color: "Faint"));
        var icon = Ui.Icon(connected ? Ui.GlyphTablet : Ui.GlyphQr, 15, connected ? "Good" : "Accent");
        var dock = new DockPanel();
        DockPanel.SetDock(icon, Dock.Right);
        dock.Children.Add(icon);
        DockPanel.SetDock(dot, Dock.Left);
        dock.Children.Add(dot);
        dock.Children.Add(text);
        _padCard.Child = dock;
        _padCard.ToolTip = connected ? "Klicken zum Trennen oder Koppeln eines weiteren iPads" : "iPad per QR-Code oder Code koppeln";
    }

    private void ShowPadMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _padCard, Placement = PlacementMode.Top };
        menu.Items.Add(Ui.MenuItem("Verbindung trennen", () => Services.Bridge.Disconnect(), Ui.GlyphClose));
        menu.Items.Add(Ui.MenuItem("Weiteres iPad koppeln …", () => new PairingWindow { Owner = this }.ShowDialog(), Ui.GlyphQr));
        menu.Items.Add(Ui.MenuItem("iPad-Einstellungen …", () => OpenSettings(SettingsWindow.Section.Pad), Ui.GlyphSettings));
        menu.IsOpen = true;
    }

    private void OpenSettings(SettingsWindow.Section section = SettingsWindow.Section.Ai)
    {
        new SettingsWindow(section) { Owner = this }.ShowDialog();
        RefreshAll();
    }

    // MARK: - Notizliste

    private UIElement BuildList()
    {
        var border = new Border { BorderThickness = new Thickness(0, 0, 1, 0) };
        border.SetResourceReference(Border.BackgroundProperty, "Surface");
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        var dock = new DockPanel();

        var header = new DockPanel { Margin = new Thickness(18, 18, 12, 12) };
        var add = Ui.IconButton(Ui.GlyphEdit, "Neue Notiz (Strg+N)", (_, _) => NewNote(null));
        DockPanel.SetDock(add, Dock.Right);
        header.Children.Add(add);
        _listTitle.FontFamily = Ui.DisplayFont;
        _listTitle.FontSize = 19;
        _listTitle.FontWeight = FontWeights.SemiBold;
        _listTitle.VerticalAlignment = VerticalAlignment.Center;
        _listTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(_listTitle);
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);

        _search.Margin = new Thickness(14, 0, 14, 10);
        _search.TextChanged += (_, _) => ReloadNotes();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) _search.Text = "";
        };
        DockPanel.SetDock(_search, Dock.Top);
        dock.Children.Add(_search);

        _notes.Style = Ui.Style("PlainList");
        _notes.ItemContainerStyle = Ui.Style("NoteItem");
        _notes.Padding = new Thickness(8, 0, 8, 10);
        _notes.SelectionChanged += NoteChosen;
        var grid = new Grid();
        grid.Children.Add(_notes);
        _emptyList.TextWrapping = TextWrapping.Wrap;
        _emptyList.TextAlignment = TextAlignment.Center;
        _emptyList.Margin = new Thickness(28, 30, 28, 0);
        _emptyList.VerticalAlignment = VerticalAlignment.Top;
        _emptyList.FontSize = 13;
        _emptyList.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        grid.Children.Add(_emptyList);
        dock.Children.Add(grid);
        border.Child = dock;
        return border;
    }

    private void ReloadNotes()
    {
        var store = Services.Store;
        var query = _search.Text.Trim();
        List<NoteMeta> notes;
        var showSubject = false;
        if (query.Length > 0)
        {
            notes = store.Library.Notes
                .Where(n => n.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                            || n.Snippet.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .OrderByDescending(n => n.Updated).ToList();
            _listTitle.Text = "Suche";
            showSubject = true;
            _emptyList.Text = $"Nichts gefunden für „{query}“.";
        }
        else if (_notebookId is Guid id && store.Library.Notebooks.Any(n => n.Id == id))
        {
            notes = store.NotesIn(id).ToList();
            _listTitle.Text = store.Library.Notebooks.First(n => n.Id == id).Name;
            _emptyList.Text = "Noch keine Notizen. Klick oben auf den Stift, um hier zu schreiben.";
        }
        else
        {
            notes = store.Library.Notes.OrderByDescending(n => n.Updated).Take(50).ToList();
            _listTitle.Text = "Zuletzt bearbeitet";
            showSubject = true;
            _emptyList.Text = "Noch keine Notizen.";
        }
        _updating = true;
        _notes.Items.Clear();
        foreach (var note in notes) _notes.Items.Add(NoteItem(note, showSubject));
        if (_openNoteId is Guid open) _notes.SelectedItem = _notes.Items.OfType<ListBoxItem>().FirstOrDefault(i => (Guid)i.Tag == open);
        _updating = false;
        _emptyList.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ListBoxItem NoteItem(NoteMeta note, bool showSubject)
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(note.Title, 14, FontWeights.SemiBold));
        var detail = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
        if (showSubject && Services.Store.NotebookOf(note) is { } notebook)
        {
            var dot = Ui.Dot(Ui.NotebookBrush(notebook.ColorName), 7);
            dot.Margin = new Thickness(0, 1, 6, 0);
            DockPanel.SetDock(dot, Dock.Left);
            detail.Children.Add(dot);
        }
        var date = Ui.Text(Home.Relative(note.UpdatedAt), 12, color: "Faint");
        detail.Children.Add(date);
        stack.Children.Add(detail);
        if (note.Snippet.Length > 0)
        {
            var snippet = Ui.Text(note.Snippet, 12.5, color: "Muted", wrap: true, margin: new Thickness(0, 3, 0, 0));
            // Feste Zeilenhöhe: genau zwei Zeilen, die zweite endet bei Bedarf mit „…“.
            snippet.LineHeight = 17;
            snippet.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            snippet.MaxHeight = 34.5;
            snippet.TextTrimming = TextTrimming.CharacterEllipsis;
            stack.Children.Add(snippet);
        }
        var item = new ListBoxItem { Content = stack, Tag = note.Id };
        var menu = new ContextMenu();
        var move = new MenuItem { Header = "In anderes Fach verschieben", Icon = Ui.Icon(Ui.GlyphFolder, 13) };
        foreach (var other in Services.Store.Library.Notebooks)
        {
            var target = other;
            var entry = Ui.MenuItem(other.Name, () =>
            {
                Services.Store.UpdateNote(note.Id, n => n.NotebookId = target.Id, touch: false);
                ReloadNotebooks();
                ReloadNotes();
                if (_openNoteId == note.Id) _editor.Open(note.Id);
            }, isChecked: other.Id == note.NotebookId);
            move.Items.Add(entry);
        }
        menu.Items.Add(move);
        menu.Items.Add(Ui.MenuItem("Umbenennen …", () =>
        {
            var dialog = new TextDialog("Umbenennen", "Titel", note.Title) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Value.Length == 0) return;
            if (_openNoteId == note.Id) _editor.SaveNow();
            Services.Store.UpdateNote(note.Id, n => n.Title = dialog.Value);
            if (_openNoteId == note.Id) _editor.Open(note.Id);
            ReloadNotes();
        }, Ui.GlyphEdit));
        menu.Items.Add(Ui.MenuItem("Als PDF sichern …", () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = PdfExport.SafeName(note.Title) + ".pdf", Filter = "PDF|*.pdf" };
            if (dialog.ShowDialog(this) != true) return;
            if (_openNoteId == note.Id) _editor.SaveNow();
            PdfExport.Write(dialog.FileName, new[] { note });
        }, Ui.GlyphSave));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.MenuItem("Löschen …", () =>
        {
            if (!Dialogs.Confirm(this, "Notiz löschen?", $"„{note.Title}“ wird mit Text, Handschrift und Bildern endgültig gelöscht.", "Löschen", danger: true)) return;
            if (_openNoteId == note.Id) CloseNote();
            Services.Store.DeleteNote(note.Id);
            ReloadNotebooks();
            ReloadNotes();
            if (_openNoteId is null) ShowHome();
        }, Ui.GlyphDelete));
        item.ContextMenu = menu;
        return item;
    }

    private void NoteChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _notes.SelectedItem is not ListBoxItem { Tag: Guid id }) return;
        OpenNote(id);
    }

    // MARK: - Inhalt

    private void BuildContent()
    {
        _homeScroller.Content = _homePanel;
        _homeScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _homeScroller.Focusable = false;
        _homeScroller.PanningMode = PanningMode.VerticalOnly;
        _homeScroller.SizeChanged += (_, args) =>
        {
            if (Math.Abs(args.NewSize.Width - args.PreviousSize.Width) > 60 && _openNoteId is null) RebuildHome();
        };
        _content.Children.Add(_homeScroller);
        _editor.Visibility = Visibility.Collapsed;
        _content.Children.Add(_editor);

        _banner.Visibility = Visibility.Collapsed;
        _banner.VerticalAlignment = VerticalAlignment.Top;
        _banner.HorizontalAlignment = HorizontalAlignment.Center;
        _banner.Margin = new Thickness(20, 16, 20, 0);
        _banner.Padding = new Thickness(16, 12, 12, 12);
        _banner.CornerRadius = new CornerRadius(12);
        _banner.BorderThickness = new Thickness(1);
        _banner.MaxWidth = 760;
        _banner.SetResourceReference(Border.BackgroundProperty, "Surface");
        _banner.SetResourceReference(Border.BorderBrushProperty, "Accent");
        _banner.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Direction = 270, Opacity = 0.2 };
        _bannerText.TextWrapping = TextWrapping.Wrap;
        _bannerText.VerticalAlignment = VerticalAlignment.Center;
        _bannerText.Margin = new Thickness(0, 0, 14, 0);
        var dock = new DockPanel();
        DockPanel.SetDock(_bannerButtons, Dock.Right);
        dock.Children.Add(_bannerButtons);
        var icon = Ui.Icon(Ui.GlyphInfo, 16, "Accent");
        icon.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(_bannerText);
        _banner.Child = dock;
        _content.Children.Add(_banner);
    }

    /// <summary>Kurze Meldung oben über dem Inhalt, mit optionalen Knöpfen.</summary>
    public void ShowBanner(string text, params (string Label, Action Action, bool Primary)[] buttons)
    {
        _bannerText.Text = text;
        _bannerButtons.Children.Clear();
        foreach (var (label, action, primary) in buttons)
        {
            var button = Ui.Button(label, (_, _) =>
            {
                _banner.Visibility = Visibility.Collapsed;
                action();
            }, primary ? "Primary" : "Soft");
            button.Margin = new Thickness(6, 0, 0, 0);
            _bannerButtons.Children.Add(button);
        }
        var close = Ui.IconButton(Ui.GlyphClose, "Schließen", (_, _) => _banner.Visibility = Visibility.Collapsed, 11);
        close.Width = close.Height = 28;
        close.Margin = new Thickness(6, 0, 0, 0);
        _bannerButtons.Children.Add(close);
        _banner.Visibility = Visibility.Visible;
    }

    private void RebuildHome()
    {
        Home.Build(_homePanel, _homeScroller.ActualWidth > 0 ? _homeScroller.ActualWidth - 68 : 900, new Home.Actions(
            OpenNote,
            NewNote,
            () => { new HomeworkWindow { Owner = this }.ShowDialog(); AfterSheet(); },
            () => { new FlashcardsWindow { Owner = this }.ShowDialog(); AfterSheet(); },
            () => { new TimetableWindow { Owner = this }.ShowDialog(); AfterSheet(); },
            () => { new PairingWindow { Owner = this }.ShowDialog(); RebuildHome(); }));
    }

    private void ShowHome()
    {
        _editor.Visibility = Visibility.Collapsed;
        _homeScroller.Visibility = Visibility.Visible;
        RebuildHome();
        _listsHidden = false;
        _listsForced = false;
        FitColumns();
    }

    public void OpenNote(Guid id)
    {
        var note = Services.Store.Note(id);
        if (note is null) return;
        if (_openNoteId != id) _editor.Leave();
        _openNoteId = id;
        if (_search.Text.Length == 0 && _notebookId != note.NotebookId)
        {
            _notebookId = note.NotebookId;
            _updating = true;
            _places.SelectedItem = null;
            _notebooks.SelectedItem = _notebooks.Items.OfType<ListBoxItem>().FirstOrDefault(i => (Guid)i.Tag == note.NotebookId);
            _updating = false;
        }
        ReloadNotes();
        _homeScroller.Visibility = Visibility.Collapsed;
        _editor.Visibility = Visibility.Visible;
        _editor.Open(id);
        if (Services.Settings.GetBool(Keys.HideListsWhileWriting, false)) _listsHidden = true;
        FitColumns();
    }

    private void CloseNote()
    {
        if (_openNoteId is null) return;
        _editor.Leave();
        _openNoteId = null;
        _listsForced = false;
        _editor.Visibility = Visibility.Collapsed;
        _homeScroller.Visibility = Visibility.Visible;
        FitColumns();
    }

    private void NewNote(Guid? notebookId)
    {
        var store = Services.Store;
        var target = notebookId ?? _notebookId ?? (_openNoteId is Guid open ? store.Note(open)?.NotebookId : null) ?? store.Library.Notebooks.FirstOrDefault()?.Id;
        if (target is null)
        {
            var notebook = store.CreateNotebook("Allgemein", "blue");
            target = notebook.Id;
        }
        _search.Text = "";
        var note = store.CreateNote(target.Value);
        _notebookId = target;
        ReloadNotebooks();
        OpenNote(note.Id);
    }

    private void ToggleLists()
    {
        if (ListsCollapsed)
        {
            _listsHidden = false;
            _listsForced = true;
        }
        else
        {
            _listsHidden = true;
            _listsForced = false;
        }
        FitColumns();
    }

    private void FitColumns()
    {
        var collapsed = ListsCollapsed;
        _sidebar.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _list.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (collapsed)
        {
            _sidebarColumn.Width = new GridLength(0);
            _listColumn.Width = new GridLength(0);
            return;
        }
        var narrow = ActualWidth < 1250;
        _sidebarColumn.Width = new GridLength(narrow ? 216 : 250);
        _listColumn.Width = new GridLength(narrow ? 256 : 300);
    }

    /// <summary>Alles neu einlesen – nach Umzug, Sicherung oder Einstellungen.</summary>
    public void RefreshAll()
    {
        ReloadNotebooks();
        ReloadNotes();
        UpdatePadCard();
        if (_openNoteId is Guid open)
        {
            if (Services.Store.Note(open) is null)
            {
                CloseNote();
                ShowHome();
            }
        }
        else RebuildHome();
    }

    // MARK: - Für die automatische Sichtprüfung

    internal TextBox SearchBoxForShots => _search;

    internal void CloseNoteForShots()
    {
        CloseNote();
        ShowHome();
    }

    internal void FocusFirstBoxForShots()
    {
        var box = _editor.Page.Boxes.FirstOrDefault();
        if (box is null) return;
        box.Focus();
        box.CaretPosition = box.Document.ContentEnd;
    }

    internal void ResizeForShots(double width, double height)
    {
        Width = width;
        Height = height;
        FitColumns();
    }

    private void OfferLegacyImport()
    {
        if (Services.Settings.GetBool(Keys.LegacyImportAsked, false)) return;
        var folder = LegacyImport.OldWindowsFolder;
        if (!LegacyImport.LooksLikeLegacyFolder(folder)) return;
        var count = LegacyImport.CountNew(folder, Services.Store);
        if (count == 0) return;
        ShowBanner($"In der alten Lernheft-App auf diesem Surface liegen {Ui.Plural(count, "Notiz", "Notizen")}. Jetzt übernehmen?",
            ("Später", () => Services.Settings.SetBool(Keys.LegacyImportAsked, true), false),
            ("Übernehmen", () =>
            {
                Services.Settings.SetBool(Keys.LegacyImportAsked, true);
                try
                {
                    var report = LegacyImport.ImportFolder(folder, Services.Store);
                    RefreshAll();
                    ShowBanner(report.Summary);
                }
                catch (Exception error)
                {
                    ShowBanner("Der Umzug hat nicht geklappt: " + error.Message);
                }
            }, true));
    }
}
