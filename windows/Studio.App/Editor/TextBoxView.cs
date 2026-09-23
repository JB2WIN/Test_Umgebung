using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;

namespace Lernheft.Studio.App.Editor;

/// <summary>Womit ein Textfeld aufgebaut wird: Papier, Schrift und ob die Zeilen auf den Linien sitzen.</summary>
public record TextEnvironment(PaperStyle Paper, string FontName, double FontSize, string ScriptFont, bool SnapToLines)
{
    public bool Lines => SnapToLines && Paper != PaperStyle.Blank;

    /// <summary>Sitzen die Zeilen dieses Felds auf Linien? Arbeitsblatt-Felder immer.</summary>
    public bool LinesFor(NoteTextBox box) => SnapToLines && (Paper != PaperStyle.Blank || box.LineHeight is not null);

    public double LineHeightFor(NoteTextBox box) => box.LineHeight is double height && height > 8 ? height : Studio.Paper.Spacing;

    public static TextEnvironment FromSettings(PaperStyle paper) => new(
        paper,
        Services.Settings.Get(Keys.TextFont, TextDocs.DefaultFont),
        Services.Settings.GetDouble(Keys.TextSize, 18),
        Services.Settings.Get(Keys.ScriptFont, TextDocs.DefaultScriptFont),
        Services.Settings.GetBool(Keys.SnapToLines, true));
}

/// <summary>Aufbauen, Speichern und Messen der formatierten Textfelder.</summary>
public static class TextDocs
{
    public const string DefaultFont = "Segoe UI";
    public const string DefaultScriptFont = "Ink Free";

