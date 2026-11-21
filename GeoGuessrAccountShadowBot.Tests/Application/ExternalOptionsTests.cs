// SPDX-License-Identifier: GPL-2.0-or-later

using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application.Automation;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Sources;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Tests.TestSupport;

namespace GeoGuessrAccountShadowBot.Tests.Application;

public sealed class ExternalOptionsTests
{
    private const string Token = "a-client-token-that-is-long-enough-to-pass";

    private static IReadOnlyList<string> Errors(ShadowOptions options)
    {
        var errors = new List<string>();
        foreach (var result in new[] { new ShadowOptionsValidator().Validate(null, options), new ExternalOptionsValidator().Validate(null, options) })
        {
            errors.AddRange(result.Failures ?? []);
        }

        return errors;
    }

    private static ShadowOptions Headless()
    {
        var options = TestOptions.Valid();
        options.Discord = new DiscordOptions { Enabled = false };
        options.Forum.ChannelId = null;
        options.Control.AuthorizedRoleIds = [];
        return options;
    }

    [Fact]
    public void Headless_NeedsNoDiscordSettings()
    {
        Errors(Headless()).Should().BeEmpty();
    }

    [Fact]
    public void Headless_RefusesWhatOnlyWorksThroughDiscord()
    {
        var options = Headless();
        options.Control.DmReply.Enabled = true;
        options.Control.ClubChat.Enabled = true;
        options.Forum.ChannelId = 3;

        Errors(options).Should().Contain(error => error.StartsWith("Control:DmReply needs Discord"))
            .And.Contain(error => error.StartsWith("Control:ClubChat needs Discord"))
            .And.Contain(error => error.StartsWith("Forum:ChannelId needs Discord"));
    }

    [Fact]
    public void WithDiscord_TheTokenIsStillRequired()
    {
        var options = TestOptions.Valid();
        options.Discord.Token = "";

        Errors(options).Should().ContainSingle().Which.Should().StartWith("Discord:Token is required");
    }

    [Fact]
    public void Api_RefusesWeakOrSharedTokens_AndEventsItCantSend()
    {
        var options = TestOptions.Valid();
        options.ExternalApi.Enabled = true;
        options.ExternalApi.Clients["short"] = new ExternalApiClientOptions { Token = "too-short" };
        options.ExternalApi.Clients["one"] = new ExternalApiClientOptions { Token = Token, Events = ["NoSuchEvent", "Schedule", EventKeys.ClubApplicationReceived] };
        options.ExternalApi.Clients["two"] = new ExternalApiClientOptions { Token = Token, Events = ["custom:vip"] };
        options.ExternalApi.Clients["Bad Name"] = new ExternalApiClientOptions { Token = Token + "x y" };

        var errors = Errors(options);

        errors.Should().Contain("ExternalApi:Clients:short:Token must be at least 32 characters. Generate one with: openssl rand -base64 32")
            .And.Contain(error => error.Contains(":two:Token is the same as the token of one"))
            .And.Contain(error => error.Contains("'NoSuchEvent' isn't an event key"))
            .And.Contain(error => error.Contains("'Schedule' isn't an event key"))
            .And.Contain(error => error.Contains("ClubApplicationReceived is switched off in Events"))
            .And.Contain(error => error.Contains("Bad Name: the name"))
            .And.Contain(error => error.Contains("Bad Name:Token must be printable ASCII"));
        errors.Should().NotContain(error => error.Contains("custom:vip"));
    }

    [Fact]
    public void Api_WithoutClients_IsRefused()
    {
        var options = TestOptions.Valid();
        options.ExternalApi.Enabled = true;

        Errors(options).Should().ContainSingle().Which.Should().Contain("no instance could connect");
    }

