using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lernheft.Studio.App.Editor;

namespace Lernheft.Studio.App.Sheets;

/// <summary>Funktion eintippen, Vorschau ansehen, als echte Striche in die Notiz setzen.</summary>
public sealed class FunctionWindow : DialogWindow
{
    private readonly TextBox _term;
    private readonly TextBlock _error = new() { FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Canvas _preview = new() { Height = 250, ClipToBounds = true };
    private readonly ComboBox _xRange = new() { Width = 96 };
    private readonly ComboBox _yRange = new() { Width = 96 };
    private readonly ComboBox _unit = new() { Width = 96 };
    private readonly ComboBox _color = new() { Width = 120 };
    private readonly CheckBox _axes;
    private readonly CheckBox _labels;

    public Func<double, double?>? Function { get; private set; }
    public GraphBuilder.Settings Settings { get; private set; } = new();
    public GraphBuilder.Result? Result { get; private set; }

    private static readonly (string Label, string Hex)[] Colors =
    {
        ("Schwarz", "#1A1F2B"), ("Blau", "#2140C8"), ("Rot", "#D2413A"), ("Grün", "#1E8A57")
    };

    public FunctionWindow() : base("Funktion zeichnen", 560)
    {
        var settings = Services.Settings;
        Body.Children.Add(Label("f(x) =", first: true));
        _term = Ui.Field(settings.Get("graphTerm", "x^2"), "x^2 - 2x + 1");
        _term.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        _term.FontSize = 15;
        Body.Children.Add(_term);
        var examples = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var example in new[] { "x^2", "0,5x^2-2x+1", "-x^2+4", "2x+1", "1/x", "sin(x)", "sqrt(x)" })
        {
            var button = Ui.Button(example, (_, _) => _term.Text = example);
            button.Padding = new Thickness(9, 3, 9, 3);
            button.MinHeight = 26;
            button.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            button.FontSize = 12;
            button.Margin = new Thickness(0, 0, 6, 6);
            examples.Children.Add(button);
        }
        Body.Children.Add(examples);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        Body.Children.Add(_error);
        Body.Children.Add(Ui.Hint("Erlaubt: + − · / ^, Klammern, sin, cos, tan, sqrt, abs, ln, log, exp, pi, e. Komma oder Punkt, „2x“ und „x²“ gehen auch."));

        Body.Children.Add(Label("Vorschau"));
        var frame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = _preview };
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        _preview.SetResourceReference(BackgroundProperty, "Paper");
        Body.Children.Add(frame);

        foreach (var value in new[] { 2, 3, 4, 5, 6, 8, 10, 12, 15, 20 })
        {
            _xRange.Items.Add(new ComboBoxItem { Content = $"±{value}", Tag = value });
            _yRange.Items.Add(new ComboBoxItem { Content = $"±{value}", Tag = value });
        }
        foreach (var value in new[] { 16, 24, 32, 48, 64 }) _unit.Items.Add(new ComboBoxItem { Content = $"{value} pt", Tag = value });
        foreach (var (label, hex) in Colors) _color.Items.Add(new ComboBoxItem { Content = label, Tag = hex });
        Select(_xRange, (int)settings.GetDouble("graphXRange", 5));
        Select(_yRange, (int)settings.GetDouble("graphYRange", 5));
        Select(_unit, (int)settings.GetDouble("graphUnit", 32));
        _color.SelectedIndex = Math.Max(0, Array.FindIndex(Colors, c => c.Hex == settings.Get("graphColor", "#1A1F2B")));

        var grid = new UniformGrid4();
        grid.Add("x-Bereich", _xRange);
        grid.Add("y-Bereich", _yRange);
        grid.Add("Kästchen", _unit);
        grid.Add("Farbe", _color);
        Body.Children.Add(grid.Build());

        _axes = Ui.Switch("Koordinatensystem zeichnen", settings.GetBool("graphAxes", true));
        _labels = Ui.Switch("Zahlen an den Achsen", settings.GetBool("graphLabels", true));
        _axes.Margin = new Thickness(0, 12, 0, 2);
        Body.Children.Add(_axes);
        Body.Children.Add(_labels);

        _term.TextChanged += (_, _) => Refresh();
        foreach (var box in new[] { _xRange, _yRange, _unit, _color }) box.SelectionChanged += (_, _) => Refresh();
        _axes.Click += (_, _) => Refresh();
        _labels.Click += (_, _) => Refresh();

