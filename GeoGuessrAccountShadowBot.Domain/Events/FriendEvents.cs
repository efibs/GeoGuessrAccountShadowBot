// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Domain.Model;

namespace GeoGuessrAccountShadowBot.Domain.Events;

/// <summary>
/// A fresh friendship snapshot, which its handler compares with the stored one.
/// </summary>
public sealed record FriendshipsFetched(FriendshipSet Friendships) : ShadowEvent;

/// <summary>
/// Someone sent the account a friend request.
/// </summary>
public sealed record FriendRequestReceived(string UserId) : ShadowEvent
{
    public override string SwitchKey => EventKeys.FriendRequestReceived;
}

/// <summary>
/// Someone accepted a friend request the account sent.
/// </summary>
public sealed record FriendRequestAccepted(string UserId) : ShadowEvent
{
    public override string SwitchKey => EventKeys.FriendRequestAccepted;
}

/// <summary>
/// An incoming friend request is gone, because it was answered from here or from another login.
/// </summary>
public sealed record FriendRequestResolved(string UserId, bool BecameFriend) : ShadowEvent;

/// <summary>
/// A friend came online, went offline, or started doing something else.
/// </summary>
public sealed record FriendPresenceChanged(string FriendId, bool IsOnline, string? Activity) : ShadowEvent
{
    public override string SwitchKey => EventKeys.FriendPresence;
}