    /// <summary>Diese Farben gelten als „automatisch“ (schwarz im Hellen, weiß im Dunkeln).</summary>
    private static readonly HashSet<string> AutoColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "#FF16202C", "#FFE9EEF4", "#FF1A1F2B", "#FF000000", "#FF0F1724", "#FFEEF2F6"
    };

    public static readonly string[] Fonts =
    {
        "Segoe UI", "Segoe UI Variable Text", "Aptos", "Calibri", "Arial", "Verdana", "Georgia", "Cambria",
        "Times New Roman", "Consolas", "Comic Sans MS", "Ink Free", "Segoe Print", "Segoe Script"
    };

    public static readonly double[] Sizes = { 11, 12, 14, 16, 18, 20, 22, 24, 28, 32, 40, 48 };

    public static IEnumerable<string> InstalledFonts()
    {
        var installed = Fonts.Where(IsInstalled).ToList();
        return installed.Count > 0 ? installed : new List<string> { "Segoe UI" };
    }

    private static readonly Dictionary<string, bool> InstalledCache = new();

    public static bool IsInstalled(string name)
    {
        if (InstalledCache.TryGetValue(name, out var known)) return known;
        var found = System.Windows.Media.Fonts.SystemFontFamilies.Any(f =>
            f.Source.Equals(name, StringComparison.OrdinalIgnoreCase)
            || f.FamilyNames.Values.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)));
        InstalledCache[name] = found;
        return found;
    }

    public static FontFamily Family(string name) =>
        new(IsInstalled(name) ? name : name.Contains("Ink") || name.Contains("Script") || name.Contains("Print")
            ? "Segoe Print, Comic Sans MS, Segoe UI"
            : "Segoe UI");

    /// <summary>Abstand von der Oberkante einer Zeile bis zur Grundlinie.</summary>
    public static double BaselineOffset(FontFamily family, double lineHeight)
    {
        var spacing = family.LineSpacing > 0 ? family.LineSpacing : 1.33;
        var baseline = family.Baseline > 0 ? family.Baseline : 1.08;
        return lineHeight * baseline / spacing;
    }

    /// <summary>
    /// Oberkante eines neuen Textfelds, damit die erste Zeile genau auf der Linie unter
    /// <paramref name="y"/> steht (ein kleines Stück darüber, wie beim Schreiben von Hand).
    /// </summary>
    public static double SnapTop(double y, TextEnvironment environment)
    {
        if (!environment.Lines) return Math.Max(0, y - environment.FontSize * 0.7);
        var baseline = BaselineOffset(Family(environment.FontName), Paper.Spacing);
        var line = Math.Max(1, Math.Ceiling((y + 4) / Paper.Spacing)) * Paper.Spacing;
        return Math.Max(0, line - 4 - baseline);
    }

    /// <summary>Oberkante für ein Feld auf den Linien eines Arbeitsblatts.</summary>
    public static double SnapToLine(double lineY, double lineHeight, TextEnvironment environment)
    {
        var baseline = BaselineOffset(Family(environment.FontName), lineHeight);
        return Math.Max(0, lineY - Math.Min(4, lineHeight * 0.12) - baseline);
    }

    /// <summary>Wie <see cref="SnapTop"/>, aber für ein Feld, das verschoben wurde: zur nächsten Linie.</summary>
    public static double SnapExisting(double top, TextEnvironment environment, NoteTextBox? model = null)
    {
        if (model?.LineHeight is not null) return Math.Max(0, top);
        if (!environment.Lines) return Math.Max(0, top);
        var baseline = BaselineOffset(Family(environment.FontName), Paper.Spacing);
        var line = Math.Max(1, Math.Round((top + 4 + baseline) / Paper.Spacing)) * Paper.Spacing;
        return Math.Max(0, line - 4 - baseline);
    }

    public static FlowDocument Create(NoteTextBox model, TextEnvironment environment)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = Family(environment.FontName),
            FontSize = environment.FontSize,
            TextAlignment = TextAlignment.Left,
            IsHyphenationEnabled = false,
            IsOptimalParagraphEnabled = false
        };
        document.Resources.Add(typeof(Paragraph), new Style(typeof(Paragraph))
        {
            Setters = { new Setter(Block.MarginProperty, new Thickness(0)) }
        });
        document.Resources.Add(typeof(List), new Style(typeof(List))
        {
            Setters =
            {
                new Setter(Block.MarginProperty, new Thickness(0)),
                new Setter(Block.PaddingProperty, new Thickness(26, 0, 0, 0))
            }
        });
        ApplyLines(document, environment, model);

        var loaded = false;
        if (!string.IsNullOrWhiteSpace(model.Xaml))
        {
            try
            {
                var range = new TextRange(document.ContentStart, document.ContentEnd);
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(model.Xaml));
                range.Load(stream, DataFormats.Xaml);
                loaded = true;
            }
            catch (Exception)
            {
                loaded = false;
            }
        }
        if (!loaded)
        {
            document.Blocks.Clear();
            var seed = model.Seed ?? new TextSeed { Text = model.Text };
            var lines = seed.Text.Replace("\r\n", "\n").Split('\n');
            foreach (var line in lines)
            {
                var run = new Run(line);
                var paragraph = new Paragraph(run);
                if (seed.Script)
                {
                    run.FontFamily = Family(environment.ScriptFont);
                }
                if (seed.FontSize is double size && size > 0) run.FontSize = Math.Clamp(size * (seed.Script ? 0.95 : 1), 8, 96);
                if (seed.Color is { } hex && !AutoColors.Contains("#FF" + hex.TrimStart('#')))
                {
                    run.Foreground = new SolidColorBrush(Theme.Hex(hex));
                }
                document.Blocks.Add(paragraph);
            }
            if (document.Blocks.Count == 0) document.Blocks.Add(new Paragraph());
            FitLineHeights(document, environment, model);
        }
        return document;
    }

    public static void ApplyLines(FlowDocument document, TextEnvironment environment, NoteTextBox model)
    {
        if (environment.LinesFor(model))
        {
            document.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            document.LineHeight = environment.LineHeightFor(model);
        }
        else
        {
            document.LineStackingStrategy = LineStackingStrategy.MaxHeight;
            document.LineHeight = double.NaN;
        }
    }

    /// <summary>Große Schrift bekommt zwei (oder mehr) Linien, kleine genau eine.</summary>
    public static void FitLineHeights(FlowDocument document, TextEnvironment environment, NoteTextBox model,
        IEnumerable<Paragraph>? only = null)
    {
        var step = environment.LineHeightFor(model);
        foreach (var paragraph in only ?? Paragraphs(document.Blocks))
        {
            if (!environment.LinesFor(model))
            {
                if (paragraph.ReadLocalValue(Block.LineHeightProperty) != DependencyProperty.UnsetValue) paragraph.ClearValue(Block.LineHeightProperty);
                continue;
            }
            var size = paragraph.FontSize;
            foreach (var inline in paragraph.Inlines) size = Math.Max(size, MaxSize(inline));
            var lines = Math.Max(1, Math.Ceiling(size * 1.3 / step));
            var height = lines * step;
            if (lines == 1)
            {
                if (paragraph.ReadLocalValue(Block.LineHeightProperty) != DependencyProperty.UnsetValue) paragraph.ClearValue(Block.LineHeightProperty);
            }
            else if (Math.Abs(paragraph.LineHeight - height) > 0.1)
            {
                paragraph.LineHeight = height;
            }
        }
    }

    private static double MaxSize(Inline inline)
    {
        var size = inline.FontSize;
        if (inline is Span span)
        {
            foreach (var child in span.Inlines) size = Math.Max(size, MaxSize(child));
        }
        return size;
    }

    public static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    yield return paragraph;
                    break;
                case List list:
                    foreach (var item in list.ListItems)
                    foreach (var inner in Paragraphs(item.Blocks))
                        yield return inner;
                    break;
                case Section section:
                    foreach (var inner in Paragraphs(section.Blocks)) yield return inner;
                    break;
            }
        }
    }

    public static string PlainText(FlowDocument document)
    {
        var text = new TextRange(document.ContentStart, document.ContentEnd).Text.Replace("\r\n", "\n");
        return text.TrimEnd('\n', ' ');
    }

    /// <summary>XAML ohne die „automatische“ Schriftfarbe und ohne die Linien-Einstellung des Papiers.</summary>
    public static string Serialize(FlowDocument document)
    {
        var range = new TextRange(document.ContentStart, document.ContentEnd);
        using var stream = new MemoryStream();
        range.Save(stream, DataFormats.Xaml);
        var xaml = Encoding.UTF8.GetString(stream.ToArray());
        try
        {
            var root = XElement.Parse(xaml);
            foreach (var element in root.DescendantsAndSelf())
            {
                foreach (var attribute in element.Attributes().ToList())
                {
                    var name = attribute.Name.LocalName;
                    if (name == "Foreground" && AutoColors.Contains(attribute.Value)) attribute.Remove();
                    else if (element == root && name is "LineHeight" or "LineStackingStrategy" or "Foreground") attribute.Remove();
                    else if (name == "LineHeight" && attribute.Value is "32" or "Auto" or "NaN") attribute.Remove();
                    else if (name == "LineStackingStrategy") attribute.Remove();
                }
            }
            return root.ToString(SaveOptions.DisableFormatting);
        }
        catch (System.Xml.XmlException)
        {
            return xaml;
        }
    }

    public static bool IsEmpty(FlowDocument document) =>
        string.IsNullOrWhiteSpace(new TextRange(document.ContentStart, document.ContentEnd).Text)
        && !document.Blocks.OfType<BlockUIContainer>().Any();
}

