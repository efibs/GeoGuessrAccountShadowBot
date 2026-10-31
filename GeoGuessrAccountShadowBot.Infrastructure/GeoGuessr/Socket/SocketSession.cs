// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using GeoGuessrAccountShadowBot.Application.Options;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Socket;

/// <summary>The server refused the handshake with an HTTP status.</summary>
internal sealed class SocketHandshakeException(int statusCode, TimeSpan? retryAfter, Exception innerException)
    : Exception($"The socket handshake was refused with status {statusCode}.", innerException)
{
    public int StatusCode { get; } = statusCode;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>How a socket ended. A null code means there was no closing handshake.</summary>
internal sealed record SocketClosure(int? Code, string? Description);

/// <summary>
/// One open socket. The seam that lets the supervisor be tested without a network.
/// </summary>
internal interface ISocketSession : IAsyncDisposable
{
    Task SendAsync(string frame, CancellationToken cancellationToken);

    /// <summary>Yields text frames until the socket ends, which is then described by <see cref="Closure"/>.</summary>
    IAsyncEnumerable<string> ReadFramesAsync(CancellationToken cancellationToken);

    SocketClosure? Closure { get; }
}

/// <summary>Opens sockets.</summary>
internal interface ISocketConnector
{
    /// <exception cref="SocketHandshakeException">The server answered with something other than an upgrade.</exception>
    Task<ISocketSession> ConnectAsync(bool afterServerRestart, CancellationToken cancellationToken);
}

/// <summary>
/// Connects the way the site's own client does: one id per connection, and the session in a cookie.
/// Other logins on the account have been seen to coexist with this, so no attempt is made to take
/// over or stay exclusive.
/// </summary>
internal sealed class ClientWebSocketConnector(IOptions<ShadowOptions> options) : ISocketConnector
{
    private const int FirstAttempt = 1;

    public async Task<ISocketSession> ConnectAsync(bool afterServerRestart, CancellationToken cancellationToken)
    {
        var settings = options.Value.GeoGuessr;

        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Cookie", $"_ncfa={settings.NcfaToken}");
        socket.Options.SetRequestHeader("Origin", GeoGuessrClient.Origin);
        socket.Options.SetRequestHeader("User-Agent", GeoGuessrClient.UserAgent);
        socket.Options.KeepAliveInterval = settings.Socket.KeepAlive;
        socket.Options.KeepAliveTimeout = settings.Socket.KeepAlive;

        var query = $"c={Uri.EscapeDataString(settings.ClientVersion)}&tabId={Guid.NewGuid()}&attempt={FirstAttempt}&visibility=visible";
        if (afterServerRestart)
        {
            query += "&reconnect=true";
        }

        var url = new UriBuilder(settings.SocketUrl) { Query = query }.Uri;

        try
        {
            await socket.ConnectAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException exception) when (socket.HttpStatusCode != 0)
        {
            var status = (int)socket.HttpStatusCode;
            var retryAfter = ReadRetryAfter(socket);
            socket.Dispose();
            throw new SocketHandshakeException(status, retryAfter, exception);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        // Response headers aren't needed any more, and may hold a Set-Cookie.
        socket.HttpResponseHeaders = null;
        return new ClientWebSocketSession(socket, settings.Socket.MaxFrameBytes);
    }

    private static TimeSpan? ReadRetryAfter(ClientWebSocket socket) =>
        socket.HttpResponseHeaders is { } headers
        && headers.TryGetValue("Retry-After", out var values)
        && int.TryParse(values.FirstOrDefault(), out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
}

/// <summary>
/// One open <see cref="ClientWebSocket"/>. It allows only one send at a time, so a lock serialises
/// heartbeats and chat messages.
/// </summary>
internal sealed class ClientWebSocketSession(ClientWebSocket socket, int maxFrameBytes) : ISocketSession
{
    private const int ReceiveBufferBytes = 16 * 1024;
    private const WebSocketCloseStatus OwnCloseCode = (WebSocketCloseStatus)4000;
    private const WebSocketCloseStatus MessageTooBig = WebSocketCloseStatus.MessageTooBig;

    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public SocketClosure? Closure { get; private set; }

    public async Task SendAsync(string frame, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SendTimeout);

        await _sendLock.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async IAsyncEnumerable<string> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferBytes);
        var message = new ArrayBufferWriter<byte>(ReceiveBufferBytes);

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    yield break;
                }

                if (result.Value.MessageType == WebSocketMessageType.Close)
                {
                    Closure = new SocketClosure((int?)socket.CloseStatus, socket.CloseStatusDescription);
                    yield break;
                }

                message.Write(buffer.AsSpan(0, result.Value.Count));
                if (message.WrittenCount > maxFrameBytes)
                {
                    await CloseOutputAsync(MessageTooBig).ConfigureAwait(false);
                    Closure = new SocketClosure(null, "frame too big");
                    yield break;
                }

                if (!result.Value.EndOfMessage)
                {
                    continue;
                }

                // Binary frames only belong to game server sockets, so here they mean nothing.
                if (result.Value.MessageType == WebSocketMessageType.Text)
                {
                    yield return Encoding.UTF8.GetString(message.WrittenSpan);
                }

                message.ResetWrittenCount();
            }

            Closure ??= new SocketClosure((int?)socket.CloseStatus, socket.CloseStatusDescription);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseOutputAsync(OwnCloseCode).ConfigureAwait(false);
        socket.Dispose();
        _sendLock.Dispose();
    }

    private async Task<ValueWebSocketReceiveResult?> ReceiveAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            Closure = new SocketClosure(null, "transport failure");
            return null;
        }
    }

    private async Task CloseOutputAsync(WebSocketCloseStatus status)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(CloseTimeout);
        try
        {
            await socket.CloseOutputAsync(status, null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            // Best effort: the connection is being abandoned either way.
        }
    }
}
