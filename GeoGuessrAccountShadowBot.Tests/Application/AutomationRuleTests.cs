// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application.Automation;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Discord.Rendering;
using Microsoft.Extensions.Configuration;
using Scriban.Runtime;

namespace GeoGuessrAccountShadowBot.Tests.Application;

public sealed class AutomationRuleParserTests
{
    private static AutomationRule Parse(string json)
    {
        var result = AutomationRuleParser.Parse(json, AutomationRuleOrigin.Discord);
        result.Errors.Should().BeEmpty();
        return result.Rule!;
    }

    private static IReadOnlyList<string> Errors(string json) => AutomationRuleParser.Parse(json, AutomationRuleOrigin.Discord).Errors;

    [Fact]
    public void FullRule_IsRead()
    {
        var rule = Parse("""
            {
              // Comments and trailing commas are fine.
              "Name": "welcome",
              "Description": "Greets new friends",
              "On": "FriendRequestAccepted",
              "When": "player.level >= 20",
              "MaxAge": "1d",
              "OncePerPlayer": true,
              "PlayerCooldown": "2h",
              "Cooldown": "00:00:30",
              "MaxRunsPerHour": 10,
              "Priority": 5,
              "StopOtherRules": true,
              "Report": "All",
              "ReportChannelId": "123456789012345678",
              "Do": [
                { "Wait": "30s" },
                { "SendDm": "Hi {{ player.nick }}" },
                { "PostDiscord": { "Text": "Greeted", "PingRoleIds": [42], "Style": "Success" } },
              ]
            }
            """);

        rule.Name.Should().Be("welcome");
        rule.Trigger.Should().Be(new AutomationTriggerSpec(AutomationTriggerKind.Event, "FriendRequestAccepted", null, TimeZoneInfo.Utc));
        rule.MaxAge.Should().Be(TimeSpan.FromDays(1));
        rule.Limits.Should().Be(new AutomationLimits(true, null, TimeSpan.FromHours(2), TimeSpan.FromSeconds(30), 10));
        rule.Report.Should().Be(AutomationReportMode.All);
        rule.ReportChannelId.Should().Be(123456789012345678);
        rule.Priority.Should().Be(5);
        rule.StopOtherRules.Should().BeTrue();
        rule.ReadsPlayer.Should().BeTrue();
        rule.Program.Select(step => step.GetType()).Should().Equal(typeof(WaitStep), typeof(SendDmStep), typeof(PostDiscordStep));
        rule.Program[2].Should().BeOfType<PostDiscordStep>().Which.PingRoleIds.Should().Equal(42UL);
    }

    [Fact]
    public void Defaults_AreSafe()
    {
        var rule = Parse("""{ "Name": "r", "On": "DmMessage", "Do": [{ "SendDm": "x" }] }""");

        rule.Enabled.Should().BeTrue();
        rule.DryRun.Should().BeFalse();
        rule.MaxAge.Should().Be(AutomationRuleParser.DefaultMaxAge);
        rule.Report.Should().Be(AutomationReportMode.Failures);
        rule.Limits.Should().Be(AutomationLimits.None);
    }

    [Fact]
    public void IfElse_CompilesToForwardJumps()
    {
        var rule = Parse("""
            { "Name": "r", "On": "DmMessage", "Do": [
              { "If": "event.text == 'hi'", "Then": [{ "SendDm": "a" }], "Else": [{ "SendDm": "b" }, { "Stop": true }] },
              { "SendDm": "c" }
            ] }
            """);

        rule.Program.Should().HaveCount(6);
        rule.Program[0].Should().BeOfType<JumpUnlessStep>().Which.Target.Should().Be(3);
        rule.Program[1].Path.Should().Be("Do[0].Then[0]");
        rule.Program[2].Should().BeOfType<JumpStep>().Which.Target.Should().Be(5);
        rule.Program[3].Path.Should().Be("Do[0].Else[0]");
        rule.Program[4].Should().BeOfType<StopStep>();
        rule.Program[5].Path.Should().Be("Do[1]");
    }

