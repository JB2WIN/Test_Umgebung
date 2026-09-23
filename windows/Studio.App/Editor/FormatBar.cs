using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Lernheft.Studio.App.Editor;

/// <summary>
/// Die Leiste über der Seite: Schrift, Größe, fett/kursiv/unterstrichen, Farben, Listen.
/// Sie wirkt auf das Textfeld, in dem zuletzt geschrieben wurde – auch wenn gerade eine
/// Auswahlliste den Fokus hat.
/// </summary>
public sealed class FormatBar : Border
{
    private readonly Func<TextBoxView?> _target;
    private readonly ComboBox _font = new() { Width = 158, Focusable = false, MaxDropDownHeight = 420 };
    private readonly ComboBox _size = new() { Width = 70, Focusable = false, MaxDropDownHeight = 420 };
    private readonly ToggleButton _bold;
    private readonly ToggleButton _italic;
    private readonly ToggleButton _underline;
    private readonly ToggleButton _strike;
    private readonly ToggleButton _superscript;
    private readonly ToggleButton _subscript;
    private readonly ToggleButton _bullets;
    private readonly ToggleButton _numbers;
    private readonly Border _colorBar = new() { Height = 3, Width = 14, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 1, 0, 0) };
    private readonly Border _highlightBar = new() { Height = 3, Width = 14, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 1, 0, 0) };
    private readonly StackPanel _formatGroup = new() { Orientation = Orientation.Horizontal };
    private bool _updating;

    private Color? _lastColor = Theme.Hex("#DC3B3B");
    private Color? _lastHighlight = Color.FromArgb(0x80, 0xFF, 0xE3, 0x4D);

    public StackPanel Right { get; } = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

    public static readonly (string Label, Color? Color)[] TextColors =
    {
        ("Automatisch", null),
        ("Blau", Theme.Hex("#2F5BEA")),
        ("Rot", Theme.Hex("#DC3B3B")),
        ("Grün", Theme.Hex("#1F9D5C")),
        ("Orange", Theme.Hex("#E9730C")),
        ("Lila", Theme.Hex("#8A4FE0")),
        ("Pink", Theme.Hex("#E0457B")),
        ("Türkis", Theme.Hex("#0E86C4")),
        ("Grau", Theme.Hex("#7A8591"))
    };

    public static readonly (string Label, Color? Color)[] Highlights =
    {
        ("Keine", null),
        ("Gelb", Color.FromArgb(0x80, 0xFF, 0xE3, 0x4D)),
        ("Grün", Color.FromArgb(0x6E, 0x7B, 0xE0, 0x8F)),
        ("Blau", Color.FromArgb(0x6E, 0x7A, 0xB8, 0xFF)),
        ("Pink", Color.FromArgb(0x6E, 0xFF, 0x8A, 0xC2)),
        ("Orange", Color.FromArgb(0x78, 0xFF, 0xB0, 0x5C))
    };

    /// <summary>Gerade wird in der Leiste etwas ausgewählt – das Textfeld soll offen bleiben.</summary>
    public bool IsInteracting => _font.IsDropDownOpen || _size.IsDropDownOpen || _popupOpen || IsMouseOver;

    private bool _popupOpen;

    public FormatBar(Func<TextBoxView?> target)
    {
        _target = target;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(BorderBrushProperty, "Line");
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(12, 6, 12, 6);

        foreach (var name in TextDocs.InstalledFonts())
        {
            // Text statt Element: So zeigt das geschlossene Feld den Namen sauber in einer Zeile,
            // die Liste darunter jede Schrift in ihrer eigenen Gestalt.
            _font.Items.Add(new ComboBoxItem { Content = name, FontFamily = TextDocs.Family(name), FontSize = 14, Tag = name });
        }
        foreach (var size in TextDocs.Sizes) _size.Items.Add(new ComboBoxItem { Content = size.ToString("0"), Tag = size });
        _font.SelectionChanged += (_, _) =>
        {
            if (_updating || _font.SelectedItem is not ComboBoxItem { Tag: string name }) return;
            Apply(box => box.Format(TextElement.FontFamilyProperty, TextDocs.Family(name)));
        };
        _size.SelectionChanged += (_, _) =>
        {
            if (_updating || _size.SelectedItem is not ComboBoxItem { Tag: double size }) return;
            Apply(box => box.Format(TextElement.FontSizeProperty, size));
        };
        _font.DropDownClosed += (_, _) => Refocus();
        _size.DropDownClosed += (_, _) => Refocus();

        _bold = Toggle(Letter("F", bold: true), "Fett (Strg+B)", () => Command(EditingCommands.ToggleBold));
        _italic = Toggle(Letter("K", italic: true), "Kursiv (Strg+I)", () => Command(EditingCommands.ToggleItalic));
        _underline = Toggle(Letter("U", underline: true), "Unterstrichen (Strg+U)", () => Command(EditingCommands.ToggleUnderline));
        _strike = Toggle(Letter("S", strike: true), "Durchgestrichen", ToggleStrike);
        _superscript = Toggle(Letter("x²"), "Hochgestellt, z. B. x² (Strg+Umschalt++)", () => ToggleBaseline(BaselineAlignment.Superscript));
        _subscript = Toggle(Letter("x₂"), "Tiefgestellt, z. B. H₂O (Strg+#)", () => ToggleBaseline(BaselineAlignment.Subscript));
        _bullets = Toggle(Ui.Icon(Ui.GlyphBullets, 14), "Aufzählung", () => Command(EditingCommands.ToggleBullets));
        _numbers = Toggle(Letter("1."), "Nummerierung", () => Command(EditingCommands.ToggleNumbering));

        _formatGroup.Children.Add(_font);
        _formatGroup.Children.Add(Gap(6));
        _formatGroup.Children.Add(_size);
        _formatGroup.Children.Add(Separator());
        _formatGroup.Children.Add(_bold);
        _formatGroup.Children.Add(_italic);
        _formatGroup.Children.Add(_underline);
        _formatGroup.Children.Add(_strike);
        _formatGroup.Children.Add(Separator());
        _formatGroup.Children.Add(ColorButton());
        _formatGroup.Children.Add(HighlightButton());
        _formatGroup.Children.Add(Separator());
        _formatGroup.Children.Add(_bullets);
        _formatGroup.Children.Add(_numbers);
        _formatGroup.Children.Add(Tool(Letter("⇤"), "Einzug verkleinern", () => Command(EditingCommands.DecreaseIndentation)));
        _formatGroup.Children.Add(Tool(Letter("⇥"), "Einzug vergrößern", () => Command(EditingCommands.IncreaseIndentation)));
        _formatGroup.Children.Add(Separator());
        _formatGroup.Children.Add(_superscript);
        _formatGroup.Children.Add(_subscript);
        _formatGroup.Children.Add(Tool(Ui.Icon(Ui.GlyphClear, 13), "Formatierung entfernen", () => Apply(box => box.ClearFormatting())));

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(Right, Dock.Right);
        dock.Children.Add(Right);
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _formatGroup,
            Focusable = false
        };
        dock.Children.Add(scroller);
        Child = dock;
        Update(null);
    }

    private static FrameworkElement Gap(double width) => new Border { Width = width };

    public static Border Separator()
    {
        var line = new Border { Width = 1, Height = 20, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        line.SetResourceReference(BackgroundProperty, "Line");
        return line;
    }

    private static TextBlock Letter(string text, bool bold = false, bool italic = false, bool underline = false, bool strike = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14.5,
            FontWeight = bold ? FontWeights.Bold : FontWeights.SemiBold,
            FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (underline) block.TextDecorations = TextDecorations.Underline;
        if (strike) block.TextDecorations = TextDecorations.Strikethrough;
        return block;
    }

    private ToggleButton Toggle(UIElement content, string tooltip, Action action)
    {
        var toggle = new ToggleButton { Style = Ui.Style("ToolToggle"), Content = content, ToolTip = tooltip };
        Ui.AutomationName(toggle, tooltip);
        toggle.Click += (_, _) =>
        {
            action();
            Update(_target());
        };
        return toggle;
    }

    public static Button Tool(UIElement content, string tooltip, Action action)
    {
        var button = new Button { Style = Ui.Style("ToolButton"), Content = content, ToolTip = tooltip };
        Ui.AutomationName(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    private void Apply(Action<TextBoxView> action)
    {
        var box = _target();
        if (box is null) return;
        action(box);
        Refocus();
    }

    private void Refocus()
    {
        var box = _target();
        if (box is null) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!box.IsKeyboardFocusWithin) box.Focus();
            Update(box);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Command(RoutedUICommand command) => Apply(box => command.Execute(null, box));

    private void ToggleStrike() => Apply(box =>
    {
        var current = box.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
        var has = current is TextDecorationCollection decorations && decorations.Any(d => d.Location == TextDecorationLocation.Strikethrough);
        box.Format(Inline.TextDecorationsProperty, has ? new TextDecorationCollection() : TextDecorations.Strikethrough);
    });

    private void ToggleBaseline(BaselineAlignment alignment) => Apply(box =>
    {
        var current = box.Selection.GetPropertyValue(Inline.BaselineAlignmentProperty);
        box.Format(Inline.BaselineAlignmentProperty, current is BaselineAlignment value && value == alignment
            ? BaselineAlignment.Baseline
            : alignment);
    });

    private FrameworkElement ColorButton()
    {
        var face = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        face.Children.Add(Letter("A"));
        _colorBar.Background = new SolidColorBrush(_lastColor ?? Colors.Black);
        face.Children.Add(_colorBar);
        return SplitButton(face, "Schriftfarbe", () => ApplyColor(_lastColor), TextColors, color =>
        {
            _lastColor = color;
            if (color is Color c) _colorBar.Background = new SolidColorBrush(c);
            else _colorBar.SetResourceReference(BackgroundProperty, "Ink");
            ApplyColor(color);
        });
    }

    private FrameworkElement HighlightButton()
    {
        var face = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        face.Children.Add(Ui.Icon(Ui.GlyphHighlight, 13));
        _highlightBar.Background = new SolidColorBrush(_lastHighlight ?? Colors.Transparent);
        face.Children.Add(_highlightBar);
        return SplitButton(face, "Textmarker", () => ApplyHighlight(_lastHighlight), Highlights, color =>
        {
            _lastHighlight = color;
            _highlightBar.Background = new SolidColorBrush(color ?? Colors.Transparent);
            ApplyHighlight(color);
        });
    }

    private void ApplyColor(Color? color) => Apply(box =>
        box.Format(TextElement.ForegroundProperty, color is Color c ? new SolidColorBrush(c) : null));

    private void ApplyHighlight(Color? color) => Apply(box =>
        box.Format(TextElement.BackgroundProperty, color is Color c ? new SolidColorBrush(c) : null));

    /// <summary>Knopf mit kleinem Pfeil daneben, der die Farbauswahl öffnet.</summary>
    private FrameworkElement SplitButton(UIElement face, string tooltip, Action main, (string Label, Color? Color)[] choices,
        Action<Color?> choose)
    {
        var button = Tool(face, tooltip, main);
        var arrow = Tool(Ui.Icon(Ui.GlyphChevronDown, 8), tooltip + " wählen", () => { });
        arrow.MinWidth = 16;
        arrow.Padding = new Thickness(2, 0, 2, 0);
        var popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            VerticalOffset = 4
        };
        var grid = new WrapPanel { Width = 180 };
        foreach (var (label, color) in choices)
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Margin = new Thickness(4),
                BorderThickness = new Thickness(1),
                ToolTip = label,
                Cursor = Cursors.Hand
            };
            swatch.SetResourceReference(BorderBrushProperty, "LineStrong");
            if (color is Color c) swatch.Background = new SolidColorBrush(c);
            else
            {
                swatch.SetResourceReference(BackgroundProperty, "Surface");
                swatch.Child = new TextBlock
                {
                    Text = choices == TextColors ? "A" : "∅",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeights.SemiBold
                };
            }
            var chosen = color;
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                popup.IsOpen = false;
                choose(chosen);
            };
            grid.Children.Add(swatch);
        }
        var frame = new Border
        {
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(6, 2, 6, 12),
            Child = grid,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.25 }
        };
        frame.SetResourceReference(BackgroundProperty, "Surface");
        frame.SetResourceReference(BorderBrushProperty, "Line");
        popup.Child = frame;
        popup.Opened += (_, _) => _popupOpen = true;
        popup.Closed += (_, _) =>
        {
            _popupOpen = false;
            Refocus();
        };
        arrow.Click += (_, _) => popup.IsOpen = true;
        return Ui.Row(0, button, arrow);
    }

    /// <summary>Zeigt an, wie der markierte Text formatiert ist.</summary>
    public void Update(TextBoxView? box)
    {
        _updating = true;
        _formatGroup.IsEnabled = box is not null;
        _formatGroup.Opacity = box is null ? 0.55 : 1;
        if (box is null)
        {
            foreach (var toggle in new[] { _bold, _italic, _underline, _strike, _superscript, _subscript, _bullets, _numbers }) toggle.IsChecked = false;
            _font.SelectedItem = _font.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == Services.Settings.Get(Keys.TextFont, TextDocs.DefaultFont));
            _size.SelectedItem = _size.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Math.Abs((double)i.Tag - Services.Settings.GetDouble(Keys.TextSize, 18)) < 0.1);
            _updating = false;
            return;
        }
        var selection = box.Selection;
        _bold.IsChecked = selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight weight && weight >= FontWeights.SemiBold;
        _italic.IsChecked = selection.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle style && style == FontStyles.Italic;
        var decorations = selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        _underline.IsChecked = decorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true;
        _strike.IsChecked = decorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true;
        var baseline = selection.GetPropertyValue(Inline.BaselineAlignmentProperty);
        _superscript.IsChecked = baseline is BaselineAlignment b1 && b1 == BaselineAlignment.Superscript;
        _subscript.IsChecked = baseline is BaselineAlignment b2 && b2 == BaselineAlignment.Subscript;
        var list = selection.Start.Paragraph?.Parent as ListItem;
        var marker = list?.List?.MarkerStyle;
        _bullets.IsChecked = marker is TextMarkerStyle.Disc or TextMarkerStyle.Circle or TextMarkerStyle.Square or TextMarkerStyle.Box;
        _numbers.IsChecked = marker is TextMarkerStyle.Decimal or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin
            or TextMarkerStyle.LowerRoman or TextMarkerStyle.UpperRoman;

        if (selection.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily family)
        {
            var name = family.Source.Split(',')[0].Trim();
            _font.SelectedItem = _font.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == name);
        }
        else _font.SelectedItem = null;
        if (selection.GetPropertyValue(TextElement.FontSizeProperty) is double size)
        {
            var item = _size.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Math.Abs((double)i.Tag - size) < 0.1);
            if (item is null)
            {
                item = new ComboBoxItem { Content = size.ToString("0.#"), Tag = size };
                _size.Items.Add(item);
            }
            _size.SelectedItem = item;
        }
        else _size.SelectedItem = null;
        _updating = false;
    }
}
