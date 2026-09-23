using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lernheft.Studio.App.Sheets;

/// <summary>
/// Karteikarten: Stapel ansehen und bearbeiten, fällige Karten lernen. Karten, die du gut kannst,
/// kommen erst nach 1, 2, 4, 8, 16 oder 32 Tagen wieder (Leitner-Boxen).
/// </summary>
public sealed class FlashcardsWindow : SheetWindow
{
    private List<(Deck Deck, Flashcard Card)> _queue = new();
    private int _index;
    private bool _flipped;
    private int _known;
    private int _total;
    private readonly HashSet<Guid> _repeated = new();
    private string _sessionTitle = "";

    public FlashcardsWindow() : base("Karteikarten", 760, 780)
    {
        PreviewKeyDown += OnKey;
        ShowOverview();
    }

    // MARK: - Übersicht

    private void ShowOverview()
    {
        CloseOnEscape = true;
        HeadingText.Text = "Karteikarten";
        HeaderRight.Children.Clear();
        Body.Children.Clear();
        var store = Services.Store;
        var due = store.DueCardCount;
        if (store.Library.Decks.Count > 0)
        {
            var all = new StackPanel();
            var text = new StackPanel();
            text.Children.Add(Ui.Text(due == 0 ? "Heute ist nichts fällig" : $"{Ui.Plural(due, "Karte", "Karten")} fällig", 17, FontWeights.SemiBold));
            text.Children.Add(Ui.Text("Karten, die du gut kannst, kommen erst nach 1, 2, 4, 8, 16 oder 32 Tagen wieder.", 12.5, color: "Muted",
                wrap: true, margin: new Thickness(0, 3, 0, 0)));
            var start = Ui.Button("Alle fälligen lernen", (_, _) => Start("Alle fälligen Karten",
                store.DueCards().OrderBy(_ => Random.Shared.Next()).ToList()), "Primary", Ui.GlyphPlay);
            start.IsEnabled = due > 0;
            start.VerticalAlignment = VerticalAlignment.Center;
            var dock = new DockPanel();
            DockPanel.SetDock(start, Dock.Right);
            dock.Children.Add(start);
            dock.Children.Add(text);
            all.Children.Add(dock);
            Body.Children.Add(Ui.Card(all));
        }

        var decks = new StackPanel();
        decks.Children.Add(Ui.CardHeader("Stapel", Ui.GlyphCards, "Neuer Stapel", () =>
        {
            var dialog = new TextDialog("Neuer Stapel", "Name", "", "Anlegen") { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Value.Length == 0) return;
            var deck = new Deck { Name = dialog.Value };
            store.Library.Decks.Add(deck);
            store.Save();
            ShowDeck(deck);
        }));
        if (store.Library.Decks.Count == 0)
        {
            decks.Children.Add(Ui.Text("Noch keine Karteikarten. Öffne eine Notiz, klick auf „KI-Helfer“ und wähle „Karteikarten“ – " +
                                       "oder leg oben rechts selbst einen Stapel an.", 13.5, color: "Muted", wrap: true));
        }
        foreach (var deck in store.Library.Decks)
        {
            var dueCount = deck.Cards.Count(c => c.DueAt <= LibraryStore.EndOfToday);
            var mastered = deck.Cards.Count(c => c.Box >= 4);
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(Ui.Text(deck.Name, 14.5, FontWeights.SemiBold));
            var counts = new WrapPanel { Margin = new Thickness(0, 3, 0, 6) };
            counts.Children.Add(Ui.Text($"{deck.Cards.Count} Karten", 12.5, color: "Faint"));
            counts.Children.Add(Ui.Text($"   {dueCount} fällig", 12.5, dueCount > 0 ? FontWeights.SemiBold : FontWeights.Normal, dueCount > 0 ? "Warn" : "Faint"));
            counts.Children.Add(Ui.Text($"   {mastered} sicher", 12.5, color: "Good"));
            info.Children.Add(counts);
            if (deck.Cards.Count > 0)
            {
                var bar = new ProgressBar { Value = mastered, Maximum = deck.Cards.Count, Height = 5, BorderThickness = new Thickness(0), MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left, Width = 360 };
                bar.SetResourceReference(ForegroundProperty, "Good");
                bar.SetResourceReference(BackgroundProperty, "SurfaceAlt");
                info.Children.Add(bar);
            }
            var learn = Ui.Button("Lernen", (_, _) => Start(deck.Name, store.DueCards(deck.Id).OrderBy(_ => Random.Shared.Next()).ToList()),
                "Primary", Ui.GlyphPlay);
            learn.IsEnabled = dueCount > 0;
            learn.ToolTip = dueCount > 0 ? null : "Heute ist in diesem Stapel nichts fällig.";
            var edit = Ui.Button("Bearbeiten", (_, _) => ShowDeck(deck), icon: Ui.GlyphEdit);
            edit.Margin = new Thickness(8, 0, 0, 0);
            var buttons = Ui.Row(0, learn, edit);
            buttons.VerticalAlignment = VerticalAlignment.Center;
            var dock = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
            DockPanel.SetDock(buttons, Dock.Right);
            dock.Children.Add(buttons);
            dock.Children.Add(info);
            decks.Children.Add(dock);
            decks.Children.Add(Ui.Divider(new Thickness(0, 2, 0, 2)));
        }
        if (decks.Children.Count > 0 && decks.Children[^1] is Border last && last.Height == 1) decks.Children.Remove(last);
        Body.Children.Add(Ui.Card(decks));
    }

