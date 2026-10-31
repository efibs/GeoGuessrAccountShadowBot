// SPDX-License-Identifier: GPL-2.0-or-later

namespace GeoGuessrAccountShadowBot.Domain.Model;

/// <summary>
/// The kinds of chat payload GeoGuessr sends. Values the bot doesn't know yet decode to <see cref="Unknown"/>.
/// </summary>
public enum ChatPayloadType
{
    Unknown,
    Text,
    Emote,
    Invite,
    Club,
    Disconnect,
    Reaction,
    Divider,
    Poll,
    HighlightComment
}

/// <summary>
/// One chat message, in the shape shared by the socket and the HTTP history. The <see cref="Id"/> is
/// the same in both, which is what lets the two sources be deduplicated against each other. It's
/// unique within a room rather than globally, so it's always combined with <see cref="RoomId"/>.
/// </summary>
public sealed record ChatMessage(
    string Id,
    ChatPayloadType PayloadType,
    string? Text,
    string SourceId,
    string RecipientId,
    bool IsSystemMessage,
    DateTimeOffset SentAt,
    string RoomId)
{
    /// <summary>The other participant of a direct message, from the point of view of the given account.</summary>
    public string PeerOf(string ownUserId) => SourceId == ownUserId ? RecipientId : SourceId;

    public bool IsSentBy(string userId) => SourceId == userId;
}