        AddButton("Abbrechen", () => DialogResult = false, isCancel: true);
        AddButton("Einfügen", Insert, "Primary", isDefault: true);
        Loaded += (_, _) =>
        {
            Refresh();
            _term.Focus();
            _term.SelectAll();
        };
    }

    private static void Select(ComboBox box, int value)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == value) ?? box.Items[0];
    }

    private GraphBuilder.Settings CurrentSettings()
    {
        var x = (int)((ComboBoxItem)_xRange.SelectedItem).Tag;
        var y = (int)((ComboBoxItem)_yRange.SelectedItem).Tag;
        var unit = (int)((ComboBoxItem)_unit.SelectedItem).Tag;
        var color = (string)((ComboBoxItem)_color.SelectedItem).Tag;
        return new GraphBuilder.Settings(-x, x, -y, y, unit, _axes.IsChecked == true, _labels.IsChecked == true, color);
    }

    private void Refresh()
    {
        if (!IsLoaded) return;
        _preview.Children.Clear();
        _error.Text = "";
        Function = MathExpression.TryCompile(_term.Text, out var function, out var error) ? function : null;
        if (Function is null && _term.Text.Trim().Length > 0) _error.Text = error ?? "";
        Settings = CurrentSettings();
        var width = Math.Max(200, _preview.ActualWidth > 0 ? _preview.ActualWidth : 500);
        var height = _preview.Height;
        var unitX = width / (Settings.XMax - Settings.XMin);
        var unitY = height / (Settings.YMax - Settings.YMin);
        var originX = width * -Settings.XMin / (Settings.XMax - Settings.XMin);
        var originY = height * Settings.YMax / (Settings.YMax - Settings.YMin);

        var grid = (SolidColorBrush)Ui.Brush("Line");
        for (var value = Math.Ceiling(Settings.XMin); value <= Settings.XMax; value++)
            _preview.Children.Add(Line(originX + value * unitX, 0, originX + value * unitX, height, grid, 0.6));
        for (var value = Math.Ceiling(Settings.YMin); value <= Settings.YMax; value++)
            _preview.Children.Add(Line(0, originY - value * unitY, width, originY - value * unitY, grid, 0.6));
        if (Settings.DrawAxes)
        {
            var axis = Ui.Brush("Muted");
            _preview.Children.Add(Line(0, originY, width, originY, axis, 1.2));
            _preview.Children.Add(Line(originX, 0, originX, height, axis, 1.2));
        }
        if (Function is null) return;
        var ink = new SolidColorBrush(Theme.AdaptInk(Theme.Hex(Settings.Color), Theme.IsDark));
        var polyline = new System.Windows.Shapes.Polyline { Stroke = ink, StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Round };
        double? last = null;
        for (var step = 0; step <= (int)width; step++)
        {
            var x = Settings.XMin + (Settings.XMax - Settings.XMin) * step / width;
            var y = Function(x);
            var inside = y is double v && v >= Settings.YMin - 0.5 && v <= Settings.YMax + 0.5;
            if (!inside || (last is double previous && Math.Abs(y!.Value - previous) > (Settings.YMax - Settings.YMin) * 0.6))
            {
                if (polyline.Points.Count > 1) _preview.Children.Add(polyline);
                polyline = new System.Windows.Shapes.Polyline { Stroke = ink, StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Round };
                last = inside ? y : null;
                if (!inside) continue;
            }
            polyline.Points.Add(new Point(originX + x * unitX, originY - y!.Value * unitY));
            last = y;
        }
        if (polyline.Points.Count > 1) _preview.Children.Add(polyline);
    }

    private static System.Windows.Shapes.Line Line(double x1, double y1, double x2, double y2, Brush brush, double thickness) =>
        new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness };

    private void Insert()
    {
        if (!MathExpression.TryCompile(_term.Text, out var function, out var error))
        {
            _error.Text = error ?? "Das versteh ich nicht.";
            return;
        }
        Function = function;
        Settings = CurrentSettings();
        Result = GraphBuilder.Build(function, Settings, 0, 0);
        var settings = Services.Settings;
        settings.Set("graphTerm", _term.Text);
        settings.SetDouble("graphXRange", Settings.XMax);
        settings.SetDouble("graphYRange", Settings.YMax);
        settings.SetDouble("graphUnit", Settings.Unit);
        settings.Set("graphColor", Settings.Color);
        settings.SetBool("graphAxes", Settings.DrawAxes);
        settings.SetBool("graphLabels", Settings.DrawLabels);
        DialogResult = true;
    }

    /// <summary>Beschriftete Felder in zwei Spalten.</summary>
    private sealed class UniformGrid4
    {
        private readonly List<(string Label, FrameworkElement Control)> _items = new();

        public void Add(string label, FrameworkElement control) => _items.Add((label, control));

        public UIElement Build()
        {
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 12, 0, 0) };
            foreach (var (label, control) in _items)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 12, 8) };
                var text = Ui.Text(label, 13, color: "Muted");
                text.VerticalAlignment = VerticalAlignment.Center;
                text.Width = 80;
                DockPanel.SetDock(text, Dock.Left);
                row.Children.Add(text);
                control.HorizontalAlignment = HorizontalAlignment.Left;
                row.Children.Add(control);
                grid.Children.Add(row);
            }
            return grid;
        }
    }
}

