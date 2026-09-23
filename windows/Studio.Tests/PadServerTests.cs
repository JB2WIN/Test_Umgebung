using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lernheft.Studio.Tests;

/// <summary>
/// Echte Verbindungen über das lokale Netz: ein nachgebautes iPad meldet sich am Surface an –
/// einmal auf dem direkten Weg, einmal über den Rückweg (Surface wählt das iPad an).
/// </summary>
public class PadServerTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task Send(WebSocket socket, JsonObject message) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonObject> Receive(WebSocket socket, string? skip = PadProtocol.Ping)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var buffer = new byte[1 << 16];
            using var collected = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
                collected.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            var message = (JsonObject)JsonNode.Parse(collected.ToArray())!;
            if (skip is not null && PadProtocol.TypeOf(message) == skip) continue;
            return message;
        }
    }

    private static JsonObject Hello(string device, string? code = null, string? secret = null, string? pairKey = null)
    {
        var hello = PadProtocol.Message(PadProtocol.Hello);
        hello["deviceId"] = device;
        hello["deviceName"] = "Test-iPad";
        hello["v"] = 1;
        if (code is not null) hello["code"] = code;
        if (secret is not null) hello["secret"] = secret;
        if (pairKey is not null) hello["pairKey"] = pairKey;
        return hello;
    }

    private static async Task<ClientWebSocket> Connect(int port)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/pad"), CancellationToken.None);
        return socket;
    }

    [Fact]
    public async Task PairsWithCodeThenReconnectsWithKey()
    {
        using var temp = new TempFolder();
        var settings = new Settings(Path.Combine(temp.Path, "settings.json"));
        using var server = new PadServer(settings, "Surface-Test") { ScanTargets = Array.Empty<IPAddress> };
        server.Start(FreePort());
        Assert.True(server.Listening);

        // Ohne Kopplungsfenster: abgelehnt.
        using (var early = await Connect(server.Port))
        {
            await Send(early, Hello("pad-1", code: "000000"));
            var denied = await Receive(early);
            Assert.Equal(PadProtocol.Denied, PadProtocol.TypeOf(denied));
        }

        var session = server.StartPairing();
        var connected = new TaskCompletionSource<PadLink>();
        server.Connected += link => connected.TrySetResult(link);

        using var socket = await Connect(server.Port);
        await Send(socket, Hello("pad-1", code: session.FormattedCode));
        var welcome = await Receive(socket);
        Assert.Equal(PadProtocol.Welcome, PadProtocol.TypeOf(welcome));
        Assert.Equal(server.ServerId, welcome.String("serverId"));
        var key = welcome.String("pairKey");
        Assert.False(string.IsNullOrEmpty(key));
        var link = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("pad-1", link.DeviceId);
        Assert.Null(server.Pairing);

        // Nachrichten laufen in beide Richtungen.
        var received = new TaskCompletionSource<JsonObject>();
        link.Received += message => received.TrySetResult(message);
        var live = PadProtocol.Message(PadProtocol.Live);
        live["sid"] = "s1";
        await Send(socket, live);
        Assert.Equal("s1", (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).String("sid"));

        var note = PadProtocol.Message(PadProtocol.Note);
        note["title"] = "Mathe";
        await link.SendAsync(note);
        Assert.Equal("Mathe", (await Receive(socket)).String("title"));

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        // Später: mit dem Schlüssel wieder da, ohne Code.
        var again = new TaskCompletionSource<PadLink>();
        server.Connected += l => again.TrySetResult(l);
        using var second = await Connect(server.Port);
        await Send(second, Hello("pad-1", pairKey: key));
        Assert.Equal(PadProtocol.Welcome, PadProtocol.TypeOf(await Receive(second)));
        await again.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(server.PairedDevices);

        // Falscher Schlüssel: abgelehnt.
        using var wrong = await Connect(server.Port);
        await Send(wrong, Hello("pad-1", pairKey: "falsch"));
        Assert.Equal(PadProtocol.Denied, PadProtocol.TypeOf(await Receive(wrong)));
    }

    [Fact]
    public async Task WrongCodeIsDeniedAndSecretWorks()
    {
        using var temp = new TempFolder();
        var settings = new Settings(Path.Combine(temp.Path, "settings.json"));
        using var server = new PadServer(settings) { ScanTargets = Array.Empty<IPAddress> };
        server.Start(FreePort());
        var session = server.StartPairing();

        using (var socket = await Connect(server.Port))
        {
            await Send(socket, Hello("pad-2", code: session.Code == "123456" ? "654321" : "123456"));
            var denied = await Receive(socket);
            Assert.Equal("Der Code stimmt nicht.", denied.String("reason"));
        }

        var payload = server.QrPayload(session);
        Assert.StartsWith("lernheftpad://pair?", payload);
        Assert.Contains("id=" + server.ServerId, payload);

        using var good = await Connect(server.Port);
        await Send(good, Hello("pad-2", secret: session.Secret));
        Assert.Equal(PadProtocol.Welcome, PadProtocol.TypeOf(await Receive(good)));
    }

    [Fact]
    public async Task HelloEndpointAnswersForDiscovery()
    {
        using var temp = new TempFolder();
        using var server = new PadServer(new Settings(Path.Combine(temp.Path, "s.json")), "Mein Surface") { ScanTargets = Array.Empty<IPAddress> };
        server.Start(FreePort());
        using var http = new HttpClient();
        var body = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/hello");
        var json = (JsonObject)JsonNode.Parse(body)!;
        Assert.Equal("LernheftStudio", json.String("app"));
        Assert.Equal("Mein Surface", json.String("name"));
    }

    /// <summary>
    /// Die Firewall blockt das Surface: dann wählt das Surface das iPad an. Hier spielt ein kleiner
    /// WebSocket-Server das iPad.
    /// </summary>
    [Fact]
    public async Task ReverseConnectionWhenFirewallBlocks()
    {
        using var temp = new TempFolder();
        var padPort = FreePort();
        var padListener = new TcpListener(IPAddress.Loopback, padPort);
        padListener.Start();

        using var server = new PadServer(new Settings(Path.Combine(temp.Path, "s.json")))
        {
            PadPort = padPort,
            ScanTargets = () => new[] { IPAddress.Loopback }
        };
        var session = server.StartPairing();
        var connected = new TaskCompletionSource<PadLink>();
        server.Connected += link => connected.TrySetResult(link);
        server.Start(FreePort());

        // Das „iPad" nimmt die Verbindung an und meldet sich mit dem Code.
        using var client = await padListener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var stream = client.GetStream();
        var head = await ReadHead(stream);
        Assert.StartsWith("GET /studio", head);
        var key = head.Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
        var answer = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {PadServer.AcceptKey(key)}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(answer));
        using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
        await Send(socket, Hello("pad-3", code: session.Code));
        var welcome = await Receive(socket);
        Assert.Equal(PadProtocol.Welcome, PadProtocol.TypeOf(welcome));
        var link = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(link.Reverse);
        padListener.Stop();
    }

    private static async Task<string> ReadHead(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one) == 0) break;
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && Encoding.ASCII.GetString(bytes.ToArray(), bytes.Count - 4, 4) == "\r\n\r\n") break;
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    [Fact]
    public async Task SilentPeerIsDropped()
    {
        using var temp = new TempFolder();
        using var server = new PadServer(new Settings(Path.Combine(temp.Path, "s.json"))) { ScanTargets = Array.Empty<IPAddress> };
        server.Start(FreePort());
        var session = server.StartPairing();
        var gone = new TaskCompletionSource<bool>();
        server.Disconnected += _ => gone.TrySetResult(true);
        var socket = await Connect(server.Port);
        await Send(socket, Hello("pad-4", code: session.Code));
        await Receive(socket);
        // Das iPad schweigt (kein Lesen, kein Pong) – nach ~14 s gilt es als weg.
        Assert.True(await gone.Task.WaitAsync(TimeSpan.FromSeconds(25)));
        socket.Abort();
    }
}
