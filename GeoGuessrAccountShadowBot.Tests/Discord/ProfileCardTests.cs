// SPDX-License-Identifier: GPL-2.0-or-later

using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Discord.Rendering;
using GeoGuessrAccountShadowBot.Domain.Model;
using static VerifyXunit.Verifier;

namespace GeoGuessrAccountShadowBot.Tests.Discord;

public sealed class ProfileCardTests
{
    private const string UserId = "bbbbbbbbbbbbbbbbbbbbbbbb";

    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly PlayerStanding GoodStanding = new(false, null, false);

    private static readonly PlayerProfile Full = new(
        UserId,
        "Some Player",
        "de",
        new DateTimeOffset(2025, 2, 20, 0, 0, 0, TimeSpan.Zero),
        28,
        "Unlimited",
        true,
        "pin/0123456789abcdef.png",
        GoodStanding,
        new PlayerClub("00000000-0000-0000-0000-000000000042", "TAG1", 20, "Test Club"),
        new PlayerRanked("Gold II", 932, 1010, [true, true, false, true]));

    private static readonly PlayerProfile Sparse = new(UserId, "Sparse", null, null, null, null, false, null, GoodStanding, null, null);

    [Fact]
    public Task FullProfile() => Verify(CardRenderer.RenderFriendRequest(Full, Now));

    [Fact]
    public Task ProfileWithNothingOptional() => Verify(CardRenderer.RenderFriendRequest(Sparse, Now));

    [Fact]
    public Task HostileProfile_StaysInert()
    {
        var hostile = Full with
        {
            Nick = "@everyone [click](http://evil.example)",
            AccountType = "**fake**",
            Club = new PlayerClub("x) [evil](http://evil.example", "<@1>", 1, "# [free nitro](http://evil.example)"),
            Ranked = new PlayerRanked("<t:0:R>", null, null, [])
        };

        return Verify(CardRenderer.RenderFriendRequest(hostile, Now));
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void EveryProfile_IsAcceptedByDiscordNet(PlayerProfile profile)
    {
        // Building validates lengths and addresses, and an invalid one would lose the whole card.
        var embed = CardRenderer.RenderFriendRequest(profile, Now).ToEmbed();

        embed.Fields.Should().NotBeEmpty();
        embed.Author!.Value.Name.Should().Be("New friend request");
    }

    public static TheoryData<PlayerProfile> Profiles() => new()
    {
        Full,
        Sparse,
        Sparse with { Nick = new string('@', 400) },
        Sparse with { ImagePath = "not a path" }
    };

    [Fact]
    public void Title_LinksToTheProfile()
    {
        CardRenderer.RenderFriendRequest(Full, Now).Url.Should().Be($"https://www.geoguessr.com/user/{UserId}");
    }

    [Fact]
    public void Picture_UsesTheAddressTheSiteUses()
    {
        CardRenderer.RenderFriendRequest(Full, Now).ThumbnailUrl
            .Should().Be("https://www.geoguessr.com/images/resize:fit:192:0/gravity:ce/plain/pin/0123456789abcdef.png");
    }

    [Theory]
    [InlineData("pin/abc.png", true)]
    [InlineData("avatarasseticon/abc.webp", true)]
    [InlineData("custom/a-b_c.jpeg", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("pin/a b.png", false)]
    [InlineData("pin/abc.svg", false)]
    [InlineData("https://evil.example/a.png", false)]
    public void Picture_OnlyAcceptsPathsLikeGeoGuessrs(string? path, bool accepted)
    {
        (ProfileCardRenderer.ImageUrl(path) is not null).Should().Be(accepted);
    }

    [Theory]
    [InlineData("de", "🇩🇪")]
    [InlineData("US", "🇺🇸")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("xyz", null)]
    [InlineData("1a", null)]
    public void Flag_ComesFromTwoLetters(string? code, string? expected)
    {
        ProfileCardRenderer.Flag(code).Should().Be(expected);
    }

    [Fact]
    public void Club_FallsBackToItsTag()
    {
        var profile = Full with { Club = Full.Club! with { Name = null } };

        CardRenderer.RenderFriendRequest(profile, Now).Fields.Single(field => field.Name == "Club").Value.Should().StartWith("[TAG1](");
    }

    [Fact]
    public void PeakRating_StandsInForAMissingRating()
    {
        var profile = Full with { Ranked = new PlayerRanked(null, null, 1010, []) };

        var field = CardRenderer.RenderFriendRequest(profile, Now).Fields.Single(item => item.Name == "Peak rating");

        field.Value.Should().Be("1010");
    }

    [Theory]
    [InlineData(true, 0, false, "Banned")]
    [InlineData(false, 1, false, "Suspended")]
    [InlineData(false, -1, false, "Good standing")]
    [InlineData(false, 0, true, "Chat banned")]
    [InlineData(false, 0, false, "Good standing")]
    public void Standing_NamesTheWorstRestriction(bool banned, int suspendedDays, bool chatBanned, string expected)
    {
        DateTimeOffset? until = suspendedDays == 0 ? null : Now.AddDays(suspendedDays);
        var profile = Full with { Standing = new PlayerStanding(banned, until, chatBanned) };

        CardRenderer.RenderFriendRequest(profile, Now).Fields.Single(field => field.Name == "Standing").Value.Should().Contain(expected);
    }

    [Theory]
    [InlineData(true, "Friend request accepted")]
    [InlineData(false, "Friend request declined")]
    public void ClosingACard_KeepsItsDetails(bool becameFriend, string expected)
    {
        var card = CardRenderer.RenderFriendRequest(Full, Now).ToEmbed();

        var closed = CardRenderer.CloseFriendRequest(card, becameFriend);

        closed.Author!.Value.Name.Should().Be(expected);
        closed.Fields.Should().HaveCount(card.Fields.Length);
        closed.Thumbnail!.Value.Url.Should().Be(card.Thumbnail!.Value.Url);
        closed.Url.Should().Be(card.Url);
    }
}
