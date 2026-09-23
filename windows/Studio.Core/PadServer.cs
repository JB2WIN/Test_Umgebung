using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lernheft.Studio;

/// <summary>
/// Eine bestehende Verbindung zu einem iPad. Nachrichten werden als JSON-Text verschickt.
/// </summary>
public sealed class PadLink : IDisposable
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private DateTime _lastHeard = DateTime.UtcNow;

    public string DeviceId { get; internal set; } = "";
    public string DeviceName { get; internal set; } = "iPad";
    public string Address { get; }
    public bool Dark { get; set; }
    public bool Reverse { get; }

    public event Action<JsonObject>? Received;
    public event Action<PadLink>? Closed;

    internal PadLink(WebSocket socket, string address, bool reverse)
    {
        _socket = socket;
        Address = address;
        Reverse = reverse;
    }

    public bool IsOpen => _socket.State == WebSocketState.Open && !_stop.IsCancellationRequested;

    internal async Task<JsonObject?> ReceiveOneAsync(TimeSpan timeout)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        limit.CancelAfter(timeout);
        try
        {
            return await ReadMessageAsync(limit.Token);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<JsonObject?> ReadMessageAsync(CancellationToken cancel)
    {
        var buffer = new byte[64 * 1024];
        using var collected = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancel);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            collected.Write(buffer, 0, result.Count);
            if (collected.Length > 256L * 1024 * 1024) throw new InvalidDataException("Nachricht zu groß");
            if (!result.EndOfMessage) continue;
            _lastHeard = DateTime.UtcNow;
            if (result.MessageType != WebSocketMessageType.Text) return new JsonObject { ["t"] = "" };
            return JsonNode.Parse(collected.ToArray()) as JsonObject ?? new JsonObject { ["t"] = "" };
        }
    }

    internal void Run()
    {
        _ = Task.Run(ReceiveLoopAsync);
        _ = Task.Run(KeepAliveAsync);
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var message = await ReadMessageAsync(_stop.Token);
                if (message is null) break;
                var type = PadProtocol.TypeOf(message);
                if (type == PadProtocol.Ping)
                {
                    _ = SendAsync(PadProtocol.Message(PadProtocol.Pong));
                    continue;
                }
                if (type is PadProtocol.Pong or "") continue;
                if (type == PadProtocol.Bye) break;
                try
                {
                    Received?.Invoke(message);
                }
                catch (Exception)
                {
                    // Ein Fehler beim Verarbeiten darf die Verbindung nicht kappen.
                }
            }
        }
        catch (Exception)
        {
            // Verbindung weg – unten wird aufgeräumt.
        }
        Close();
    }

    /// <summary>Alle paar Sekunden ein Lebenszeichen; bleibt die Gegenseite stumm, ist sie weg.</summary>
    private async Task KeepAliveAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (DateTime.UtcNow - _lastHeard > TimeSpan.FromSeconds(14))
            {
                Close();
                return;
            }
            await SendAsync(PadProtocol.Message(PadProtocol.Ping));
        }
    }

    public async Task<bool> SendAsync(JsonObject message)
    {
        if (!IsOpen) return false;
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendLock.WaitAsync();
        try
        {
            if (!IsOpen) return false;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(bytes.Length > 1_000_000 ? 60 : 15));
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, limit.Token);
            return true;
        }
        catch (Exception)
        {
            Close();
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Send(JsonObject message) => _ = SendAsync(message);

    public void Close()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _ = Task.Run(async () =>
        {
            // Ordentlich verabschieden, bevor die Leitung zugeht – höchstens eine Sekunde lang.
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", limit.Token);
                }
            }
            catch (Exception) { }
            Closed?.Invoke(this);
        });
    }

    public void Dispose()
    {
        Close();
        _socket.Dispose();
    }
}

/// <summary>
/// Wartet auf das iPad. Zwei Wege führen zueinander:
///
/// 1. Das iPad verbindet sich mit dem Surface (Adresse aus dem QR-Code oder im WLAN gesucht).
/// 2. Blockt die Windows-Firewall eingehende Verbindungen, sucht das Surface das iPad im WLAN und
///    verbindet sich von sich aus – ausgehende Verbindungen lässt Windows immer zu.
///
/// Angemeldet wird in beiden Fällen gleich: Das iPad schickt „hello" mit Code, QR-Geheimnis oder
/// dem Schlüssel aus einer früheren Kopplung.
/// </summary>
public sealed class PadServer : IDisposable
{
    private readonly Settings _settings;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private TcpListener? _listener;
    private PairingSession? _pairing;
    private PadLink? _current;
    private readonly HashSet<string> _dialing = new();

