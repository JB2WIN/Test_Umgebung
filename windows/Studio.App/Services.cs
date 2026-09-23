using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Lernheft.Studio.App;

/// <summary>Was die ganze App gemeinsam nutzt: Daten, Einstellungen, KI-Zähler, iPad-Verbindung.</summary>
public static class Services
{
    public static string DataRoot { get; private set; } = LibraryStore.DefaultRoot;
    public static LibraryStore Store { get; private set; } = null!;
    public static Settings Settings { get; private set; } = null!;
    public static UsageTracker Usage { get; private set; } = null!;
    public static PadServer Pad { get; private set; } = null!;
    public static PadBridge Bridge { get; private set; } = null!;

    public static string LogPath => Path.Combine(DataRoot, "logs", "lernheft-studio.log");

    public static void Init(string? root = null, bool seed = true)
    {
        DataRoot = root ?? LibraryStore.DefaultRoot;
        Directory.CreateDirectory(DataRoot);
        Settings = new Settings(Path.Combine(DataRoot, "settings.json"))
        {
            Protect = ProtectSecret,
            Unprotect = UnprotectSecret
        };
        Store = new LibraryStore(DataRoot, seed);
        Usage = new UsageTracker(Settings);
        Pad = new PadServer(Settings);
        Bridge = new PadBridge(Pad);
    }

    public static GeminiClient? Gemini() => GeminiClient.FromSettings(Settings, Usage);

    // Schlüssel liegen mit DPAPI verschlüsselt auf der Platte – nur dieses Windows-Konto kann sie lesen.
    private static string ProtectSecret(string value)
    {
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return "dpapi:" + Convert.ToBase64String(data);
    }

    private static string UnprotectSecret(string stored)
    {
        if (!stored.StartsWith("dpapi:")) return stored;
        var data = ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(data);
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > 2_000_000) info.Delete();
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