/// <summary>
/// Ein Textfeld auf der Seite – wie in OneNote. Außen herum ist nichts zu sehen, solange man nicht
/// darin schreibt; Rahmen, Griff zum Verschieben und Breitenregler malt die Seite darüber.
/// </summary>
public sealed class TextBoxView : RichTextBox
{
    public NoteTextBox Model { get; }
    private TextEnvironment _environment;
    private bool _silent;

    /// <summary>Der Inhalt hat sich geändert (Tippen, Formatieren).</summary>
    public event Action<TextBoxView>? Edited;

    public TextBoxView(NoteTextBox model, TextEnvironment environment)
    {
        Model = model;
        _environment = environment;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        FocusVisualStyle = null;
        AcceptsReturn = true;
        AcceptsTab = true;
        IsDocumentEnabled = true;
        AutoWordSelection = false;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SnapsToDevicePixels = false;
        Cursor = Cursors.IBeam;
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(CaretBrushProperty, "Ink");
        SetResourceReference(SelectionBrushProperty, "Accent");
        SelectionOpacity = 0.3;
        SpellCheck.IsEnabled = Services.Settings.GetBool(Keys.SpellCheck, true);
        Language = System.Windows.Markup.XmlLanguage.GetLanguage("de-DE");
        Width = Math.Max(40, model.Width);
        Template = PlainTemplate();

        _silent = true;
        Document = TextDocs.Create(model, environment);
        _silent = false;

        TextChanged += (_, _) =>
        {
            if (_silent) return;
            Edited?.Invoke(this);
        };
        DataObject.AddPastingHandler(this, OnPasting);
    }

