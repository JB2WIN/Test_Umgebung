namespace Lernheft.Studio;

/// <summary>
/// iCloud Drive auf dem Surface. „iCloud für Windows“ legt die Dateien in einen normalen Ordner
/// (meist C:\Users\Name\iCloudDrive); Dateien, die nur in der Cloud liegen, lädt Windows beim
/// ersten Öffnen nach. Hier wird nur aufgelistet – ohne dabei etwas herunterzuladen.
/// </summary>
public static class CloudDrive
{
    /// <summary>Was sich in eine Notiz einfügen lässt.</summary>
    public static readonly string[] Extensions =
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".heic", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".svg", ".txt", ".md", ".rtf"
    };

    public enum Kind { Folder, Pdf, Image, Drawing, Text }

    public sealed record Entry(string Path, string Name, Kind Kind, DateTime Modified, long Size, bool OnlyInCloud);

    // Windows-Dateiattribute für Platzhalter von Cloud-Anbietern.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>Wo iCloud Drive liegt: eigene Wahl aus den Einstellungen, sonst die üblichen Orte.</summary>
    public static string? FindFolder(string? chosen = null, string? profile = null)
    {
        if (!string.IsNullOrWhiteSpace(chosen) && Directory.Exists(chosen)) return chosen;
        profile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile)) return null;
        foreach (var name in new[] { "iCloudDrive", "iCloud Drive", Path.Combine("iCloudDrive", "iCloud Drive") })
        {
            var candidate = Path.Combine(profile, name);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static bool IsSupported(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static Kind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => Kind.Pdf,
        ".svg" => Kind.Drawing,
        ".txt" or ".md" or ".rtf" => Kind.Text,
        _ => Kind.Image
    };

    /// <summary>Inhalt eines Ordners: erst Unterordner, dann einfügbare Dateien, jeweils nach Name.</summary>
    public static List<Entry> List(string folder)
    {
        var result = new List<Entry>();
        foreach (var directory in Safe(() => new DirectoryInfo(folder).EnumerateDirectories()))
        {
            if (Hidden(directory)) continue;
            result.Add(new Entry(directory.FullName, directory.Name, Kind.Folder, directory.LastWriteTime, 0, false));
        }
        foreach (var file in Safe(() => new DirectoryInfo(folder).EnumerateFiles()))
        {
            if (Hidden(file) || !IsSupported(file.Name)) continue;
            result.Add(ToEntry(file));
        }
        return result
            .OrderBy(e => e.Kind == Kind.Folder ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Die zuletzt geänderten einfügbaren Dateien – quer durch alle Ordner.</summary>
    public static List<Entry> Recent(string root, int count = 40, int maxFiles = 20_000)
    {
        var found = new List<Entry>();
        var pending = new Stack<(DirectoryInfo Folder, int Depth)>();
        pending.Push((new DirectoryInfo(root), 0));
        var seen = 0;
        while (pending.Count > 0 && seen < maxFiles)
        {
            var (folder, depth) = pending.Pop();
            foreach (var file in Safe(() => folder.EnumerateFiles()))
            {
                seen++;
                if (Hidden(file) || !IsSupported(file.Name)) continue;
                found.Add(ToEntry(file));
            }
            if (depth >= 8) continue;
            foreach (var directory in Safe(() => folder.EnumerateDirectories()))
            {
                if (!Hidden(directory)) pending.Push((directory, depth + 1));
            }
        }
        return found.OrderByDescending(e => e.Modified).Take(count).ToList();
    }

    /// <summary>Sucht Dateien, deren Name alle Suchwörter enthält.</summary>
    public static List<Entry> Search(string root, string query, int count = 60)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return new List<Entry>();
        return Recent(root, int.MaxValue)
            .Where(e => words.All(w => e.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .Take(count)
            .ToList();
    }

    /// <summary>Holt die Datei (falls nötig aus der Cloud) als lokale Kopie – im Hintergrund aufrufen.</summary>
    public static async Task<string> MakeLocalCopyAsync(string path, CancellationToken cancel = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "LernheftStudio-iCloud", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(path));
        await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true))
        await using (var copy = File.Create(target))
        {
            await source.CopyToAsync(copy, cancel);
        }
        return target;
    }

    public static string Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ? "" : relative;
    }

    private static Entry ToEntry(FileInfo file) => new(
        file.FullName, file.Name, KindOf(file.Name), file.LastWriteTime, file.Length,
        (file.Attributes & (RecallOnOpen | RecallOnDataAccess | FileAttributes.Offline)) != 0);

    private static bool Hidden(FileSystemInfo info) =>
        info.Name.StartsWith('.') || info.Name.StartsWith('~') ||
        (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    private static IEnumerable<T> Safe<T>(Func<IEnumerable<T>> list)
    {
        try
        {
            return list().ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Array.Empty<T>();
        }
    }
}
