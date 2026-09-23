using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lernheft.Studio.App.Sheets;

namespace Lernheft.Studio.App.Editor;

/// <summary>
/// Die offene Notiz: Kopfzeile, Formatleiste und die Seite. Speichert von allein und hält das iPad
/// auf dem Laufenden.
/// </summary>
public sealed class NoteEditor : UserControl
{
    private readonly PageSurface _page = new();
    private readonly ScrollViewer _scroller = new();
    private readonly Grid _pageFrame = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TextBox _title = new();
    private readonly TextBlock _subject = new();
    private readonly Border _subjectDot = new();
    private readonly FormatBar _format;
    private readonly Border _imageBar = new();
    private readonly TextBlock _imageHint = new();
    private readonly StackPanel _imageButtons = new() { Orientation = Orientation.Horizontal };
    private readonly Button _padChip = new();
    private readonly TextBlock _zoomLabel = new();
    private readonly Border _busy = new();
    private readonly TextBlock _busyText = new();
    private readonly TextBlock _busyTime = new();
    private readonly Button _busyCancel = new();
    private readonly Border _toast = new();
    private readonly TextBlock _toastText = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _inkTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4.5) };
    private readonly DispatcherTimer _busyClock = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _busyStarted;
    private CancellationTokenSource? _busyCancelSource;

    private TextBoxView? _lastBox;
    private double _zoomFactor = 1;
    private bool _loading;
    private bool _dirtySinceOpen;
    private bool _followingLive;

    public NoteMeta? Note => _page.Note;
    public PageSurface Page => _page;

    /// <summary>Die Listen links ein- oder ausblenden.</summary>
    public event Action? ToggleLists;

    /// <summary>Titel, Fach oder Vorschau haben sich geändert – die Liste links auffrischen.</summary>
    public event Action? MetaChanged;

    /// <summary>Die Notiz wurde gelöscht.</summary>
    public event Action? Deleted;

    public NoteEditor()
    {
        _format = new FormatBar(() => _lastBox);
        Focusable = false;
        SetResourceReference(BackgroundProperty, "Desk");

        var root = new DockPanel { LastChildFill = true };
        var header = BuildHeader();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(_format, Dock.Top);
        root.Children.Add(_format);
        BuildImageBar();
        DockPanel.SetDock(_imageBar, Dock.Top);
        root.Children.Add(_imageBar);
        BuildFormatRight();

        // Die Seite mit einem leichten Schatten (ohne teuren Weichzeichner).
        var shadowFar = new Border { CornerRadius = new CornerRadius(3), Margin = new Thickness(-1, 3, -1, -5), Opacity = 0.18 };
        shadowFar.SetResourceReference(Border.BackgroundProperty, "LineStrong");
        var shadowNear = new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(-1, 0, -1, -2), Opacity = 0.5 };
        shadowNear.SetResourceReference(Border.BackgroundProperty, "Line");
        _page.LayoutTransform = _scale;
        _pageFrame.Children.Add(shadowFar);
        _pageFrame.Children.Add(shadowNear);
        _pageFrame.Children.Add(_page);
        _pageFrame.HorizontalAlignment = HorizontalAlignment.Center;
        _pageFrame.VerticalAlignment = VerticalAlignment.Top;
        _pageFrame.Margin = new Thickness(24, 34, 24, 80);

        _scroller.Content = _pageFrame;
        _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroller.PanningMode = PanningMode.Both;
        _scroller.Focusable = false;
        _scroller.SizeChanged += (_, _) => FitZoom();
        _scroller.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange != 0 && !_followingLive) Services.Bridge.ViewChanged(this);
        };
        _scroller.PreviewMouseWheel += OnWheel;

        var stack = new Grid();
        stack.Children.Add(_scroller);
        BuildBusy();
        stack.Children.Add(_busy);
        BuildToast();
        stack.Children.Add(_toast);
        root.Children.Add(stack);
        Content = root;

        _page.ContentChanged += () =>
        {
            if (_loading) return;
            _dirtySinceOpen = true;
            _saveTimer.Stop();
            _saveTimer.Start();
            Services.Bridge.TextChanged(this);
        };
        _page.InkEdited += (added, removed) =>
        {
            _dirtySinceOpen = true;
            ScheduleInkSave();
            Services.Bridge.SendInkOps(this, added, removed);
        };
        _page.FocusedBoxChanged += box =>
        {
            if (box is not null) _lastBox = box;
            _format.Update(box ?? (_format.IsInteracting ? _lastBox : null));
            if (box is null && !_format.IsInteracting) _lastBox = null;
        };
        _page.SelectionFormatChanged += () => _format.Update(_page.FocusedBox);
        _page.Message += Toast;
        _page.PagesNeeded += count =>
        {
            if (Note is null || count <= Note.PageCount) return;
            Services.Store.UpdateNote(Note.Id, n => n.PageCount = count, touch: false);
            _page.RefreshPaper();
            Services.Bridge.NoteMetaChanged(this);
        };
        _page.LivePoint += FollowLive;
        _page.ImageSelectionChanged += UpdateImageBar;

        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };
        _inkTimer.Tick += (_, _) =>
        {
            _inkTimer.Stop();
            SaveInk();
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            HideToast();
        };
        _busyClock.Tick += (_, _) => _busyTime.Text = $"läuft … {(int)(DateTime.Now - _busyStarted).TotalSeconds} s";

        Theme.Changed += () =>
        {
            _page.RefreshTheme();
            Services.Bridge.TextChanged(this);
        };
        Services.Bridge.StatusChanged += UpdatePadChip;
        UpdatePadChip();

        InputBindings.Add(new KeyBinding(new Command(Undo), Key.Z, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Command(Redo), Key.Y, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Command(() => SetZoom(_zoomFactor * 1.15)), Key.OemPlus, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Command(() => SetZoom(_zoomFactor / 1.15)), Key.OemMinus, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Command(() => SetZoom(1)), Key.D0, ModifierKeys.Control));
        PreviewKeyDown += OnPreviewKey;
    }

    // MARK: - Kopfzeile

    private FrameworkElement BuildHeader()
    {
        var bar = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(10, 8, 12, 8) };
        bar.SetResourceReference(Border.BackgroundProperty, "Surface");
        bar.SetResourceReference(Border.BorderBrushProperty, "Line");
        var dock = new DockPanel { LastChildFill = true };

        var lists = Ui.IconButton(Ui.GlyphMenu, "Listen ein-/ausblenden (F11)", (_, _) => ToggleLists?.Invoke());
        DockPanel.SetDock(lists, Dock.Left);
        dock.Children.Add(lists);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(Ui.IconButton(Ui.GlyphUndo, "Rückgängig (Strg+Z)", (_, _) => Undo()));
        right.Children.Add(Ui.IconButton(Ui.GlyphRedo, "Wiederholen (Strg+Y)", (_, _) => Redo()));
        right.Children.Add(FormatBar.Separator());

        _padChip.Style = Ui.Style("Soft");
        _padChip.Margin = new Thickness(0, 0, 8, 0);
        _padChip.Click += (_, _) =>
        {
            if (Services.Bridge.Connected) ShowPadMenu();
            else new PairingWindow { Owner = Window.GetWindow(this) }.ShowDialog();
        };
        right.Children.Add(_padChip);

        var ai = Ui.Button("KI-Helfer", (_, _) => OpenAi(), "Primary", "✦", "Grammatik, Mathe, Karteikarten, Zusammenfassen, Fragen");
        if (ai.Content is StackPanel row && row.Children[0] is TextBlock star)
        {
            star.FontFamily = new FontFamily("Segoe UI Symbol");
            star.FontSize = 13;
        }
        ai.Margin = new Thickness(0, 0, 6, 0);
        right.Children.Add(ai);
        var more = Ui.IconButton(Ui.GlyphMore, "Mehr");
        more.Click += (_, _) => ShowMoreMenu(more);
        right.Children.Add(more);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 12, 0) };
        _title.Style = Ui.Style("Plain");
        _title.FontFamily = Ui.DisplayFont;
        _title.FontSize = 19;
        _title.FontWeight = FontWeights.SemiBold;
        _title.MinWidth = 120;
        _title.MaxWidth = 520;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        _title.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                e.Handled = true;
                Save();
                _page.Focus();
            }
        };
        Ui.AutomationName(_title, "Titel der Notiz");
        titleRow.Children.Add(_title);

        var chip = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 3, 10, 3),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "In ein anderes Fach verschieben"
        };
        chip.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        _subjectDot.Width = _subjectDot.Height = 8;
        _subjectDot.CornerRadius = new CornerRadius(4);
        _subjectDot.Margin = new Thickness(0, 0, 7, 0);
        _subject.FontSize = 12;
        _subject.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        chip.Child = Ui.Row(0, _subjectDot, _subject);
        chip.MouseLeftButtonUp += (_, _) => ShowMoveMenu(chip);
        titleRow.Children.Add(chip);
        dock.Children.Add(titleRow);

        bar.Child = dock;
        return bar;
    }

    private void BuildFormatRight()
    {
        var right = _format.Right;
        right.Children.Add(FormatBar.Separator());
        right.Children.Add(FormatBar.Tool(Ui.Row(6, Ui.Icon(Ui.GlyphPicture, 14), new TextBlock { Text = "Einfügen", VerticalAlignment = VerticalAlignment.Center }),
            "Bild, PDF, SVG oder Text einfügen", () => ShowInsertMenu()));
        right.Children.Add(FormatBar.Tool(Ui.Icon(Ui.GlyphCalculator, 14), "Funktion zeichnen", InsertGraph));
        right.Children.Add(FormatBar.Separator());
        right.Children.Add(FormatBar.Tool(Ui.Icon(Ui.GlyphZoomOut, 13), "Verkleinern (Strg+-)", () => SetZoom(_zoomFactor / 1.15)));
        _zoomLabel.MinWidth = 42;
        _zoomLabel.TextAlignment = TextAlignment.Center;
        _zoomLabel.VerticalAlignment = VerticalAlignment.Center;
        _zoomLabel.FontSize = 12;
        _zoomLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var zoomReset = FormatBar.Tool(_zoomLabel, "Auf Seitenbreite (Strg+0)", () => SetZoom(1));
        right.Children.Add(zoomReset);
        right.Children.Add(FormatBar.Tool(Ui.Icon(Ui.GlyphZoomIn, 13), "Vergrößern (Strg++)", () => SetZoom(_zoomFactor * 1.15)));
    }

    private void BuildImageBar()
    {
        _imageBar.Visibility = Visibility.Collapsed;
        _imageBar.Padding = new Thickness(16, 7, 16, 7);
        _imageBar.BorderThickness = new Thickness(0, 0, 0, 1);
        _imageBar.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        _imageBar.SetResourceReference(Border.BorderBrushProperty, "Line");
        var dock = new DockPanel();
        var done = Ui.Button("Fertig", (_, _) => { _page.ExitImageMode(); UpdateImageBar(); }, "Primary");
        DockPanel.SetDock(done, Dock.Right);
        dock.Children.Add(done);
        _imageHint.VerticalAlignment = VerticalAlignment.Center;
        _imageHint.Margin = new Thickness(0, 0, 14, 0);
        _imageHint.FontSize = 13;
        dock.Children.Add(Ui.Row(0, Ui.Icon(Ui.GlyphPicture, 15, "Accent"), _imageHint, _imageButtons));
        _imageHint.Margin = new Thickness(10, 0, 14, 0);
        _imageBar.Child = dock;
    }

    private void UpdateImageBar()
    {
        _imageBar.Visibility = _page.ImageMode ? Visibility.Visible : Visibility.Collapsed;
        _format.Visibility = _page.ImageMode ? Visibility.Collapsed : Visibility.Visible;
        _imageButtons.Children.Clear();
        if (!_page.ImageMode) return;
        if (_page.SelectedImage is null)
        {
            _imageHint.Text = _page.Content.Backgrounds.Count == 0
                ? "In dieser Notiz gibt es noch keine Bilder."
                : "Klick auf ein Bild, um es zu verschieben oder zu vergrößern.";
            return;
        }
        _imageHint.Text = _page.ImageEraser ? "Wisch über die Stellen, die weg sollen." : "Ziehen verschiebt, der runde Griff ändert die Größe.";
        var eraser = Ui.Button(_page.ImageEraser ? "Radierer an" : "Radierer", (_, _) => _page.SetImageEraser(!_page.ImageEraser),
            _page.ImageEraser ? "Primary" : "Soft", Ui.GlyphErase);
        eraser.Margin = new Thickness(0, 0, 8, 0);
        var original = Ui.Button("Original", (_, _) => _page.RestoreSelectedImage(), icon: Ui.GlyphRefresh);
        original.Margin = new Thickness(0, 0, 8, 0);
        var delete = Ui.Button("Löschen", (_, _) => _page.DeleteSelectedImage(), "Danger", Ui.GlyphDelete);
        _imageButtons.Children.Add(eraser);
        _imageButtons.Children.Add(original);
        _imageButtons.Children.Add(delete);
    }

    private void BuildBusy()
    {
        _busy.Visibility = Visibility.Collapsed;
        _busy.HorizontalAlignment = HorizontalAlignment.Center;
        _busy.VerticalAlignment = VerticalAlignment.Center;
        _busy.CornerRadius = new CornerRadius(14);
        _busy.Padding = new Thickness(26, 20, 26, 20);
        _busy.BorderThickness = new Thickness(1);
        _busy.SetResourceReference(Border.BackgroundProperty, "Surface");
        _busy.SetResourceReference(Border.BorderBrushProperty, "Line");
        _busy.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.22 };
        var stack = new StackPanel { MinWidth = 260 };
        _busyText.FontSize = 14;
        _busyText.TextAlignment = TextAlignment.Center;
        _busyText.TextWrapping = TextWrapping.Wrap;
        _busyText.MaxWidth = 320;
        stack.Children.Add(_busyText);
        var bar = new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 14, 0, 10), BorderThickness = new Thickness(0) };
        bar.SetResourceReference(ForegroundProperty, "Accent");
        bar.SetResourceReference(BackgroundProperty, "SurfaceAlt");
        stack.Children.Add(bar);
        _busyTime.FontSize = 12;
        _busyTime.TextAlignment = TextAlignment.Center;
        _busyTime.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        stack.Children.Add(_busyTime);
        _busyCancel.Style = Ui.Style("Soft");
        _busyCancel.Content = "Abbrechen";
        _busyCancel.HorizontalAlignment = HorizontalAlignment.Center;
        _busyCancel.Margin = new Thickness(0, 12, 0, 0);
        _busyCancel.Click += (_, _) => _busyCancelSource?.Cancel();
        stack.Children.Add(_busyCancel);
        _busy.Child = stack;
    }

    private void BuildToast()
    {
        _toast.Visibility = Visibility.Collapsed;
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toast.VerticalAlignment = VerticalAlignment.Bottom;
        _toast.Margin = new Thickness(0, 0, 0, 26);
        _toast.CornerRadius = new CornerRadius(10);
        _toast.Padding = new Thickness(16, 10, 16, 10);
        _toast.MaxWidth = 560;
        _toast.SetResourceReference(Border.BackgroundProperty, "Text");
        _toastText.SetResourceReference(TextBlock.ForegroundProperty, "Chrome");
        _toastText.TextWrapping = TextWrapping.Wrap;
        _toastText.FontSize = 13;
        _toast.Child = _toastText;
        _toast.MouseLeftButtonUp += (_, _) => HideToast();
    }

    public void Toast(string text)
    {
        _toastText.Text = text;
        _toast.Visibility = Visibility.Visible;
        _toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(text.Length / 18.0, 3.5, 9));
        _toastTimer.Start();
    }

    private void HideToast()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => _toast.Visibility = Visibility.Collapsed;
        _toast.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Etwas Langes läuft (KI, Import). Liefert ein Abbruch-Zeichen.</summary>
    public CancellationToken ShowBusy(string text, bool cancellable = true)
    {
        _busyCancelSource?.Cancel();
        _busyCancelSource = new CancellationTokenSource();
        _busyText.Text = text;
        _busyStarted = DateTime.Now;
        _busyTime.Text = "läuft … 0 s";
        _busyCancel.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        _busy.Visibility = Visibility.Visible;
        _busyClock.Start();
        return _busyCancelSource.Token;
    }

    public void HideBusy()
    {
        _busy.Visibility = Visibility.Collapsed;
        _busyClock.Stop();
    }

    // MARK: - Öffnen und Speichern

    public void Open(Guid noteId)
    {
        SaveNow();
        var note = Services.Store.Note(noteId);
        if (note is null) return;
        _loading = true;
        var content = Services.Store.LoadContent(noteId);
        var ink = Services.Store.LoadInk(noteId);
        // Alte Notizen: getippten Text einmalig in Textfelder umwandeln.
        if (content.TypedText is not null || content.Blocks is not null) LegacyImport.ConvertLegacyText(content, ink, note.Width);
        _page.Load(note, content, ink, TextEnvironment.FromSettings(note.PaperStyle));
        _title.Text = note.Title;
        UpdateSubject();
        _lastBox = null;
        _format.Update(null);
        UpdateImageBar();
        _zoomFactor = 1;
        FitZoom();
        _scroller.ScrollToTop();
        _scroller.ScrollToLeftEnd();
        _loading = false;
        _dirtySinceOpen = false;
        Services.Bridge.Attach(this);
        if (note.Title == "Neue Notiz" && content.TextBoxes.Count == 0 && ink.Strokes.Count == 0)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _title.Focus();
                _title.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    private void UpdateSubject()
    {
        if (Note is null) return;
        var notebook = Services.Store.NotebookOf(Note);
        _subject.Text = notebook?.Name ?? "Ohne Fach";
        _subjectDot.Background = Ui.NotebookBrush(notebook?.ColorName);
    }

    /// <summary>Speichert Text, Titel und Bilder – nur wenn sich etwas geändert hat.</summary>
    public void Save()
    {
        if (Note is null) return;
        _page.CommitAll();
        var changed = Services.Store.SaveContent(Note.Id, _page.Content);
        var title = string.IsNullOrWhiteSpace(_title.Text) ? "Ohne Titel" : _title.Text.Trim();
        if (changed || title != Note.Title)
        {
            Note.Title = title;
            Services.Store.RefreshSnippet(Note.Id, _page.Content, _page.Ink.Strokes.Count > 0);
            MetaChanged?.Invoke();
            Services.Bridge.NoteMetaChanged(this);
        }
    }

    private void ScheduleInkSave()
    {
        _inkTimer.Stop();
        _inkTimer.Start();
    }

    private void SaveInk()
    {
        if (Note is null) return;
        if (Services.Store.SaveInk(Note.Id, _page.Ink))
        {
            Services.Store.RefreshSnippet(Note.Id, _page.Content, _page.Ink.Strokes.Count > 0);
            MetaChanged?.Invoke();
        }
    }

    /// <summary>Sofort speichern – beim Wechseln der Notiz und beim Schließen.</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        if (_inkTimer.IsEnabled)
        {
            _inkTimer.Stop();
            SaveInk();
        }
        if (Note is null) return;
        Save();
    }

    /// <summary>Beim Verlassen: speichern und, wenn gewünscht, nach Hausaufgaben suchen.</summary>
    public void Leave()
    {
        var note = Note;
        SaveNow();
        if (note is not null && _dirtySinceOpen) _ = AutoHomeworkAsync(note);
        _dirtySinceOpen = false;
        Services.Bridge.Detach(this);
    }

    public void MarkInkChangedFromPad()
    {
        _dirtySinceOpen = true;
        ScheduleInkSave();
    }

    // MARK: - Zoom und Bildlauf

    private void FitZoom()
    {
        if (Note is null) return;
        // Rand links und rechts plus etwas Luft für den Seitenschatten – sonst taucht ein unnötiger Querbalken auf.
        var available = Math.Max(200, _scroller.ViewportWidth > 0 ? _scroller.ViewportWidth : _scroller.ActualWidth) - 48 - 6;
        var fit = Math.Clamp(Math.Floor(available / Note.Width * 1000) / 1000, 0.5, 1.4);
        _scroller.HorizontalScrollBarVisibility = _zoomFactor > 1.001 || available < Note.Width * 0.5
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Disabled;
        var zoom = Math.Clamp(fit * _zoomFactor, 0.35, 4);
        if (Math.Abs(_scale.ScaleX - zoom) > 0.001)
        {
            _scale.ScaleX = _scale.ScaleY = zoom;
            _page.Zoom = zoom;
        }
        _zoomLabel.Text = $"{Math.Round(_zoomFactor * 100)} %";
    }

    public double Zoom => _scale.ScaleX;

    private void SetZoom(double factor)
    {
        if (Note is null) return;
        var locked = Services.Settings.GetBool(Keys.ZoomLocked, false);
        if (locked && Math.Abs(factor - 1) > 0.001)
        {
            Toast("Der Zoom ist gesperrt. Entsperren kannst du ihn in den Einstellungen.");
            return;
        }
        // Die Mitte des sichtbaren Bereichs soll an ihrem Platz bleiben.
        var middle = (_scroller.VerticalOffset + _scroller.ViewportHeight / 2) / Math.Max(0.01, Zoom);
        _zoomFactor = Math.Clamp(factor, 0.4, 3);
        FitZoom();
        _pageFrame.UpdateLayout();
        _scroller.ScrollToVerticalOffset(middle * Zoom - _scroller.ViewportHeight / 2);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        SetZoom(_zoomFactor * (e.Delta > 0 ? 1.1 : 1 / 1.1));
    }

    /// <summary>Welcher Teil der Notiz gerade zu sehen ist (in Seiteneinheiten).</summary>
    public Rect Viewport
    {
        get
        {
            var zoom = Math.Max(0.01, Zoom);
            var offsetY = _scroller.VerticalOffset - _pageFrame.Margin.Top;
            return new Rect(0, Math.Max(0, offsetY / zoom), Note?.Width ?? Paper.PageWidth, _scroller.ViewportHeight / zoom);
        }
    }

    /// <summary>Zeichnet jemand auf dem iPad außerhalb des sichtbaren Bereichs, rollt die Seite mit.</summary>
    private void FollowLive(Point point)
    {
        var view = Viewport;
        var margin = Math.Min(120, view.Height * 0.15);
        if (point.Y >= view.Top + margin && point.Y <= view.Bottom - margin) return;
        var target = (point.Y - view.Height * 0.4) * Zoom + _pageFrame.Margin.Top;
        _followingLive = true;
        _scroller.ScrollToVerticalOffset(Math.Max(0, target));
        Dispatcher.BeginInvoke(() => _followingLive = false, DispatcherPriority.ContextIdle);
    }

    // MARK: - Tasten

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (control && e.Key == Key.V && _page.FocusedBox is null && Clipboard.ContainsImage())
        {
            e.Handled = true;
            PasteImage();
            return;
        }
        if (control && e.Key == Key.V && _page.FocusedBox is not null && Clipboard.ContainsImage() && !Clipboard.ContainsText())
        {
            e.Handled = true;
            PasteImage();
            return;
        }
        if (e.Key == Key.Delete && _page.ImageMode && _page.SelectedImage is not null)
        {
            e.Handled = true;
            _page.DeleteSelectedImage();
        }
        if (control && e.Key == Key.OemPlus && (Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _page.FocusedBox is { } box)
        {
            e.Handled = true;
            var current = box.Selection.GetPropertyValue(System.Windows.Documents.Inline.BaselineAlignmentProperty);
            box.Format(System.Windows.Documents.Inline.BaselineAlignmentProperty,
                current is BaselineAlignment b && b == BaselineAlignment.Superscript ? BaselineAlignment.Baseline : BaselineAlignment.Superscript);
        }
    }

    private void Undo()
    {
        if (_page.FocusedBox is { } box && box.CanUndo)
        {
            box.Undo();
            return;
        }
        _page.Undo();
    }

    private void Redo()
    {
        if (_page.FocusedBox is { } box && box.CanRedo)
        {
            box.Redo();
            return;
        }
        _page.Redo();
    }

    // MARK: - iPad

    private void UpdatePadChip()
    {
        var connected = Services.Bridge.Connected;
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Border.BackgroundProperty, connected ? "Good" : "Faint");
        var label = new TextBlock
        {
            Text = connected ? Services.Bridge.DeviceName : "iPad verbinden",
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var icon = Ui.Icon(Ui.GlyphTablet, 14);
        icon.Margin = new Thickness(0, 0, 8, 0);
        _padChip.Content = Ui.Row(0, connected ? dot : icon, label);
        _padChip.ToolTip = connected
            ? "Das iPad zeichnet mit – alles erscheint sofort hier."
            : "Mit dem iPad per QR-Code oder Code koppeln und darauf zeichnen";
    }

    private void ShowPadMenu()
    {
        var menu = Ui.Menu(_padChip);
        menu.Items.Add(Ui.MenuItem("Verbindung trennen", () => Services.Bridge.Disconnect(), Ui.GlyphClose));
        menu.Items.Add(Ui.MenuItem("Weiteres iPad koppeln …", () => new PairingWindow { Owner = Window.GetWindow(this) }.ShowDialog(), Ui.GlyphQr));
        menu.IsOpen = true;
    }

    // MARK: - Menüs

    private void ShowMoreMenu(UIElement target)
    {
        if (Note is null) return;
        var menu = Ui.Menu(target);
        menu.Placement = PlacementMode.Bottom;
        menu.Items.Add(Ui.MenuItem("Seite hinzufügen", AddPage, Ui.GlyphPage));
        var paper = new MenuItem { Header = "Papier", Icon = Ui.Icon(Ui.GlyphDocument, 13) };
        foreach (var style in Enum.GetValues<PaperStyle>())
        {
            var chosen = style;
            paper.Items.Add(Ui.MenuItem(Studio.Paper.Label(style), () => SetPaper(chosen), isChecked: Note.PaperStyle == style));
        }
        menu.Items.Add(paper);
        menu.Items.Add(Ui.MenuItem("Bilder bearbeiten", () => { _page.EnterImageMode(); UpdateImageBar(); }, Ui.GlyphPicture,
            enabled: _page.Content.Backgrounds.Count > 0));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.MenuItem("Hausaufgaben in dieser Notiz suchen", () => _ = FindHomeworkAsync(), Ui.GlyphChecklist));
        menu.Items.Add(Ui.MenuItem("Als PDF sichern …", () => _ = ExportPdfAsync(), Ui.GlyphSave));
        menu.Items.Add(Ui.MenuItem("Drucken …", Print, Ui.GlyphPrint));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.MenuItem("In anderes Fach verschieben …", () => ShowMoveMenu(target), Ui.GlyphFolder));
        menu.Items.Add(Ui.MenuItem("Notiz löschen", DeleteNote, Ui.GlyphDelete));
        menu.IsOpen = true;
    }

    private void ShowMoveMenu(UIElement target)
    {
        if (Note is null) return;
        var menu = Ui.Menu(target);
        foreach (var notebook in Services.Store.Library.Notebooks)
        {
            var chosen = notebook;
            var item = Ui.MenuItem(notebook.Name, () =>
            {
                Services.Store.UpdateNote(Note.Id, n => n.NotebookId = chosen.Id);
                UpdateSubject();
                MetaChanged?.Invoke();
                Services.Bridge.NoteMetaChanged(this);
            }, isChecked: notebook.Id == Note.NotebookId);
            item.Icon = Ui.Dot(Ui.NotebookBrush(notebook.ColorName), 9);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void ShowInsertMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.Mouse };
        menu.Items.Add(Ui.MenuItem("Bild, PDF oder SVG aus Datei …", () => _ = ImportFileAsync(), Ui.GlyphFolder));
        menu.Items.Add(Ui.MenuItem("Seiten mit der Kamera einscannen …", Scan, Ui.GlyphCamera));
        menu.Items.Add(Ui.MenuItem("Bild aus der Zwischenablage", PasteImage, Ui.GlyphCopy, enabled: Clipboard.ContainsImage()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.MenuItem("Funktion zeichnen …", InsertGraph, Ui.GlyphCalculator));
        menu.Items.Add(Ui.MenuItem("Seite hinzufügen", AddPage, Ui.GlyphPage));
        menu.IsOpen = true;
    }

    private void AddPage()
    {
        if (Note is null) return;
        Services.Store.UpdateNote(Note.Id, n => n.PageCount += 1, touch: false);
        _page.RefreshPaper();
        Services.Bridge.NoteMetaChanged(this);
        Dispatcher.BeginInvoke(() => _scroller.ScrollToVerticalOffset((Note.PageCount - 1) * Paper.PageHeight * Zoom), DispatcherPriority.Loaded);
    }

    private void SetPaper(PaperStyle style)
    {
        if (Note is null) return;
        Services.Store.UpdateNote(Note.Id, n => n.Paper = Studio.Paper.ToJson(style), touch: false);
        _page.RefreshPaper();
        _page.SetEnvironment(TextEnvironment.FromSettings(style));
        Services.Bridge.NoteMetaChanged(this);
        Services.Bridge.TextChanged(this);
    }

    /// <summary>Nach dem Ändern der Schrift-Einstellungen.</summary>
    public void RefreshEnvironment()
    {
        if (Note is null) return;
        _page.SetEnvironment(TextEnvironment.FromSettings(Note.PaperStyle));
        _format.Update(_page.FocusedBox);
        foreach (var box in _page.Boxes) SpellCheck.SetIsEnabled(box, Services.Settings.GetBool(Keys.SpellCheck, true));
        _page.RefreshTheme();
    }

    private void DeleteNote()
    {
        if (Note is null) return;
        if (!Dialogs.Confirm(Window.GetWindow(this), "Notiz löschen?",
                $"„{Note.Title}“ wird mit Text, Handschrift und Bildern endgültig gelöscht.", "Löschen", danger: true)) return;
        var id = Note.Id;
        _saveTimer.Stop();
        _inkTimer.Stop();
        Services.Bridge.Detach(this);
        Services.Store.DeleteNote(id);
        Deleted?.Invoke();
    }

    // MARK: - Einfügen

    private async Task ImportFileAsync()
    {
        if (Note is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Datei einfügen",
            Filter = "Alles, was geht|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.heic;*.webp;*.svg;*.txt;*.md;*.rtf|" +
                     "PDF|*.pdf|Bilder|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.heic;*.webp|SVG (z. B. aus Nebo)|*.svg|Text|*.txt;*.md;*.rtf"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await Importer.ImportAsync(this, dialog.FileName);
    }

    public void InsertText(string text, Point? at = null)
    {
        if (Note is null || string.IsNullOrWhiteSpace(text)) return;
        _page.PushUndo();
        var top = at?.Y ?? _page.ContentBottom + Paper.Spacing;
        var model = new NoteTextBox
        {
            X = at?.X ?? 80,
            Y = TextDocs.SnapTop(top, _page.Environment),
            Width = Math.Max(240, Note.Width - (at?.X ?? 80) - 48),
            Text = text.Trim(),
            Seed = new TextSeed { Text = text.Trim() }
        };
        var view = _page.AddBox(model);
        _page.AddTextItems(Array.Empty<NoteTextBox>());
        Dispatcher.BeginInvoke(() => view.BringIntoView(), DispatcherPriority.Loaded);
    }

    public void PlaceImages(List<PageBackground> backgrounds) => _page.AddImages(backgrounds);

    private void PasteImage()
    {
        if (Note is null || !Clipboard.ContainsImage()) return;
        var image = Clipboard.GetImage();
        if (image is null) return;
        var shrunk = ImageTools.Shrink(image);
        var file = Services.Store.SaveImage(Note.Id, ImageTools.Png(shrunk), "png", "paste");
        var view = Viewport;
        var width = Math.Min(Note.Width - 96, shrunk.PixelWidth / 1.6);
        var height = width * shrunk.PixelHeight / Math.Max(1, shrunk.PixelWidth);
        var background = new PageBackground
        {
            File = file,
            X = Math.Round((Note.Width - width) / 2),
            Y = Math.Round(view.Top + 40),
            Width = Math.Round(width),
            Height = Math.Round(height),
            Page = (int)((view.Top + 40) / Paper.PageHeight)
        };
        _page.AddImages(new[] { background });
        Toast("Bild eingefügt. Unter „Mehr → Bilder bearbeiten“ kannst du es verschieben und vergrößern.");
    }

    private void Scan()
    {
        if (Note is null) return;
        var window = new ScanWindow(Note) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true || window.Pages.Count == 0) return;
        var placement = new ImportPlacementWindow(window.Pages.Count, window.Pages[0].Preview) { Owner = Window.GetWindow(this) };
        if (placement.ShowDialog() != true) return;
        PlaceImported(window.Pages.Select(p => (p.File, p.PixelWidth, p.PixelHeight)).ToList(), placement.NewPages, placement.WidthFraction);
    }

    /// <summary>Legt eingefügte Seiten auf neue Seiten oder an die sichtbare Stelle.</summary>
    public void PlaceImported(List<(string File, int PixelWidth, int PixelHeight)> images, bool newPages, double widthFraction)
    {
        if (Note is null || images.Count == 0) return;
        var width = Note.Width * widthFraction;
        var added = new List<PageBackground>();
        if (newPages)
        {
            var bottom = _page.ContentBottom;
            var start = bottom <= 1 ? 0 : (int)Math.Ceiling(bottom / Paper.PageHeight);
            for (var index = 0; index < images.Count; index++)
            {
                var (file, pw, ph) = images[index];
                var height = width * ph / Math.Max(1, pw);
                if (height > Paper.PageHeight - 32)
                {
                    height = Paper.PageHeight - 32;
                    var w = height * pw / Math.Max(1, ph);
                    added.Add(new PageBackground { File = file, Page = start + index, X = Math.Round((Note.Width - w) / 2), Y = (start + index) * Paper.PageHeight + 16, Width = Math.Round(w), Height = Math.Round(height) });
                    continue;
                }
                added.Add(new PageBackground { File = file, Page = start + index, X = Math.Round((Note.Width - width) / 2), Y = (start + index) * Paper.PageHeight + 16, Width = Math.Round(width), Height = Math.Round(height) });
            }
        }
        else
        {
            var top = Viewport.Top + 24;
            foreach (var (file, pw, ph) in images)
            {
                var height = width * ph / Math.Max(1, pw);
                added.Add(new PageBackground { File = file, Page = (int)(top / Paper.PageHeight), X = Math.Round((Note.Width - width) / 2), Y = Math.Round(top), Width = Math.Round(width), Height = Math.Round(height) });
                top += height + 16;
            }
        }
        var needed = (int)Math.Ceiling((added.Max(b => b.Y + b.Height) + 1) / Paper.PageHeight);
        if (needed > Note.PageCount)
        {
            Services.Store.UpdateNote(Note.Id, n => n.PageCount = needed, touch: false);
            _page.RefreshPaper();
            Services.Bridge.NoteMetaChanged(this);
        }
        _page.AddImages(added);
        _page.EnterImageMode();
        UpdateImageBar();
        var first = added[0];
        Dispatcher.BeginInvoke(() => _scroller.ScrollToVerticalOffset(Math.Max(0, first.Y * Zoom - 40)), DispatcherPriority.Loaded);
    }

    private void InsertGraph()
    {
        if (Note is null) return;
        var dialog = new FunctionWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        var result = dialog.Result;
        var settings = dialog.Settings;
        // Nullpunkt in die Mitte des sichtbaren Bereichs, auf das Kästchenraster gerundet.
        var view = Viewport;
        var originX = Math.Round(Note.Width / 2 / Paper.Spacing) * Paper.Spacing;
        var centerY = view.Top + Math.Min(view.Height, 700) / 2;
        var halfHeight = (settings.YMax - settings.YMin) * settings.Unit / 2;
        var originY = Math.Round(Math.Max(halfHeight + 24, centerY) / Paper.Spacing) * Paper.Spacing;
        var placed = GraphBuilder.Build(dialog.Function, settings, originX, originY);
        _page.PushUndo(includeInk: true);
        var labels = placed.Labels.Select(label => new NoteTextBox
        {
            X = label.X,
            Y = label.Y,
            Width = label.Width + 8,
            Text = label.Text,
            LineHeight = 20,
            Seed = new TextSeed { Text = label.Text, FontSize = 12.5, Color = "#5A6774" }
        }).ToList();
        _page.AddTextItems(labels);
        _page.ApplyInk(placed.Strokes, Array.Empty<string>());
        Services.Bridge.SendInkOps(this, placed.Strokes, new List<string>());
        ScheduleInkSave();
    }

    // MARK: - KI, Hausaufgaben, PDF

    public AiContext NoteContext(bool withImages = true)
    {
        _page.CommitAll();
        var note = Note!;
        var images = withImages
            ? PageRenderer.AiImages(note, _page.Content, _page.Ink, _page.Environment)
            : new List<GeminiImage>();
        return new AiContext(note.Title, _page.Content.PlainText, images);
    }

    private void OpenAi()
    {
        if (Note is null) return;
        var context = NoteContext();
        OpenAiWindow(context, AiWindow.Tab.Grammar, autoRun: false);
    }

    public void OpenAiWindow(AiContext context, AiWindow.Tab tab, bool autoRun)
    {
        if (Note is null) return;
        var note = Note;
        var window = new AiWindow(context, tab, autoRun)
        {
            Owner = Window.GetWindow(this),
            AppendText = text => InsertText(text),
            ReplaceText = text =>
            {
                // Korrekturen übernehmen: alle Textfelder in eines, in der Reihenfolge der Seite.
                var first = _page.Content.TextBoxes.OrderBy(b => b.Y).FirstOrDefault();
                _page.DeleteBoxesIn(new Rect(-10_000, -10_000, 20_000 + note.Width, 20_000 + note.PageCount * Paper.PageHeight));
                var model = new NoteTextBox
                {
                    X = first?.X ?? 80,
                    Y = first?.Y ?? TextDocs.SnapTop(40, _page.Environment),
                    Width = first?.Width ?? note.Width - 128,
                    Text = text,
                    Seed = new TextSeed { Text = text }
                };
                _page.AddTextItems(new[] { model });
            },
            SaveCards = cards => Services.Store.AddCards(cards, note.Id, note.Title)
        };
        window.Show();
    }

    private async Task FindHomeworkAsync()
    {
        if (Note is null) return;
        var client = Services.Gemini();
        if (client is null)
        {
            Toast("Dafür brauchst du einen Gemini-Schlüssel (Einstellungen → KI).");
            return;
        }
        var context = NoteContext();
        var cancel = ShowBusy(context.Images.Count == 0 ? "Suche Hausaufgaben im Text …" : $"Lese {Ui.Plural(context.Images.Count, "Seite", "Seiten")} …");
        try
        {
            var message = await AiTasks.FindHomeworkAsync(client, Services.Store, Note, context.TypedText, context.Images, cancel);
            Toast(message);
            MetaChanged?.Invoke();
        }
        catch (Exception error)
        {
            if (!cancel.IsCancellationRequested) Toast(GeminiClient.Describe(error));
        }
        finally
        {
            HideBusy();
        }
    }

    /// <summary>Beim Schließen einer Notiz in angehakten Fächern im Hintergrund suchen.</summary>
    private async Task AutoHomeworkAsync(NoteMeta note)
    {
        if (!Services.Settings.GetBool(Keys.AutoHomework, false)) return;
        var notebook = Services.Store.NotebookOf(note);
        if (notebook is null || !notebook.ScanHomework) return;
        var client = Services.Gemini();
        if (client is null) return;
        try
        {
            var content = Services.Store.LoadContent(note.Id);
            var ink = Services.Store.LoadInk(note.Id);
            var images = PageRenderer.AiImages(note, content, ink, TextEnvironment.FromSettings(note.PaperStyle), limit: 2);
            var message = await AiTasks.FindHomeworkAsync(client, Services.Store, note, content.PlainText, images);
            Services.Log("Hausaufgaben: " + message);
            MetaChanged?.Invoke();
        }
        catch (Exception error)
        {
            Services.Log("Hausaufgaben-Suche: " + error.Message);
        }
    }

    private async Task ExportPdfAsync()
    {
        if (Note is null) return;
        SaveNow();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Als PDF sichern",
            FileName = PdfExport.SafeName(Note.Title) + ".pdf",
            Filter = "PDF|*.pdf",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        ShowBusy("Erstelle das PDF …", cancellable: false);
        try
        {
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
            PdfExport.Write(dialog.FileName, new[] { Note });
            Toast("PDF gespeichert: " + Path.GetFileName(dialog.FileName));
        }
        catch (Exception error)
        {
            Toast("Das PDF konnte nicht erstellt werden: " + error.Message);
        }
        finally
        {
            HideBusy();
        }
    }

    private void Print()
    {
        if (Note is null) return;
        SaveNow();
        PdfExport.Print(Window.GetWindow(this), Note);
    }
}
