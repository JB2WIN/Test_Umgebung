using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Lernheft.Studio.App.Sheets;

/// <summary>
/// Der KI-Helfer: Grammatik, Mathe (lösen, Tipp, prüfen), Karteikarten, Zusammenfassen und Fragen.
/// Dieselben Werkzeuge wie früher auf dem iPad – jetzt am Surface.
/// </summary>
public sealed class AiWindow : SheetWindow
{
    public enum Tab { Grammar, Math, Cards, Summary, Ask }

    private readonly AiContext _context;
    private Tab _tab;
    private readonly StackPanel _content = new();
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _running;
    private readonly Button _stop;

    public Action<string>? AppendText { get; init; }
    public Action<string>? ReplaceText { get; init; }
    public Action<List<Flashcard>>? SaveCards { get; init; }

    // Zustand je Werkzeug, damit Umschalten nichts verliert.
    private AiTasks.GrammarResult? _grammar;
    private bool _grammarApplied;
    private string _mathInput = "";
    private string _mathOutput = "";
    private int _cardCount = 8;
    private List<Flashcard> _cards = new();
    private bool _cardsSaved;
    private AiTasks.SummaryStyle _summaryStyle = AiTasks.SummaryStyle.Bullets;
    private string _summary = "";
    private readonly List<AiTasks.ChatMessage> _chat = new();

    public AiWindow(AiContext context, Tab tab, bool autoRun) : base("KI-Helfer", 720, 800)
    {
        _context = context;
        _tab = tab;
        ShowInTaskbar = true;
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        _stop = Ui.Button("Stopp", (_, _) => _running?.Cancel(), icon: Ui.GlyphClose);
        _stop.Visibility = Visibility.Collapsed;
        HeaderRight.Children.Add(_stop);

        var tabs = Ui.Segments(new[] { "Grammatik", "Mathe", "Karteikarten", "Zusammenfassen", "Fragen" }, (int)tab, index =>
        {
            _tab = (Tab)index;
            Build();
        });
        tabs.Margin = new Thickness(0, 0, 0, 10);
        Body.Children.Add(tabs);

        var banner = Ui.Row(8, Ui.Icon(context.FromSelection ? Ui.GlyphTablet : Ui.GlyphDocument, 13, "Faint"),
            Ui.Text(context.Description, 12, color: "Faint"));
        banner.Margin = new Thickness(2, 0, 0, 14);
        Body.Children.Add(banner);

        if (Services.Gemini() is null) Body.Children.Add(KeyMissing());
        Body.Children.Add(_content);
        Body.Children.Add(_error);
        Build();
        Closed += (_, _) => _running?.Cancel();
        if (autoRun && tab == Tab.Math && Services.Gemini() is not null) Loaded += (_, _) => RunMath(AiTasks.MathMode.Solve);
    }

