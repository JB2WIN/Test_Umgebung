using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using Lernheft.Studio.App.Editor;
using Lernheft.Studio.App.Sheets;

namespace Lernheft.Studio.App;

/// <summary>
/// Dateien in eine Notiz holen: PDF-Seiten und Bilder als Bilder, SVG-Exporte (z. B. aus Nebo) als
/// echte Striche, Textdateien als Textfeld.
/// </summary>
public static class Importer
{
    /// <summary>
    /// Platzierung ohne Rückfrage – für Dateien vom iPad. <paramref name="Top"/> ist die Stelle
    /// (in Seiteneinheiten), die das iPad gerade zeigt; ohne Angabe gilt der Ausschnitt am Surface.
    /// </summary>
    public sealed record Preset(bool NewPages, double WidthFraction, double? Top);

    /// <summary>Fügt eine Datei ein. Mit <paramref name="preset"/> ohne Dialoge; dann kommt ein Fehler als Text zurück.</summary>
    public static async Task<string?> ImportAsync(NoteEditor editor, string path, Preset? preset = null)
    {
        if (editor.Note is not { } note) return "Keine Notiz offen.";
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var owner = Window.GetWindow(editor);
        try
        {
            switch (extension)
            {
                case ".pdf":
                    await ImportPdfAsync(editor, note, path, owner, preset);
                    break;
                case ".svg":
                    ImportSvg(editor, note, path);
                    break;
                case ".txt":
                case ".md":
                    InsertText(editor, await File.ReadAllTextAsync(path), preset);
                    break;
                case ".rtf":
                    InsertText(editor, RtfToText(path), preset);
                    break;
                default:
                    ImportImage(editor, note, path, owner, preset);
                    break;
            }
            return null;
        }
        catch (Exception error)
        {
            editor.HideBusy();
            if (preset is not null) return $"„{Path.GetFileName(path)}“ ließ sich nicht einfügen: {error.Message}";
            Dialogs.Info(owner, "Das hat nicht geklappt", $"„{Path.GetFileName(path)}“ ließ sich nicht einfügen.\n\n{error.Message}");
            return error.Message;
        }
    }

    private static void InsertText(NoteEditor editor, string text, Preset? preset)
    {
        if (preset is { NewPages: false, Top: double top }) editor.InsertText(text, new Point(80, top + 24));
        else editor.InsertText(text);
    }

