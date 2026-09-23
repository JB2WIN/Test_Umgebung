using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Lernheft.Studio.App.Sheets;

/// <summary>Grundlage aller Fenster: gleiche Farben, Schrift und Titelleiste.</summary>
public class StudioWindow : Window
{
    public StudioWindow()
    {
        SetResourceReference(BackgroundProperty, "Chrome");
        SetResourceReference(ForegroundProperty, "Text");
        FontFamily = Ui.UiFont;
        FontSize = 13.5;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = MainWindow.AppIcon;
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && CloseOnEscape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    protected bool CloseOnEscape { get; set; } = true;
}

/// <summary>Ein Fenster mit großer Überschrift, Inhalt zum Blättern und Knöpfen unten.</summary>
public class SheetWindow : StudioWindow
{
    protected readonly StackPanel Body = new() { Margin = new Thickness(24, 4, 24, 24) };
    protected readonly DockPanel Footer = new() { LastChildFill = false, Margin = new Thickness(24, 12, 24, 18) };
    protected readonly TextBlock HeadingText;
    protected readonly StackPanel HeaderRight = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    protected readonly ScrollViewer Scroller;

    public SheetWindow(string title, double width = 640, double height = 720, bool footer = false)
    {
        Title = title;
        Width = width;
        Height = height;
        MinWidth = Math.Min(width, 420);
        MinHeight = 360;
        HeadingText = Ui.Heading(title, 22);
        var header = new DockPanel { Margin = new Thickness(24, 18, 20, 12) };
        DockPanel.SetDock(HeaderRight, Dock.Right);
        header.Children.Add(HeaderRight);
        header.Children.Add(HeadingText);

        Scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = Body,
            Focusable = false,
            PanningMode = PanningMode.VerticalOnly
        };
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        if (footer)
        {
            var footerBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = Footer };
            footerBorder.SetResourceReference(Border.BorderBrushProperty, "Line");
            footerBorder.SetResourceReference(Border.BackgroundProperty, "Surface");
            DockPanel.SetDock(footerBorder, Dock.Bottom);
            root.Children.Add(footerBorder);
        }
        root.Children.Add(Scroller);
        Content = root;
    }

    protected Button AddFooterButton(string text, Action action, string style = "Soft", bool right = true, bool isDefault = false, bool isCancel = false)
    {
        var button = Ui.Button(text, (_, _) => action(), style);
        button.MinWidth = 104;
        button.IsDefault = isDefault;
        button.IsCancel = isCancel;
        button.Margin = right ? new Thickness(8, 0, 0, 0) : new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(button, right ? Dock.Right : Dock.Left);
        Footer.Children.Add(button);
        return button;
    }
}

/// <summary>Kleines Fenster mit Überschrift, Feldern und Knöpfen; passt sich dem Inhalt an.</summary>
public class DialogWindow : StudioWindow
{
    protected readonly StackPanel Body = new();
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };

    public DialogWindow(string title, double width = 460)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = Ui.DisplayFont,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14),
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(Body);
        root.Children.Add(_buttons);
        Content = root;
    }

    protected static TextBlock Label(string text, bool first = false) =>
        Ui.Text(text, 12, FontWeights.SemiBold, "Muted", margin: new Thickness(0, first ? 0 : 14, 0, 6));

    protected Button AddButton(string text, Action action, string style = "Soft", bool isDefault = false, bool isCancel = false)
    {
        var button = Ui.Button(text, (_, _) => action(), style);
        button.MinWidth = 104;
        button.IsDefault = isDefault;
        button.IsCancel = isCancel;
        button.Margin = new Thickness(8, 0, 0, 0);
        _buttons.Children.Add(button);
        return button;
    }
}

public static class Dialogs
{
    /// <summary>Frage mit Ja/Nein in unserem Aussehen (statt der grauen Windows-MessageBox).</summary>
    public static bool Confirm(Window? owner, string title, string message, string yes, bool danger = false, string no = "Abbrechen")
    {
        var dialog = new MessageDialog(title, message, yes, no, danger) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    public static void Info(Window? owner, string title, string message)
    {
        var dialog = new MessageDialog(title, message, "OK", null, false) { Owner = owner };
        dialog.ShowDialog();
    }

    /// <summary>Drei Wege, z. B. „Hinzufügen“ / „Alles ersetzen“ / „Abbrechen“. Liefert 1, 2 oder 0.</summary>
    public static int Choose(Window? owner, string title, string message, string first, string second, bool secondDanger)
    {
        var dialog = new MessageDialog(title, message, first, "Abbrechen", false, second, secondDanger) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Choice;
    }

    private sealed class MessageDialog : DialogWindow
    {
        public int Choice { get; private set; }

        public MessageDialog(string title, string message, string yes, string? no, bool danger, string? second = null, bool secondDanger = false)
            : base(title, 440)
        {
            Body.Children.Add(Ui.Text(message, 13.5, color: "Muted", wrap: true));
            if (no is not null) AddButton(no, () => { Choice = 0; DialogResult = false; }, isCancel: true);
            if (second is not null) AddButton(second, () => { Choice = 2; DialogResult = true; }, secondDanger ? "Danger" : "Soft");
            var ok = AddButton(yes, () => { Choice = 1; DialogResult = true; }, danger ? "Danger" : "Primary", isDefault: true);
            if (danger)
            {
                ok.Style = Ui.Style("Primary");
                ok.SetResourceReference(BackgroundProperty, "Bad");
                ok.SetResourceReference(BorderBrushProperty, "Bad");
                ok.SetResourceReference(ForegroundProperty, "AccentText");
            }
            Loaded += (_, _) => ok.Focus();
        }
    }
}

/// <summary>Neues Fach anlegen oder bearbeiten: Name, eine der 20 Farben, Hausaufgaben-Suche.</summary>
public sealed class NotebookDialog : DialogWindow
{
    private readonly TextBox _name;
    private readonly CheckBox _scan;
    private readonly Dictionary<string, Border> _dots = new();
    private readonly TextBlock _colorLabel;