    [Fact]
    public void Shorthands_AndObjects_MeanTheSame()
    {
        var rule = Parse("""
            { "Name": "r", "On": "Manual", "Do": [
              { "AcceptFriendRequest": true },
              { "DeclineClubApplication": "{{ event.who }}" },
              { "SendFriendRequest": { "Player": "{{ event.who }}" } },
              { "RequireApproval": "Really?" },
              { "Raise": "next" },
              { "Raise": { "Event": "vip", "ForEach": "friends", "Data": { "reason": "{{ rule.name }}" } } }
            ] }
            """);

        rule.Program[0].Should().Be(new PlayerActionStep("Do[0]", PlayerActionKind.AcceptFriendRequest, null));
        rule.Program[1].Should().BeOfType<PlayerActionStep>().Which.Player!.Source.Should().Be("{{ event.who }}");
        rule.Program[2].Should().BeOfType<PlayerActionStep>().Which.Kind.Should().Be(PlayerActionKind.SendFriendRequest);
        rule.Program[3].Should().BeOfType<ApprovalStep>().Which.Timeout.Should().Be(AutomationRuleParser.DefaultApprovalTimeout);
        rule.Program[4].Should().BeOfType<RaiseStep>().Which.EventName.Should().Be("next");
        var raise = rule.Program[5].Should().BeOfType<RaiseStep>().Subject;
        raise.ForEach.Should().Be(AutomationAudience.Friends);
        raise.Data.Keys.Should().Equal("reason");
    }

    [Fact]
    public void Triggers_CoverSchedulesManualAndCustomEvents()
    {
        var schedule = Parse("""{ "Name": "r", "On": "Schedule", "Cron": "0 18 * * 1", "TimeZone": "Europe/Berlin", "Do": [{ "Stop": true }] }""");
        schedule.Trigger.Kind.Should().Be(AutomationTriggerKind.Schedule);
        schedule.Trigger.TimeZone.Id.Should().Be("Europe/Berlin");
        schedule.Trigger.Key.Should().Be("Schedule");

        Parse("""{ "Name": "r", "On": "manual", "Do": [{ "Stop": true }] }""").Trigger.Key.Should().Be("Manual");
        Parse("""{ "Name": "r", "On": "Custom:vip-joined", "Do": [{ "Stop": true }] }""").Trigger.Key.Should().Be("Custom:vip-joined");
        Parse("""{ "Name": "r", "On": "badgeunlocked", "Do": [{ "Stop": true }] }""").Trigger.Key.Should().Be("BadgeUnlocked");
    }

    [Fact]
    public void EveryProblem_IsReportedWithItsPath()
    {
        var errors = Errors("""
            { "Name": "Bad Name", "On": "Sometimes", "When": "player.level >=", "Colour": "red", "Do": [
              { "SendDm": "" },
              { "Teleport": "home" },
              { "Wait": "30 years" },
              { "If": "true" }
            ] }
            """);

        errors.Should().Contain(error => error.StartsWith("Name:", StringComparison.Ordinal));
        errors.Should().Contain(error => error.StartsWith("On: 'Sometimes' isn't a trigger", StringComparison.Ordinal));
        errors.Should().Contain(error => error.StartsWith("When: doesn't parse", StringComparison.Ordinal));
        errors.Should().Contain(error => error.Contains("'Colour' isn't a known property", StringComparison.Ordinal));
        errors.Should().Contain(error => error.StartsWith("Do[0].SendDm: needs text", StringComparison.Ordinal));
        errors.Should().Contain(error => error.StartsWith("Do[1].Teleport: 'Teleport' isn't a step", StringComparison.Ordinal));
        errors.Should().Contain(error => error.StartsWith("Do[2].Wait:", StringComparison.Ordinal));
        errors.Should().Contain(error => error.StartsWith("Do[3].Then: is required", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "Name": "r", "On": "Schedule", "Do": [{ "Stop": true }] }""", "Cron: is required")]
    [InlineData("""{ "Name": "r", "On": "Schedule", "Cron": "every day", "Do": [{ "Stop": true }] }""", "Cron: isn't a valid")]
    [InlineData("""{ "Name": "r", "On": "DmMessage", "Cron": "* * * * *", "Do": [{ "Stop": true }] }""", "Cron: only applies")]
    [InlineData("""{ "Name": "r", "On": "DmMessage", "TimeZone": "Mars/Olympus", "Do": [{ "Stop": true }] }""", "TimeZone:")]
    [InlineData("""{ "Name": "r", "On": "DmMessage", "Do": [] }""", "Do: must have at least one step")]
    [InlineData("""{ "Name": "r", "On": "DmMessage" }""", "Do: is required")]
    [InlineData("""{ "Name": "r", "On": "DmMessage", "Do": [{ "Raise": { "Event": "x", "ForEach": "Friends", "Player": "y" } }] }""", "can't have both")]
    [InlineData("""[1, 2]""", "must be a JSON object")]
    [InlineData("""{ "Name": """, "isn't valid JSON")]
    public void InvalidRules_AreRefused(string json, string expected)
    {
        Errors(json).Should().Contain(error => error.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void TooManySteps_AreRefused()
    {
        var steps = string.Join(",", Enumerable.Repeat("""{ "Stop": true }""", AutomationRuleParser.MaxSteps + 1));

        Errors($$"""{ "Name": "r", "On": "DmMessage", "Do": [{{steps}}] }""").Should().ContainSingle().Which.Should().Contain("at most");
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("5m", 300)]
    [InlineData("1.5h", 5400)]
    [InlineData("2d", 172800)]
    [InlineData("00:10:00", 600)]
    [InlineData("1.00:00:00", 86400)]
    public void Durations_TakeShortAndLongForms(string text, int seconds)
    {
        AutomationRuleParser.TryParseDuration(text, out var duration).Should().BeTrue();
        duration.Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void RulesFromTheConfiguration_ReadLikeJson()
    {
        // The configuration turns every value into a string and every array into numbered keys.
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""
                { "Control": { "Automation": { "Rules": [
                  { "Name": "first", "On": "DmMessage", "OncePerPlayer": true, "MaxRunsPerHour": 3,
                    "Do": [{ "SendDm": "a" }, { "PostDiscord": { "Text": "b", "PingRoleIds": [7, 8] } }, { "AcceptFriendRequest": {} }] },
                  { "Name": "second", "On": "Manual", "Do": { "Stop": true } }
                ] } } }
                """)))
            .Build();

