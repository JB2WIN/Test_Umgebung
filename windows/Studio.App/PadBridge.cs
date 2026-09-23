using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lernheft.Studio.App.Editor;

namespace Lernheft.Studio.App;

/// <summary>
/// Verbindet die offene Notiz mit dem iPad. Das iPad bekommt Papier, Bilder und Text der Seite und
/// schickt Striche zurück – schon während sie entstehen. Alles läuft hier auf dem UI-Thread.
/// </summary>
public sealed class PadBridge
{
    private readonly PadServer _server;
    private PadLink? _link;
    private NoteEditor? _editor;
    private readonly Dispatcher _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _textTimer;
    private readonly DispatcherTimer _viewTimer;
    private readonly Dictionary<int, string> _sentPages = new();
    private readonly Dictionary<Guid, string> _boxSignatures = new();
    private readonly Dictionary<Guid, (int First, int Last)> _boxPages = new();
    private readonly Dictionary<Guid, string> _sentImages = new();
    private readonly Dictionary<string, byte[]> _imageCache = new();
    private bool _fullTextRefresh = true;
    private string? _importFolder;
    private bool _userDisconnected;

    public bool Connected => _link is { IsOpen: true };
    public string DeviceName => _link?.DeviceName ?? "";

    public event Action? StatusChanged;
    public event Action<Guid>? OpenRequested;
    public event Action<Guid?>? NewNoteRequested;
    public event Action? LibraryChanged;

