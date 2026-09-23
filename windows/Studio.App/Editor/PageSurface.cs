using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Lernheft.Studio.App.Editor;

/// <summary>
/// Eine Notizseite: Papier, eingefügte Bilder, Textfelder und die Handschrift vom iPad.
/// Alle Maße sind Seiteneinheiten (800 breit, 1120 je Seite) – vergrößert wird von außen.
///
/// Wie in OneNote: Irgendwo hinklicken legt ein Textfeld an und setzt den Cursor hinein. Bleibt es
/// leer, verschwindet es wieder, sobald man woanders hinklickt.
/// </summary>
public sealed class PageSurface : Canvas
{
    private readonly PaperLayer _paper = new();
    private readonly ImageLayer _images = new();
    private readonly Canvas _texts = new() { ClipToBounds = false };
    private readonly InkLayer _ink = new();
    private readonly Canvas _chrome = new() { ClipToBounds = false };
    private readonly Dictionary<Guid, TextBoxView> _boxes = new();

    // Rahmen um das Textfeld, in dem man gerade schreibt (oder über dem die Maus steht).
    private readonly Rectangle _focusFrame = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed, RadiusX = 3, RadiusY = 3 };
    private readonly Rectangle _hoverFrame = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed, RadiusX = 3, RadiusY = 3 };
    private readonly Border _bar = new() { Visibility = Visibility.Collapsed, Cursor = Cursors.SizeAll };
    private readonly Border _widthHandle = new() { Visibility = Visibility.Collapsed, Cursor = Cursors.SizeWE, Background = Brushes.Transparent };
    private readonly Border _widthGrip = new() { IsHitTestVisible = false, CornerRadius = new CornerRadius(2) };
    private readonly Button _deleteBox = new();

    // Bilder bearbeiten
    private readonly Rectangle _imageFrame = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Ellipse _imageHandle = new() { Visibility = Visibility.Collapsed, Cursor = Cursors.SizeNWSE };
    private readonly Ellipse _eraserCursor = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };

    private TextBoxView? _focused;
    private TextBoxView? _hovered;
    private TextEnvironment _environment = new(PaperStyle.Lined, TextDocs.DefaultFont, 18, TextDocs.DefaultScriptFont, true);
    private double _zoom = 1;

    public NoteMeta? Note { get; private set; }
    public NoteContent Content { get; private set; } = new();
    public InkDocument Ink { get; private set; } = new();

    /// <summary>Text oder Bilder haben sich geändert.</summary>
    public event Action? ContentChanged;

    /// <summary>Die Handschrift wurde hier am Surface geändert (z. B. Graph eingefügt, Rückgängig).</summary>
    public event Action<List<InkStroke>, List<string>>? InkEdited;

    public event Action<TextBoxView?>? FocusedBoxChanged;
    public event Action? SelectionFormatChanged;
    public event Action<string>? Message;
    public event Action<int>? PagesNeeded;

    /// <summary>Ein Strich vom iPad ist hier gerade gewachsen – damit man ihm folgen kann.</summary>
    public event Action<Point>? LivePoint;

    public TextBoxView? FocusedBox => _focused;

    public IEnumerable<TextBoxView> Boxes => _boxes.Values;

    public bool ImageMode { get; private set; }
    public bool ImageEraser { get; private set; }
    public Guid? SelectedImage { get; private set; }
    public event Action? ImageSelectionChanged;

    public PageSurface()
    {
        Background = Brushes.Transparent;
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = false;
        Children.Add(_paper);
        Children.Add(_images);
        Children.Add(_texts);
        Children.Add(_ink);
        Children.Add(_chrome);

        _focusFrame.SetResourceReference(Shape.StrokeProperty, "Accent");
        _hoverFrame.SetResourceReference(Shape.StrokeProperty, "LineStrong");
        _bar.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        _bar.SetResourceReference(Border.BorderBrushProperty, "Accent");
        _widthGrip.SetResourceReference(Border.BackgroundProperty, "Accent");
        _widthHandle.Child = _widthGrip;
        _imageFrame.SetResourceReference(Shape.StrokeProperty, "Accent");
        _imageHandle.SetResourceReference(Shape.FillProperty, "Accent");
        _imageHandle.SetResourceReference(Shape.StrokeProperty, "Surface");
        _eraserCursor.SetResourceReference(Shape.StrokeProperty, "Bad");
        _eraserCursor.Fill = new SolidColorBrush(Color.FromArgb(40, 220, 60, 60));

        BuildBar();
        _chrome.Children.Add(_hoverFrame);
        _chrome.Children.Add(_focusFrame);
        _chrome.Children.Add(_bar);
        _chrome.Children.Add(_widthHandle);
        _chrome.Children.Add(_imageFrame);
        _chrome.Children.Add(_imageHandle);
        _chrome.Children.Add(_eraserCursor);

        _bar.MouseLeftButtonDown += BarDown;
        _bar.MouseMove += BarMove;
        _bar.MouseLeftButtonUp += BarUp;
        _widthHandle.MouseLeftButtonDown += WidthDown;
        _widthHandle.MouseMove += WidthMove;
        _widthHandle.MouseLeftButtonUp += WidthUp;
        _imageHandle.MouseLeftButtonDown += ImageHandleDown;
        _imageHandle.MouseMove += ImageHandleMove;
        _imageHandle.MouseLeftButtonUp += ImageHandleUp;

        MouseMove += TrackHover;
        MouseLeave += (_, _) => SetHovered(null);
    }

    private void BuildBar()
    {
        var grip = new TextBlock
        {
            Text = "• • • •",
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        grip.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        _deleteBox.Style = Ui.Style("IconButton");
        _deleteBox.Content = Ui.Icon(Ui.GlyphDelete, 10);
        _deleteBox.ToolTip = "Textfeld löschen";
        _deleteBox.HorizontalAlignment = HorizontalAlignment.Right;
        _deleteBox.Focusable = false;
        _deleteBox.Click += (_, _) =>
        {
            if (_focused is not { } box) return;
            PushUndo();
            RemoveBox(box);
            ContentChanged?.Invoke();
        };
        var grid = new Grid();
        grid.Children.Add(grip);
        grid.Children.Add(_deleteBox);
        _bar.Child = grid;
    }

    // MARK: - Laden

    public void Load(NoteMeta note, NoteContent content, InkDocument ink, TextEnvironment environment)
    {
        Note = note;
        Content = content;
        Ink = ink;
        _environment = environment;
        _undo.Clear();
        _redo.Clear();
        ExitImageMode();
        SetFocused(null);
        SetHovered(null);

        _texts.Children.Clear();
        _boxes.Clear();
        foreach (var model in content.TextBoxes.ToList()) AddView(model);

        _images.NoteId = note.Id;
        _images.Seamless = Services.Settings.GetBool(Keys.SeamlessImport, true);
        _images.Dark = Theme.IsDark;
        _images.SetAll(content.Backgrounds, Services.Store, note.Width);
        _ink.SetAll(ink.Strokes, Theme.IsDark);
        RefreshPaper();
    }

    public void RefreshPaper()
    {
        if (Note is null) return;
        _paper.Kind = Note.PaperStyle;
        _paper.Pages = Math.Max(1, Note.PageCount);
        _paper.PageWidth = Note.Width;
        _paper.Zoom = _zoom;
        _paper.Refresh();
        Width = Note.Width;
        Height = Math.Max(1, Note.PageCount) * Paper.PageHeight;
        _texts.Width = _ink.Width = _chrome.Width = _images.Width = Width;
        _texts.Height = _ink.Height = _chrome.Height = _images.Height = Height;
    }

    /// <summary>Nach Wechsel von Hell/Dunkel oder der Einstellung „nahtlos“.</summary>
    public void RefreshTheme()
    {
        if (Note is null) return;
        _paper.InvalidateVisual();
        _images.Dark = Theme.IsDark;
        _images.Seamless = Services.Settings.GetBool(Keys.SeamlessImport, true);
        _images.SetAll(Content.Backgrounds, Services.Store, Note.Width);
        _ink.SetAll(Ink.Strokes, Theme.IsDark);
        UpdateChrome();
    }

    public void SetEnvironment(TextEnvironment environment)
    {
        _environment = environment;
        foreach (var box in _boxes.Values) box.Environment = environment;
    }

    public TextEnvironment Environment => _environment;

    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Max(0.1, value);
            _paper.Zoom = _zoom;
            _paper.InvalidateVisual();
            UpdateChrome();
        }
    }

    // MARK: - Textfelder

    private TextBoxView AddView(NoteTextBox model)
    {
        var view = new TextBoxView(model, _environment);
        SetLeft(view, model.X);
        SetTop(view, model.Y);
        view.Edited += OnBoxEdited;
        view.GotKeyboardFocus += (_, _) => SetFocused(view);
        view.LostKeyboardFocus += (_, e) => OnBoxLostFocus(view, e);
        view.SelectionChanged += (_, _) =>
        {
            if (view == _focused) SelectionFormatChanged?.Invoke();
        };
        view.SizeChanged += (_, _) =>
        {
            if (view == _focused || view == _hovered) UpdateChrome();
        };
        _texts.Children.Add(view);
        _boxes[model.Id] = view;
        return view;
    }

    private void OnBoxEdited(TextBoxView view)
    {
        if (view == _focused) UpdateChrome();
        CheckPages(Canvas.GetTop(view) + view.ActualHeight);
        ContentChanged?.Invoke();
    }

    private void OnBoxLostFocus(TextBoxView view, KeyboardFocusChangedEventArgs e)
    {
        // Das Kontextmenü der Rechtschreibprüfung oder die Formatleiste nehmen den Fokus nur kurz.
        if (e.NewFocus is DependencyObject target && IsInsideBox(target, view)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (view.IsKeyboardFocusWithin) return;
            if (_focused == view) SetFocused(null);
            if (view.IsEmpty && _boxes.ContainsKey(view.Model.Id))
            {
                RemoveBox(view);
                ContentChanged?.Invoke();
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private static bool IsInsideBox(DependencyObject target, TextBoxView view)
    {
        for (var current = target; current is not null; current = VisualOrLogicalParent(current))
        {
            if (current == view) return true;
        }
        return false;
    }

    private static DependencyObject? VisualOrLogicalParent(DependencyObject item) =>
        item is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(item) ?? LogicalTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);

    public TextBoxView AddBox(NoteTextBox model, bool focus = false)
    {
        Content.TextBoxes.Add(model);
        var view = AddView(model);
        if (focus)
        {
            view.Focus();
            view.CaretPosition = view.Document.ContentEnd;
        }
        return view;
    }

    private void RemoveBox(TextBoxView view)
    {
        if (_focused == view) SetFocused(null);
        if (_hovered == view) SetHovered(null);
        _texts.Children.Remove(view);
        _boxes.Remove(view.Model.Id);
        Content.TextBoxes.RemoveAll(b => b.Id == view.Model.Id);
    }

    /// <summary>Neues Textfeld an der Stelle, an die geklickt wurde.</summary>
    public TextBoxView CreateBoxAt(Point point)
    {
        var model = new NoteTextBox { X = Math.Max(4, Math.Round(point.X - 2)), Width = DefaultWidth(point.X) };

        // Liegt dort ein Arbeitsblatt mit Linien? Dann auf dessen Linien schreiben.
        var placed = false;
        if (Note is not null && _environment.SnapToLines)
        {
            foreach (var background in Content.Backgrounds)
            {
                if (!_images.Originals.TryGetValue(background.Id, out var source)) continue;
                var frame = ImageLayer.Frame(background, source, Note.Width);
                if (!frame.Contains(point)) continue;
                var found = WorksheetLines.Detect(source, frame.Top, frame.Height);
                if (!found.Found || found.Spacing < 14) continue;
                var line = found.Lines.Where(l => l >= point.Y - 4).DefaultIfEmpty(found.Nearest(point.Y)).Min();
                model.LineHeight = Math.Round(found.Spacing, 1);
                model.Y = TextDocs.SnapToLine(line, found.Spacing, _environment);
                placed = true;
                Message?.Invoke($"Arbeitsblatt erkannt: Der Text sitzt auf seinen Linien ({found.Lines.Count} Linien).");
                break;
            }
        }
        if (!placed) model.Y = TextDocs.SnapTop(point.Y, _environment);
        return AddBox(model, focus: true);
    }

    private double DefaultWidth(double x)
    {
        var right = (Note?.Width ?? Paper.PageWidth) - 40;
        return Math.Max(160, Math.Min(620, right - x));
    }

    public void AddTextItems(IEnumerable<NoteTextBox> models)
    {
        foreach (var model in models) AddBox(model);
        ContentChanged?.Invoke();
    }

    /// <summary>Löscht Textfelder, deren Mitte im Rechteck liegt (Lasso „Löschen“ auf dem iPad).</summary>
    public int DeleteBoxesIn(Rect area)
    {
        var gone = _boxes.Values.Where(view =>
        {
            var center = new Point(GetLeft(view) + view.ActualWidth / 2, GetTop(view) + Math.Max(16, view.ActualHeight) / 2);
            return area.Contains(center);
        }).ToList();
        if (gone.Count == 0) return 0;
        PushUndo();
        foreach (var view in gone) RemoveBox(view);
        ContentChanged?.Invoke();
        return gone.Count;
    }

    public void CommitAll()
    {
        foreach (var view in _boxes.Values)
        {
            view.Commit();
            view.Model.X = GetLeft(view);
            view.Model.Y = GetTop(view);
        }
    }

    public void CommitBox(TextBoxView view)
    {
        view.Commit();
        view.Model.X = GetLeft(view);
        view.Model.Y = GetTop(view);
    }

    /// <summary>Unterkante von allem, was auf der Seite steht.</summary>
    public double ContentBottom
    {
        get
        {
            var bottom = Ink.Bottom;
            foreach (var view in _boxes.Values) bottom = Math.Max(bottom, GetTop(view) + view.ActualHeight);
            foreach (var background in Content.Backgrounds)
            {
                _images.Originals.TryGetValue(background.Id, out var source);
                bottom = Math.Max(bottom, ImageLayer.Frame(background, source, Note?.Width ?? Paper.PageWidth).Bottom);
            }
            return bottom;
        }
    }

    private void CheckPages(double bottom)
    {
        if (Note is null) return;
        var needed = (int)Math.Ceiling((bottom + 160) / Paper.PageHeight);
        if (needed > Note.PageCount) PagesNeeded?.Invoke(needed);
    }

    // MARK: - Klicks auf die Seite

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (e.OriginalSource is DependencyObject source && (IsChrome(source) || InsideAnyBox(source))) return;
        var point = e.GetPosition(this);
        if (ImageMode)
        {
            ImageDown(point);
            e.Handled = true;
            return;
        }
        // Klick ins Leere: neues Feld – außer man verlässt gerade ein Feld, dann nur verlassen.
        if (_focused is { } focused && !focused.IsEmpty)
        {
            Keyboard.ClearFocus();
            Focus();
        }
        if (point.Y > Height - 4) return;
        CreateBoxAt(point);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
    }

    private bool IsChrome(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualOrLogicalParent(current))
        {
            if (current == _chrome) return true;
            if (current == this) return false;
        }
        return false;
    }

    private bool InsideAnyBox(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualOrLogicalParent(current))
        {
            if (current is TextBoxView) return true;
            if (current == this) return false;
        }
        return false;
    }

    private void TrackHover(object sender, MouseEventArgs e)
    {
        if (ImageMode)
        {
            UpdateEraserCursor(e.GetPosition(this));
            return;
        }
        var source = e.OriginalSource as DependencyObject;
        TextBoxView? box = null;
        for (var current = source; current is not null && current != this; current = VisualOrLogicalParent(current))
        {
            if (current is TextBoxView view)
            {
                box = view;
                break;
            }
        }
        SetHovered(box == _focused ? null : box);
    }

    private void SetFocused(TextBoxView? view)
    {
        if (_focused == view) return;
        if (_focused is not null) CommitBox(_focused);
        _focused = view;
        UpdateChrome();
        FocusedBoxChanged?.Invoke(view);
    }

    private void SetHovered(TextBoxView? view)
    {
        if (_hovered == view) return;
        _hovered = view;
        UpdateChrome();
    }

    // MARK: - Rahmen, Griff und Breitenregler

    public void UpdateChrome()
    {
        var unit = 1 / _zoom;
        Place(_focusFrame, _focused, unit, 1.2 * unit, null);
        Place(_hoverFrame, _hovered is not null && _hovered != _focused ? _hovered : null, unit, unit, new DoubleCollection { 3, 3 });

        if (_focused is { } box && !ImageMode)
        {
            var left = GetLeft(box);
            var top = GetTop(box);
            var width = Math.Max(box.ActualWidth, 40);
            var height = Math.Max(box.ActualHeight, 16);
            var barHeight = 18 * unit;
            _bar.Visibility = Visibility.Visible;
            _bar.Width = width + 12 * unit;
            _bar.Height = barHeight;
            _bar.CornerRadius = new CornerRadius(5 * unit, 5 * unit, 0, 0);
            _bar.BorderThickness = new Thickness(1.2 * unit, 1.2 * unit, 1.2 * unit, 0);
            SetLeft(_bar, left - 6 * unit);
            SetTop(_bar, top - 4 * unit - barHeight);
            _deleteBox.Width = _deleteBox.Height = barHeight;
            _deleteBox.Margin = new Thickness(0, 0, 2 * unit, 0);
            ((TextBlock)_deleteBox.Content).FontSize = 10 * unit;
            if (((Grid)_bar.Child).Children[0] is TextBlock grip) grip.FontSize = 8 * unit;

            _widthHandle.Visibility = Visibility.Visible;
            _widthHandle.Width = 12 * unit;
            _widthHandle.Height = height + 8 * unit;
            SetLeft(_widthHandle, left + width + 6 * unit - 6 * unit);
            SetTop(_widthHandle, top - 4 * unit);
            _widthGrip.Width = 3 * unit;
            _widthGrip.Height = Math.Min(28 * unit, height);
            _widthGrip.HorizontalAlignment = HorizontalAlignment.Center;
            _widthGrip.VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            _bar.Visibility = Visibility.Collapsed;
            _widthHandle.Visibility = Visibility.Collapsed;
        }
        UpdateImageChrome();
    }

    private void Place(Rectangle frame, TextBoxView? box, double unit, double thickness, DoubleCollection? dash)
    {
        if (box is null || ImageMode)
        {
            frame.Visibility = Visibility.Collapsed;
            return;
        }
        frame.Visibility = Visibility.Visible;
        frame.StrokeThickness = thickness;
        frame.StrokeDashArray = dash;
        frame.RadiusX = frame.RadiusY = 3 * unit;
        frame.Width = Math.Max(box.ActualWidth, 40) + 12 * unit;
        frame.Height = Math.Max(box.ActualHeight, 16) + 8 * unit;
        SetLeft(frame, GetLeft(box) - 6 * unit);
        SetTop(frame, GetTop(box) - 4 * unit);
    }

    private Point _dragStart;
    private Point _dragOrigin;
    private double _widthOrigin;
    private bool _dragging;

    private void BarDown(object sender, MouseButtonEventArgs e)
    {
        if (_focused is null || e.OriginalSource is DependencyObject source && IsDeleteButton(source)) return;
        PushUndo();
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragOrigin = new Point(GetLeft(_focused), GetTop(_focused));
        _bar.CaptureMouse();
        e.Handled = true;
    }

    private bool IsDeleteButton(DependencyObject source)
    {
        for (var current = source; current is not null && current != _bar; current = VisualOrLogicalParent(current))
        {
            if (current == _deleteBox) return true;
        }
        return false;
    }

    private void BarMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _focused is null) return;
        var delta = e.GetPosition(this) - _dragStart;
        SetLeft(_focused, Math.Clamp(_dragOrigin.X + delta.X, 0, Math.Max(0, Width - 40)));
        SetTop(_focused, Math.Max(0, _dragOrigin.Y + delta.Y));
        UpdateChrome();
    }

    private void BarUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || _focused is null) return;
        _dragging = false;
        _bar.ReleaseMouseCapture();
        // Beim Loslassen auf die Linien setzen.
        SetLeft(_focused, Math.Round(GetLeft(_focused)));
        SetTop(_focused, TextDocs.SnapExisting(GetTop(_focused), _environment, _focused.Model));
        CommitBox(_focused);
        UpdateChrome();
        CheckPages(GetTop(_focused) + _focused.ActualHeight);
        ContentChanged?.Invoke();
        _focused.Focus();
    }

    private void WidthDown(object sender, MouseButtonEventArgs e)
    {
        if (_focused is null) return;
        PushUndo();
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _widthOrigin = _focused.Width;
        _widthHandle.CaptureMouse();
        e.Handled = true;
    }

    private void WidthMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _focused is null) return;
        var delta = e.GetPosition(this).X - _dragStart.X;
        var max = Math.Max(80, Width - GetLeft(_focused) - 4);
        _focused.Width = Math.Clamp(_widthOrigin + delta, 60, max);
        UpdateChrome();
    }

    private void WidthUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || _focused is null) return;
        _dragging = false;
        _widthHandle.ReleaseMouseCapture();
        _focused.Width = Math.Round(_focused.Width);
        CommitBox(_focused);
        ContentChanged?.Invoke();
        _focused.Focus();
    }

    // MARK: - Handschrift

    public void SetInk(InkDocument ink)
    {
        Ink = ink;
        _ink.SetAll(ink.Strokes, Theme.IsDark);
    }

    /// <summary>Striche hinzu oder weg – vom iPad oder von hier.</summary>
    public void ApplyInk(IEnumerable<InkStroke> add, IEnumerable<string> remove, IEnumerable<string>? finishedLive = null)
    {
        var added = add.ToList();
        var removed = remove.ToList();
        Ink.Apply(added, removed);
        foreach (var id in removed) _ink.Remove(id);
        foreach (var stroke in added) _ink.Add(stroke);
        foreach (var sid in finishedLive ?? Enumerable.Empty<string>()) _ink.RemoveLive(sid);
        if (added.Count > 0) CheckPages(added.Max(s => s.Bounds().MaxY));
    }

    public void UpdateLive(string sid, InkStroke template, IReadOnlyList<double> points)
    {
        _ink.UpdateLive(sid, template, points);
        if (points.Count >= 3) LivePoint?.Invoke(new Point(points[^3], points[^2]));
    }

    public void EndLive(string sid) => _ink.RemoveLive(sid);

    public void ClearLive() => _ink.ClearLive();

    /// <summary>Striche, die hier am Surface entstehen (Funktion zeichnen): speichern und dem iPad melden.</summary>
    public void AddInkFromSurface(List<InkStroke> strokes)
    {
        if (strokes.Count == 0) return;
        PushUndo(includeInk: true);
        ApplyInk(strokes, Array.Empty<string>());
        InkEdited?.Invoke(strokes, new List<string>());
    }

    // MARK: - Bilder

    public void ReloadImages()
    {
        if (Note is null) return;
        _images.SetAll(Content.Backgrounds, Services.Store, Note.Width);
        UpdateChrome();
    }

    public BitmapSource? ImageSource(Guid id) => _images.Originals.TryGetValue(id, out var source) ? source : null;

    public Rect ImageFrame(PageBackground background) =>
        ImageLayer.Frame(background, ImageSource(background.Id), Note?.Width ?? Paper.PageWidth);

    public void EnterImageMode()
    {
        ImageMode = true;
        ImageEraser = false;
        SelectedImage = Content.Backgrounds.LastOrDefault()?.Id;
        Keyboard.ClearFocus();
        SetFocused(null);
        SetHovered(null);
        Cursor = Cursors.Arrow;
        UpdateChrome();
        ImageSelectionChanged?.Invoke();
    }

    public void ExitImageMode()
    {
        if (!ImageMode) return;
        ImageMode = false;
        ImageEraser = false;
        SelectedImage = null;
        Cursor = null;
        _eraserCursor.Visibility = Visibility.Collapsed;
        UpdateChrome();
        ImageSelectionChanged?.Invoke();
    }

    public void SetImageEraser(bool on)
    {
        ImageEraser = on;
        Cursor = on ? Cursors.None : Cursors.Arrow;
        if (!on) _eraserCursor.Visibility = Visibility.Collapsed;
        UpdateChrome();
        ImageSelectionChanged?.Invoke();
    }

    private void UpdateImageChrome()
    {
        var background = ImageMode && SelectedImage is Guid id ? Content.Backgrounds.FirstOrDefault(b => b.Id == id) : null;
        if (background is null)
        {
            _imageFrame.Visibility = _imageHandle.Visibility = Visibility.Collapsed;
            return;
        }
        var unit = 1 / _zoom;
        var frame = ImageFrame(background);
        _imageFrame.Visibility = Visibility.Visible;
        _imageFrame.StrokeThickness = 2 * unit;
        _imageFrame.StrokeDashArray = new DoubleCollection { 4, 3 };
        _imageFrame.Width = frame.Width;
        _imageFrame.Height = frame.Height;
        SetLeft(_imageFrame, frame.X);
        SetTop(_imageFrame, frame.Y);
        _imageHandle.Visibility = ImageEraser ? Visibility.Collapsed : Visibility.Visible;
        _imageHandle.Width = _imageHandle.Height = 22 * unit;
        _imageHandle.StrokeThickness = 2 * unit;
        SetLeft(_imageHandle, frame.Right - 11 * unit);
        SetTop(_imageHandle, frame.Bottom - 11 * unit);
    }

    private enum ImageDrag { None, Move, Erase }
    private ImageDrag _imageDrag;
    private Rect _imageStart;
    private readonly List<Point> _erasePoints = new();

    private void ImageDown(Point point)
    {
        var hit = Content.Backgrounds.AsEnumerable().Reverse().FirstOrDefault(b => ImageFrame(b).Contains(point));
        if (ImageEraser)
        {
            if (SelectedImage is null && hit is not null) SelectedImage = hit.Id;
            if (SelectedImage is null) return;
            _imageDrag = ImageDrag.Erase;
            _erasePoints.Clear();
            _erasePoints.Add(point);
            CaptureMouse();
            return;
        }
        SelectedImage = hit?.Id;
        ImageSelectionChanged?.Invoke();
        UpdateChrome();
        if (hit is null) return;
        PushUndo();
        _imageDrag = ImageDrag.Move;
        _dragStart = point;
        _imageStart = ImageFrame(hit);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!ImageMode || _imageDrag == ImageDrag.None) return;
        var point = e.GetPosition(this);
        var background = Content.Backgrounds.FirstOrDefault(b => b.Id == SelectedImage);
        if (background is null) return;
        if (_imageDrag == ImageDrag.Move)
        {
            var delta = point - _dragStart;
            SetFrame(background, new Rect(_imageStart.X + delta.X, Math.Max(0, _imageStart.Y + delta.Y), _imageStart.Width, _imageStart.Height));
        }
        else if (_imageDrag == ImageDrag.Erase)
        {
            if (_erasePoints.Count == 0 || (_erasePoints[^1] - point).Length > 3) _erasePoints.Add(point);
            UpdateEraserCursor(point);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!ImageMode || _imageDrag == ImageDrag.None) return;
        var drag = _imageDrag;
        _imageDrag = ImageDrag.None;
        ReleaseMouseCapture();
        if (drag == ImageDrag.Erase && SelectedImage is Guid id)
        {
            PushUndo();
            EraseInImages(new[] { id }, _erasePoints, EraserRadius);
            _erasePoints.Clear();
        }
        ContentChanged?.Invoke();
    }

    private const double EraserRadius = 14;

    private void UpdateEraserCursor(Point point)
    {
        if (!ImageEraser)
        {
            _eraserCursor.Visibility = Visibility.Collapsed;
            return;
        }
        _eraserCursor.Visibility = Visibility.Visible;
        _eraserCursor.Width = _eraserCursor.Height = EraserRadius * 2;
        _eraserCursor.StrokeThickness = 1.5 / _zoom;
        SetLeft(_eraserCursor, point.X - EraserRadius);
        SetTop(_eraserCursor, point.Y - EraserRadius);
    }

    private void ImageHandleDown(object sender, MouseButtonEventArgs e)
    {
        var background = Content.Backgrounds.FirstOrDefault(b => b.Id == SelectedImage);
        if (background is null) return;
        PushUndo();
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _imageStart = ImageFrame(background);
        _imageHandle.CaptureMouse();
        e.Handled = true;
    }

    private void ImageHandleMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var background = Content.Backgrounds.FirstOrDefault(b => b.Id == SelectedImage);
        if (background is null) return;
        var delta = e.GetPosition(this).X - _dragStart.X;
        var ratio = _imageStart.Height / Math.Max(1, _imageStart.Width);
        var width = Math.Max(60, _imageStart.Width + delta);
        SetFrame(background, new Rect(_imageStart.X, _imageStart.Y, width, width * ratio));
    }

    private void ImageHandleUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _imageHandle.ReleaseMouseCapture();
        ContentChanged?.Invoke();
    }

    private void SetFrame(PageBackground background, Rect frame)
    {
        background.X = Math.Round(frame.X, 1);
        background.Y = Math.Round(frame.Y, 1);
        background.Width = Math.Round(frame.Width, 1);
        background.Height = Math.Round(frame.Height, 1);
        background.Page = (int)(Math.Max(0, frame.Y) / Paper.PageHeight);
        _images.Move(background, Note?.Width ?? Paper.PageWidth);
        UpdateImageChrome();
        CheckPages(frame.Bottom);
    }

    /// <summary>Radiert in Bildern – vom Radierer hier oder vom Radierer auf dem iPad.</summary>
    public bool EraseInImages(IEnumerable<Guid>? only, IReadOnlyList<Point> points, double radius)
    {
        if (Note is null || points.Count == 0) return false;
        var changed = false;
        foreach (var background in Content.Backgrounds.ToList())
        {
            if (only is not null && !only.Contains(background.Id)) continue;
            if (!_images.Originals.TryGetValue(background.Id, out var source)) continue;
            var frame = ImageFrame(background);
            var grown = frame;
            grown.Inflate(radius, radius);
            var hits = points.Where(grown.Contains).ToList();
            if (hits.Count == 0) continue;
            var scaleX = source.PixelWidth / Math.Max(1, frame.Width);
            var scaleY = source.PixelHeight / Math.Max(1, frame.Height);
            var local = hits.Select(p => new Point((p.X - frame.X) * scaleX, (p.Y - frame.Y) * scaleY)).ToList();
            var erased = ImageTools.Erase(source, local, radius * scaleX);

            var store = Services.Store;
            if (background.OriginalFile is null)
            {
                var copy = "orig-" + Guid.NewGuid().ToString("N")[..8] + "-" + background.File;
                try
                {
                    System.IO.File.Copy(store.ImagePath(Note.Id, background.File), store.ImagePath(Note.Id, copy));
                    background.OriginalFile = copy;
                }
                catch (System.IO.IOException) { }
            }
            if (!background.File.StartsWith("edit-")) background.File = "edit-" + Guid.NewGuid().ToString("N")[..8] + ".png";
            System.IO.File.WriteAllBytes(store.ImagePath(Note.Id, background.File), ImageTools.Png(erased));
            _images.Place(background, store, Note.Width, erased);
            changed = true;
        }
        if (changed) ContentChanged?.Invoke();
        return changed;
    }

    public void RestoreSelectedImage()
    {
        if (Note is null || Content.Backgrounds.FirstOrDefault(b => b.Id == SelectedImage) is not { } background) return;
        if (background.OriginalFile is not { } original || !System.IO.File.Exists(Services.Store.ImagePath(Note.Id, original)))
        {
            Message?.Invoke("Für dieses Bild gibt es keine unveränderte Fassung.");
            return;
        }
        PushUndo();
        background.File = original;
        background.OriginalFile = null;
        _images.Place(background, Services.Store, Note.Width);
        ContentChanged?.Invoke();
    }

    public void DeleteSelectedImage()
    {
        if (Content.Backgrounds.FirstOrDefault(b => b.Id == SelectedImage) is not { } background) return;
        PushUndo();
        Content.Backgrounds.Remove(background);
        _images.Remove(background.Id);
        SelectedImage = null;
        UpdateChrome();
        ImageSelectionChanged?.Invoke();
        ContentChanged?.Invoke();
    }

    public void AddImages(IEnumerable<PageBackground> backgrounds)
    {
        if (Note is null) return;
        PushUndo();
        foreach (var background in backgrounds)
        {
            Content.Backgrounds.Add(background);
            _images.Place(background, Services.Store, Note.Width);
            CheckPages(background.Y + background.Height);
        }
        ContentChanged?.Invoke();
    }

    // MARK: - Rückgängig (für Verschieben, Löschen, Bilder, Graphen)

    private record Snapshot(string Content, string? Ink);

    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void PushUndo(bool includeInk = false)
    {
        CommitAll();
        _undo.Push(Capture(includeInk));
        if (_undo.Count > 60)
        {
            var keep = _undo.Take(60).Reverse().ToList();
            _undo.Clear();
            foreach (var item in keep) _undo.Push(item);
        }
        _redo.Clear();
    }

    private Snapshot Capture(bool includeInk) => new(
        JsonSerializer.Serialize(Content, LibraryStore.Json),
        includeInk ? Ink.ToJson() : null);

    public void Undo() => Step(_undo, _redo);

    public void Redo() => Step(_redo, _undo);

    private void Step(Stack<Snapshot> from, Stack<Snapshot> to)
    {
        if (from.Count == 0 || Note is null) return;
        CommitAll();
        var target = from.Pop();
        to.Push(Capture(target.Ink is not null));
        var content = JsonSerializer.Deserialize<NoteContent>(target.Content, LibraryStore.Json) ?? new NoteContent();
        Content.TextBoxes = content.TextBoxes;
        Content.Backgrounds = content.Backgrounds;
        SetFocused(null);
        _texts.Children.Clear();
        _boxes.Clear();
        foreach (var model in Content.TextBoxes) AddView(model);
        _images.SetAll(Content.Backgrounds, Services.Store, Note.Width);

        if (target.Ink is not null)
        {
            var restored = JsonSerializer.Deserialize<InkDocument>(target.Ink, InkDocument.Options) ?? new InkDocument();
            var before = Ink.Strokes.Where(s => s.Id is not null).ToDictionary(s => s.Id!);
            var after = restored.Strokes.Where(s => s.Id is not null).ToDictionary(s => s.Id!);
            var removed = before.Keys.Where(id => !after.ContainsKey(id)).ToList();
            var added = after.Values.Where(s => !before.ContainsKey(s.Id!)).ToList();
            Ink = restored;
            _ink.SetAll(Ink.Strokes, Theme.IsDark);
            if (added.Count > 0 || removed.Count > 0) InkEdited?.Invoke(added, removed);
        }
        UpdateChrome();
        ContentChanged?.Invoke();
    }
}
