using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Lernheft.Studio.App;

/// <summary>Kleine Bausteine, damit alle Fenster gleich aussehen.</summary>
public static class Ui
{
    public const string IconFontName = "Segoe Fluent Icons, Segoe MDL2 Assets";

    // Symbole aus „Segoe Fluent Icons" bzw. „Segoe MDL2 Assets"
    public const string GlyphHome = "";
    public const string GlyphCalendar = "";
    public const string GlyphChecklist = "";
    public const string GlyphCards = "";
    public const string GlyphAdd = "";
    public const string GlyphClose = "";
    public const string GlyphMore = "";
    public const string GlyphSettings = "";
    public const string GlyphSearch = "";
    public const string GlyphDelete = "";
    public const string GlyphEdit = "";
    public const string GlyphCheck = "";
    public const string GlyphShare = "";
    public const string GlyphPrint = "";
    public const string GlyphSave = "";
    public const string GlyphDocument = "";
    public const string GlyphPage = "";
    public const string GlyphFolder = "";
    public const string GlyphPicture = "";
    public const string GlyphCamera = "";
    public const string GlyphCalculator = "";
    public const string GlyphInfo = "";
    public const string GlyphWarning = "";
    public const string GlyphTablet = "";
    public const string GlyphConnect = "";
    public const string GlyphZoomIn = "";
    public const string GlyphZoomOut = "";
    public const string GlyphFullScreen = "";
    public const string GlyphUndo = "";
    public const string GlyphRedo = "";
    public const string GlyphCopy = "";
    public const string GlyphSync = "";
    public const string GlyphChevronDown = "";
    public const string GlyphChevronRight = "";
    public const string GlyphChevronLeft = "";
    public const string GlyphChevronUp = "";
    public const string GlyphMenu = "";
    public const string GlyphBullets = "";
    public const string GlyphHighlight = "";
    public const string GlyphFontColor = "";
    public const string GlyphBold = "";
    public const string GlyphItalic = "";
    public const string GlyphUnderline = "";
    public const string GlyphQr = "";
    public const string GlyphDownload = "";
    public const string GlyphUpload = "";
    public const string GlyphPlay = "";
    public const string GlyphLink = "";
    public const string GlyphMail = "";
    public const string GlyphErase = "";
    public const string GlyphCloud = "\uE753";
    public const string GlyphRefresh = "";
    public const string GlyphClear = "";

    public static SolidColorBrush Brush(string key) => (SolidColorBrush)Application.Current.Resources[key];

    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    public static FontFamily IconFont => (FontFamily)Application.Current.Resources["IconFont"];
    public static FontFamily DisplayFont => (FontFamily)Application.Current.Resources["DisplayFont"];
    public static FontFamily UiFont => (FontFamily)Application.Current.Resources["UiFont"];

    public static void Tint(FrameworkElement element, DependencyProperty property, string key) =>
        element.SetResourceReference(property, key);

