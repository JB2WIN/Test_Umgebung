using System.Text.Json.Nodes;
using Lernheft.Studio;

// Test-Surface für die CI: öffnet ein Kopplungsfenster, schreibt den QR-Inhalt in payload.txt,
// schickt dem iPad eine Notiz mit einem Strich und wartet, bis vom iPad ein neuer Strich kommt.
var folder = Path.GetFullPath(args.Length > 0 ? args[0] : "probe");
Directory.CreateDirectory(folder);
var log = Path.Combine(folder, "messages.log");
void Log(string text)
{
    lock (folder) File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
}

var settings = new Settings(Path.Combine(folder, "settings.json"));
var server = new PadServer(settings, "CI-Surface");
var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
var noteId = Guid.NewGuid();
var received = new List<string>();
var strokeOk = "";
var importFolder = Path.Combine(folder, "import");
var importFiles = 0;
var importResult = "";
var insertResult = "";

void Check()
{
    if (strokeOk.Length > 0 && importResult.Length > 0 && insertResult.Length > 0)
        done.TrySetResult(strokeOk + " | " + importResult + " | " + insertResult);
}

server.Connected += link =>
{
    Log($"verbunden: {link.DeviceName} ({link.Address}, Rückweg: {link.Reverse})");
    link.Received += message =>
    {
        var type = PadProtocol.TypeOf(message);
        lock (received) received.Add(type);
        Log("empfangen: " + type + " " + Shorten(message.ToJsonString()));
        if (type == PadProtocol.Ops && PadProtocol.StrokesFromJson(message["add"]).Count > 0 && strokeOk.Length == 0)
        {
            var toast = PadProtocol.Message(PadProtocol.Toast);
            toast["text"] = "Verbindung geprüft – dein Strich ist am Surface angekommen.";
            link.Send(toast);
            var stroke = PadProtocol.StrokesFromJson(message["add"])[0];
            strokeOk = $"OK Strich {stroke.Id} mit {stroke.Count} Punkten, Breite {stroke.Width}, Art {stroke.Kind}";
            Check();
        }
        else if (type == PadProtocol.ImportBegin)
        {
            Directory.CreateDirectory(importFolder);
        }
        else if (type == PadProtocol.ImportFile)
        {
            var target = LegacyImport.TargetPath(importFolder, message.String("id") ?? "");
            var data = message.String("data");
            if (target is not null && data is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, Convert.FromBase64String(data));
                importFiles++;
            }
        }
        else if (type == PadProtocol.InsertFile)
        {
            var files = message["files"] as JsonArray ?? new JsonArray();
            var described = files.OfType<JsonObject>()
                .Select(f => $"{f.String("name")} ({Convert.FromBase64String(f.String("data") ?? "").Length} Byte)").ToList();
            var reply = PadProtocol.Message(PadProtocol.Inserted);
            reply["requestId"] = message.String("requestId");
            reply["ok"] = described.Count > 0;
            reply["message"] = described.Count > 0 ? "Eingefügt." : "Keine Datei.";
            link.Send(reply);
            insertResult = described.Count > 0
                ? $"Einfügen: {string.Join(", ", described)}, Platz {message.String("placement")}"
                : "Einfügen ohne Datei";
            if (described.Count == 0) done.TrySetResult("FEHLER " + insertResult);
            Check();
        }
        else if (type == PadProtocol.ImportEnd)
        {
            var store = new LibraryStore(Path.Combine(folder, "studio"));
            var report = LegacyImport.ImportFolder(importFolder, store);
            var strokes = store.Library.Notes.Sum(n => store.LoadInk(n.Id).Strokes.Count);
            var reply = PadProtocol.Message(PadProtocol.ImportDone);
            reply["ok"] = report.Notes > 0;
            reply["notes"] = report.Notes;
            reply["message"] = report.Summary;
            link.Send(reply);
            importResult = report.Notes > 0 && strokes > 0
                ? $"Umzug: {importFiles} Dateien, {report.Notes} Notizen, {strokes} Striche – {report.Summary}"
                : $"Umzug unvollständig: {importFiles} Dateien, {report.Notes} Notizen, {strokes} Striche";
            if (!(report.Notes > 0 && strokes > 0)) done.TrySetResult("FEHLER " + importResult);
            Check();
        }
    };

    var note = PadProtocol.Message(PadProtocol.Note);
    note["id"] = noteId.ToString();
    note["title"] = "Verbindungstest";
    note["subject"] = "CI";
    note["color"] = "#2F5BEA";
    note["paper"] = "lined";
    note["pageCount"] = 1;
    note["pageWidth"] = 800;
    note["pageHeight"] = Paper.PageHeight;
    link.Send(note);

    var fromSurface = new InkStroke { Id = "surface1", Color = "#12804A", Width = 3, Kind = "pen" };
    for (var i = 0; i <= 40; i++) fromSurface.Add(100 + i * 8, 200 + Math.Sin(i / 4.0) * 20, 0.6);
    var ink = PadProtocol.Message(PadProtocol.Ink);
    ink["noteId"] = noteId.ToString();
    ink["strokes"] = PadProtocol.StrokesToJson(new[] { fromSurface });
    link.Send(ink);
};
server.Disconnected += link => Log("getrennt: " + link.DeviceName);

server.Start(PadProtocol.StudioPort);
Log($"lauscht: {server.Listening} Port {server.Port} {server.ListenProblem}");
var session = server.StartPairing(TimeSpan.FromMinutes(20));
var addresses = PadServer.LocalAddresses().Select(a => a.ToString()).Append("127.0.0.1").Distinct();
var payload = session.QrPayload(server.ServerId, server.ServerName, addresses, server.Port);
File.WriteAllText(Path.Combine(folder, "payload.txt"), payload);
Log("QR: " + payload);

var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromMinutes(8)));
string result;
lock (received) result = finished == done.Task ? done.Task.Result : "FEHLT – empfangen: " + string.Join(", ", received);
Log(result);
File.WriteAllText(Path.Combine(folder, "result.txt"), result);
// Noch kurz verbunden bleiben, damit das Bildschirmfoto die Bestätigung zeigt.
await Task.Delay(8000);
server.Dispose();
return finished == done.Task ? 0 : 1;

static string Shorten(string text) => text.Length > 300 ? text[..300] + " …" : text;