        var configured = new ConfiguredAutomationRules(configuration);

        configured.Errors.Should().BeEmpty();
        configured.Rules.Select(rule => rule.Name).Should().Equal("first", "second");
        var first = configured.Rules[0];
        first.Origin.Should().Be(AutomationRuleOrigin.Configuration);
        first.Limits.OncePerPlayer.Should().BeTrue();
        first.Limits.MaxRunsPerHour.Should().Be(3);
        first.Program[1].Should().BeOfType<PostDiscordStep>().Which.PingRoleIds.Should().Equal(7UL, 8UL);
        first.Program[2].Should().Be(new PlayerActionStep("Do[2]", PlayerActionKind.AcceptFriendRequest, null));
        configured.Rules[1].Program.Should().ContainSingle().Which.Should().BeOfType<StopStep>();
    }

    [Fact]
    public void DuplicateConfigNames_AreReported()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Control:Automation:Rules:0:Name"] = "same",
                ["Control:Automation:Rules:0:On"] = "Manual",
                ["Control:Automation:Rules:0:Do:0:Stop"] = "true",
                ["Control:Automation:Rules:1:Name"] = "same",
                ["Control:Automation:Rules:1:On"] = "Manual",
                ["Control:Automation:Rules:1:Do:0:Stop"] = "true"
            })
            .Build();

        new ConfiguredAutomationRules(configuration).Errors.Should().ContainSingle().Which.Should().Contain("used twice");
    }
}

public sealed class AutomationScriptTests
{
    private static AutomationScope Scope(object? player = null, object? data = null) => new(new ScriptObject
    {
        ["player"] = player,
        ["event"] = data
    });

    private static AutomationScript Expression(string source) => AutomationScript.ParseExpression(source, out _)!;

    private static AutomationScript Template(string source) => AutomationScript.ParseTemplate(source, out _)!;

    [Theory]
    [InlineData("player.level >= 20", true)]
    [InlineData("player.level < 20", false)]
    [InlineData("player.country == 'DE' && player.is_pro", true)]
    [InlineData("player.club == null", true)]
    [InlineData("player.missing", false)]
    [InlineData("event.text | string.downcase | string.contains 'discord'", true)]
    [InlineData("event.text | regex.match `\\d+` | array.size > 0", true)]
    public void Conditions_ReadTheVariables(string condition, bool expected)
    {
        var scope = Scope(
            new ScriptObject { ["level"] = 42, ["country"] = "DE", ["is_pro"] = true, ["club"] = null },
            new ScriptObject { ["text"] = "Join our DISCORD at 123" });

        scope.Evaluate(Expression(condition)).Should().Be(expected);
    }

