// SPDX-License-Identifier: GPL-2.0-or-later

namespace GeoGuessrAccountShadowBot.Domain.Model;

/// <summary>
/// A player's club. The name takes a second lookup, so it can be missing while the tag is known.
/// </summary>
public sealed record PlayerClub(string ClubId, string Tag, int Level, string? Name);

/// <summary>
/// Anything GeoGuessr has restricted on an account.
/// </summary>
public sealed record PlayerStanding(bool IsBanned, DateTimeOffset? SuspendedUntil, bool IsChatBanned);

/// <summary>
/// A player's competitive numbers, which an unranked player doesn't have.
/// </summary>
public sealed record PlayerRanked(string? Division, int? Rating, int? PeakRating, IReadOnlyList<bool> RecentGames);

/// <summary>
/// What's publicly known about a player, for judging who sent a friend request.
/// </summary>
public sealed record PlayerProfile(
    string UserId,
    string Nick,
    string? CountryCode,
    DateTimeOffset? CreatedAt,
    int? Level,
    string? AccountType,
    bool IsPro,
    string? ImagePath,
    PlayerStanding Standing,
    PlayerClub? Club,
    PlayerRanked? Ranked);
