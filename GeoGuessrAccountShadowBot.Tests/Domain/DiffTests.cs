// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;
using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;

namespace GeoGuessrAccountShadowBot.Tests.Domain;

public sealed class FriendshipSetTests
{
    private static FriendshipSet Set(string[]? friends = null, string[]? incoming = null, string[]? outgoing = null) => new(
        (friends ?? []).ToImmutableSortedSet(),
        (incoming ?? []).ToImmutableSortedSet(),
        (outgoing ?? []).ToImmutableSortedSet());

    [Fact]
    public void DiffFrom_FindsNewIncomingRequest()
    {
        var diff = Set(incoming: ["new"]).DiffFrom(Set());

        diff.NewIncoming.Should().Equal("new");
        diff.Accepted.Should().BeEmpty();
    }

    [Fact]
    public void DiffFrom_FindsAcceptedOutgoingRequest()
    {
        var diff = Set(friends: ["them"]).DiffFrom(Set(outgoing: ["them"]));

        diff.Accepted.Should().Equal("them");
    }

    [Fact]
    public void DiffFrom_TreatsUnseenNewFriendAsAccepted()
    {
        Set(friends: ["them"]).DiffFrom(Set()).Accepted.Should().Equal("them");
    }

    [Fact]
    public void DiffFrom_DoesNotTreatAnsweredIncomingRequestAsAcceptedOutgoing()
    {
        var diff = Set(friends: ["them"]).DiffFrom(Set(incoming: ["them"]));

        diff.Accepted.Should().BeEmpty();
        diff.ResolvedIncoming.Should().Equal("them");
    }

    [Fact]
    public void DiffFrom_ReportsDeclinedIncomingRequestAsResolved()
    {
        Set().DiffFrom(Set(incoming: ["them"])).ResolvedIncoming.Should().Equal("them");
    }

    [Theory]
    [InlineData(10, 0, true)]
    [InlineData(10, 4, true)]
    [InlineData(10, 5, false)]
    [InlineData(10, 9, false)]
    [InlineData(4, 0, false)]
    public void LooksLikeGlitch_DependsOnHowManyFriendsVanished(int before, int after, bool expected)
    {
        var previous = Set(friends: Enumerable.Range(0, before).Select(i => $"f{i}").ToArray());
        var current = Set(friends: Enumerable.Range(0, after).Select(i => $"f{i}").ToArray());

        current.LooksLikeGlitchComparedTo(previous).Should().Be(expected);
    }
}

public sealed class ClubRosterTests
{
    [Fact]
    public void DiffFrom_FindsJoinsLeavesAndRoleChanges()
    {
        var previous = ClubRoster.From([new("stays", "a", 1), new("leaves", "b", 1), new("promoted", "c", 1)]);
        var current = ClubRoster.From([new("stays", "a", 1), new("promoted", "c", 2), new("joins", "d", 1)]);

        var diff = current.DiffFrom(previous);

        diff.Joined.Select(member => member.UserId).Should().Equal("joins");
        diff.Left.Select(member => member.UserId).Should().Equal("leaves");
        diff.RoleChanged.Select(member => member.UserId).Should().Equal("promoted");
    }

    [Fact]
    public void LooksLikeGlitch_WhenRosterEmptied()
    {
        var previous = ClubRoster.From(Enumerable.Range(0, 10).Select(i => new ClubMember($"m{i}", "n", 1)));

        ClubRoster.From([]).LooksLikeGlitchComparedTo(previous).Should().BeTrue();
    }
}

public sealed class RankedSnapshotTests
{
    [Fact]
    public void DivisionChangedFrom_ComparesDivisionNumbers()
    {
        var gold = new RankedSnapshot(2, "Gold", 900, "s1");

        (gold with { Rating = 950 }).DivisionChangedFrom(gold).Should().BeFalse();
        (gold with { DivisionNumber = 3 }).DivisionChangedFrom(gold).Should().BeTrue();
    }

    [Fact]
    public void SeasonChangedFrom_IgnoresAFailedSeasonRead()
    {
        var season = new RankedSnapshot(null, null, null, "s1");

        (season with { SeasonId = null }).SeasonChangedFrom(season).Should().BeFalse();
        (season with { SeasonId = "s2" }).SeasonChangedFrom(season).Should().BeTrue();
    }
}

public sealed class EventKeyTests
{
    [Fact]
    public void ClubMemberChanged_UsesOneSwitchPerKindOfChange()
    {
        var member = new ClubMember("id", "nick", 1);

        new ClubMemberChanged(member, ClubMemberChange.Joined).SwitchKey.Should().Be(EventKeys.ClubMemberJoined);
        new ClubMemberChanged(member, ClubMemberChange.Left).SwitchKey.Should().Be(EventKeys.ClubMemberLeft);
        new ClubMemberChanged(member, ClubMemberChange.RoleChanged).SwitchKey.Should().Be(EventKeys.ClubRoleChanged);
    }
}