    private static void ImportImage(NoteEditor editor, NoteMeta note, string path, Window? owner, Preset? preset)
    {
        var source = ImageTools.Load(path) ?? throw new InvalidDataException("Diese Bilddatei kann Windows nicht lesen.");
        var shrunk = ImageTools.Shrink(source, 2600);
        var png = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase);
        var file = Services.Store.SaveImage(note.Id, png ? ImageTools.Png(shrunk) : ImageTools.Jpeg(shrunk, 88), png ? "png" : "jpg");
        if (preset is not null)
        {
            editor.PlaceImported(new List<(string, int, int)> { (file, shrunk.PixelWidth, shrunk.PixelHeight) }, preset.NewPages,
                preset.WidthFraction, preset.Top, select: false);
            return;
        }
        var placement = new ImportPlacementWindow(1, ImageTools.Load(path, 500), Path.GetFileName(path)) { Owner = owner };
        if (placement.ShowDialog() != true)
        {
            TryDelete(Services.Store.ImagePath(note.Id, file));
            return;
        }
        editor.PlaceImported(new List<(string, int, int)> { (file, shrunk.PixelWidth, shrunk.PixelHeight) }, placement.NewPages, placement.WidthFraction);
    }

    private static async Task ImportPdfAsync(NoteEditor editor, NoteMeta note, string path, Window? owner, Preset? preset)
    {
        editor.ShowBusy($"Lese {Path.GetFileName(path)} …", cancellable: false);
        var pages = new List<(string File, int Width, int Height)>();
        BitmapSource? preview = null;
        string text;
        try
        {
            var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var document = await global::Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            var count = (int)Math.Min(document.PageCount, 40);
            for (uint index = 0; index < count; index++)
            {
                using var page = document.GetPage(index);
                var size = page.Size;
                var scale = Math.Min(2.5, 1800 / Math.Max(size.Width, size.Height));
                using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, new global::Windows.Data.Pdf.PdfPageRenderOptions
                {
                    DestinationWidth = (uint)(size.Width * scale),
                    DestinationHeight = (uint)(size.Height * scale)
                });
                using var managed = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(managed);
                var bitmap = ImageTools.FromBytes(managed.ToArray()) ?? throw new InvalidDataException("Seite nicht lesbar");
                preview ??= bitmap;
                var name = Services.Store.SaveImage(note.Id, ImageTools.Jpeg(bitmap, 86), "jpg", "pdf");
                pages.Add((name, bitmap.PixelWidth, bitmap.PixelHeight));
            }
            text = Services.Settings.GetBool(Keys.ImportTextFromPdf, false) ? PdfText(path) : "";
        }
        finally
        {
            editor.HideBusy();
        }
        if (pages.Count == 0) throw new InvalidDataException("Das PDF hat keine Seiten.");
        if (preset is not null)
        {
            editor.PlaceImported(pages, preset.NewPages, preset.WidthFraction, preset.Top, select: false);
            if (text.Length > 0) editor.InsertText(text);
            return;
        }
        var placement = new ImportPlacementWindow(pages.Count, preview, Path.GetFileName(path)) { Owner = owner };
        if (placement.ShowDialog() != true)
        {
            foreach (var page in pages) TryDelete(Services.Store.ImagePath(note.Id, page.File));
            return;
        }
        editor.PlaceImported(pages, placement.NewPages, placement.WidthFraction);
        if (text.Length > 0) editor.InsertText(text);
    }

    private static string PdfText(string path)
    {
        try
        {
            using var document = UglyToad.PdfPig.PdfDocument.Open(path);
            var builder = new StringBuilder();
            foreach (var page in document.GetPages().Take(40))
            {
                var text = PageText(page);
                if (text.Length == 0) continue;
                if (builder.Length > 0) builder.Append("\n\n");
                builder.Append(text);
            }
            return builder.ToString();
        }
        catch (Exception error)
        {
            Services.Log("PDF-Text: " + error.Message);
            return "";
        }
    }

    /// <summary>Wörter zeilenweise von oben nach unten, links nach rechts.</summary>
    private static string PageText(UglyToad.PdfPig.Content.Page page)
    {
        var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (words.Count == 0) return "";
        var lines = new List<List<UglyToad.PdfPig.Content.Word>>();
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom))
        {
            var height = Math.Max(2, word.BoundingBox.Height);
            var line = lines.FirstOrDefault(l => Math.Abs(l[0].BoundingBox.Bottom - word.BoundingBox.Bottom) < height * 0.5);
            if (line is null) lines.Add(new List<UglyToad.PdfPig.Content.Word> { word });
            else line.Add(word);
        }
        var builder = new StringBuilder();
        double? previousBottom = null;
        foreach (var line in lines)
        {
            var bottom = line[0].BoundingBox.Bottom;
            var height = Math.Max(2, line.Max(w => w.BoundingBox.Height));
            if (previousBottom is double last) builder.Append(last - bottom > height * 2.2 ? "\n\n" : "\n");
            builder.Append(string.Join(" ", line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
            previousBottom = bottom;
        }
        return builder.ToString().Trim();
    }

    private static void ImportSvg(NoteEditor editor, NoteMeta note, string path)
    {
        var outcome = SvgInk.Load(path, note.Width);
        // Hinter den bisherigen Inhalt, auf eine neue Seite.
        var bottom = editor.Page.ContentBottom;
        var startPage = bottom <= 1 ? 0 : (int)Math.Ceiling(bottom / Paper.PageHeight);
        var shift = startPage * Paper.PageHeight;
        var moved = outcome.Strokes.Select(stroke => stroke.Moved(0, shift)).ToList();
        var needed = startPage + Math.Max(1, (int)Math.Ceiling(outcome.Height / Paper.PageHeight));
        if (needed > note.PageCount)
        {
            Services.Store.UpdateNote(note.Id, n => n.PageCount = needed, touch: false);
            editor.Page.RefreshPaper();
            Services.Bridge.NoteMetaChanged(editor);
        }
        editor.Page.AddInkFromSurface(moved);
        editor.MarkInkChangedFromPad();
        editor.Toast(outcome.FilledShapesOnly
            ? "Das SVG enthält die Schrift als Flächen. Die Striche sind da, sehen aber etwas anders aus als im Original."
            : $"{moved.Count} Striche übernommen.");
    }

    private static string RtfToText(string path)
    {
        var document = new FlowDocument();
        var range = new TextRange(document.ContentStart, document.ContentEnd);
        using var stream = File.OpenRead(path);
        range.Load(stream, DataFormats.Rtf);
        return range.Text.Replace("\r\n", "\n").Trim();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
    }
}
