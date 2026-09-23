using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lernheft.Studio.App.Editor;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Lernheft.Studio.App;

/// <summary>Notizen als PDF sichern oder drucken – Seite für Seite, so wie sie aussehen.</summary>
public static class PdfExport
{
    public static string SafeName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(title.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return clean.Length == 0 ? "Notiz" : clean;
    }

    /// <summary>Die Seiten, die tatsächlich etwas enthalten – leere Seiten am Ende fallen weg.</summary>
    private static int PagesToPrint(NoteMeta note, NoteContent content, InkDocument ink)
    {
        var used = PageRenderer.UsedPages(note, content, ink);
        var last = used.Count == 0 ? 0 : used.Max();
        return Math.Max(1, Math.Min(Math.Max(1, note.PageCount), last + 1));
    }

    public static void Write(string path, IEnumerable<NoteMeta> notes)
    {
        using var document = new PdfDocument();
        document.Info.Title = notes.Count() == 1 ? notes.First().Title : "Lernheft";
        document.Info.Creator = "Lernheft Studio";
        foreach (var note in notes)
        {
            var content = Services.Store.LoadContent(note.Id);
            var ink = Services.Store.LoadInk(note.Id);
            if (content.TypedText is not null || content.Blocks is not null) LegacyImport.ConvertLegacyText(content, ink, note.Width);
            var environment = TextEnvironment.FromSettings(note.PaperStyle);
            for (var index = 0; index < PagesToPrint(note, content, ink); index++)
            {
                var bitmap = PageRenderer.Page(note, content, ink, environment, index, 2.2);
                var page = document.AddPage();
                page.Width = XUnit.FromPoint(595);
                page.Height = XUnit.FromPoint(595 * Paper.PageHeight / note.Width);
                using var graphics = XGraphics.FromPdfPage(page);
                using var stream = new MemoryStream(ImageTools.Jpeg(bitmap, 90));
                using var image = XImage.FromStream(stream);
                graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
            }
        }
        document.Save(path);
    }

    public static void Print(Window? owner, NoteMeta note)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        var content = Services.Store.LoadContent(note.Id);
        var ink = Services.Store.LoadInk(note.Id);
        var environment = TextEnvironment.FromSettings(note.PaperStyle);
        var pageWidth = dialog.PrintableAreaWidth;
        var pageHeight = dialog.PrintableAreaHeight;
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(pageWidth, pageHeight);
        for (var index = 0; index < PagesToPrint(note, content, ink); index++)
        {
            var bitmap = PageRenderer.Page(note, content, ink, environment, index, 2.4);
            var page = new FixedPage { Width = pageWidth, Height = pageHeight, Background = Brushes.White };
            var scale = Math.Min(pageWidth / note.Width, pageHeight / Paper.PageHeight);
            var image = new Image
            {
                Source = bitmap,
                Width = note.Width * scale,
                Height = Paper.PageHeight * scale,
                Stretch = Stretch.Fill
            };
            FixedPage.SetLeft(image, (pageWidth - image.Width) / 2);
            FixedPage.SetTop(image, (pageHeight - image.Height) / 2);
            page.Children.Add(image);
            var container = new PageContent();
            ((System.Windows.Markup.IAddChild)container).AddChild(page);
            document.Pages.Add(container);
        }
        dialog.PrintDocument(document.DocumentPaginator, note.Title);
    }
}
