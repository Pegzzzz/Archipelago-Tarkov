using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArchipelagoTarkov.Ap;

public enum ApConnectionState
{
    Disconnected,
    Connecting,
    WaitingForRoomInfo,
    Authenticating,
    Connected,
}

/// <summary>
/// Minimal Archipelago websocket client. It only moves packets: every packet received is queued in
/// <see cref="Incoming"/> and consumed by the owning session on the SPT update loop, so all game
/// changes happen on one thread.
/// </summary>
public sealed class ApClient : IDisposable
{
    public const string GameName = "Escape from Tarkov";
    private static readonly JsonSerializerOptions SerializeOptions = new() { WriteIndented = false };

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    /// <summary>Bumped on every open/close so callbacks from an old socket can't touch the new one.</summary>
    private int _generation;

    public ConcurrentQueue<JsonObject> Incoming { get; } = new();
    public ApConnectionState State { get; private set; } = ApConnectionState.Disconnected;
    public string? LastError { get; private set; }
    public string? ConnectedUri { get; private set; }

    /// <summary>Open the socket. Tries wss:// then ws:// when the address has no scheme. Never throws.</summary>
    public async Task<bool> OpenAsync(string address, CancellationToken token)
    {
        Close();
        var generation = Interlocked.Increment(ref _generation);
        State = ApConnectionState.Connecting;
        LastError = null;
        List<Uri> candidates;
        try
        {
            candidates = CandidateUris(address).ToList();
        }
        catch (Exception ex)
        {
            LastError = $"'{address}' is not a valid server address ({ex.Message})";
            State = ApConnectionState.Disconnected;
            return false;
        }

        foreach (var uri in candidates)
        {
            var socket = new ClientWebSocket();
            try
            {
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                socket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
                // Archipelago rooms can send large DataPackage messages; be generous.
                socket.Options.SetBuffer(1 << 16, 1 << 16);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
                if (generation != Volatile.Read(ref _generation))
                {
                    // Closed or reopened while we were connecting.
                    socket.Dispose();
                    return false;
                }

                _socket = socket;
                _cts = new CancellationTokenSource();
                ConnectedUri = uri.ToString();
                State = ApConnectionState.WaitingForRoomInfo;
                var cts = _cts;
                _receiveLoop = Task.Run(() => ReceiveLoopAsync(socket, generation, cts.Token));
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"{uri}: {Unwrap(ex)}";
                socket.Dispose();
            }
        }

        if (generation == Volatile.Read(ref _generation))
        {
            State = ApConnectionState.Disconnected;
        }

        return false;
    }

    public void MarkAuthenticating() => State = ApConnectionState.Authenticating;

    public void MarkConnected() => State = ApConnectionState.Connected;