/// <summary>Vor dem Einfügen fragen: auf neue Seiten oder an die sichtbare Stelle, und wie breit?</summary>
public sealed class ImportPlacementWindow : DialogWindow
{
    private readonly Slider _width = new() { Minimum = 0.2, Maximum = 1, TickFrequency = 0.05, IsSnapToTickEnabled = true };
    private bool _newPages = true;

    public bool NewPages => _newPages;
    public double WidthFraction => _width.Value;

    public ImportPlacementWindow(int count, BitmapSource? preview, string? fileName = null) : base("Einfügen", 520)
    {
        if (preview is not null)
        {
            var image = new Image { Source = preview, MaxHeight = 220, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
            var frame = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Center,
                BorderThickness = new Thickness(1),
                Child = image
            };
            frame.SetResourceReference(Border.BorderBrushProperty, "Line");
            Body.Children.Add(frame);
            Body.Children.Add(Ui.Text((fileName is null ? "" : fileName + " · ") + Ui.Plural(count, "Seite", "Seiten"), 12, color: "Faint",
                margin: new Thickness(0, 8, 0, 4)));
        }
        Body.Children.Add(Label("Wohin?"));
        Body.Children.Add(Ui.Segments(new[] { "Auf neue Seiten", "Hier, wo ich gerade bin" }, 0, index => _newPages = index == 0));
        Body.Children.Add(Ui.Hint("Danach kannst du die Seiten mit der Maus verschieben und an der Ecke vergrößern."));
        Body.Children.Add(Label("Breite"));
        _width.Value = Services.Settings.GetDouble("importWidthFraction", 1);
        var value = Ui.Text("", 13, color: "Muted");
        value.Width = 50;
        value.TextAlignment = TextAlignment.Right;
        void Show() => value.Text = $"{Math.Round(_width.Value * 100)} %";
        _width.ValueChanged += (_, _) => Show();
        Show();
        var row = new DockPanel();
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(value);
        row.Children.Add(_width);
        Body.Children.Add(row);
        AddButton("Abbrechen", () => DialogResult = false, isCancel: true);
        AddButton("Einfügen", () =>
        {
            Services.Settings.SetDouble("importWidthFraction", _width.Value);
            DialogResult = true;
        }, "Primary", isDefault: true);
    }
}

/// <summary>
/// Arbeitsblätter mit der Kamera des Surface abfotografieren. Fotografiert wird mit der
/// Windows-Kamera (die kann Papier gerade ziehen), danach holt Lernheft die neuen Bilder ab.
/// </summary>
public sealed class ScanWindow : SheetWindow
{
    public record ScannedPage(string File, int PixelWidth, int PixelHeight, BitmapSource Preview);

    private readonly NoteMeta _note;
    private readonly List<string> _picked = new();
    private readonly WrapPanel _thumbs = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _insert;
    private DateTime _openedCamera = DateTime.MaxValue;

    public List<ScannedPage> Pages { get; } = new();

    public ScanWindow(NoteMeta note) : base("Seiten einscannen", 720, 660, footer: true)
    {
        _note = note;
        var steps = new StackPanel();
        steps.Children.Add(Ui.CardHeader("Womit?", Ui.GlyphCamera));
        steps.Children.Add(Ui.Text("Die Windows-Kamera macht die Fotos – dort kannst du auch mehrere Seiten hintereinander aufnehmen. " +
                                   "Wenn du fertig bist, komm hierher zurück und hol sie ab.", 13.5, color: "Muted", wrap: true,
            margin: new Thickness(0, 0, 0, 12)));
        var row = new WrapPanel();
        var camera = Ui.Button("Kamera öffnen", (_, _) => OpenCamera(), "Primary", Ui.GlyphCamera);
        camera.Margin = new Thickness(0, 0, 8, 8);
        var fetch = Ui.Button("Neue Fotos holen", (_, _) => FetchFromCameraRoll(), icon: Ui.GlyphDownload);
        fetch.Margin = new Thickness(0, 0, 8, 8);
        var pick = Ui.Button("Bilder auswählen …", (_, _) => PickFiles(), icon: Ui.GlyphFolder);
        pick.Margin = new Thickness(0, 0, 8, 8);
        row.Children.Add(camera);
        row.Children.Add(fetch);
        row.Children.Add(pick);
        steps.Children.Add(row);
        steps.Children.Add(_status);
        Body.Children.Add(Ui.Card(steps));

        var chosen = new StackPanel();
        chosen.Children.Add(Ui.CardHeader("Diese Seiten kommen in die Notiz", Ui.GlyphPicture));
        chosen.Children.Add(_thumbs);
        Body.Children.Add(Ui.Card(chosen));

        AddFooterButton("Abbrechen", () => DialogResult = false, isCancel: true);
        _insert = AddFooterButton("Einfügen", Insert, "Primary", isDefault: true);
        ShowThumbs();
    }

