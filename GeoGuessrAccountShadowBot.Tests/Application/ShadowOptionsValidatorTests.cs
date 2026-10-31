// SPDX-License-Identifier: GPL-2.0-or-later

using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Tests.TestSupport;

namespace GeoGuessrAccountShadowBot.Tests.Application;

public sealed class ShadowOptionsValidatorTests
{
    private static IEnumerable<string> Validate(ShadowOptions options) =>
        new ShadowOptionsValidator().Validate(null, options).Failures ?? [];

    [Fact]
    public void Validate_AcceptsTheShippedDefaultsOnceSecretsAndChannelsAreSet()
    {
        Validate(TestOptions.Valid()).Should().BeEmpty();
    }

    [Fact]
    public void Validate_ReportsEveryProblemAtOnce()
    {
        var options = TestOptions.Valid();
        options.GeoGuessr.NcfaToken = "";
        options.Discord.Token = "";
        options.Discord.GuildId = 0;

        Validate(options).Should().HaveCount(3);
    }

    [Theory]
    [InlineData("_ncfa=abc")]
    [InlineData("abc; other=1")]
    [InlineData("abc def")]
    [InlineData("abc\ndef")]
    public void Validate_RefusesTokensThatCouldBreakTheCookieHeader(string token)
    {
        var options = TestOptions.Valid();
        options.GeoGuessr.NcfaToken = token;

        Validate(options).Should().ContainSingle().Which.Should().Contain("bare cookie value");
    }

    [Theory]
    [InlineData("http://www.geoguessr.com")]
    [InlineData("https://geoguessr.com.evil.example")]
    [InlineData("https://evil.example")]
    public void Validate_RefusesApiHostsTheCookieMustNotReach(string url)
    {
        var options = TestOptions.Valid();
        options.GeoGuessr.ApiBaseUrl = new Uri(url);

        Validate(options).Should().ContainSingle().Which.Should().Contain("ApiBaseUrl");
    }

    [Fact]
    public void Validate_RefusesAnInsecureSocketUrl()
    {
        var options = TestOptions.Valid();
        options.GeoGuessr.SocketUrl = new Uri("ws://api.geoguessr.com/ws");

        Validate(options).Should().ContainSingle().Which.Should().Contain("SocketUrl");
    }

    [Fact]
    public void Validate_RefusesUnknownAndMissingEvents()
    {
        var options = TestOptions.Valid();
        options.Events["NotAnEvent"] = new EventOptions();
        options.Events.Remove(EventKeys.BadgeUnlocked);

        Validate(options).Should().HaveCount(2);
    }

    [Fact]
    public void Validate_RequiresAChannelForEnabledEvents()
    {
        var options = TestOptions.Valid();
        options.Discord.DefaultChannelId = 0;
        foreach (var settings in options.Events.Values)
        {
            settings.Enabled = false;
        }

        options.Events[EventKeys.BadgeUnlocked].Enabled = true;

        Validate(options).Should().ContainSingle().Which.Should().Contain(EventKeys.BadgeUnlocked);
    }

    [Fact]
    public void Validate_RequiresTheForumForDirectMessages()
    {
        var options = TestOptions.Valid();
        options.Forum.ChannelId = null;

        Validate(options).Should().ContainSingle().Which.Should().Contain("Forum:ChannelId");
    }

    [Fact]
    public void Validate_RequiresAcknowledgedIntentForReplies()
    {
        var options = TestOptions.Valid();
        options.Control.DmReply.Enabled = true;

        Validate(options).Should().ContainSingle().Which.Should().Contain("MessageContentIntent");
    }

    [Fact]
    public void Validate_RequiresTheSocketForReplies()
    {
        var options = TestOptions.Valid();
        options.Control.DmReply.Enabled = true;
        options.Discord.MessageContentIntent = true;
        options.GeoGuessr.Socket.Enabled = false;

        Validate(options).Should().ContainSingle().Which.Should().Contain("Socket:Enabled");
    }

    [Fact]
    public void Validate_RequiresRolesForControlFeatures()
    {
        var options = TestOptions.Valid();
        options.Control.AuthorizedRoleIds = [];
        options.Control.FriendRequestButtons.Enabled = true;

        Validate(options).Should().ContainSingle().Which.Should().Contain("AuthorizedRoleIds");
    }

    [Fact]
    public void Validate_RefusesEveryoneAsAnAuthorizedRole()
    {
        var options = TestOptions.Valid();
        options.Control.AuthorizedRoleIds = [options.Discord.GuildId];

        Validate(options).Should().ContainSingle().Which.Should().Contain("@everyone");
    }

    [Fact]
    public void Validate_RequiresTheFriendRequestEventForButtons()
    {
        var options = TestOptions.Valid();
        options.Control.FriendRequestButtons.Enabled = true;
        options.Events[EventKeys.FriendRequestReceived].Enabled = false;

        Validate(options).Should().ContainSingle().Which.Should().Contain("FriendRequestButtons");
    }

    [Theory]
    [InlineData(EventKeys.ClubChat)]
    [InlineData(EventKeys.ClubMemberJoined)]
    [InlineData(EventKeys.ClubMissionBoard)]
    public void Validate_RequiresTheClubForClubEvents(string key)
    {
        var options = TestOptions.Valid();
        options.Events[key].Enabled = true;

        Validate(options).Should().ContainSingle().Which.Should().Contain("Club:Enabled");
    }

    [Fact]
    public void Validate_RefusesPollingFastEnoughToBeRude()
    {
        var options = TestOptions.Valid();
        options.Polling.Interval = TimeSpan.FromSeconds(5);
        options.Polling.RankedInterval = TimeSpan.FromMinutes(1);

        Validate(options).Should().HaveCount(2);
    }

    [Fact]
    public void Validate_RefusesAYieldThatStartsAboveItsMaximum()
    {
        var options = TestOptions.Valid();
        options.GeoGuessr.Yield.Initial = TimeSpan.FromHours(2);

        Validate(options).Should().ContainSingle().Which.Should().Contain("Yield");
    }
}
