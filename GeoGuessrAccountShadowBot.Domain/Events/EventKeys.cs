// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Frozen;

namespace GeoGuessrAccountShadowBot.Domain.Events;

/// <summary>
/// The configuration key of every event that can be switched on or off.
/// </summary>
public static class EventKeys
{
    public const string DmMessage = nameof(DmMessage);
    public const string FriendRequestReceived = nameof(FriendRequestReceived);
    public const string FriendRequestAccepted = nameof(FriendRequestAccepted);
    public const string FriendPresence = nameof(FriendPresence);

    public const string InviteGame = nameof(InviteGame);
    public const string InviteChallenge = nameof(InviteChallenge);
    public const string InviteTeam = nameof(InviteTeam);
    public const string InviteParty = nameof(InviteParty);
    public const string InviteClub = nameof(InviteClub);
    public const string ClubApplicationAccepted = nameof(ClubApplicationAccepted);

    public const string BadgeUnlocked = nameof(BadgeUnlocked);
    public const string StreakAtRisk = nameof(StreakAtRisk);
    public const string StreakSaved = nameof(StreakSaved);
    public const string ModerationAction = nameof(ModerationAction);
    public const string RatingRefund = nameof(RatingRefund);
    public const string ActivityReaction = nameof(ActivityReaction);
    public const string AccountOther = nameof(AccountOther);

    public const string ClubChat = nameof(ClubChat);
    public const string ClubMemberJoined = nameof(ClubMemberJoined);
    public const string ClubMemberLeft = nameof(ClubMemberLeft);
    public const string ClubRoleChanged = nameof(ClubRoleChanged);
    public const string ClubMissionBoard = nameof(ClubMissionBoard);

    public const string RankedDivisionChanged = nameof(RankedDivisionChanged);
    public const string SeasonChanged = nameof(SeasonChanged);

    /// <summary>Every key, used to validate configuration against the code.</summary>
    public static readonly FrozenSet<string> All = FrozenSet.ToFrozenSet(
    [
        DmMessage, FriendRequestReceived, FriendRequestAccepted, FriendPresence,
        InviteGame, InviteChallenge, InviteTeam, InviteParty, InviteClub, ClubApplicationAccepted,
        BadgeUnlocked, StreakAtRisk, StreakSaved, ModerationAction, RatingRefund, ActivityReaction, AccountOther,
        ClubChat, ClubMemberJoined, ClubMemberLeft, ClubRoleChanged, ClubMissionBoard,
        RankedDivisionChanged, SeasonChanged
    ]);
}