    /// <summary>Turn what the player typed into the addresses to try. Throws on an address that can't be used.</summary>
    public static IEnumerable<Uri> CandidateUris(string address)
    {
        var trimmed = address.Trim().TrimEnd('/');
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "wss://" + trimmed[8..];
        }
        else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "ws://" + trimmed[7..];
        }

        if (trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            var explicitUri = new Uri(trimmed);
            if (explicitUri.IsDefaultPort && !trimmed[(trimmed.IndexOf("//", StringComparison.Ordinal) + 2)..].Contains(':'))
            {
                explicitUri = new UriBuilder(explicitUri) { Port = 38281 }.Uri;
            }

            return [explicitUri];
        }

        // host, host:port, [ipv6]:port
        var withPort = trimmed;
        var lastColon = trimmed.LastIndexOf(':');
        var closingBracket = trimmed.LastIndexOf(']');
        if (lastColon < 0 || lastColon < closingBracket)
        {
            withPort = trimmed + ":38281";
        }

        var plain = new Uri($"ws://{withPort}");
        var secure = new Uri($"wss://{withPort}");
        if (plain.Port is <= 0 or > 65535)
        {
            throw new UriFormatException("invalid port");
        }

        // Local servers almost never use TLS; try plain first for them to avoid a slow failed handshake.
        var host = plain.Host;
        var local = host is "localhost" or "127.0.0.1" or "[::1]" || host.StartsWith("192.168.") || host.StartsWith("10.");
        return local ? [plain, secure] : [secure, plain];
    }

    /// <summary>True when <paramref name="address"/> can be turned into a server URI.</summary>
    public static bool IsValidAddress(string address, out string? error)
    {
        try
        {
            _ = CandidateUris(address).ToList();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"'{address}' is not a valid server address ({ex.Message}). Use host:port, e.g. archipelago.gg:38281.";
            return false;
        }
    }

    private static string Unwrap(Exception ex)
    {
        while (ex.InnerException != null)
        {
            ex = ex.InnerException;
        }

        return ex.Message;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, int generation, CancellationToken token)
    {
        var buffer = new byte[1 << 16];
        var message = new MemoryStream();
        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    LastError = $"server closed the connection ({socket.CloseStatusDescription ?? result.CloseStatus?.ToString()})";
                    break;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                try
                {
                    var node = JsonNode.Parse(text);
                    if (node is JsonArray array)
                    {
                        foreach (var packet in array)
                        {
                            if (packet is JsonObject obj && generation == Volatile.Read(ref _generation))
                            {
                                Incoming.Enqueue((JsonObject)obj.DeepClone());
                            }
                        }
                    }
                }
                catch (JsonException ex)
                {
                    LastError = $"bad packet from server: {ex.Message}";
                }
            }
        }
        catch (OperationCanceledException)
        {
            // closing
        }
        catch (Exception ex)
        {
            LastError = Unwrap(ex);
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation))
            {
                State = ApConnectionState.Disconnected;
            }
        }
    }

    public async Task SendAsync(params JsonObject[] packets)
    {
        var socket = _socket;
        var generation = Volatile.Read(ref _generation);
        if (socket == null || socket.State != WebSocketState.Open)
        {
            return;
        }

        var array = new JsonArray();
        foreach (var packet in packets)
        {
            array.Add(packet);
        }

        var bytes = Encoding.UTF8.GetBytes(array.ToJsonString(SerializeOptions));
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _generation))
            {
                LastError = Unwrap(ex);
                State = ApConnectionState.Disconnected;
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Fire-and-forget send that never throws on the caller.</summary>
    public void Send(params JsonObject[] packets)
    {
        _ = SendAsync(packets);
    }

    public void Close()
    {
        Interlocked.Increment(ref _generation);
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignored
        }

        var socket = _socket;
        _socket = null;
        if (socket != null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).Wait(TimeSpan.FromSeconds(2));
                }
            }
            catch
            {
                // ignored
            }

            socket.Dispose();
        }

        State = ApConnectionState.Disconnected;
        while (Incoming.TryDequeue(out _))
        {
        }
    }

    public void Dispose() => Close();

    // ------------------------------------------------------------------ packet builders
    public static JsonObject ConnectPacket(string slot, string? password, string uuid, IEnumerable<string> tags) =>
        new()
        {
            ["cmd"] = "Connect",
            ["password"] = password ?? "",
            ["game"] = GameName,
            ["name"] = slot,
            ["uuid"] = uuid,
            ["version"] = new JsonObject { ["major"] = 0, ["minor"] = 6, ["build"] = 4, ["class"] = "Version" },
            ["items_handling"] = 0b111,
            ["tags"] = new JsonArray(tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["slot_data"] = true,
        };

    public static JsonObject ConnectUpdatePacket(IEnumerable<string> tags) =>
        new()
        {
            ["cmd"] = "ConnectUpdate",
            ["items_handling"] = 0b111,
            ["tags"] = new JsonArray(tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
        };

    public static JsonObject LocationChecksPacket(IEnumerable<long> locations) =>
        new()
        {
            ["cmd"] = "LocationChecks",
            ["locations"] = new JsonArray(locations.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
        };

    public static JsonObject StatusPacket(int status) => new() { ["cmd"] = "StatusUpdate", ["status"] = status };

    public static JsonObject SayPacket(string text) => new() { ["cmd"] = "Say", ["text"] = text };

    public static JsonObject SyncPacket() => new() { ["cmd"] = "Sync" };

    public static JsonObject GetDataPackagePacket(IEnumerable<string> games) =>
        new()
        {
            ["cmd"] = "GetDataPackage",
            ["games"] = new JsonArray(games.Select(g => (JsonNode?)JsonValue.Create(g)).ToArray()),
        };

    public static JsonObject DeathLinkPacket(string source, string cause) =>
        new()
        {
            ["cmd"] = "Bounce",
            ["tags"] = new JsonArray("DeathLink"),
            ["data"] = new JsonObject
            {
                ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                ["source"] = source,
                ["cause"] = cause,
            },
        };
}