    public string ServerId { get; }
    public string ServerName { get; set; }
    public int Port { get; private set; }
    public bool Listening { get; private set; }
    public string? ListenProblem { get; private set; }

    /// <summary>Port, auf dem das iPad selbst lauscht (für den Rückweg). Für Proben änderbar.</summary>
    public int PadPort { get; set; } = PadProtocol.PadPort;

    /// <summary>Für Proben: nur an diese Adressen wählen statt das WLAN abzusuchen.</summary>
    public Func<IEnumerable<IPAddress>>? ScanTargets { get; set; }

    public event Action<PadLink>? Connected;
    public event Action<PadLink>? Disconnected;
    public event Action? PairingChanged;

    public PadLink? Current
    {
        get { lock (_lock) return _current; }
    }

    public PairingSession? Pairing
    {
        get { lock (_lock) return _pairing is { IsValid: true } ? _pairing : null; }
    }

    public PadServer(Settings settings, string? serverName = null)
    {
        _settings = settings;
        var id = settings.Get(Keys.ServerId, "");
        if (id.Length == 0)
        {
            id = Guid.NewGuid().ToString("N");
            settings.Set(Keys.ServerId, id);
        }
        ServerId = id;
        ServerName = settings.Get(Keys.ServerName, serverName ?? Environment.MachineName);
    }

    // MARK: - Gekoppelte Geräte

    public List<PairedDevice> PairedDevices
    {
        get
        {
            try
            {
                return JsonSerializer.Deserialize<List<PairedDevice>>(_settings.Get(Keys.PairedDevices, "[]")) ?? new();
            }
            catch (JsonException)
            {
                return new List<PairedDevice>();
            }
        }
    }

    private void SaveDevices(List<PairedDevice> devices) =>
        _settings.Set(Keys.PairedDevices, JsonSerializer.Serialize(devices));

    public void Forget(string deviceId)
    {
        SaveDevices(PairedDevices.Where(d => d.DeviceId != deviceId).ToList());
        if (Current is { } link && link.DeviceId == deviceId) link.Close();
        PairingChanged?.Invoke();
    }

    // MARK: - Start

