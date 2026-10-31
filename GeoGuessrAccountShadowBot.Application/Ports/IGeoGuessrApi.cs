// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Domain;
using GeoGuessrAccountShadowBot.Domain.Model;

namespace GeoGuessrAccountShadowBot.Application.Ports;

/// <summary>The session cookie was rejected. Retrying can't help until the token is replaced.</summary>
public sealed class GeoGuessrUnauthorizedException(string message) : Exception(message);

/// <summary>GeoGuessr couldn't be reached or answered with an error. Trying again later may work.</summary>
public sealed class GeoGuessrUnavailableException(string message, Exception innerException) : Exception(message, innerException);

/// <summary>A chat room with one partner, and as many of its messages as the response carried.</summary>
public sealed record ChatRoomSummary(string PeerId, int MessageCount, IReadOnlyList<ChatMessage> Messages);

/// <summary>
/// The GeoGuessr HTTP API. Reading never marks anything as read, so the bot leaves no trace.
/// </summary>
public interface IGeoGuessrApi
{
    Task<string> GetOwnUserIdAsync(CancellationToken cancellationToken = default);

    Task<FriendshipSet> GetFriendshipsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AccountNotification>> GetNotificationsAsync(int page, int count, CancellationToken cancellationToken = default);

    /// <summary>Every chat room of the account. Unread state can't be relied on for this.</summary>
    Task<IReadOnlyList<ChatRoomSummary>> GetChatRoomsAsync(CancellationToken cancellationToken = default);

    /// <summary>The stored messages with one partner, newest first.</summary>
    Task<IReadOnlyList<ChatMessage>> GetChatHistoryAsync(string peerId, CancellationToken cancellationToken = default);

    /// <summary>The player's current name, or null if the player can't be found.</summary>
    Task<string?> GetPlayerNickAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything public about a player, or null if the player can't be found. Only the base profile
    /// is required: a missing club name or rating leaves that part empty instead of failing.
    /// </summary>
    Task<PlayerProfile?> GetPlayerProfileAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>The account's club, or null if it isn't in one.</summary>
    Task<string?> GetOwnClubIdAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ClubMember>> GetClubMembersAsync(string clubId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChatMessage>> GetClubChatAsync(string clubId, CancellationToken cancellationToken = default);

    Task<RankedSnapshot> GetRankedAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Accepts an incoming friend request. Never retried, since it changes the account.</summary>
    Task<Result> AcceptFriendRequestAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Declines an incoming friend request. This stops that person from sending another one, and on
    /// an existing friend it would end the friendship, so callers must confirm the request exists.
    /// </summary>
    Task<Result> DeclineFriendRequestAsync(string userId, CancellationToken cancellationToken = default);
}