    [Theory]
    [InlineData("https://dragon.example.com/", true)]
    [InlineData("https://dragon.example.com/shadow/", true)]
    [InlineData("http://dragon-headless:8080", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://10.0.0.5:8080", true)]
    [InlineData("http://172.20.0.3:8080", true)]
    [InlineData("http://192.168.1.10", true)]
    [InlineData("http://100.101.102.103", true)]
    [InlineData("http://[fd00::1]:8080", true)]
    [InlineData("http://dragon.example.com", false)]
    [InlineData("http://8.8.8.8", false)]
    [InlineData("http://172.32.0.1", false)]
    [InlineData("https://user:pass@dragon.example.com", false)]
    [InlineData("https://dragon.example.com/?token=x", false)]
    [InlineData("ftp://dragon", false)]
    public void SourceUrls_NeedHttps_UnlessTheyStayOnAPrivateNetwork(string url, bool valid)
    {
        var options = TestOptions.Valid();
        options.Sources["dragon"] = new SourceOptions { Url = new Uri(url), Token = Token };

        Errors(options).Should().HaveCount(valid ? 0 : 1);
    }

    [Fact]
    public void Sources_NeedAScriptableName_AKnownType_AndAToken()
    {
        var options = TestOptions.Valid();
        options.Sources["my-source"] = new SourceOptions { Url = new Uri("https://example.com"), Type = "Weather" };

        Errors(options).Should().HaveCount(3)
            .And.Contain(error => error.Contains("sources.my-source"))
            .And.Contain(error => error.Contains("Type must be ShadowBot"))
            .And.Contain(error => error.Contains("Token is required"));
    }
}

public sealed class SourceRuleTests
{
    private static AutomationRule Parse(string json)
    {
        var result = AutomationRuleParser.Parse(json, AutomationRuleOrigin.Configuration);
        result.Errors.Should().BeEmpty();
        return result.Rule!;
    }

    private static IReadOnlyList<string> ParseErrors(string json) => AutomationRuleParser.Parse(json, AutomationRuleOrigin.Configuration).Errors;

    [Theory]
    [InlineData("Source:dragon:ClubApplicationReceived", "Source:dragon:ClubApplicationReceived")]
    [InlineData("source:dragon:clubapplicationreceived", "Source:dragon:ClubApplicationReceived")]
    [InlineData("Source:dragon:Custom:vip", "Source:dragon:Custom:vip")]
    public void SourceTriggers_AreReadIntoTheirKey(string on, string key)
    {
        var rule = Parse($$"""{ "Name": "r", "On": "{{on}}", "Do": [{ "Stop": true }] }""");

        rule.Trigger.Kind.Should().Be(AutomationTriggerKind.Source);
        rule.Trigger.Key.Should().Be(key);
    }

    [Theory]
    [InlineData("Source:dragon")]
    [InlineData("Source:Dragon:ClubApplicationReceived")]
    [InlineData("Source:dragon:Schedule")]
    [InlineData("Source:dragon:Source:other:ClubChat")]
    public void BadSourceTriggers_AreRefused(string on)
    {
        ParseErrors($$"""{ "Name": "r", "On": "{{on}}", "Do": [{ "Stop": true }] }""").Should().ContainSingle().Which.Should().StartWith("On:");
    }

    [Fact]
    public void ForEach_CanNameASourcesGroup()
    {
        var rule = Parse("""{ "Name": "r", "On": "Manual", "Do": [{ "Raise": { "Event": "x", "ForEach": "Source:dragon:ClubApplications" } }] }""");

        rule.Program.OfType<RaiseStep>().Single().Should().Match<RaiseStep>(raise =>
            raise.ForEach == AutomationAudience.ClubApplications && raise.ForEachSource == "dragon");
    }

    [Fact]
    public void ScriptsReadingASource_AreFound_AndDontCountAsReadingThePlayer()
    {
        var rule = Parse("""
            { "Name": "r", "On": "FriendRequestAccepted",
              "When": "sources.dragon.player.has_applied && !player.is_friend",
              "Do": [{ "SendDm": "{{ sources.other.account.club_id }} {{ sources.dragon.player.level }}" }] }
            """);

        rule.ReadsPlayer.Should().BeFalse();
        rule.SourceReads.Should().BeEquivalentTo([new SourceRead("dragon", "player"), new SourceRead("other", "account")]);
    }

    [Fact]
    public void Checks_FindUnknownSources_AndDiscordStepsWhenHeadless()
    {
        var options = TestOptions.Valid();
        options.Discord.Enabled = false;
        options.Sources["dragon"] = new SourceOptions();

        var rule = Parse("""
            { "Name": "r", "On": "Source:nope:ClubApplicationReceived",
              "When": "sources.dragon.weather.rain || sources.missing.player.is_friend",
              "Do": [{ "PostDiscord": "hi" }, { "Raise": { "Event": "x", "ForEach": "Source:gone:Friends" } }] }
            """);

        AutomationRuleChecks.Check(rule, options).Should().BeEquivalentTo(
        [
            "On: there is no source named nope in Sources.",
            "A script reads sources.dragon.weather, and a shadow bot source only has player and account.",
            "A script reads sources.missing, and there is no source named missing in Sources.",
            "Do[1].ForEach: there is no source named gone in Sources.",
            "Do[0]: this step needs Discord, and Discord:Enabled is false."
        ]);
    }
}