    // MARK: - Stapel bearbeiten

    private void ShowDeck(Deck deck)
    {
        CloseOnEscape = false;
        HeadingText.Text = deck.Name;
        HeaderRight.Children.Clear();
        var back = Ui.Button("Zurück", (_, _) =>
        {
            Services.Store.Save();
            ShowOverview();
        }, icon: Ui.GlyphChevronLeft);
        HeaderRight.Children.Add(back);
        Body.Children.Clear();

        var head = new StackPanel();
        head.Children.Add(Ui.Text("Name", 12, FontWeights.SemiBold, "Muted", margin: new Thickness(0, 0, 0, 6)));
        var name = Ui.Field(deck.Name);
        name.TextChanged += (_, _) =>
        {
            deck.Name = name.Text.Trim().Length == 0 ? "Stapel" : name.Text.Trim();
            HeadingText.Text = deck.Name;
        };
        head.Children.Add(name);
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var study = Ui.Button($"Ganzen Stapel üben ({deck.Cards.Count})", (_, _) =>
        {
            Services.Store.Save();
            Start(deck.Name, deck.Cards.Select(c => (deck, c)).OrderBy(_ => Random.Shared.Next()).ToList());
        }, "Primary", Ui.GlyphPlay);
        study.IsEnabled = deck.Cards.Count > 0;
        study.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(study);
        row.Children.Add(Ui.Button("Stapel löschen", (_, _) =>
        {
            if (!Dialogs.Confirm(this, "Stapel löschen?", $"„{deck.Name}“ mit {Ui.Plural(deck.Cards.Count, "Karte", "Karten")} wird gelöscht.", "Löschen", danger: true)) return;
            Services.Store.DeleteDeck(deck.Id);
            ShowOverview();
        }, "Danger", Ui.GlyphDelete));
        head.Children.Add(row);
        Body.Children.Add(Ui.Card(head));

        var cards = new StackPanel();
        cards.Children.Add(Ui.CardHeader("Karten", Ui.GlyphCards, "Karte hinzufügen", () =>
        {
            deck.Cards.Add(new Flashcard { Front = "", Back = "" });
            Services.Store.Save();
            ShowDeck(deck);
            Dispatcher.BeginInvoke(() => Scroller.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
        }));
        if (deck.Cards.Count == 0) cards.Children.Add(Ui.Text("Der Stapel ist leer.", 13.5, color: "Muted"));
        foreach (var card in deck.Cards.ToList())
        {
            var front = Ui.Field(card.Front, "Vorderseite", multiline: true);
            front.FontWeight = FontWeights.SemiBold;
            front.TextChanged += (_, _) => card.Front = front.Text;
            var backField = Ui.Field(card.Back, "Rückseite", multiline: true);
            backField.Margin = new Thickness(0, 6, 0, 0);
            backField.TextChanged += (_, _) => card.Back = backField.Text;
            var fields = new StackPanel();
            fields.Children.Add(front);
            fields.Children.Add(backField);
            var dueText = card.DueAt <= LibraryStore.EndOfToday ? "fällig" : "fällig am " + card.DueAt.ToString("d. MMM", new System.Globalization.CultureInfo("de-DE"));
            fields.Children.Add(Ui.Text($"Box {card.Box} · {dueText}", 11.5, color: "Faint", margin: new Thickness(2, 6, 0, 0)));
            var remove = Ui.IconButton(Ui.GlyphDelete, "Karte löschen", (_, _) =>
            {
                deck.Cards.Remove(card);
                Services.Store.Save();
                ShowDeck(deck);
            }, 12);
            remove.VerticalAlignment = VerticalAlignment.Top;
            remove.Margin = new Thickness(8, 0, 0, 0);
            var dock = new DockPanel { Margin = new Thickness(0, 6, 0, 10) };
            DockPanel.SetDock(remove, Dock.Right);
            dock.Children.Add(remove);
            dock.Children.Add(fields);
            cards.Children.Add(dock);
        }
        Body.Children.Add(Ui.Card(cards));
        Closing -= SaveOnClose;
        Closing += SaveOnClose;
    }

    private void SaveOnClose(object? sender, System.ComponentModel.CancelEventArgs e) => Services.Store.Save();

    // MARK: - Lernen

    private void Start(string title, List<(Deck, Flashcard)> queue)
    {
        _queue = queue;
        _index = 0;
        _flipped = false;
        _known = 0;
        _total = queue.Count;
        _repeated.Clear();
        _sessionTitle = title;
        CloseOnEscape = false;
        ShowCard();
    }

    private void ShowCard()
    {
        HeadingText.Text = _sessionTitle;
        HeaderRight.Children.Clear();
        HeaderRight.Children.Add(Ui.Button("Beenden", (_, _) =>
        {
            Services.Store.Save();
            ShowOverview();
        }, icon: Ui.GlyphClose));
        Body.Children.Clear();
        if (_index >= _queue.Count)
        {
            Services.Store.Save();
            Body.Children.Add(Finished());
            return;
        }
        var (_, card) = _queue[_index];
        var progress = new ProgressBar { Value = _index, Maximum = Math.Max(1, _queue.Count), Height = 5, BorderThickness = new Thickness(0), Margin = new Thickness(0, 4, 0, 6) };
        progress.SetResourceReference(ForegroundProperty, "Accent");
        progress.SetResourceReference(BackgroundProperty, "SurfaceAlt");
        Body.Children.Add(progress);
        Body.Children.Add(Ui.Text($"Karte {_index + 1} von {_queue.Count}", 12.5, color: "Faint", margin: new Thickness(0, 0, 0, 14)));
        var face = IndexCard(_flipped ? card.Back : card.Front, _flipped ? "ANTWORT" : "FRAGE");
        face.MouseLeftButtonUp += (_, _) => Flip();
        face.Cursor = Cursors.Hand;
        Body.Children.Add(face);
        if (!_flipped)
        {
            var show = Ui.Button("Antwort zeigen  (Leertaste)", (_, _) => Flip(), "Primary");
            show.HorizontalAlignment = HorizontalAlignment.Center;
            show.MinWidth = 280;
            show.MinHeight = 42;
            show.Margin = new Thickness(0, 22, 0, 0);
            Body.Children.Add(show);
            return;
        }
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(0, 22, 0, 0) };
        grid.Children.Add(Rate("Nochmal", "gleich", "Bad", LibraryStore.Rating.Again, "1"));
        grid.Children.Add(Rate("Schwer", "1 Tag", "Warn", LibraryStore.Rating.Hard, "2"));
        grid.Children.Add(Rate("Gut", Days(Math.Min(card.Box + 1, 6)), "Good", LibraryStore.Rating.Good, "3"));
        grid.Children.Add(Rate("Leicht", Days(Math.Min(card.Box + 2, 6)), "Accent", LibraryStore.Rating.Easy, "4"));
        Body.Children.Add(grid);
    }

