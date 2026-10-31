// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net.WebSockets;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain;

namespace GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Socket;

/// <summary>
/// Holds the socket that is open right now, for anything that needs to send through it.
/// </summary>
internal sealed class CurrentSocket
{
    public ISocketSession? Session { get; set; }
}

/// <summary>
/// Sends chat messages over the open socket. A send that fails isn't retried, because it may have
/// gone out anyway, and sending it twice would be worse than reporting a failure.
/// </summary>
internal sealed class GeoGuessrChatSender(CurrentSocket current) : IGeoGuessrChatSender
{
    private static readonly Error NotConnected =
        new(ErrorKind.Unavailable, "chat.offline", "Not sent, because the bot isn't connected to GeoGuessr right now.");

    public Task<Result> SendDirectMessageAsync(string peerId, string text, CancellationToken cancellationToken = default) =>
        SendAsync(SocketTopics.FriendChat(peerId), text, cancellationToken);

    public Task<Result> SendClubMessageAsync(string clubId, string text, CancellationToken cancellationToken = default) =>
        SendAsync(SocketTopics.ClubChat(clubId), text, cancellationToken);

    private async Task<Result> SendAsync(string topic, string text, CancellationToken cancellationToken)
    {
        if (current.Session is not { } session)
        {
            return NotConnected;
        }

        try
        {
            await session.SendAsync(OutboundFrames.ChatMessage(topic, text), cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return NotConnected;
        }
    }
}