    private void OpenCamera()
    {
        _openedCamera = DateTime.Now.AddSeconds(-2);
        try
        {
            Process.Start(new ProcessStartInfo("microsoft.windows.camera:") { UseShellExecute = true });
            SetStatus("Kamera läuft. Fotografiere deine Seiten, komm zurück und klick auf „Neue Fotos holen“.", "Muted");
        }
        catch (Exception error)
        {
            SetStatus("Die Windows-Kamera ließ sich nicht öffnen: " + error.Message + " Du kannst die Bilder auch auswählen.", "Bad");
        }
    }

    private void SetStatus(string text, string color)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, color);
    }

    private void FetchFromCameraRoll()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var folders = new[] { "Camera Roll", "Eigene Aufnahmen", "Kameraalbum", "" }
            .Select(name => name.Length == 0 ? pictures : Path.Combine(pictures, name))
            .Where(Directory.Exists).ToList();
        var since = _openedCamera == DateTime.MaxValue ? DateTime.Now.AddHours(-1) : _openedCamera;
        var found = folders.SelectMany(folder => Directory.EnumerateFiles(folder))
            .Where(file => Path.GetExtension(file).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".heic")
            .Where(file => File.GetLastWriteTime(file) >= since)
            .OrderBy(File.GetLastWriteTime)
            .Distinct().ToList();
        if (found.Count == 0)
        {
            SetStatus("Keine neuen Fotos gefunden. Liegen sie woanders, nimm „Bilder auswählen“.", "Warn");
            return;
        }
        foreach (var file in found.Where(file => !_picked.Contains(file))) _picked.Add(file);
        SetStatus($"{Ui.Plural(found.Count, "Foto", "Fotos")} gefunden.", "Good");
        ShowThumbs();
    }

    private void PickFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Seiten auswählen",
            Multiselect = true,
            Filter = "Bilder|*.jpg;*.jpeg;*.png;*.bmp;*.heic;*.tif;*.tiff|Alle Dateien|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames.Where(file => !_picked.Contains(file))) _picked.Add(file);
        ShowThumbs();
    }

    private void ShowThumbs()
    {
        _thumbs.Children.Clear();
        _insert.IsEnabled = _picked.Count > 0;
        if (_picked.Count == 0)
        {
            _thumbs.Children.Add(Ui.Text("Noch nichts ausgewählt.", 13, color: "Faint"));
            return;
        }
        foreach (var file in _picked.ToList())
        {
            var image = ImageTools.Load(file, 240);
            if (image is null) continue;
            var box = new StackPanel { Margin = new Thickness(0, 0, 12, 12), Width = 140 };
            var frame = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), ClipToBounds = true, Background = Brushes.White };
            frame.SetResourceReference(Border.BorderBrushProperty, "Line");
            frame.Child = new Image { Source = image, Height = 170, Stretch = Stretch.Uniform };
            box.Children.Add(frame);
            var remove = Ui.Button("Entfernen", (_, _) =>
            {
                _picked.Remove(file);
                ShowThumbs();
            }, "Ghost");
            remove.HorizontalAlignment = HorizontalAlignment.Center;
            box.Children.Add(remove);
            _thumbs.Children.Add(box);
        }
    }

    private void Insert()
    {
        foreach (var file in _picked)
        {
            try
            {
                var source = ImageTools.Load(file);
                if (source is null) continue;
                var shrunk = ImageTools.Shrink(source, 2600);
                var name = Services.Store.SaveImage(_note.Id, ImageTools.Jpeg(shrunk, 88), "jpg", "scan");
                Pages.Add(new ScannedPage(name, shrunk.PixelWidth, shrunk.PixelHeight, ImageTools.Load(file, 400) ?? shrunk));
            }
            catch (Exception error)
            {
                SetStatus($"{Path.GetFileName(file)} ließ sich nicht einfügen: {error.Message}", "Bad");
            }
        }
        if (Pages.Count > 0) DialogResult = true;
    }
}