    public string NotebookName => _name.Text.Trim();
    public string ColorName { get; private set; }
    public bool ScanHomework => _scan.IsChecked == true;

    public NotebookDialog(Notebook? notebook) : base(notebook is null ? "Neues Fach" : "Fach bearbeiten", 470)
    {
        ColorName = notebook?.ColorName ?? NotebookColors.All[Services.Store.Library.Notebooks.Count % NotebookColors.All.Length].Name;
        Body.Children.Add(Label("Name", first: true));
        _name = Ui.Field(notebook?.Name ?? "", "z. B. Englisch");
        Body.Children.Add(_name);

        Body.Children.Add(Label("Farbe"));
        var grid = new WrapPanel { Margin = new Thickness(-3, 0, 0, 0) };
        foreach (var (name, hex, _) in NotebookColors.All)
        {
            var dot = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Margin = new Thickness(3),
                Background = new SolidColorBrush(Theme.Hex(hex)),
                Cursor = Cursors.Hand,
                ToolTip = NotebookColors.Label(name),
                BorderThickness = new Thickness(3)
            };
            dot.SetResourceReference(Border.BorderBrushProperty, "Chrome");
            var chosen = name;
            dot.MouseLeftButtonUp += (_, _) => Pick(chosen);
            _dots[name] = dot;
            grid.Children.Add(dot);
        }
        Body.Children.Add(grid);
        _colorLabel = Ui.Hint("", new Thickness(2, 6, 0, 0));
        Body.Children.Add(_colorLabel);
        Pick(ColorName);

        _scan = Ui.Switch("Hausaufgaben in diesem Fach von der KI erkennen lassen",
            notebook?.ScanHomework ?? (notebook is null ? false : notebook.ScanHomework));
        _scan.Margin = new Thickness(0, 14, 0, 0);
        Body.Children.Add(_scan);
        Body.Children.Add(Ui.Hint(Services.Settings.GetBool(Keys.AutoHomework, false)
            ? "Beim Schließen einer Notiz in diesem Fach schaut die KI nach Aufgaben."
            : "Dafür muss in den Einstellungen zusätzlich „Hausaufgaben automatisch erkennen“ an sein."));

        AddButton("Abbrechen", () => DialogResult = false, isCancel: true);
        var ok = AddButton(notebook is null ? "Anlegen" : "Sichern", () =>
        {
            if (NotebookName.Length == 0)
            {
                _name.Focus();
                return;
            }
            DialogResult = true;
        }, "Primary", isDefault: true);
        _name.TextChanged += (_, _) => ok.IsEnabled = NotebookName.Length > 0;
        ok.IsEnabled = NotebookName.Length > 0;
        Loaded += (_, _) =>
        {
            _name.Focus();
            _name.SelectAll();
        };
    }

    private void Pick(string name)
    {
        ColorName = name;
        foreach (var (key, dot) in _dots)
        {
            if (key == name) dot.SetResourceReference(Border.BorderBrushProperty, "Text");
            else dot.SetResourceReference(Border.BorderBrushProperty, "Chrome");
        }
        _colorLabel.Text = NotebookColors.Label(name);
    }
}

/// <summary>Ein einzelner Text, z. B. ein neuer Name.</summary>
public sealed class TextDialog : DialogWindow
{
    private readonly TextBox _box;

    public string Value => _box.Text.Trim();

    public TextDialog(string title, string label, string initial = "", string ok = "Sichern", bool multiline = false) : base(title, 480)
    {
        Body.Children.Add(Label(label, first: true));
        _box = Ui.Field(initial, multiline: multiline);
        if (multiline) _box.MinHeight = 90;
        Body.Children.Add(_box);
        AddButton("Abbrechen", () => DialogResult = false, isCancel: true);
        AddButton(ok, () => DialogResult = true, "Primary", isDefault: !multiline);
        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }
}