    private static string Days(int box)
    {
        var days = LibraryStore.IntervalDays(box);
        return days == 1 ? "1 Tag" : $"{days} Tage";
    }

    private void Flip()
    {
        if (_index >= _queue.Count) return;
        _flipped = !_flipped;
        ShowCard();
        if (Body.Children.Count > 2 && Body.Children[2] is FrameworkElement card)
        {
            var scale = new ScaleTransform(1, 1);
            card.RenderTransformOrigin = new Point(0.5, 0.5);
            card.RenderTransform = scale;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    private Button Rate(string title, string detail, string color, LibraryStore.Rating rating, string key)
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(title, 15, FontWeights.SemiBold, color));
        stack.Children.Add(Ui.Text($"{detail}  ·  {key}", 11.5, color: "Faint"));
        ((TextBlock)stack.Children[0]).HorizontalAlignment = HorizontalAlignment.Center;
        ((TextBlock)stack.Children[1]).HorizontalAlignment = HorizontalAlignment.Center;
        var button = new Button { Style = Ui.Style("Soft"), Content = stack, Margin = new Thickness(4, 0, 4, 0), Padding = new Thickness(8, 10, 8, 10) };
        button.Click += (_, _) => RateCard(rating);
        return button;
    }

    private void RateCard(LibraryStore.Rating rating)
    {
        if (_index >= _queue.Count || !_flipped) return;
        var entry = _queue[_index];
        LibraryStore.Review(entry.Card, rating);
        if (rating == LibraryStore.Rating.Again && _repeated.Add(entry.Card.Id)) _queue.Add(entry);
        if (rating is LibraryStore.Rating.Good or LibraryStore.Rating.Easy && !_repeated.Contains(entry.Card.Id)) _known++;
        _flipped = false;
        _index++;
        ShowCard();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_queue.Count == 0 || _index >= _queue.Count || Keyboard.FocusedElement is TextBox) return;
        if (e.Key is Key.Space or Key.Enter)
        {
            e.Handled = true;
            if (!_flipped) Flip();
            return;
        }
        if (!_flipped) return;
        var rating = e.Key switch
        {
            Key.D1 or Key.NumPad1 => LibraryStore.Rating.Again,
            Key.D2 or Key.NumPad2 => LibraryStore.Rating.Hard,
            Key.D3 or Key.NumPad3 => LibraryStore.Rating.Good,
            Key.D4 or Key.NumPad4 => LibraryStore.Rating.Easy,
            _ => (LibraryStore.Rating?)null
        };
        if (rating is null) return;
        e.Handled = true;
        RateCard(rating.Value);
    }

    private UIElement Finished()
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) };
        var icon = Ui.Icon(Ui.GlyphCheck, 54, "Good");
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock
        {
            Text = _total == 0 ? "Heute ist nichts fällig" : "Geschafft!",
            FontFamily = Ui.DisplayFont,
            FontSize = 30,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 6)
        });
        if (_total > 0)
        {
            var summary = Ui.Text($"{_known} von {_total} Karten sofort gewusst.", 14.5, color: "Muted");
            summary.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(summary);
        }
        var back = Ui.Button("Zurück zu den Stapeln", (_, _) => ShowOverview(), "Primary");
        back.HorizontalAlignment = HorizontalAlignment.Center;
        back.Margin = new Thickness(0, 22, 0, 0);
        stack.Children.Add(back);
        return stack;
    }

    /// <summary>Karteikarte im Look einer echten Karte: rote Kopflinie, blaue Linien.</summary>
    private static Border IndexCard(string text, string label)
    {
        var lines = new Canvas { IsHitTestVisible = false };
        var grid = new Grid { MinHeight = 330 };
        grid.Children.Add(lines);
        var caption = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0x9E)),
            Margin = new Thickness(22, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        grid.Children.Add(caption);
        var body = new TextBlock
        {
            Text = text,
            FontSize = text.Length > 160 ? 17 : text.Length > 70 ? 20 : 24,
            FontFamily = Ui.DisplayFont,
            Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0x1C, 0x28)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(40, 70, 40, 30)
        };
        grid.Children.Add(body);
        grid.SizeChanged += (_, args) =>
        {
            lines.Children.Clear();
            var width = args.NewSize.Width;
            lines.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = width,
                Height = 2,
                Fill = new SolidColorBrush(Color.FromRgb(0xE6, 0x8A, 0x80))
            });
            Canvas.SetTop(lines.Children[0], 56);
            for (var y = 92.0; y < args.NewSize.Height - 10; y += 36)
            {
                var line = new System.Windows.Shapes.Rectangle { Width = width, Height = 1, Fill = new SolidColorBrush(Color.FromRgb(0xC6, 0xD9, 0xE8)) };
                Canvas.SetTop(line, y);
                lines.Children.Add(line);
            }
        };
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFD, 0xFD, 0xFA)),
            CornerRadius = new CornerRadius(14),
            ClipToBounds = true,
            MaxWidth = 620,
            Child = grid,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Direction = 270, Opacity = 0.22 }
        };
    }
}