    /// <summary>Ohne den Standardrahmen von Windows – nur der reine Text.</summary>
    private static ControlTemplate? _template;

    private static ControlTemplate PlainTemplate()
    {
        if (_template is not null) return _template;
        var template = new ControlTemplate(typeof(RichTextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(ScrollViewer.FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.PaddingProperty, new Thickness(0));
        host.SetValue(ScrollViewer.MarginProperty, new Thickness(0));
        host.SetValue(ClipToBoundsProperty, false);
        template.VisualTree = host;
        template.Seal();
        _template = template;
        return template;
    }

    public TextEnvironment Environment
    {
        get => _environment;
        set
        {
            _environment = value;
            _silent = true;
            TextDocs.ApplyLines(Document, value, Model);
            TextDocs.FitLineHeights(Document, value, Model);
            _silent = false;
        }
    }

    public bool IsEmpty => TextDocs.IsEmpty(Document);

    public string PlainText => TextDocs.PlainText(Document);

    /// <summary>Den Inhalt ins Modell übernehmen (vor dem Speichern).</summary>
    public void Commit()
    {
        Model.Xaml = TextDocs.Serialize(Document);
        Model.Text = PlainText;
        Model.Seed = null;
        Model.Width = Width;
    }

    /// <summary>Formatierung, die nur die Auswahl betrifft – mit passender Zeilenhöhe danach.</summary>
    public void Format(DependencyProperty property, object? value)
    {
        BeginChange();
        try
        {
            if (value is null)
            {
                Selection.ApplyPropertyValue(property, property == TextElement.ForegroundProperty
                    ? Foreground
                    : property == TextElement.BackgroundProperty ? null! : DependencyProperty.UnsetValue);
            }
            else
            {
                Selection.ApplyPropertyValue(property, value);
            }
            if (property == TextElement.FontSizeProperty || property == TextElement.FontFamilyProperty)
            {
                TextDocs.FitLineHeights(Document, _environment, Model, SelectedParagraphs());
            }
        }
        finally
        {
            EndChange();
        }
    }

    public IEnumerable<Paragraph> SelectedParagraphs()
    {
        var start = Selection.Start.Paragraph;
        var end = Selection.End.Paragraph;
        var inside = false;
        foreach (var paragraph in TextDocs.Paragraphs(Document.Blocks))
        {
            if (paragraph == start) inside = true;
            if (inside) yield return paragraph;
            if (paragraph == end) yield break;
        }
    }

    public void ClearFormatting()
    {
        BeginChange();
        try
        {
            Selection.ClearAllProperties();
            TextDocs.FitLineHeights(Document, _environment, Model, SelectedParagraphs());
        }
        finally
        {
            EndChange();
        }
    }

    /// <summary>Beim Einfügen aus anderen Programmen nur Text und einfache Formatierung übernehmen.</summary>
    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.Rtf) || e.DataObject.GetDataPresent(DataFormats.Xaml)) return;
        if (e.DataObject.GetDataPresent(DataFormats.UnicodeText)) return;
        e.CancelCommand();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape verlässt das Textfeld, wie in OneNote.
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Keyboard.ClearFocus();
            (Parent as FrameworkElement)?.Focus();
            return;
        }
        base.OnKeyDown(e);
    }
}
