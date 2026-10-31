// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Frozen;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;

namespace GeoGuessrAccountShadowBot.Application.Notifications;

/// <summary>
/// How one notification type is presented.
/// </summary>
/// <param name="SwitchKey">The event switch, or null if the type never notifies by itself.</param>
/// <param name="Style">The look of the card.</param>
/// <param name="Title">The card title.</param>
public sealed record NotificationRoute(string? SwitchKey, CardStyle Style, string Title);

/// <summary>
/// Maps GeoGuessr's notification types to event switches. Unknown types fall into
/// <see cref="EventKeys.AccountOther"/>, so a new type GeoGuessr adds stays visible if wanted.
/// </summary>
public static class NotificationClassifier
{
    private static readonly NotificationRoute Other = new(EventKeys.AccountOther, CardStyle.Info, "New notification");

    private static readonly NotificationRoute Refund = new(EventKeys.RatingRefund, CardStyle.Info, "Rating refund");

    private static readonly NotificationRoute Moderation = new(EventKeys.ModerationAction, CardStyle.Warning, "Moderation action on a report");

    private static readonly FrozenDictionary<NotificationType, NotificationRoute> Routes = new Dictionary<NotificationType, NotificationRoute>
    {
        // The friendship diff is the only producer of these two. The notification just rings the bell.
        [NotificationType.FriendRequest] = new(null, CardStyle.Info, ""),
        [NotificationType.FriendRequestAccepted] = new(null, CardStyle.Info, ""),

        [NotificationType.ChallengeInvite] = new(EventKeys.InviteChallenge, CardStyle.Info, "Challenge invite"),
        [NotificationType.GameInvite] = new(EventKeys.InviteGame, CardStyle.Info, "Game invite"),
        [NotificationType.TeamInvite] = new(EventKeys.InviteTeam, CardStyle.Info, "Team invite"),
        [NotificationType.PartyInvitation] = new(EventKeys.InviteParty, CardStyle.Info, "Party invite"),
        [NotificationType.ClubInvitation] = new(EventKeys.InviteClub, CardStyle.Info, "Club invite"),
        [NotificationType.ClubApplicationAccepted] = new(EventKeys.ClubApplicationAccepted, CardStyle.Success, "Club application accepted"),

        [NotificationType.UnlockedBadge] = new(EventKeys.BadgeUnlocked, CardStyle.Success, "Badge unlocked"),
        [NotificationType.DailyChallengeStreakAtRisk] = new(EventKeys.StreakAtRisk, CardStyle.Warning, "Daily streak at risk"),
        [NotificationType.DailyChallengeStreakSaved] = new(EventKeys.StreakSaved, CardStyle.Success, "Daily streak saved"),
        [NotificationType.ActivityReaction] = new(EventKeys.ActivityReaction, CardStyle.Info, "Someone reacted to an activity"),

        [NotificationType.ReportedCheaterActionTaken] = Moderation,
        [NotificationType.ReportActionTaken] = Moderation,

        [NotificationType.SeasonGameRefund] = Refund,
        [NotificationType.RatingRefund] = Refund,
        [NotificationType.RankedSystemGameRefund] = Refund,
        [NotificationType.TeamRatingRefund] = Refund,
        [NotificationType.RankedTeamGameRefund] = Refund,
        [NotificationType.RankedV2Refund] = Refund,

        [NotificationType.NewUserReferralSubscribed] = Other with { Title = "A referred user subscribed" },
        [NotificationType.EnteredTeamDuelsMatchmaking] = Other with { Title = "Entered team duels matchmaking" },
        [NotificationType.EnteredUnrankedTeamDuelsMatchmaking] = Other with { Title = "Entered unranked team duels matchmaking" },
        [NotificationType.MapCollaborationRequest] = Other with { Title = "Map collaboration request" },
        [NotificationType.MapCollaborationInvite] = Other with { Title = "Map collaboration invite" },
        [NotificationType.FriendStartedDailyChallenge] = Other with { Title = "A friend started the daily challenge" },
        [NotificationType.FriendPlayedDailyChallenge] = Other with { Title = "A friend played the daily challenge" },
        [NotificationType.NewFriendRecommendation] = Other with { Title = "New friend recommendation" }
    }.ToFrozenDictionary();

    public static NotificationRoute Classify(NotificationType type) => Routes.GetValueOrDefault(type, Other);

    /// <summary>Whether a notification means the friendship lists changed.</summary>
    public static bool RingsFriendshipDoorbell(NotificationType type) =>
        type is NotificationType.FriendRequest or NotificationType.FriendRequestAccepted;
}
