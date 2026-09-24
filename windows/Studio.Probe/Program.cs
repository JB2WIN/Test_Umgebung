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

server.Connected += link =>
{
    Log($"verbunden: {link.DeviceName} ({link.Address}, Rückweg: {link.Reverse})");
    link.Received += message =>
    {
        var type = PadProtocol.TypeOf(message);
        lock (received) received.Add(type);
        Log("empfangen: " + type + " " + Shorten(message.ToJsonString()));
        if (type == PadProtocol.Ops && PadProtocol.StrokesFromJson(message["add"]).Count > 0)
        {
            var toast = PadProtocol.Message(PadProtocol.Toast);
            toast["text"] = "Verbindung geprüft – dein Strich ist am Surface angekommen.";
            link.Send(toast);
            var stroke = PadProtocol.StrokesFromJson(message["add"])[0];
            done.TrySetResult($"OK Strich {stroke.Id} mit {stroke.Count} Punkten, Breite {stroke.Width}, Art {stroke.Kind}");
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