    private UIElement KeyMissing()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("Kein Gemini-API-Schlüssel", 14, FontWeights.SemiBold));
        panel.Children.Add(Ui.Text("Die KI-Funktionen brauchen einen Schlüssel aus Google AI Studio.", 13, color: "Muted", wrap: true,
            margin: new Thickness(0, 4, 0, 10)));
        var open = Ui.Button("Schlüssel eintragen", (_, _) =>
        {
            new SettingsWindow(SettingsWindow.Section.Ai) { Owner = this }.ShowDialog();
            Build();
        }, "Primary");
        open.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(open);
        var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 14), Child = panel };
        card.SetResourceReference(Border.BackgroundProperty, "WarnSoft");
        return card;
    }

    private bool HasKey => Services.Gemini() is not null;

    private void Build()
    {
        _content.Children.Clear();
        _error.Visibility = Visibility.Collapsed;
        switch (_tab)
        {
            case Tab.Grammar: BuildGrammar(); break;
            case Tab.Math: BuildMath(); break;
            case Tab.Cards: BuildCards(); break;
            case Tab.Summary: BuildSummary(); break;
            case Tab.Ask: BuildAsk(); break;
        }
    }

    private Button Action(string text, Action action, string style = "Primary", string? icon = null)
    {
        var button = Ui.Button(text, (_, _) => action(), style, icon);
        button.IsEnabled = HasKey && _running is null;
        button.Margin = new Thickness(0, 0, 8, 0);
        button.MinHeight = 36;
        return button;
    }

    private UIElement Thinking()
    {
        var bar = new ProgressBar { IsIndeterminate = true, Height = 3, Width = 200, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 12, 0) };
        bar.SetResourceReference(ForegroundProperty, "Accent");
        bar.SetResourceReference(BackgroundProperty, "SurfaceAlt");
        var row = Ui.Row(0, bar, Ui.Text("Denkt nach …", 13, color: "Muted"));
        row.Margin = new Thickness(0, 14, 0, 0);
        return row;
    }

    private async void Run(Func<CancellationToken, Task> work)
    {
        var client = Services.Gemini();
        if (client is null) return;
        _running?.Cancel();
        _running = new CancellationTokenSource();
        var cancel = _running.Token;
        _stop.Visibility = Visibility.Visible;
        _error.Visibility = Visibility.Collapsed;
        Build();
        _content.Children.Add(Thinking());
        try
        {
            await work(cancel);
        }
        catch (Exception error)
        {
            if (!cancel.IsCancellationRequested)
            {
                _error.Text = GeminiClient.Describe(error);
                _error.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _running = null;
            _stop.Visibility = Visibility.Collapsed;
            var errorText = _error.Text;
            var errorVisible = _error.Visibility;
            Build();
            _error.Text = errorText;
            _error.Visibility = errorVisible;
        }
    }

    // MARK: - Grammatik

    private void BuildGrammar()
    {
        _content.Children.Add(Ui.Text("Findet Rechtschreib-, Grammatik- und Kommafehler und erklärt sie kurz. Dein Inhalt bleibt, wie er ist.",
            13.5, color: "Muted", wrap: true, margin: new Thickness(0, 0, 0, 12)));
        var check = Action("Text prüfen", () => Run(async cancel =>
        {
            _grammar = null;
            _grammarApplied = false;
            _grammar = await AiTasks.GrammarAsync(Services.Gemini()!, _context, cancel);
        }), icon: Ui.GlyphCheck);
        check.IsEnabled &= !_context.IsEmpty;
        check.HorizontalAlignment = HorizontalAlignment.Left;
        _content.Children.Add(check);
        if (_grammar is null) return;

        if (_grammar.Changes.Count == 0)
        {
            var good = Ui.Row(8, Ui.Icon(Ui.GlyphCheck, 16, "Good"), Ui.Text("Keine Fehler gefunden. Stark!", 15, FontWeights.SemiBold, "Good"));
            good.Margin = new Thickness(0, 16, 0, 0);
            _content.Children.Add(good);
            return;
        }
        _content.Children.Add(Ui.Text(Ui.Plural(_grammar.Changes.Count, "Korrektur", "Korrekturen"), 15, FontWeights.SemiBold,
            margin: new Thickness(0, 18, 0, 8)));
        foreach (var change in _grammar.Changes)
        {
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
            var from = new Run(change.From) { TextDecorations = TextDecorations.Strikethrough };
            from.SetResourceReference(TextElement.ForegroundProperty, "Bad");
            var arrow = new Run("  →  ");
            arrow.SetResourceReference(TextElement.ForegroundProperty, "Faint");
            var to = new Run(change.To) { FontWeight = FontWeights.SemiBold };
            to.SetResourceReference(TextElement.ForegroundProperty, "Good");
            line.Inlines.Add(from);
            line.Inlines.Add(arrow);
            line.Inlines.Add(to);
            var stack = new StackPanel();
            stack.Children.Add(line);
            stack.Children.Add(Ui.Text(change.Why, 12.5, color: "Muted", wrap: true, margin: new Thickness(0, 4, 0, 0)));
            var card = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8), Child = stack };
            card.SetResourceReference(Border.BackgroundProperty, "Surface");
            _content.Children.Add(card);
        }
        var hasTyped = !string.IsNullOrWhiteSpace(_context.TypedText);
        var apply = Ui.Button(_grammarApplied ? "Übernommen" : hasTyped ? "Alle Korrekturen übernehmen" : "Korrigierten Text einfügen", (_, _) =>
        {
            if (hasTyped) ReplaceText?.Invoke(_grammar.Corrected);
            else AppendText?.Invoke(_grammar.Corrected);
            _grammarApplied = true;
            Build();
        }, "Primary", _grammarApplied ? Ui.GlyphCheck : null);
        apply.IsEnabled = !_grammarApplied;
        apply.HorizontalAlignment = HorizontalAlignment.Left;
        apply.Margin = new Thickness(0, 8, 0, 0);
        _content.Children.Add(apply);
        if (hasTyped)
        {
            _content.Children.Add(Ui.Hint("Beim Übernehmen werden die Textfelder der Notiz zu einem Feld mit dem korrigierten Text zusammengefasst. Mit Strg+Z holst du den alten Stand zurück."));
        }
    }

    // MARK: - Mathe

    private void BuildMath()
    {
        _content.Children.Add(Ui.Text(_context.FromSelection
                ? "Die Aufgabe aus dem markierten Ausschnitt – oder tipp eine andere ein."
                : "Tipp die Aufgabe ein – oder lass das Feld leer, dann nehme ich deine Notiz.",
            13.5, color: "Muted", wrap: true, margin: new Thickness(0, 0, 0, 10)));
        var input = Ui.Field(_mathInput, "z. B. x² − 5x + 6 = 0", multiline: true);
        input.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        input.MinHeight = 64;
        input.TextChanged += (_, _) => _mathInput = input.Text;
        _content.Children.Add(input);
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(Action("Lösen", () => RunMath(AiTasks.MathMode.Solve), icon: Ui.GlyphCalculator));
        row.Children.Add(Action("Nur Tipp", () => RunMath(AiTasks.MathMode.Hint), "Soft"));
        row.Children.Add(Action("Rechnung prüfen", () => RunMath(AiTasks.MathMode.Check), "Soft"));
        _content.Children.Add(row);
        if (_mathOutput.Length > 0) _content.Children.Add(AnswerCard(_mathOutput));
    }

    private void RunMath(AiTasks.MathMode mode)
    {
        if (string.IsNullOrWhiteSpace(_mathInput) && _context.IsEmpty)
        {
            _error.Text = "Tipp eine Aufgabe ein oder schreib sie in die Notiz.";
            _error.Visibility = Visibility.Visible;
            return;
        }
        Run(async cancel =>
        {
            _mathOutput = "";
            _mathOutput = await AiTasks.MathAsync(Services.Gemini()!, _context, _mathInput, mode, cancel);
        });
    }

    // MARK: - Karteikarten

    private void BuildCards()
    {
        _content.Children.Add(Ui.Text("Erstellt Lernkarten aus deiner Notiz. Danach wiederholst du sie unter „Karteikarten“.",
            13.5, color: "Muted", wrap: true, margin: new Thickness(0, 0, 0, 12)));
        var count = new ComboBox { Width = 90, Margin = new Thickness(10, 0, 16, 0) };
        foreach (var value in new[] { 3, 5, 8, 10, 12, 15, 20, 25 }) count.Items.Add(value);
        count.SelectedItem = _cardCount;
        count.SelectionChanged += (_, _) => _cardCount = count.SelectedItem is int value ? value : 8;
        var create = Action("Karteikarten erstellen", () => Run(async cancel =>
        {
            _cards = new List<Flashcard>();
            _cardsSaved = false;
            _cards = await AiTasks.CardsAsync(Services.Gemini()!, _context, _cardCount, cancel);
        }), icon: Ui.GlyphCards);
        create.IsEnabled &= !_context.IsEmpty;
        var row = Ui.Row(0, Ui.Text("Anzahl", 13.5, color: "Muted"), count, create);
        ((FrameworkElement)row.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        _content.Children.Add(row);
        if (_cards.Count == 0) return;

        foreach (var card in _cards.ToList())
        {
            var front = Ui.Field(card.Front, "Vorderseite", multiline: true);
            front.FontWeight = FontWeights.SemiBold;
            front.TextChanged += (_, _) => card.Front = front.Text;
            var back = Ui.Field(card.Back, "Rückseite", multiline: true);
            back.Margin = new Thickness(0, 6, 0, 0);
            back.TextChanged += (_, _) => card.Back = back.Text;
            var remove = Ui.IconButton(Ui.GlyphDelete, "Karte entfernen", (_, _) =>
            {
                _cards.Remove(card);
                Build();
            }, 12);
            remove.VerticalAlignment = VerticalAlignment.Top;
            remove.Margin = new Thickness(8, 0, 0, 0);
            var fields = new StackPanel();
            fields.Children.Add(front);
            fields.Children.Add(back);
            var dock = new DockPanel();
            DockPanel.SetDock(remove, Dock.Right);
            dock.Children.Add(remove);
            dock.Children.Add(fields);
            var border = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Margin = new Thickness(0, 12, 0, 0), Child = dock };
            border.SetResourceReference(Border.BackgroundProperty, "Surface");
            _content.Children.Add(border);
        }
        var save = Ui.Button(_cardsSaved ? "Gespeichert" : $"{_cards.Count} Karten speichern", (_, _) =>
        {
            SaveCards?.Invoke(_cards.Where(c => c.Front.Trim().Length > 0 && c.Back.Trim().Length > 0).ToList());
            _cardsSaved = true;
            Build();
        }, "Primary", _cardsSaved ? Ui.GlyphCheck : Ui.GlyphSave);
        save.IsEnabled = !_cardsSaved;
        save.HorizontalAlignment = HorizontalAlignment.Left;
        save.Margin = new Thickness(0, 14, 0, 0);
        _content.Children.Add(save);
    }

    // MARK: - Zusammenfassen

    private void BuildSummary()
    {
        var styles = Ui.Segments(new[] { "Kurz", "Stichpunkte", "Lernzettel" }, (int)_summaryStyle, index => _summaryStyle = (AiTasks.SummaryStyle)index);
        styles.Margin = new Thickness(0, 0, 0, 12);
        _content.Children.Add(styles);
        var run = Action("Zusammenfassen", () => Run(async cancel =>
        {
            _summary = "";
            _summary = await AiTasks.SummaryAsync(Services.Gemini()!, _context, _summaryStyle, cancel);
        }), icon: Ui.GlyphDocument);
        run.IsEnabled &= !_context.IsEmpty;
        run.HorizontalAlignment = HorizontalAlignment.Left;
        _content.Children.Add(run);
        if (_summary.Length > 0) _content.Children.Add(AnswerCard(_summary));
    }

    // MARK: - Fragen

    private void BuildAsk()
    {
        if (_chat.Count == 0)
        {
            _content.Children.Add(Ui.Text("Frag alles zu deiner Notiz – z. B. „Erklär mir das nochmal einfacher“ oder „Frag mich ab“.",
                13.5, color: "Muted", wrap: true, margin: new Thickness(0, 0, 0, 12)));
        }
        foreach (var message in _chat)
        {
            var bubble = new Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = message.FromUser ? new Thickness(80, 0, 0, 10) : new Thickness(0, 0, 80, 10),
                HorizontalAlignment = message.FromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Child = Markdown(message.Text)
            };
            bubble.SetResourceReference(Border.BackgroundProperty, message.FromUser ? "AccentSoft" : "Surface");
            _content.Children.Add(bubble);
        }
        var input = Ui.Field("", "Deine Frage …", multiline: true);
        input.MinHeight = 44;
        input.AcceptsReturn = false;
        var send = Ui.Button("", null, "Primary", Ui.GlyphChevronRight, "Senden (Enter)");
        send.Width = 44;
        send.Margin = new Thickness(8, 0, 0, 0);
        send.IsEnabled = HasKey && _running is null;
        void Send()
        {
            var text = input.Text.Trim();
            if (text.Length == 0 || _running is not null) return;
            _chat.Add(new AiTasks.ChatMessage(true, text));
            Run(async cancel =>
            {
                var answer = await AiTasks.AskAsync(Services.Gemini()!, _context, _chat, cancel);
                _chat.Add(new AiTasks.ChatMessage(false, answer));
            });
        }
        send.Click += (_, _) => Send();
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
            e.Handled = true;
            Send();
        };
        var dock = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(send, Dock.Right);
        dock.Children.Add(send);
        dock.Children.Add(input);
        _content.Children.Add(dock);
        Dispatcher.BeginInvoke(() => input.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(() => Scroller.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // MARK: - Antworten

    private UIElement AnswerCard(string text)
    {
        var stack = new StackPanel();
        stack.Children.Add(Markdown(text));
        var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var insert = Ui.Button("In die Notiz einfügen", (_, _) => AppendText?.Invoke(AiTasks.Plain(text)), icon: Ui.GlyphAdd);
        insert.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(insert);
        row.Children.Add(Ui.Button("Kopieren", (_, _) => Clipboard.SetText(AiTasks.Plain(text)), icon: Ui.GlyphCopy));
        stack.Children.Add(row);
        var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0, 16, 0, 0), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        card.BorderThickness = new Thickness(1);
        return card;
    }

    /// <summary>Einfaches Markdown: **fett**, *kursiv*, Listen – mehr schreibt die KI nicht.</summary>
    public static UIElement Markdown(string text)
    {
        var stack = new StackPanel();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var block = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 1, 0, 1) };
            if (line.Length == 0)
            {
                stack.Children.Add(new Border { Height = 6 });
                continue;
            }
            var indent = 0.0;
            var bullet = Regex.Match(line, @"^\s*[-•*]\s+(.*)$");
            var number = Regex.Match(line, @"^\s*(\d+)[.)]\s+(.*)$");
            if (bullet.Success)
            {
                indent = 16;
                block.Inlines.Add(new Run("•  "));
                line = bullet.Groups[1].Value;
            }
            else if (number.Success)
            {
                indent = 16;
                block.Inlines.Add(new Run(number.Groups[1].Value + ".  ") { FontWeight = FontWeights.SemiBold });
                line = number.Groups[2].Value;
            }
            line = Regex.Replace(line, @"^#+\s*", "");
            foreach (var part in Regex.Split(line, @"(\*\*[^*]+\*\*|\*[^*]+\*)"))
            {
                if (part.Length == 0) continue;
                if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
                    block.Inlines.Add(new Run(part[2..^2]) { FontWeight = FontWeights.SemiBold });
                else if (part.StartsWith('*') && part.EndsWith('*') && part.Length > 2)
                    block.Inlines.Add(new Run(part[1..^1]) { FontStyle = FontStyles.Italic });
                else block.Inlines.Add(new Run(part));
            }
            block.Margin = new Thickness(indent, 1, 0, 1);
            stack.Children.Add(block);
        }
        return stack;
    }
}
