// SPDX-License-Identifier: GPL-2.0-or-later

namespace GeoGuessrAccountShadowBot.Domain.Model;

/// <summary>
/// The values of GeoGuessr's notification <c>type</c> field.
/// </summary>
public enum NotificationType
{
    Unknown = 0,
    FriendRequest = 1,
    ChallengeInvite = 2,
    UnlockedBadge = 7,
    GameInvite = 8,
    FriendRequestAccepted = 9,
    TeamInvite = 10,
    SeasonGameRefund = 11,
    ReportedCheaterActionTaken = 12,
    RatingRefund = 13,
    NewUserReferralSubscribed = 14,
    RankedSystemGameRefund = 15,
    EnteredTeamDuelsMatchmaking = 16,
    TeamRatingRefund = 17,
    RankedTeamGameRefund = 18,
    MapCollaborationRequest = 19,
    ClubInvitation = 20,
    PartyInvitation = 21,
    MapCollaborationInvite = 22,
    EnteredUnrankedTeamDuelsMatchmaking = 23,
    ClubApplicationAccepted = 24,
    FriendStartedDailyChallenge = 25,
    ReportActionTaken = 26,
    FriendPlayedDailyChallenge = 27,
    DailyChallengeStreakSaved = 28,
    DailyChallengeStreakAtRisk = 29,
    NewFriendRecommendation = 30,
    RankedV2Refund = 31,
    ActivityReaction = 32
}

/// <summary>
/// One entry of the account's notification list.
/// </summary>
public sealed record AccountNotification(
    string Id,
    NotificationType Type,
    string? SentById,
    string? SentByNick,
    DateTimeOffset SentAt);