    public static TextBlock Text(string text, double size = 13.5, FontWeight? weight = null, string color = "Text",
        bool wrap = false, Thickness? margin = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            Margin = margin ?? new Thickness(0)
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, color);
        return block;
    }

    public static TextBlock Heading(string text, double size = 22) => new()
    {
        Text = text,
        FontFamily = DisplayFont,
        FontSize = size,
        FontWeight = FontWeights.SemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    public static TextBlock Hint(string text, Thickness? margin = null) =>
        Text(text, 12, color: "Faint", wrap: true, margin: margin ?? new Thickness(0, 6, 0, 0));

    public static TextBlock Icon(string glyph, double size = 15, string? color = null)
    {
        var block = new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (color is not null) block.SetResourceReference(TextBlock.ForegroundProperty, color);
        return block;
    }

    public static Button Button(string text, RoutedEventHandler? click = null, string style = "Soft", string? icon = null,
        string? tooltip = null)
    {
        var button = new Button { Style = Style(style), ToolTip = tooltip };
        if (icon is null)
        {
            button.Content = text;
        }
        else
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Icon(icon, 14));
            if (text.Length > 0) row.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            button.Content = row;
        }
        if (click is not null) button.Click += click;
        return button;
    }

    public static Button IconButton(string glyph, string tooltip, RoutedEventHandler? click = null, double size = 15)
    {
        var button = new Button
        {
            Style = Style("IconButton"),
            Content = Icon(glyph, size),
            ToolTip = tooltip
        };
        AutomationName(button, tooltip);
        if (click is not null) button.Click += click;
        return button;
    }

    public static void AutomationName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    public static Border Card(UIElement child, Thickness? padding = null)
    {
        var border = new Border { Style = Style("Card"), Child = child };
        if (padding is Thickness value) border.Padding = value;
        return border;
    }

    /// <summary>Kartenkopf mit Titel links und optionalem Knopf rechts.</summary>
    public static DockPanel CardHeader(string title, string? glyph = null, string? action = null, Action? onAction = null)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
        if (action is not null)
        {
            var button = Button(action, (_, _) => onAction?.Invoke(), "Ghost");
            button.Margin = new Thickness(0, -6, -8, 0);
            DockPanel.SetDock(button, Dock.Right);
            dock.Children.Add(button);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph is not null)
        {
            var icon = Icon(glyph, 15, "Accent");
            icon.Margin = new Thickness(0, 1, 9, 0);
            row.Children.Add(icon);
        }
        var heading = new TextBlock { Text = title, Style = Style("CardTitle"), Margin = new Thickness(0) };
        row.Children.Add(heading);
        dock.Children.Add(row);
        return dock;
    }

    public static Border Badge(string text, string foreground = "Accent", string background = "AccentSoft")
    {
        var label = Text(text, 11, FontWeights.SemiBold, foreground);
        var border = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = label
        };
        border.SetResourceReference(Border.BackgroundProperty, background);
        return border;
    }

    public static Border Dot(Brush fill, double size = 10) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = fill,
        VerticalAlignment = VerticalAlignment.Center
    };

    public static StackPanel Row(double spacing, params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (var index = 0; index < children.Length; index++)
        {
            if (children[index] is FrameworkElement element && index > 0)
            {
                var margin = element.Margin;
                element.Margin = new Thickness(margin.Left + spacing, margin.Top, margin.Right, margin.Bottom);
            }
            row.Children.Add(children[index]);
        }
        return row;
    }

    public static TextBox Field(string text = "", string placeholder = "", bool multiline = false)
    {
        var box = new TextBox { Style = Style(multiline ? "MultiField" : "Field"), Text = text };
        if (placeholder.Length > 0) Placeholder.Set(box, placeholder);
        return box;
    }

    public static CheckBox Switch(string label, bool value, Action<bool>? changed = null)
    {
        var box = new CheckBox { Style = Style("Switch"), Content = WrapText(label), IsChecked = value, Margin = new Thickness(0, 5, 0, 5) };
        if (changed is not null)
        {
            box.Checked += (_, _) => changed(true);
            box.Unchecked += (_, _) => changed(false);
        }
        return box;
    }

    public static TextBlock WrapText(string text, string color = "Text") => Text(text, 13.5, color: color, wrap: true);

    public static Border Divider(Thickness? margin = null)
    {
        var line = new Border { Height = 1, Margin = margin ?? new Thickness(0, 10, 0, 10) };
        line.SetResourceReference(Border.BackgroundProperty, "Line");
        return line;
    }

    public static Brush NotebookBrush(string? colorName) =>
        new SolidColorBrush(Theme.Hex(NotebookColors.Hex(colorName)));

    /// <summary>Segmentierte Auswahl (wie ein iPad-Picker).</summary>
    public static Border Segments(IEnumerable<string> labels, int selected, Action<int> changed)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var group = "seg" + Guid.NewGuid().ToString("N")[..6];
        var index = 0;
        foreach (var label in labels)
        {
            var current = index;
            var button = new RadioButton
            {
                Style = Style("Segment"),
                Content = label,
                GroupName = group,
                IsChecked = index == selected
            };
            button.Checked += (_, _) => changed(current);
            row.Children.Add(button);
            index++;
        }
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            Child = row,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        border.BorderThickness = new Thickness(1);
        return border;
    }

    public static ContextMenu Menu(UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        return menu;
    }

    public static MenuItem MenuItem(string header, Action action, string? glyph = null, bool enabled = true, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled, IsChecked = isChecked };
        if (glyph is not null) item.Icon = Icon(glyph, 13);
        item.Click += (_, _) => action();
        return item;
    }

    public static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";
}

/// <summary>Grauer Hinweistext in leeren Eingabefeldern.</summary>
public static class Placeholder
{
    public static void Set(TextBox box, string text)
    {
        box.Loaded += (_, _) => Attach(box, text);
    }

    private static void Attach(TextBox box, string text)
    {
        var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(box);
        if (layer is null) return;
        var adorner = new PlaceholderAdorner(box, text);
        layer.Add(adorner);
        // Adorner liegen über allem – ist das Feld weggeklappt oder winzig, darf der Hinweis nicht stehen bleiben.
        void Refresh() => adorner.Visibility = box.Text.Length == 0 && box.IsVisible && box.ActualWidth >= 40
            ? Visibility.Visible
            : Visibility.Collapsed;
        box.TextChanged += (_, _) => Refresh();
        box.IsVisibleChanged += (_, _) => Refresh();
        box.SizeChanged += (_, _) =>
        {
            Refresh();
            adorner.InvalidateVisual();
        };
        Refresh();
    }

    private sealed class PlaceholderAdorner : System.Windows.Documents.Adorner
    {
        private readonly TextBox _box;
        private readonly string _text;

        public PlaceholderAdorner(TextBox box, string text) : base(box)
        {
            _box = box;
            _text = text;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext context)
        {
            var brush = Ui.Brush("Faint");
            var formatted = new FormattedText(_text, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface(_box.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                _box.FontSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(10, _box.ActualWidth - _box.Padding.Left - _box.Padding.Right - 6),
                MaxLineCount = _box.AcceptsReturn ? 4 : 1,
                Trimming = TextTrimming.CharacterEllipsis
            };
            var top = _box.VerticalContentAlignment == VerticalAlignment.Top || _box.AcceptsReturn
                ? _box.Padding.Top + 1
                : (_box.ActualHeight - formatted.Height) / 2;
            context.DrawText(formatted, new Point(_box.Padding.Left + 3, top));
        }
    }
}

/// <summary>Winziger Befehl für Tastenkürzel.</summary>
public sealed class Command(Action action, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => action();
}