    public void Start(int preferredPort = PadProtocol.StudioPort)
    {
        for (var port = preferredPort; port < preferredPort + 6; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                _listener = listener;
                Port = port;
                Listening = true;
                ListenProblem = null;
                _ = Task.Run(AcceptLoopAsync);
                break;
            }
            catch (SocketException error)
            {
                ListenProblem = error.Message;
            }
        }
        if (!Listening) Port = preferredPort;
        _ = Task.Run(DialLoopAsync);
    }

    public PairingSession StartPairing(TimeSpan? lifetime = null)
    {
        var session = new PairingSession(lifetime ?? TimeSpan.FromMinutes(10));
        lock (_lock) _pairing = session;
        PairingChanged?.Invoke();
        return session;
    }

    public void StopPairing()
    {
        lock (_lock) _pairing = null;
        PairingChanged?.Invoke();
    }

    public string QrPayload(PairingSession session) =>
        session.QrPayload(ServerId, ServerName, LocalAddresses().Select(a => a.ToString()), Port);

    // MARK: - Eingehende Verbindungen

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                if (_stop.IsCancellationRequested) return;
                await Task.Delay(200);
                continue;
            }
            _ = Task.Run(() => HandleIncomingAsync(client));
        }
    }

    private async Task HandleIncomingAsync(TcpClient client)
    {
        client.NoDelay = true;
        var stream = client.GetStream();
        var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var head = await ReadHeadAsync(stream, timeout.Token);
            if (head is null)
            {
                client.Dispose();
                return;
            }
            var lines = head.Split("\r\n");
            var requestLine = lines[0].Split(' ');
            var path = requestLine.Length > 1 ? requestLine[1] : "/";
            var headers = Headers(lines);

            if (!headers.TryGetValue("upgrade", out var upgrade) || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
            {
                // Kurze Antwort für die Suche im WLAN: „Hier ist ein Lernheft Studio."
                var body = path.StartsWith("/hello")
                    ? new JsonObject
                    {
                        ["app"] = "LernheftStudio",
                        ["id"] = ServerId,
                        ["name"] = ServerName,
                        ["v"] = PadProtocol.Version,
                        ["pairing"] = Pairing is not null
                    }.ToJsonString()
                    : "Lernheft Studio";
                var bytes = Encoding.UTF8.GetBytes(body);
                var response = $"HTTP/1.1 200 OK\r\nContent-Type: {(path.StartsWith("/hello") ? "application/json" : "text/plain")}; charset=utf-8\r\n" +
                               $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);
                await stream.WriteAsync(bytes, timeout.Token);
                client.Dispose();
                return;
            }

            if (!headers.TryGetValue("sec-websocket-key", out var key))
            {
                client.Dispose();
                return;
            }
            var accept = AcceptKey(key);
            var handshake = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), timeout.Token);

            var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = true,
                KeepAliveInterval = TimeSpan.Zero
            });
            await AuthenticateAsync(new PadLink(socket, address, reverse: false), client);
        }
        catch (Exception)
        {
            client.Dispose();
        }
    }

    private static async Task<string?> ReadHeadAsync(Stream stream, CancellationToken cancel)
    {
        var buffer = new List<byte>(1024);
        var one = new byte[1];
        while (buffer.Count < 16 * 1024)
        {
            var read = await stream.ReadAsync(one, cancel);
            if (read == 0) return null;
            buffer.Add(one[0]);
            var count = buffer.Count;
            if (count >= 4 && buffer[count - 4] == '\r' && buffer[count - 3] == '\n'
                && buffer[count - 2] == '\r' && buffer[count - 1] == '\n')
            {
                return Encoding.ASCII.GetString(buffer.ToArray(), 0, count - 4);
            }
        }
        return null;
    }

    private static Dictionary<string, string> Headers(string[] lines)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return headers;
    }

    public static string AcceptKey(string key) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

    // MARK: - Anmeldung

    private async Task AuthenticateAsync(PadLink link, TcpClient client)
    {
        var hello = await link.ReceiveOneAsync(TimeSpan.FromSeconds(10));
        if (hello is null || PadProtocol.TypeOf(hello) != PadProtocol.Hello)
        {
            link.Dispose();
            client.Dispose();
            return;
        }

        var deviceId = hello.String("deviceId") ?? "";
        var deviceName = hello.String("deviceName") ?? "iPad";
        var expected = hello.String("serverId");
        var reason = "";
        string? pairKey = null;

        if (!string.IsNullOrEmpty(expected) && expected != ServerId)
        {
            reason = "other";
        }
        else if (deviceId.Length == 0)
        {
            reason = "Das iPad hat sich nicht ausgewiesen.";
        }
        else
        {
            var devices = PairedDevices;
            var known = devices.FirstOrDefault(d => d.DeviceId == deviceId);
            var offeredKey = hello.String("pairKey");
            if (known is not null && !string.IsNullOrEmpty(offeredKey) && CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(offeredKey), Encoding.UTF8.GetBytes(known.PairKey)))
            {
                pairKey = known.PairKey;
                known.Name = deviceName;
                known.LastSeen = AppleTime.Now;
                known.LastAddress = link.Address;
                SaveDevices(devices);
            }
            else
            {
                PairingSession? session;
                lock (_lock) session = _pairing;
                if (session is not null && session.Accepts(hello.String("code"), hello.String("secret")))
                {
                    pairKey = PairingSession.RandomToken(32);
                    devices.RemoveAll(d => d.DeviceId == deviceId);
                    devices.Add(new PairedDevice
                    {
                        DeviceId = deviceId,
                        Name = deviceName,
                        PairKey = pairKey,
                        LastAddress = link.Address
                    });
                    SaveDevices(devices);
                    lock (_lock) _pairing = null;
                    PairingChanged?.Invoke();
                }
                else if (session is null)
                {
                    reason = known is null
                        ? "Am Surface ist gerade kein Kopplungsfenster offen. Klick dort auf „iPad verbinden“."
                        : "Dieses iPad ist am Surface nicht mehr gekoppelt. Koppel es neu.";
                }
                else
                {
                    reason = session.IsValid
                        ? "Der Code stimmt nicht."
                        : "Zu viele falsche Versuche. Öffne am Surface das Kopplungsfenster neu.";
                }
            }
        }

        if (pairKey is null)
        {
            var denied = PadProtocol.Message(PadProtocol.Denied);
            denied["reason"] = reason;
            await link.SendAsync(denied);
            await Task.Delay(300);
            link.Dispose();
            client.Dispose();
            return;
        }

        link.DeviceId = deviceId;
        link.DeviceName = deviceName;
        link.Dark = hello.Flag("dark");

        var welcome = PadProtocol.Message(PadProtocol.Welcome);
        welcome["serverId"] = ServerId;
        welcome["serverName"] = ServerName;
        welcome["pairKey"] = pairKey;
        welcome["v"] = PadProtocol.Version;
        if (!await link.SendAsync(welcome))
        {
            link.Dispose();
            client.Dispose();
            return;
        }

        PadLink? previous;
        lock (_lock)
        {
            previous = _current;
            _current = link;
        }
        if (previous is not null)
        {
            var bye = PadProtocol.Message(PadProtocol.Toast);
            bye["text"] = $"{deviceName} hat übernommen.";
            await previous.SendAsync(bye);
            previous.Close();
        }

        link.Closed += closed =>
        {
            var wasCurrent = false;
            lock (_lock)
            {
                if (_current == closed)
                {
                    _current = null;
                    wasCurrent = true;
                }
            }
            client.Dispose();
            if (wasCurrent) Disconnected?.Invoke(closed);
        };
        Connected?.Invoke(link);
        link.Run();
    }

    // MARK: - Rückweg: das iPad im WLAN suchen

    private async Task DialLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var pairing = Pairing is not null;
            var devices = PairedDevices;
            var wanted = Current is null && (pairing || devices.Count > 0);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(pairing ? 2 : 5), _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (!wanted || Current is not null) continue;

            var targets = new List<IPAddress>();
            foreach (var device in devices)
            {
                if (IPAddress.TryParse(device.LastAddress, out var last)) targets.Add(last);
            }
            targets.AddRange(ScanTargets?.Invoke() ?? SubnetHosts());
            await DialAllAsync(targets.Distinct().ToList());
        }
    }

    private async Task DialAllAsync(List<IPAddress> targets)
    {
        using var gate = new SemaphoreSlim(48);
        var tasks = targets.Select(async address =>
        {
            await gate.WaitAsync(_stop.Token);
            try
            {
                if (Current is null) await DialAsync(address);
            }
            finally
            {
                gate.Release();
            }
        });
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
    }

    private async Task DialAsync(IPAddress address)
    {
        var key = address.ToString();
        lock (_lock)
        {
            if (!_dialing.Add(key)) return;
        }
        var client = new TcpClient { NoDelay = true };
        try
        {
            using (var connect = new CancellationTokenSource(TimeSpan.FromMilliseconds(400)))
            {
                await client.ConnectAsync(address, PadPort, connect.Token);
            }
            var stream = client.GetStream();
            var socketKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var request = $"GET /studio HTTP/1.1\r\nHost: {address}:{PadPort}\r\nUpgrade: websocket\r\n" +
                          $"Connection: Upgrade\r\nSec-WebSocket-Key: {socketKey}\r\nSec-WebSocket-Version: 13\r\n\r\n";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
            var head = await ReadHeadAsync(stream, timeout.Token);
            if (head is null || !head.StartsWith("HTTP/1.1 101") ||
                !Headers(head.Split("\r\n")).TryGetValue("sec-websocket-accept", out var accept) ||
                accept != AcceptKey(socketKey))
            {
                client.Dispose();
                return;
            }
            var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = false,
                KeepAliveInterval = TimeSpan.Zero
            });
            await AuthenticateAsync(new PadLink(socket, key, reverse: true), client);
        }
        catch (Exception)
        {
            client.Dispose();
        }
        finally
        {
            lock (_lock) _dialing.Remove(key);
        }
    }

    // MARK: - Netz

    /// <summary>Die eigenen IPv4-Adressen im WLAN/LAN – ohne Loopback und ohne „169.254".</summary>
    public static List<IPAddress> LocalAddresses()
    {
        var result = new List<(IPAddress Address, int Rank)>();
        foreach (var card in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (card.OperationalStatus != OperationalStatus.Up) continue;
            if (card.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var name = (card.Name + " " + card.Description).ToLowerInvariant();
            if (name.Contains("vethernet") || name.Contains("hyper-v") || name.Contains("virtualbox")
                || name.Contains("vmware") || name.Contains("wsl") || name.Contains("docker")) continue;
            var rank = card.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1;
            IPInterfaceProperties properties;
            try
            {
                properties = card.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }
            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) continue;
                var bytes = address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue;
                result.Add((address, rank));
            }
        }
        return result.OrderBy(entry => entry.Rank).Select(entry => entry.Address).Distinct().ToList();
    }

    /// <summary>Alle Nachbarn im eigenen /24-Netz.</summary>
    public static IEnumerable<IPAddress> SubnetHosts()
    {
        foreach (var own in LocalAddresses())
        {
            var bytes = own.GetAddressBytes();
            for (var host = 1; host < 255; host++)
            {
                if (host == bytes[3]) continue;
                yield return new IPAddress(new[] { bytes[0], bytes[1], bytes[2], (byte)host });
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener?.Stop(); }
        catch (SocketException) { }
        Current?.Dispose();
    }
}
