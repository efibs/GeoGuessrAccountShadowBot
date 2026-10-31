// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeoGuessrAccountShadowBot.Probe;

/// <summary>
/// The connection settings a probe run can vary to learn how the socket behaves.
/// </summary>
internal sealed record SocketProbeOptions(string TabId, bool Heartbeat, TimeSpan? KeepAliveTimeout);

/// <summary>
/// One connection to the GeoGuessr socket. It records every frame, and prints only each frame's
/// code, topic, size and payload keys.
/// </summary>
internal sealed class SocketProbe(ProbeSettings settings, CaptureWriter capture, SocketProbeOptions options) : IDisposable
{
    private const int ReceiveBufferBytes = 16 * 1024;
    private const int MaxFrameBytes = 4 * 1024 * 1024;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions FrameSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string[] HandshakeHeaders = ["Server", "cf-mitigated", "cf-ray", "Content-Type", "Retry-After"];

    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Opens the socket, and reports how the handshake went if it was refused.</summary>
    public async Task<bool> ConnectAsync(CancellationToken cancellationToken)
    {
        _socket.Options.CollectHttpResponseDetails = true;
        _socket.Options.SetRequestHeader("Cookie", $"_ncfa={settings.NcfaToken}");
        _socket.Options.SetRequestHeader("Origin", "https://www.geoguessr.com");
        _socket.Options.SetRequestHeader("User-Agent", ProbeSettings.UserAgent);

        if (options.KeepAliveTimeout is { } timeout)
        {
            // With a timeout set, a server that doesn't answer pings aborts the connection, which is
            // exactly the answer this option is here to get.
            _socket.Options.KeepAliveInterval = timeout;
            _socket.Options.KeepAliveTimeout = timeout;
        }

        var query = $"c={Uri.EscapeDataString(settings.ClientVersion)}&tabId={options.TabId}&attempt=1&visibility=visible";
        var url = new UriBuilder(settings.SocketUrl) { Query = query }.Uri;

        try
        {
            await _socket.ConnectAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException exception)
        {
            Report("meta", $"handshake refused: HTTP {(int)_socket.HttpStatusCode}, {exception.WebSocketErrorCode}");
            PrintHandshakeHeaders();
            return false;
        }

        Report("meta", $"connected: HTTP {(int)_socket.HttpStatusCode}, subprotocol [{_socket.SubProtocol}], tabId {options.TabId}");
        PrintHandshakeHeaders();
        return true;
    }

    public async Task SendAsync(string code, string? topic = null, string? payload = null, string? client = "web", CancellationToken cancellationToken = default)
    {
        var text = JsonSerializer.Serialize(new OutboundFrame(code, topic, payload, client), FrameSerializerOptions);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }

        capture.Write("out", text);
        Print("out", $"{code} {topic}");
    }

    /// <summary>Receives until the server closes, the connection fails, or the token is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = options.Heartbeat ? HeartbeatLoopAsync(heartbeatStop.Token) : Task.CompletedTask;

        try
        {
            await ReceiveLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CloseAsync().ConfigureAwait(false);
        }
        catch (WebSocketException exception)
        {
            Report("meta", $"connection failed: {exception.WebSocketErrorCode} ({exception.Message})");
        }

        await heartbeatStop.CancelAsync().ConfigureAwait(false);
        await heartbeat.ConfigureAwait(false);
    }

    public void Dispose()
    {
        _socket.Dispose();
        _sendLock.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferBytes);
        var message = new ArrayBufferWriter<byte>(ReceiveBufferBytes);

        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Report("meta", $"closed by server: {(int?)_socket.CloseStatus} [{_socket.CloseStatusDescription}]");
                    await CloseAsync().ConfigureAwait(false);
                    return;
                }

                message.Write(buffer.AsSpan(0, result.Count));
                if (message.WrittenCount > MaxFrameBytes)
                {
                    Report("meta", $"frame over {MaxFrameBytes} bytes, stopping");
                    return;
                }

                if (!result.EndOfMessage)
                {
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    HandleTextFrame(Encoding.UTF8.GetString(message.WrittenSpan));
                }
                else
                {
                    Report("meta", $"binary frame, {message.WrittenCount} bytes");
                }

                message.ResetWrittenCount();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false) && _socket.State == WebSocketState.Open)
            {
                await SendAsync("HeartBeat", client: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The run is over.
        }
        catch (WebSocketException)
        {
            // The receive loop reports the failure.
        }
    }

    private async Task CloseAsync()
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            // 4000 is the code the site's own client uses for a deliberate close.
            await _socket.CloseOutputAsync((WebSocketCloseStatus)4000, "probe finished", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            // Best effort: the process is about to exit either way.
        }
    }

    private void HandleTextFrame(string text)
    {
        capture.Write("in", text);
        Print("in", DescribeFrame(text));
    }

    private void Report(string direction, string text)
    {
        capture.Write(direction, text);
        Print(direction, text);
    }

    private void PrintHandshakeHeaders()
    {
        if (_socket.HttpResponseHeaders is not { } headers)
        {
            return;
        }

        foreach (var name in HandshakeHeaders)
        {
            if (headers.TryGetValue(name, out var values))
            {
                Report("meta", $"  {name}: {string.Join(", ", values)}");
            }
        }

        // The other headers aren't needed, and Set-Cookie must never be printed.
        _socket.HttpResponseHeaders = null;
    }

    private static string DescribeFrame(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var description = new StringBuilder();
            description.Append(ReadString(root, "code") ?? "<no code>").Append(' ').Append(ReadString(root, "topic"));
            description.Append(CultureInfo.InvariantCulture, $" ({text.Length} chars)");

            var extras = root.EnumerateObject()
                .Select(property => property.Name)
                .Where(name => name is not ("code" or "topic" or "payload"));
            description.Append(" fields: ").AppendJoin(',', extras);

            if (root.TryGetProperty("payload", out var payload))
            {
                description.Append(" payload: ").Append(DescribePayload(payload));
            }

            return description.ToString();
        }
        catch (JsonException)
        {
            return $"not JSON ({text.Length} chars)";
        }
    }

    private static string DescribePayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.String)
        {
            return $"{payload.ValueKind} {JsonShape.Describe(payload.GetRawText())}";
        }

        try
        {
            return $"string-encoded {JsonShape.Describe(payload.GetString() ?? "null")}";
        }
        catch (JsonException)
        {
            return "plain string";
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void Print(string direction, string text) =>
        Console.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss.fff} {direction,-4} {text}");

    private sealed record OutboundFrame(string Code, string? Topic, string? Payload, string? Client);
}
