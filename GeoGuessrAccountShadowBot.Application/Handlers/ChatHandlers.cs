// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Pipeline;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;

namespace GeoGuessrAccountShadowBot.Application.Handlers;

/// <summary>
/// Turns chat payloads into something readable, and recognises the bot's own replies.
/// </summary>
public static class ChatText
{
    /// <summary>The text to mirror, or null if the payload isn't a message a person wrote.</summary>
    public static string? Describe(ChatMessage message)
    {
        if (message.IsSystemMessage)
        {
            return null;
        }

        return message.PayloadType switch
        {
            ChatPayloadType.Text => message.Text,
            ChatPayloadType.Emote => $"[emote] {message.Text}",
            ChatPayloadType.Invite => "[sent an invite]",
            ChatPayloadType.Poll => "[posted a poll]",
            ChatPayloadType.HighlightComment => $"[comment on a highlight] {message.Text}",
            _ => null
        };
    }

    /// <summary>GeoGuessr echoes text unchanged, so an exact hash matches a reply with its echo.</summary>
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>
/// Mirrors a direct message. An outgoing message is matched against replies sent through the bot:
/// those only need confirming, and anything else came from another login and is worth showing.
/// </summary>
public sealed class DirectMessageHandler(IShadowStore store, IEventContext context, AccountContext account)
    : IShadowEventHandler<DirectMessageReceived>
{
    public async ValueTask HandleAsync(DirectMessageReceived shadowEvent, CancellationToken cancellationToken)
    {
        var message = shadowEvent.Message;
        var peerId = message.PeerOf(account.UserId);

        if (message.IsSentBy(account.UserId) && message.Text is { } sentText)
        {
            var pending = await store.FindPendingReplyAsync(peerId, ChatText.Hash(sentText), cancellationToken).ConfigureAwait(false);
            if (pending is not null)
            {
                await store.RemovePendingReplyAsync(pending.Id, cancellationToken).ConfigureAwait(false);
                context.Notify(new ReplyFeedback(pending.ThreadId, pending.MessageId, ReplyFeedbackKind.Delivered, null));
                return;
            }
        }

        if (ChatText.Describe(message) is not { } text)
        {
            return;
        }

        var origin = message.IsSentBy(account.UserId) ? ChatLineOrigin.OtherLogin : ChatLineOrigin.Partner;
        context.Notify(new ChatLine(peerId, origin, new UntrustedText(text), message.SentAt));
    }
}

/// <summary>
/// Mirrors a club chat message.
/// </summary>
public sealed class ClubChatMessageHandler(IEventContext context) : IShadowEventHandler<ClubChatMessageReceived>
{
    public ValueTask HandleAsync(ClubChatMessageReceived shadowEvent, CancellationToken cancellationToken)
    {
        var message = shadowEvent.Message;
        if (ChatText.Describe(message) is { } text)
        {
            context.Notify(new EventCard(
                EventKeys.ClubChat, CardStyle.Info, "Club chat", null, message.SourceId, new UntrustedText(text), message.SentAt));
        }

        return ValueTask.CompletedTask;
    }
}
