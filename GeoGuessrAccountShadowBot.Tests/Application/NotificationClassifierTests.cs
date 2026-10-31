// SPDX-License-Identifier: GPL-2.0-or-later

using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;

namespace GeoGuessrAccountShadowBot.Tests.Application;

public sealed class NotificationClassifierTests
{
    public static TheoryData<NotificationType, string> ExpectedSwitches => new()
    {
        { NotificationType.ChallengeInvite, EventKeys.InviteChallenge },
        { NotificationType.GameInvite, EventKeys.InviteGame },
        { NotificationType.TeamInvite, EventKeys.InviteTeam },
        { NotificationType.PartyInvitation, EventKeys.InviteParty },
        { NotificationType.ClubInvitation, EventKeys.InviteClub },
        { NotificationType.ClubApplicationAccepted, EventKeys.ClubApplicationAccepted },
        { NotificationType.UnlockedBadge, EventKeys.BadgeUnlocked },
        { NotificationType.DailyChallengeStreakAtRisk, EventKeys.StreakAtRisk },
        { NotificationType.DailyChallengeStreakSaved, EventKeys.StreakSaved },
        { NotificationType.ActivityReaction, EventKeys.ActivityReaction },
        { NotificationType.ReportActionTaken, EventKeys.ModerationAction },
        { NotificationType.ReportedCheaterActionTaken, EventKeys.ModerationAction },
        { NotificationType.RatingRefund, EventKeys.RatingRefund },
        { NotificationType.SeasonGameRefund, EventKeys.RatingRefund },
        { NotificationType.RankedSystemGameRefund, EventKeys.RatingRefund },
        { NotificationType.TeamRatingRefund, EventKeys.RatingRefund },
        { NotificationType.RankedTeamGameRefund, EventKeys.RatingRefund },
        { NotificationType.RankedV2Refund, EventKeys.RatingRefund }
    };

    [Theory]
    [MemberData(nameof(ExpectedSwitches))]
    public void Classify_UsesTheRightSwitch(NotificationType type, string expected)
    {
        NotificationClassifier.Classify(type).SwitchKey.Should().Be(expected);
    }

    [Theory]
    [InlineData(NotificationType.FriendRequest)]
    [InlineData(NotificationType.FriendRequestAccepted)]
    public void FriendNotifications_NeverNotifyThemselves(NotificationType type)
    {
        NotificationClassifier.Classify(type).SwitchKey.Should().BeNull();
        NotificationClassifier.RingsFriendshipDoorbell(type).Should().BeTrue();
    }

    [Fact]
    public void EveryKnownType_HasATitleOrIsADoorbell()
    {
        foreach (var type in Enum.GetValues<NotificationType>().Where(type => type != NotificationType.Unknown))
        {
            var route = NotificationClassifier.Classify(type);
            (route.SwitchKey is null || route.Title.Length > 0).Should().BeTrue(type.ToString());
        }
    }

    [Fact]
    public void TypesGeoGuessrAddsLater_LandInTheOtherSwitch()
    {
        NotificationClassifier.Classify((NotificationType)999).SwitchKey.Should().Be(EventKeys.AccountOther);
    }
}
