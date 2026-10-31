// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Domain.Model;

namespace GeoGuessrAccountShadowBot.Domain.Events;

/// <summary>
/// A direct message in or out of the account, seen on the socket or during catch-up.
/// </summary>
public sealed record DirectMessageReceived(ChatMessage Message) : ShadowEvent
{
    public override string DedupeKey => $"chat:{Message.RoomId}:{Message.Id}";

    public override string SwitchKey => EventKeys.DmMessage;
}

/// <summary>
/// A message in the account's club chat.
/// </summary>
public sealed record ClubChatMessageReceived(string ClubId, ChatMessage Message) : ShadowEvent
{
    public override string DedupeKey => $"clubchat:{ClubId}:{Message.Id}";

    public override string SwitchKey => EventKeys.ClubChat;
}
