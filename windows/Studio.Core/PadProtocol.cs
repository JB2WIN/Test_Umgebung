using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lernheft.Studio;

/// <summary>
/// Die Sprache zwischen Lernheft Studio (Surface) und Lernheft Pad (iPad).
///
/// Jede Nachricht ist ein JSON-Objekt mit dem Feld „t" für die Art. Die Verbindung ist ein
/// WebSocket im eigenen WLAN. Dieselben Namen stehen in der iPad-App (PadProtocol.swift).
/// </summary>
public static class PadProtocol
{
    public const int Version = 1;

    /// <summary>Hier lauscht das Surface.</summary>
    public const int StudioPort = 47650;

    /// <summary>Hier lauscht das iPad – für den Fall, dass die Windows-Firewall eingehende Verbindungen sperrt.</summary>
    public const int PadPort = 47660;

    public const string Scheme = "lernheftpad";

    // Anmeldung
    public const string Hello = "hello";        // Pad → Studio
    public const string Welcome = "welcome";    // Studio → Pad
    public const string Denied = "denied";      // Studio → Pad
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Bye = "bye";

    // Die offene Notiz (Studio → Pad)
    public const string Note = "note";          // Kopfdaten oder „keine Notiz offen"
    public const string Ink = "ink";            // alle Striche
    public const string Images = "images";      // eingefügte Bilder
    public const string Text = "text";          // Textebene einer Seite als Bild
    public const string View = "view";          // Ausschnitt, den das Surface gerade zeigt

    // Zeichnen
    public const string Live = "live";          // Pad → Studio: Strich, während er entsteht
    public const string Ops = "ops";            // beide Richtungen: Striche hinzu / weg

    // Wünsche vom iPad
    public const string Pages = "pages";        // mehr Seiten
    public const string EraseImage = "eraseImage";
    public const string AddText = "addText";
    public const string DeleteIn = "deleteIn";
    public const string Convert = "convert";    // Handschrift → Text/Schönschrift
    public const string Converted = "converted";
    public const string Ai = "ai";              // Ausschnitt an den KI-Helfer
    public const string Open = "open";          // Notiz am Surface öffnen
    public const string NewNote = "newNote";
    public const string Theme = "theme";
    public const string Toast = "toast";        // kurze Meldung fürs iPad

    // Umzug der alten Notizen vom iPad
    public const string ImportBegin = "importBegin";
    public const string ImportFile = "importFile";
    public const string ImportEnd = "importEnd";
    public const string ImportDone = "importDone";

    public static JsonObject Message(string type) => new() { ["t"] = type };

    public static string TypeOf(JsonObject message) => message["t"]?.GetValue<string>() ?? "";

    public static string? String(this JsonObject message, string key) =>
        message[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static double Number(this JsonObject message, string key, double fallback = 0)
    {
        if (message[key] is not JsonValue value) return fallback;
        if (value.TryGetValue<double>(out var number)) return number;
        if (value.TryGetValue<int>(out var integer)) return integer;
        if (value.TryGetValue<long>(out var big)) return big;
        return fallback;
    }

    public static bool Flag(this JsonObject message, string key) =>
        message[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    public static Guid? Guid(this JsonObject message, string key) =>
        System.Guid.TryParse(message.String(key), out var id) ? id : null;

    public static JsonArray StrokesToJson(IEnumerable<InkStroke> strokes)
    {
        var array = new JsonArray();
        foreach (var stroke in strokes) array.Add(JsonSerializer.SerializeToNode(stroke, InkDocument.Options));
        return array;
    }

    public static List<InkStroke> StrokesFromJson(JsonNode? node)
    {
        if (node is not JsonArray array) return new List<InkStroke>();
        var result = new List<InkStroke>();
        foreach (var item in array)
        {
            if (item is null) continue;
            var stroke = item.Deserialize<InkStroke>(InkDocument.Options);
            if (stroke is not null && stroke.Count > 0) result.Add(stroke);
        }
        return result;
    }

    public static List<double> NumbersFromJson(JsonNode? node)
    {
        var result = new List<double>();
        if (node is not JsonArray array) return result;
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<double>(out var number)) result.Add(number);
        }
        return result;
    }

    public static List<string> StringsFromJson(JsonNode? node)
    {
        var result = new List<string>();
        if (node is not JsonArray array) return result;
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text)) result.Add(text);
        }
        return result;
    }
}

/// <summary>Ein gekoppeltes iPad – es darf sich später ohne neuen Code wieder melden.</summary>
public class PairedDevice
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "iPad";
    public string PairKey { get; set; } = "";
    public double LastSeen { get; set; } = AppleTime.Now;
    public string LastAddress { get; set; } = "";
}

/// <summary>
/// Ein Kopplungsfenster: ein sechsstelliger Code zum Abtippen und ein langes Geheimnis im QR-Code.
/// Beides gilt nur, solange das Fenster offen ist.
/// </summary>
public class PairingSession
{
    public string Code { get; }
    public string Secret { get; }
    public DateTime Expires { get; }
    private int _failures;

    public PairingSession(TimeSpan lifetime)
    {
        Code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000", CultureInfo.InvariantCulture);
        Secret = RandomToken(18);
        Expires = DateTime.UtcNow + lifetime;
    }

    public bool IsValid => DateTime.UtcNow < Expires && _failures < 8;

    public bool Accepts(string? code, string? secret)
    {
        if (!IsValid) return false;
        var ok = (!string.IsNullOrEmpty(secret) && CryptographicOperations.FixedTimeEquals(
                     System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(Secret)))
                 || (!string.IsNullOrEmpty(code) && Normalize(code) == Code);
        if (!ok) _failures++;
        return ok;
    }

    public static string Normalize(string code) => new(code.Where(char.IsDigit).ToArray());

    public string FormattedCode => Code[..3] + " " + Code[3..];

    /// <summary>Inhalt des QR-Codes: alles, was das iPad zum Verbinden braucht.</summary>
    public string QrPayload(string serverId, string serverName, IEnumerable<string> addresses, int port) =>
        $"{PadProtocol.Scheme}://pair?v={PadProtocol.Version}" +
        $"&id={Uri.EscapeDataString(serverId)}" +
        $"&n={Uri.EscapeDataString(serverName)}" +
        $"&h={Uri.EscapeDataString(string.Join(",", addresses))}" +
        $"&p={port}" +
        $"&s={Uri.EscapeDataString(Secret)}";

    public static string RandomToken(int bytes)
    {
        var data = RandomNumberGenerator.GetBytes(bytes);
        return System.Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