    public PadBridge(PadServer server)
    {
        _server = server;
        _textTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(320) };
        _textTimer.Tick += (_, _) =>
        {
            _textTimer.Stop();
            SendTextLayers();
        };
        _viewTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(140) };
        _viewTimer.Tick += (_, _) =>
        {
            _viewTimer.Stop();
            SendView();
        };
        server.Connected += link => _dispatcher.BeginInvoke(() => OnConnected(link));
        server.Disconnected += link => _dispatcher.BeginInvoke(() => OnDisconnected(link));
    }

    private void OnConnected(PadLink link)
    {
        _link = link;
        _userDisconnected = false;
        link.Received += message => _dispatcher.BeginInvoke(() => Handle(link, message));
        ResetSent();
        SendCurrentNote();
        StatusChanged?.Invoke();
    }

    private void OnDisconnected(PadLink link)
    {
        if (_link != link) return;
        _link = null;
        _editor?.Page.ClearLive();
        StatusChanged?.Invoke();
    }

    public void Disconnect()
    {
        if (_link is null) return;
        _userDisconnected = true;
        var bye = PadProtocol.Message(PadProtocol.Bye);
        bye["reason"] = "Am Surface getrennt.";
        var link = _link;
        _ = link.SendAsync(bye).ContinueWith(_ => link.Close());
    }

    public bool UserDisconnected => _userDisconnected;

    private void ResetSent()
    {
        _sentPages.Clear();
        _boxSignatures.Clear();
        _boxPages.Clear();
        _sentImages.Clear();
        _fullTextRefresh = true;
    }

    // MARK: - Welche Notiz ist offen?

    public void Attach(NoteEditor editor)
    {
        _editor = editor;
        ResetSent();
        SendCurrentNote();
    }

    public void Detach(NoteEditor editor)
    {
        if (_editor != editor) return;
        _editor = null;
        ResetSent();
        SendCurrentNote();
    }

    private void SendCurrentNote()
    {
        if (_link is null) return;
        if (_editor?.Note is null)
        {
            var none = PadProtocol.Message(PadProtocol.Note);
            none["id"] = null;
            var recent = new JsonArray();
            foreach (var note in Services.Store.Library.Notes.OrderByDescending(n => n.Updated).Take(10))
            {
                var notebook = Services.Store.NotebookOf(note);
                recent.Add(new JsonObject
                {
                    ["id"] = note.Id.ToString(),
                    ["title"] = note.Title,
                    ["subject"] = notebook?.Name ?? "",
                    ["color"] = NotebookColors.Hex(notebook?.ColorName)
                });
            }
            none["recent"] = recent;
            var notebooks = new JsonArray();
            foreach (var notebook in Services.Store.Library.Notebooks)
            {
                notebooks.Add(new JsonObject
                {
                    ["id"] = notebook.Id.ToString(),
                    ["name"] = notebook.Name,
                    ["color"] = NotebookColors.Hex(notebook.ColorName)
                });
            }
            none["notebooks"] = notebooks;
            _link.Send(none);
            return;
        }
        SendNoteMeta();
        SendInk();
        SendImages();
        _fullTextRefresh = true;
        SendTextLayers();
        SendView();
    }

    private void SendNoteMeta()
    {
        if (_link is null || _editor?.Note is not { } note) return;
        var notebook = Services.Store.NotebookOf(note);
        var message = PadProtocol.Message(PadProtocol.Note);
        message["id"] = note.Id.ToString();
        message["title"] = note.Title;
        message["subject"] = notebook?.Name ?? "";
        message["color"] = NotebookColors.Hex(notebook?.ColorName);
        message["paper"] = note.Paper;
        message["pageCount"] = Math.Max(1, note.PageCount);
        message["pageWidth"] = note.Width;
        message["pageHeight"] = Paper.PageHeight;
        _link.Send(message);
    }

    public void NoteMetaChanged(NoteEditor editor)
    {
        if (editor != _editor) return;
        SendNoteMeta();
    }

    private void SendInk()
    {
        if (_link is null || _editor?.Note is not { } note) return;
        var message = PadProtocol.Message(PadProtocol.Ink);
        message["noteId"] = note.Id.ToString();
        message["strokes"] = PadProtocol.StrokesToJson(_editor.Page.Ink.Strokes);
        _link.Send(message);
    }

    public void SendInkOps(NoteEditor editor, List<InkStroke> added, List<string> removed)
    {
        if (_link is null || editor != _editor || editor.Note is null) return;
        var message = PadProtocol.Message(PadProtocol.Ops);
        message["noteId"] = editor.Note.Id.ToString();
        message["add"] = PadProtocol.StrokesToJson(added);
        message["remove"] = new JsonArray(removed.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray());
        _link.Send(message);
    }

    // MARK: - Bilder

    private void SendImages()
    {
        if (_link is null || _editor?.Note is not { } note) return;
        var items = new JsonArray();
        foreach (var background in _editor.Page.Content.Backgrounds)
        {
            var frame = _editor.Page.ImageFrame(background);
            var path = Services.Store.ImagePath(note.Id, background.File);
            var revision = background.File + "|" + (File.Exists(path) ? new FileInfo(path).Length : 0);
            var item = new JsonObject
            {
                ["id"] = background.Id.ToString(),
                ["x"] = frame.X,
                ["y"] = frame.Y,
                ["w"] = frame.Width,
                ["h"] = frame.Height,
                ["rev"] = revision
            };
            if (!_sentImages.TryGetValue(background.Id, out var sent) || sent != revision)
            {
                var data = ImageForPad(path, revision);
                if (data is null) continue;
                item["data"] = Convert.ToBase64String(data);
                _sentImages[background.Id] = revision;
            }
            items.Add(item);
        }
        var message = PadProtocol.Message(PadProtocol.Images);
        message["noteId"] = note.Id.ToString();
        message["seamless"] = Services.Settings.GetBool(Keys.SeamlessImport, true);
        message["items"] = items;
        _link.Send(message);
    }

    /// <summary>Große Scans verkleinert schicken – das iPad braucht keine 12 Megapixel.</summary>
    private byte[]? ImageForPad(string path, string revision)
    {
        if (_imageCache.TryGetValue(revision, out var cached)) return cached;
        var source = ImageTools.Load(path);
        if (source is null) return null;
        var shrunk = ImageTools.Shrink(source, 2000);
        var transparent = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        var data = transparent ? ImageTools.Png(shrunk) : ImageTools.Jpeg(shrunk, 86);
        if (_imageCache.Count > 40) _imageCache.Clear();
        _imageCache[revision] = data;
        return data;
    }

    // MARK: - Textebene

    public void TextChanged(NoteEditor editor)
    {
        if (editor != _editor) return;
        if (_link is null) return;
        _textTimer.Stop();
        _textTimer.Start();
        // Bilder können sich mit geändert haben (verschoben, radiert).
        SendImagesIfChanged();
    }

    private string _lastImageSignature = "";

    private void SendImagesIfChanged()
    {
        if (_editor?.Note is null) return;
        var signature = string.Join(";", _editor.Page.Content.Backgrounds.Select(b => $"{b.Id}:{b.File}:{b.X}:{b.Y}:{b.Width}:{b.Height}"));
        if (signature == _lastImageSignature) return;
        _lastImageSignature = signature;
        SendImages();
    }

    private void SendTextLayers()
    {
        if (_link is null || _editor?.Note is not { } note) return;
        var page = _editor.Page;
        page.CommitAll();
        var environment = page.Environment;
        var pagesToRender = new SortedSet<int>();
        var pageCount = Math.Max(1, note.PageCount);

        if (_fullTextRefresh)
        {
            for (var index = 0; index < pageCount; index++) pagesToRender.Add(index);
            _boxSignatures.Clear();
            _boxPages.Clear();
            _fullTextRefresh = false;
        }

        var present = new HashSet<Guid>();
        foreach (var view in page.Boxes)
        {
            var model = view.Model;
            present.Add(model.Id);
            var top = model.Y;
            var bottom = model.Y + Math.Max(view.ActualHeight, 32);
            var span = ((int)(top / Paper.PageHeight), (int)(bottom / Paper.PageHeight));
            var signature = $"{model.X}|{model.Y}|{model.Width}|{view.ActualHeight}|{Hash(model.Xaml)}";
            var changed = !_boxSignatures.TryGetValue(model.Id, out var old) || old != signature;
            if (changed)
            {
                for (var p = span.Item1; p <= span.Item2; p++) pagesToRender.Add(p);
                if (_boxPages.TryGetValue(model.Id, out var previous))
                {
                    for (var p = previous.First; p <= previous.Last; p++) pagesToRender.Add(p);
                }
            }
            _boxSignatures[model.Id] = signature;
            _boxPages[model.Id] = span;
        }
        foreach (var gone in _boxPages.Keys.Where(id => !present.Contains(id)).ToList())
        {
            var (first, last) = _boxPages[gone];
            for (var p = first; p <= last; p++) pagesToRender.Add(p);
            _boxPages.Remove(gone);
            _boxSignatures.Remove(gone);
        }

        foreach (var index in pagesToRender.Where(p => p >= 0 && p < pageCount))
        {
            byte[]? data;
            try
            {
                data = PageRenderer.TextLayer(note, page.Content, environment, index, 3, _link.Dark);
            }
            catch (Exception error)
            {
                Services.Log("Textebene: " + error.Message);
                continue;
            }
            var hash = data is null ? "" : Convert.ToHexString(SHA1.HashData(data));
            if (_sentPages.TryGetValue(index, out var sent) && sent == hash) continue;
            if (!_sentPages.ContainsKey(index) && data is null) continue;
            _sentPages[index] = hash;
            var message = PadProtocol.Message(PadProtocol.Text);
            message["noteId"] = note.Id.ToString();
            message["page"] = index;
            message["scale"] = 3;
            message["data"] = data is null ? null : Convert.ToBase64String(data);
            _link.Send(message);
        }
    }

    private static string Hash(string text) => text.Length == 0 ? "" : Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..12];

    // MARK: - Ausschnitt

    public void ViewChanged(NoteEditor editor)
    {
        if (editor != _editor || _link is null) return;
        if (!_viewTimer.IsEnabled) _viewTimer.Start();
    }

    private void SendView()
    {
        if (_link is null || _editor?.Note is not { } note) return;
        var view = _editor.Viewport;
        var message = PadProtocol.Message(PadProtocol.View);
        message["noteId"] = note.Id.ToString();
        message["top"] = Math.Round(view.Top, 1);
        message["height"] = Math.Round(view.Height, 1);
        _link.Send(message);
    }

    // MARK: - Nachrichten vom iPad

    private void Handle(PadLink link, JsonObject message)
    {
        if (link != _link) return;
        var type = PadProtocol.TypeOf(message);
        try
        {
            switch (type)
            {
                case PadProtocol.Theme:
                    link.Dark = message.Flag("dark");
                    _fullTextRefresh = true;
                    SendTextLayers();
                    return;
                case PadProtocol.Open:
                    if (message.Guid("noteId") is Guid open) OpenRequested?.Invoke(open);
                    return;
                case PadProtocol.NewNote:
                    NewNoteRequested?.Invoke(message.Guid("notebookId"));
                    return;
                case PadProtocol.ImportBegin:
                    BeginImport();
                    return;
                case PadProtocol.ImportFile:
                    ImportFile(message);
                    return;
                case PadProtocol.ImportEnd:
                    _ = FinishImportAsync();
                    return;
            }

            // Alles Weitere betrifft die offene Notiz.
            if (_editor?.Note is not { } note || message.Guid("noteId") != note.Id)
            {
                if (type == PadProtocol.Ops || type == PadProtocol.Live) SendCurrentNote();
                return;
            }
            var page = _editor.Page;
            switch (type)
            {
                case PadProtocol.Live:
                {
                    var sid = message.String("sid") ?? "";
                    var template = new InkStroke
                    {
                        Color = message.String("c") ?? "#1A1F2B",
                        Width = message.Number("w", 2.5),
                        Kind = message.String("k") ?? "pen",
                        WidthFactors = message.Flag("wf")
                    };
                    var points = PadProtocol.NumbersFromJson(message["p"]);
                    if (points.Count >= 3) page.UpdateLive(sid, template, points);
                    if (message.Flag("cancel")) page.EndLive(sid);
                    break;
                }
                case PadProtocol.Ops:
                {
                    var add = PadProtocol.StrokesFromJson(message["add"]);
                    var remove = PadProtocol.StringsFromJson(message["remove"]);
                    var live = PadProtocol.StringsFromJson(message["live"]);
                    page.ApplyInk(add, remove, live);
                    _editor.MarkInkChangedFromPad();
                    break;
                }
                case PadProtocol.Pages:
                {
                    var count = (int)message.Number("count", note.PageCount);
                    if (count > note.PageCount && count < 400)
                    {
                        Services.Store.UpdateNote(note.Id, n => n.PageCount = count, touch: false);
                        page.RefreshPaper();
                        SendNoteMeta();
                    }
                    break;
                }
                case PadProtocol.EraseImage:
                {
                    var numbers = PadProtocol.NumbersFromJson(message["points"]);
                    var points = new List<Point>();
                    for (var i = 0; i + 1 < numbers.Count; i += 2) points.Add(new Point(numbers[i], numbers[i + 1]));
                    if (page.EraseInImages(null, points, message.Number("radius", 12))) SendImages();
                    break;
                }
                case PadProtocol.AddText:
                {
                    var items = new List<NoteTextBox>();
                    if (message["items"] is JsonArray array)
                    {
                        foreach (var node in array.OfType<JsonObject>())
                        {
                            var text = node.String("text") ?? "";
                            if (text.Length == 0) continue;
                            items.Add(new NoteTextBox
                            {
                                X = node.Number("x"),
                                Y = node.Number("y"),
                                Width = Math.Max(24, node.Number("width", 120)),
                                Text = text,
                                LineHeight = node.Flag("free") ? node.Number("fontSize", 15) * 1.35 : null,
                                Seed = new TextSeed
                                {
                                    Text = text,
                                    FontSize = node["fontSize"] is null ? null : node.Number("fontSize"),
                                    Color = node.String("color"),
                                    Script = node.Flag("script")
                                }
                            });
                        }
                    }
                    if (items.Count > 0)
                    {
                        page.PushUndo();
                        page.AddTextItems(items);
                    }
                    break;
                }
                case PadProtocol.DeleteIn:
                    page.DeleteBoxesIn(new Rect(message.Number("x"), message.Number("y"), Math.Max(0, message.Number("w")), Math.Max(0, message.Number("h"))));
                    break;
                case PadProtocol.Convert:
                    _ = ConvertAsync(link, _editor, message);
                    break;
                case PadProtocol.Ai:
                {
                    var image = message.String("image");
                    var images = string.IsNullOrEmpty(image)
                        ? new List<GeminiImage>()
                        : new List<GeminiImage> { new(Convert.FromBase64String(image), "image/png") };
                    var context = new AiContext(note.Title, "", images, FromSelection: true);
                    var mode = message.String("mode") ?? "ask";
                    Application.Current.MainWindow?.Activate();
                    _editor.OpenAiWindow(context, mode == "math" ? Sheets.AiWindow.Tab.Math : Sheets.AiWindow.Tab.Ask, autoRun: mode == "math");
                    break;
                }
            }
        }
        catch (Exception error)
        {
            Services.Log($"iPad-Nachricht {type}: {error}");
        }
    }

    /// <summary>Handschrift in Text oder Schönschrift umwandeln – an derselben Stelle.</summary>
    private async Task ConvertAsync(PadLink link, NoteEditor editor, JsonObject message)
    {
        var requestId = message.String("requestId") ?? "";
        var script = message.String("mode") == "script";
        var area = new Rect(message.Number("x"), message.Number("y"), Math.Max(1, message.Number("w")), Math.Max(1, message.Number("h")));
        var reply = PadProtocol.Message(PadProtocol.Converted);
        reply["requestId"] = requestId;
        var cancel = editor.ShowBusy("Lese die Handschrift vom iPad …");
        try
        {
            string text;
            var client = Services.Gemini();
            var image = message.String("image");
            if (client is not null && !string.IsNullOrEmpty(image))
            {
                text = await AiTasks.TranscribeAsync(client, new GeminiImage(Convert.FromBase64String(image), "image/png"), cancel);
            }
            else
            {
                var ids = PadProtocol.StringsFromJson(message["strokes"]).ToHashSet();
                var strokes = editor.Page.Ink.Strokes.Where(s => s.Id is not null && ids.Contains(s.Id)).ToList();
                text = await Recognizer.RecognizeAsync(strokes);
                if (text.Length == 0)
                {
                    throw new InvalidOperationException(client is null
                        ? "Ohne Gemini-Schlüssel klappt das Lesen nur mit der Windows-Handschrifterkennung – und die hat hier nichts erkannt."
                        : "Hier konnte ich keinen Text erkennen.");
                }
            }
            text = text.Trim();
            if (text.Length == 0) throw new InvalidOperationException("Hier konnte ich keinen Text erkennen. Probier „Glätten“ für Zeichnungen.");

            var lines = Math.Max(1, text.Split('\n').Length);
            var size = Math.Clamp(area.Height / lines * 0.62, 14, 44);
            var model = new NoteTextBox
            {
                X = Math.Max(0, area.X),
                Width = Math.Max(160, area.Width + size),
                Text = text,
                Seed = new TextSeed
                {
                    Text = text,
                    Script = script,
                    FontSize = script ? Math.Round(size) : null,
                    Color = script ? message.String("color") : null
                }
            };
            model.Y = script
                ? Math.Max(0, area.Y)
                : TextDocs.SnapTop(area.Y + Math.Min(area.Height, 32) - 8, editor.Page.Environment);
            if (script) model.LineHeight = Math.Round(size * 1.35, 1);
            editor.Page.PushUndo();
            editor.Page.AddTextItems(new[] { model });
            reply["ok"] = true;
            reply["text"] = text;
        }
        catch (Exception error)
        {
            reply["ok"] = false;
            reply["message"] = error is OperationCanceledException ? "Abgebrochen." : GeminiClient.Describe(error);
            if (error is not OperationCanceledException) editor.Toast("Umwandeln hat nicht geklappt: " + GeminiClient.Describe(error));
        }
        finally
        {
            editor.HideBusy();
        }
        await link.SendAsync(reply);
    }

    // MARK: - Umzug der alten Notizen vom iPad

    private void BeginImport()
    {
        _importFolder = Path.Combine(Path.GetTempPath(), "LernheftStudio-vom-iPad-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_importFolder);
    }

    private void ImportFile(JsonObject message)
    {
        if (_importFolder is null) BeginImport();
        var id = message.String("id") ?? "";
        var target = LegacyImport.TargetPath(_importFolder!, id);
        var data = message.String("data");
        if (target is null || data is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, Convert.FromBase64String(data));
    }

    private async Task FinishImportAsync()
    {
        var reply = PadProtocol.Message(PadProtocol.ImportDone);
        var folder = _importFolder;
        _importFolder = null;
        try
        {
            if (folder is null) throw new InvalidOperationException("Es kam nichts an.");
            // Auf dem UI-Thread: die Bibliothek darf nicht gleichzeitig woanders verändert werden.
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
            var report = LegacyImport.ImportFolder(folder, Services.Store);
            reply["ok"] = true;
            reply["notes"] = report.Notes;
            reply["message"] = report.Summary;
            LibraryChanged?.Invoke();
            ShowImportResult(report.Summary);
        }
        catch (Exception error)
        {
            reply["ok"] = false;
            reply["message"] = error.Message;
        }
        finally
        {
            try { if (folder is not null) Directory.Delete(folder, true); }
            catch (IOException) { }
        }
        if (_link is not null) await _link.SendAsync(reply);
        SendCurrentNote();
    }

    private static void ShowImportResult(string summary)
    {
        if (Application.Current.MainWindow is MainWindow main) main.ShowBanner("Notizen vom iPad übernommen: " + summary);
    }

    public void LibraryUpdated()
    {
        if (_editor is null) SendCurrentNote();
    }
}
