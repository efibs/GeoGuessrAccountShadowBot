// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json.Serialization;
using GeoGuessrAccountShadowBot.Domain.Model;

namespace GeoGuessrAccountShadowBot.Application.Notifications;

/// <summary>
/// Something waiting to be delivered to Discord. These are stored in the same transaction as the
/// state change that caused them, so a crash can't lose a notification.
/// </summary>
[JsonDerivedType(typeof(EventCard), "card")]
[JsonDerivedType(typeof(ChatLine), "chat")]
[JsonDerivedType(typeof(FriendRequestCard), "friendRequest")]
[JsonDerivedType(typeof(FriendRequestOutcome), "friendRequestOutcome")]
[JsonDerivedType(typeof(StatusMessage), "status")]
[JsonDerivedType(typeof(ReplyFeedback), "replyFeedback")]
public abstract record OutboxMessage;

/// <summary>The look of a card, which carries meaning at a glance.</summary>
public enum CardStyle
{
    Info,
    Success,
    Warning,
    Danger
}

/// <summary>
/// A notification card in the channel configured for its event.
/// </summary>
/// <param name="EventKey">The event, which decides the channel and role pings.</param>
/// <param name="Style">The look of the card.</param>
/// <param name="Title">Text written by the bot.</param>
/// <param name="Detail">Further text written by the bot, if any.</param>
/// <param name="ActorUserId">The GeoGuessr user the card is about, resolved to a name on delivery.</param>
/// <param name="Body">Text from GeoGuessr, escaped on delivery.</param>
/// <param name="OccurredAt">When it happened, shown as a Discord timestamp.</param>
public sealed record EventCard(
    string EventKey,
    CardStyle Style,
    string Title,
    string? Detail,
    string? ActorUserId,
    UntrustedText? Body,
    DateTimeOffset? OccurredAt) : OutboxMessage;

/// <summary>Who wrote a mirrored chat line.</summary>
public enum ChatLineOrigin
{
    /// <summary>The chat partner.</summary>
    Partner,

    /// <summary>The account, from a browser or another login rather than through the bot.</summary>
    OtherLogin
}

/// <summary>
/// One chat message in the forum post of its partner.
/// </summary>
public sealed record ChatLine(
    string PartnerUserId,
    ChatLineOrigin Origin,
    UntrustedText Text,
    DateTimeOffset SentAt) : OutboxMessage;

/// <summary>
/// A card for an incoming friend request, with buttons when those are enabled.
/// </summary>
public sealed record FriendRequestCard(string UserId) : OutboxMessage;

/// <summary>
/// Closes a friend request card, because the request was answered.
/// </summary>
public sealed record FriendRequestOutcome(string UserId, bool BecameFriend) : OutboxMessage;

/// <summary>
/// A note to the people running the bot.
/// </summary>
public sealed record StatusMessage(string Title, string Detail, bool Ping) : OutboxMessage;

/// <summary>What became of a reply typed in Discord.</summary>
public enum ReplyFeedbackKind
{
    Delivered,
    Unconfirmed,
    NotSent,
    Refused
}

/// <summary>
/// Tells the author of a reply what became of it.
/// </summary>
/// <param name="ThreadId">The forum post holding the reply.</param>
/// <param name="MessageId">The reply itself.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Reason">An explanation to post, if the reaction alone isn't enough.</param>
public sealed record ReplyFeedback(ulong ThreadId, ulong MessageId, ReplyFeedbackKind Kind, string? Reason) : OutboxMessage;
