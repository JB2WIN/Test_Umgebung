using System.Globalization;
using System.Text.Json;

namespace Lernheft.Studio;

/// <summary>
/// Die Einstellungen der App als kleine Datei (settings.json) im Datenordner.
/// Schlüssel und Passwörter gehen über <see cref="Protect"/>/<see cref="Unprotect"/> –
/// unter Windows ist das DPAPI, damit sie nicht im Klartext auf der Platte liegen.
/// </summary>
public class Settings
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, string> _values;

    public Func<string, string> Protect { get; set; } = value => value;
    public Func<string, string> Unprotect { get; set; } = value => value;

    public event Action<string>? Changed;

    public Settings(string path)
    {
        _path = path;
        _values = Load(path);
    }

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>();
        }
    }

    public IReadOnlyDictionary<string, string> All
    {
        get { lock (_lock) return new Dictionary<string, string>(_values); }
    }

    public bool Has(string key)
    {
        lock (_lock) return _values.ContainsKey(key);
    }

    public string Get(string key, string fallback = "")
    {
        lock (_lock) return _values.TryGetValue(key, out var value) ? value : fallback;
    }

    public void Set(string key, string value)
    {
        lock (_lock)
        {
            if (_values.TryGetValue(key, out var old) && old == value) return;
            _values[key] = value;
            Flush();
        }
        Changed?.Invoke(key);
    }

    public void Remove(string key)
    {
        lock (_lock)
        {
            if (!_values.Remove(key)) return;
            Flush();
        }
        Changed?.Invoke(key);
    }

    public bool GetBool(string key, bool fallback) =>
        Get(key, fallback ? "true" : "false") == "true";

    public void SetBool(string key, bool value) => Set(key, value ? "true" : "false");

    public double GetDouble(string key, double fallback) =>
        double.TryParse(Get(key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public void SetDouble(string key, double value) => Set(key, value.ToString("R", CultureInfo.InvariantCulture));

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public void SetInt(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    public string GetSecret(string key)
    {
        var stored = Get(key, "");
        if (stored.Length == 0) return "";
        try
        {
            return Unprotect(stored);
        }
        catch (Exception)
        {
            return "";
        }
    }

    public void SetSecret(string key, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) Remove(key);
        else Set(key, Protect(trimmed));
    }

    private void Flush()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
    }
}

/// <summary>Namen der Einstellungen an einer Stelle, damit sich nichts vertippt.</summary>
public static class Keys
{
    public const string GeminiKey = "geminiKey";
    public const string GeminiBackupKey = "geminiBackupKey";
    public const string GeminiModel = "geminiModel";
    public const string KeyLabel = "keyLabel";
    public const string KeyLabelBackup = "keyLabelBackup";
    public const string FallbackOnOverload = "fallbackOnOverload";

    public const string Appearance = "appearance";
    public const string HideListsWhileWriting = "hideSidebarWhileWriting";
    public const string SeamlessImport = "seamlessImport";
    public const string ImportTextFromPdf = "importTextFromPDF";
    public const string ZoomLocked = "zoomLocked";

    public const string TextFont = "textFont";
    public const string TextSize = "textSize";
    public const string ScriptFont = "handFont";
    public const string SpellCheck = "spellCheck";
    public const string SnapToLines = "snapToLines";

    public const string AutoHomework = "autoHomework";
    public const string BackupIncludesKey = "backupIncludesKey";

    public const string BudgetEur = "budgetEur";
    public const string PriceInput = "priceInputPerMillion";
    public const string PriceOutput = "priceOutputPerMillion";
    public const string UsdToEur = "usdToEur";

    public const string TimetableLink = "timetableLink";
    public const string UntisHost = "untisHost";
    public const string UntisType = "untisType";
    public const string UntisId = "untisId";
    public const string UntisLastSync = "untisLastSync";
    public const string UntisCookies = "untisCookies";

    public const string ServerId = "serverId";
    public const string ServerName = "serverName";
    public const string PairedDevices = "pairedDevices";
    public const string PadPort = "padPort";

    public const string LegacyImportAsked = "legacyImportAsked";
    public const string ListWidth = "listWidth";
    public const string ICloudFolder = "iCloudFolder";
}