    [Fact]
    public void Templates_RenderPlainForGeoGuessr()
    {
        var scope = Scope(new ScriptObject { ["nick"] = "*bold*" });

        scope.RenderPlain(Template("Hi {{ player.nick }}, {{ 1 + 1 }} times!")).Should().Be("Hi *bold*, 2 times!");
    }

    [Fact]
    public void MarkedTemplates_KeepTheAuthorsMarkdown_AndEscapeTheValues()
    {
        var scope = Scope(new ScriptObject { ["nick"] = "@everyone **x** [link](https://evil)" });

        var discord = AutomationRenderer.ToDiscord(scope.RenderMarked(Template("**Welcome** {{ player.nick }}")));

        discord.Should().Be(@"**Welcome** \@everyone \*\*x\*\* \[link\]\(https\:\/\/evil\)");
    }

    [Fact]
    public void Markers_CantBeSmuggledIn()
    {
        var hostile = $"{AutomationMarkup.Close}**not escaped?**{AutomationMarkup.Open}";
        var scope = Scope(new ScriptObject { ["nick"] = hostile });

        var discord = AutomationRenderer.ToDiscord(scope.RenderMarked(Template("{{ player.nick }}")));

        discord.Should().Be(@"\*\*not escaped?\*\*");
    }

    [Fact]
    public void RunawayLoops_AreStopped()
    {
        var act = () => Scope().RenderPlain(Template("{{ for i in 1..100000 }}x{{ end }}"));

        act.Should().Throw<AutomationScriptException>();
    }

    [Fact]
    public void Includes_AreUnavailable()
    {
        var act = () => Scope().RenderPlain(Template("{{ include 'appsettings.json' }}"));

        act.Should().Throw<AutomationScriptException>();
    }

    [Theory]
    [InlineData("Hi {{ player.nick }}", false)]
    [InlineData("{{ if player.is_friend }}x{{ end }}", false)]
    [InlineData("{{ player.level }}", true)]
    [InlineData("{{ player.club.tag }}", true)]
    [InlineData("{{ player | object.keys }}", true)]
    [InlineData("{{ event.text }}", false)]
    public void OnlyProfileFields_CostALookup(string source, bool expected)
    {
        Template(source).ReadsPlayer.Should().Be(expected);
    }

    [Fact]
    public void ParseErrors_AreReported()
    {
        AutomationScript.ParseTemplate("{{ if }}", out var error).Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }
}

public sealed class AutomationRenderingTests
{
    [Fact]
    public void UnmarkedText_IsTheAuthors()
    {
        AutomationRenderer.ToDiscord("**Hello** <@&1>").Should().Be("**Hello** <@&1>");
    }

    [Fact]
    public void Report_EscapesTheOutcome_AndKeepsTheLines()
    {
        var report = new AutomationReport("welcome", 7, AutomationRunState.Failed, "Do[0]: *boom*", "p", [$"✅ Send a DM to {AutomationMarkup.Untrusted("_nick_")}"], false, null);

        var card = AutomationRenderer.RenderReport(report, new GeoGuessrAccountShadowBot.Domain.Model.UntrustedText("_nick_"));

        card.Title.Should().Be("welcome: failed");
        card.Description.Should().Contain(@"Do\[0\]\: \*boom\*").And.Contain(@"✅ Send a DM to \_nick\_");
        card.Author.Should().Be("Automation run #7");
    }

    [Fact]
    public void Approval_ListsWhatHappensAndWhenItEnds()
    {
        var request = new AutomationApprovalRequest(
            "vip", 3, null, "Let them in?", ["Accept the club application of x"], new DateTimeOffset(2026, 6, 2, 12, 0, 0, TimeSpan.Zero), null);

        var card = AutomationRenderer.RenderApproval(request, null);

        card.Title.Should().Be("Approve vip?");
        card.Description.Should().Contain("Let them in?").And.Contain("- Accept the club application of x").And.Contain("<t:1780401600:R>");
    }

    [Fact]
    public void Post_UsesTheStyle()
    {
        var card = AutomationRenderer.RenderPost(new AutomationPost("r", null, "Title", "Body", [], CardStyle.Danger));

        card.Title.Should().Be("Title");
        card.Description.Should().Be("Body");
        card.Author.Should().Be("Automation r");
    }
}
