// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;
using System.Text;
using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application;
using GeoGuessrAccountShadowBot.Application.Automation;
using GeoGuessrAccountShadowBot.Application.Connection;
using GeoGuessrAccountShadowBot.Application.Handlers;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;
using GeoGuessrAccountShadowBot.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GeoGuessrAccountShadowBot.Tests.Application;

public sealed class AutomationEngineTests
{
    private const string Own = ShadowTestHost.OwnUserId;
    private const string Peer = ShadowTestHost.PeerId;
    private const string Other = "cccccccccccccccccccccccc";
    private const ulong Manager = 700;
    private const ulong ManagerRole = 701;

    private const string Welcome = """
        { "Name": "welcome", "On": "FriendRequestAccepted", "Do": [{ "SendDm": "Hi {{ player.nick }}!" }] }
        """;

    private static async Task<ShadowTestHost> HostAsync(Action<ShadowOptions>? configure, params string[] rules)
    {
        var json = $$"""{ "Control": { "Automation": { "Rules": [{{string.Join(",", rules)}}] } } }""";
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

        var host = await ShadowTestHost.CreateAsync(options =>
        {
            options.Control.Automation.Enabled = true;
            options.Control.Automation.ManagerRoleIds = [ManagerRole];
            options.Events[EventKeys.FriendPresence].Enabled = true;
            configure?.Invoke(options);
        }, configuration);

        host.Roles.GetRoleIdsAsync(ShadowTestHost.AuthorizedUser, Arg.Any<CancellationToken>()).Returns([ShadowTestHost.AuthorizedRole]);
        host.Roles.GetRoleIdsAsync(Manager, Arg.Any<CancellationToken>()).Returns([ManagerRole]);
        host.Sender.SendDirectMessageAsync(default!, default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(Result.Success());
        host.Sender.SendClubMessageAsync(default!, default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(Result.Success());
        host.Api.GetPlayerNickAsync(Peer, Arg.Any<CancellationToken>()).Returns("Peery");
        host.Api.GetPlayerNickAsync(Other, Arg.Any<CancellationToken>()).Returns("Otto");
        host.Api.GetPlayerProfileAsync(Peer, Arg.Any<CancellationToken>()).Returns(Profile(Peer, "Peery", level: 30));
        host.Api.GetPlayerProfileAsync(Other, Arg.Any<CancellationToken>()).Returns(Profile(Other, "Otto", level: 5));
        host.Api.GetFriendshipsAsync(Arg.Any<CancellationToken>()).Returns(Friendships());

        await ReloadAsync(host);
        return host;
    }

    private static Task<ShadowTestHost> HostAsync(params string[] rules) => HostAsync(null, rules);

    private static PlayerProfile Profile(string id, string nick, int level) => new(
        id, nick, "DE", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), level, "Free", false, null,
        new PlayerStanding(false, null, false), null, null);

    private static FriendshipSet Friendships(string[]? friends = null, string[]? incoming = null, string[]? outgoing = null) => new(
        (friends ?? []).ToImmutableSortedSet(), (incoming ?? []).ToImmutableSortedSet(), (outgoing ?? []).ToImmutableSortedSet());

    private static ChatMessage Dm(string from, string text, DateTimeOffset sentAt, string id = "m1")
    {
        var to = from == Own ? Peer : Own;
        return new ChatMessage(id, ChatPayloadType.Text, text, from, to, false, sentAt, $"{Own}:{Peer}");
    }

    private static async Task ReloadAsync(ShadowTestHost host)
    {
        var scope = host.Services.CreateAsyncScope();
        await using (scope)
        {
            await host.Get<AutomationRuleRegistry>().ReloadAsync(
                scope.ServiceProvider.GetRequiredService<IShadowStore>(),
                scope.ServiceProvider.GetRequiredService<IAutomationStore>(),
                CancellationToken.None);
        }
    }

    /// <summary>Runs everything due, like the worker does, until nothing is left for now.</summary>
    private static async Task RunAutomationsAsync(ShadowTestHost host)
    {
        var worker = host.Get<AutomationWorker>();
        for (var pass = 0; pass < 20 && await worker.RunDueAsync(CancellationToken.None) > 0; pass++)
        {
        }
    }

    private static Task<IReadOnlyList<AutomationRun>> RunsAsync(ShadowTestHost host) =>
        WithAutomationStoreAsync(host, store => store.ReadRecentRunsAsync(null, 100));

    private static async Task<T> WithAutomationStoreAsync<T>(ShadowTestHost host, Func<IAutomationStore, Task<T>> action)
    {
        var scope = host.Services.CreateAsyncScope();
        await using (scope)
        {
            return await action(scope.ServiceProvider.GetRequiredService<IAutomationStore>());
        }
    }

    private static async Task<IReadOnlyList<T>> QueuedAsync<T>(ShadowTestHost host) =>
        (await host.ReadQueuedMessagesAsync()).OfType<T>().ToArray();

    [Fact]
    public async Task AcceptedFriendRequest_GetsTheWelcomeDm_ShownAsSentByTheRule()
    {
        await using var host = await HostAsync(Welcome);

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await RunAutomationsAsync(host);

        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "Hi Peery!", Arg.Any<CancellationToken>());
        (await RunsAsync(host)).Should().ContainSingle().Which.State.Should().Be(AutomationRunState.Completed);

        await host.ProcessAsync(new DirectMessageReceived(Dm(Own, "Hi Peery!", host.Time.GetUtcNow())));

        var line = (await QueuedAsync<ChatLine>(host)).Should().ContainSingle().Subject;
        line.Origin.Should().Be(ChatLineOrigin.Automation);
        line.AutomationRule.Should().Be("welcome");
    }

    [Fact]
    public async Task EventWithoutACard_StillStartsItsRules()
    {
        await using var host = await HostAsync(options => options.Events[EventKeys.FriendRequestAccepted].Card = false, Welcome);

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await RunAutomationsAsync(host);

        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "Hi Peery!", Arg.Any<CancellationToken>());
        (await QueuedAsync<EventCard>(host)).Should().BeEmpty();
    }

    [Fact]
    public async Task SilentEvents_StartNothing()
    {
        await using var host = await HostAsync(Welcome);

        await host.ProcessAsync(new FriendRequestAccepted(Peer) { Silent = true });

        (await RunsAsync(host)).Should().BeEmpty();
    }

    [Fact]
    public async Task SwitchedOffAutomations_StartNothing()
    {
        await using var host = await HostAsync(options => options.Control.Automation.Enabled = false, Welcome);

        await host.ProcessAsync(new FriendRequestAccepted(Peer));

        (await RunsAsync(host)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheAccountsOwnMessages_NeverTriggerARule()
    {
        await using var host = await HostAsync("""{ "Name": "echo", "On": "DmMessage", "Do": [{ "SendDm": "You said {{ event.text }}" }] }""");

        await host.ProcessAsync(new DirectMessageReceived(Dm(Own, "hello", host.Time.GetUtcNow())));

        (await RunsAsync(host)).Should().BeEmpty();
    }

    [Fact]
    public async Task AutoReply_SeesTheMessageText()
    {
        await using var host = await HostAsync("""
            { "Name": "away", "On": "DmMessage", "When": "event.text | string.contains 'help'", "Do": [{ "SendDm": "Back soon. You said: {{ event.text }}" }] }
            """);

        await host.ProcessAsync(new DirectMessageReceived(Dm(Peer, "need help", host.Time.GetUtcNow(), "m1")));
        await host.ProcessAsync(new DirectMessageReceived(Dm(Peer, "just chatting", host.Time.GetUtcNow(), "m2")));
        await RunAutomationsAsync(host);

        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "Back soon. You said: need help", Arg.Any<CancellationToken>());
        (await RunsAsync(host)).Select(run => run.State).Should().BeEquivalentTo([AutomationRunState.Completed, AutomationRunState.Skipped]);
    }

    [Fact]
    public async Task FalseCondition_SkipsQuietly()
    {
        await using var host = await HostAsync("""
            { "Name": "pros-only", "On": "FriendRequestAccepted", "When": "player.level >= 50", "Do": [{ "SendDm": "x" }] }
            """);

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await RunAutomationsAsync(host);

        await host.Sender.DidNotReceiveWithAnyArgs().SendDirectMessageAsync(default!, default!, default);
        (await RunsAsync(host)).Single().Outcome.Should().Contain("condition");
        (await QueuedAsync<AutomationReport>(host)).Should().BeEmpty("skips aren't worth a report unless asked for");
    }

    [Fact]
    public async Task OldEvents_AreSkipped()
    {
        await using var host = await HostAsync("""{ "Name": "reply", "On": "DmMessage", "MaxAge": "10m", "Do": [{ "SendDm": "x" }] }""");

        await host.ProcessAsync(new DirectMessageReceived(Dm(Peer, "hi", host.Time.GetUtcNow() - TimeSpan.FromHours(1))));
        await RunAutomationsAsync(host);

        await host.Sender.DidNotReceiveWithAnyArgs().SendDirectMessageAsync(default!, default!, default);
        (await RunsAsync(host)).Single().Outcome.Should().Contain("old");
    }

    [Fact]
    public async Task OncePerPlayer_HoldsAcrossEvents()
    {
        await using var host = await HostAsync("""
            { "Name": "welcome", "On": "FriendRequestAccepted", "OncePerPlayer": true, "Do": [{ "SendDm": "Hi" }] }
            """);

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await RunAutomationsAsync(host);
        host.Time.Advance(TimeSpan.FromDays(30));
        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await RunAutomationsAsync(host);

        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "Hi", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlayerCooldown_LetsAnotherPlayerThrough()
    {
        await using var host = await HostAsync("""
            { "Name": "welcome", "On": "FriendRequestAccepted", "PlayerCooldown": "1h", "Do": [{ "SendDm": "Hi" }] }
            """);

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await host.ProcessAsync(new FriendRequestAccepted(Other));
        await RunAutomationsAsync(host);

        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "Hi", Arg.Any<CancellationToken>());
        await host.Sender.Received(1).SendDirectMessageAsync(Other, "Hi", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Wait_PausesTheRunUntilItsTime()
    {
        await using var host = await HostAsync("""
            { "Name": "later", "On": "FriendRequestAccepted", "Do": [{ "Wait": "10m" }, { "SendDm": "Hi" }] }
            """);

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await RunAutomationsAsync(host);

        await host.Sender.DidNotReceiveWithAnyArgs().SendDirectMessageAsync(default!, default!, default);
        (await RunsAsync(host)).Single().State.Should().Be(AutomationRunState.Waiting);

        host.Time.Advance(TimeSpan.FromMinutes(10));
        await RunAutomationsAsync(host);

        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "Hi", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IfElse_TakesTheRightBranch()
    {
        await using var host = await HostAsync("""
            { "Name": "sort", "On": "FriendRequestAccepted", "Do": [
              { "If": "player.level >= 20", "Then": [{ "SendDm": "veteran" }], "Else": [{ "SendDm": "newcomer" }] }
            ] }
            """);

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await host.ProcessAsync(new FriendRequestAccepted(Other));
        await RunAutomationsAsync(host);

        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "veteran", Arg.Any<CancellationToken>());
        await host.Sender.Received(1).SendDirectMessageAsync(Other, "newcomer", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Approval_PausesUntilApproved()
    {
        await using var host = await HostAsync("""
            { "Name": "careful", "On": "FriendRequestAccepted", "Do": [{ "RequireApproval": "Greet {{ player.nick }}?" }, { "SendDm": "Hi" }] }
            """);

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await RunAutomationsAsync(host);

        var request = (await QueuedAsync<AutomationApprovalRequest>(host)).Should().ContainSingle().Subject;
        request.Planned.Should().ContainSingle().Which.Should().Contain("Send a DM");
        await host.Sender.DidNotReceiveWithAnyArgs().SendDirectMessageAsync(default!, default!, default);

        var stranger = await host.Get<AutomationService>().DecideAsync(999, request.RunId, approve: true, CancellationToken.None);
        stranger.Ok.Should().BeFalse();

        var reply = await host.Get<AutomationService>().DecideAsync(ShadowTestHost.AuthorizedUser, request.RunId, approve: true, CancellationToken.None);
        reply.Ok.Should().BeTrue();
        await RunAutomationsAsync(host);

        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "Hi", Arg.Any<CancellationToken>());
        (await QueuedAsync<AutomationApprovalOutcome>(host)).Should().ContainSingle().Which.State.Should().Be(AutomationRunState.Pending);
    }

    [Fact]
    public async Task Approval_CanBeRejected_OrRunOut()
    {
        await using var host = await HostAsync("""
            { "Name": "careful", "On": "FriendRequestAccepted", "Do": [{ "RequireApproval": { "Timeout": "1h" } }, { "SendDm": "Hi" }] }
            """);

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await host.ProcessAsync(new FriendRequestAccepted(Other));
        await RunAutomationsAsync(host);
        var requests = await QueuedAsync<AutomationApprovalRequest>(host);

        await host.Get<AutomationService>().DecideAsync(ShadowTestHost.AuthorizedUser, requests[0].RunId, approve: false, CancellationToken.None);
        host.Time.Advance(TimeSpan.FromHours(1));
        await RunAutomationsAsync(host);

        await host.Sender.DidNotReceiveWithAnyArgs().SendDirectMessageAsync(default!, default!, default);
        var runs = await RunsAsync(host);
        runs.Single(run => run.Id == requests[0].RunId).State.Should().Be(AutomationRunState.Rejected);
        runs.Single(run => run.Id == requests[1].RunId).State.Should().Be(AutomationRunState.Expired);
        (await QueuedAsync<AutomationReport>(host)).Should().ContainSingle("an expired approval is a failure worth reporting")
            .Which.State.Should().Be(AutomationRunState.Expired);

        var late = await host.Get<AutomationService>().DecideAsync(ShadowTestHost.AuthorizedUser, requests[1].RunId, approve: true, CancellationToken.None);
        late.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task ARunInterruptedDuringAnAction_IsNeverRepeated()
    {
        await using var host = await HostAsync(Welcome);
        await host.ProcessAsync(new FriendRequestAccepted(Peer));

        // As if the bot stopped right after marking the action as started.
        var run = (await RunsAsync(host)).Single();
        var scope = host.Services.CreateAsyncScope();
        await using (scope)
        {
            var automations = scope.ServiceProvider.GetRequiredService<IAutomationStore>();
            await automations.UpdateRunAsync(run with { State = AutomationRunState.Acting, AdmittedAt = host.Time.GetUtcNow() });
            await scope.ServiceProvider.GetRequiredService<IShadowStore>().CommitAsync();
        }

        await RunAutomationsAsync(host);

        await host.Sender.DidNotReceiveWithAnyArgs().SendDirectMessageAsync(default!, default!, default);
        var finished = (await RunsAsync(host)).Single();
        finished.State.Should().Be(AutomationRunState.Failed);
        finished.Outcome.Should().Contain("isn't repeated");
    }

    [Fact]
    public async Task APlayersDmCap_DropsFurtherMessages()
    {
        await using var host = await HostAsync(
            options => options.Control.Automation.MaxDmsPerPlayerPerHour = 1,
            """{ "Name": "reply", "On": "DmMessage", "Do": [{ "SendDm": "auto" }] }""");

        await host.ProcessAsync(new DirectMessageReceived(Dm(Peer, "1", host.Time.GetUtcNow(), "m1")));
        await host.ProcessAsync(new DirectMessageReceived(Dm(Peer, "2", host.Time.GetUtcNow(), "m2")));
        await RunAutomationsAsync(host);

        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "auto", Arg.Any<CancellationToken>());
        (await RunsAsync(host)).Should().OnlyContain(run => run.State == AutomationRunState.Completed && run.FinishedAt != null);
    }

    [Fact]
    public async Task TheGlobalRate_DelaysActionsInsteadOfDroppingThem()
    {
        await using var host = await HostAsync(options => options.Control.Automation.MaxActionsPerMinute = 1, Welcome);

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await host.ProcessAsync(new FriendRequestAccepted(Other));
        await RunAutomationsAsync(host);

        await host.Sender.ReceivedWithAnyArgs(1).SendDirectMessageAsync(default!, default!, default);

        host.Time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(2));
        await RunAutomationsAsync(host);

        await host.Sender.ReceivedWithAnyArgs(2).SendDirectMessageAsync(default!, default!, default);
    }

    [Fact]
    public async Task OfflineSocket_PostponesTheDm()
    {
        await using var host = await HostAsync(Welcome);
        host.Get<ConnectionStatus>().Update(new ConnectionState(ConnectionPhase.Polling));

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await RunAutomationsAsync(host);
        await host.Sender.DidNotReceiveWithAnyArgs().SendDirectMessageAsync(default!, default!, default);

        host.Get<ConnectionStatus>().Update(new ConnectionState(ConnectionPhase.Connected));
        host.Time.Advance(TimeSpan.FromSeconds(30));
        await RunAutomationsAsync(host);

        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "Hi Peery!", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedSend_FailsTheRun_AndIsReported()
    {
        await using var host = await HostAsync(Welcome);
        host.Sender.SendDirectMessageAsync(default!, default!, Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new Error(ErrorKind.Unavailable, "chat.send_failed", "The socket closed."));

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await RunAutomationsAsync(host);

        (await RunsAsync(host)).Single().State.Should().Be(AutomationRunState.Failed);
        (await QueuedAsync<AutomationReport>(host)).Should().ContainSingle().Which.Lines.Should().Contain(line => line.Contains("The socket closed."));
        (await host.WithStoreAsync(store => store.ReadPendingRepliesOlderThanAsync(DateTimeOffset.MaxValue))).Should().BeEmpty();
    }

    [Fact]
    public async Task Raise_ForEachFriend_StartsARunPerFriend()
    {
        await using var host = await HostAsync(
            """{ "Name": "weekly", "On": "Manual", "Do": [{ "Raise": { "Event": "nudge", "ForEach": "Friends", "Data": { "why": "weekly" } } }] }""",
            """{ "Name": "nudge", "On": "Custom:nudge", "Do": [{ "SendDm": "Hey {{ player.nick }}, {{ event.why }} game?" }] }""");
        await host.ProcessAsync(new FriendshipsFetched(Friendships(friends: [Peer, Other])));

        var reply = await host.Get<AutomationService>().RunAsync(Manager, "weekly", null, null, CancellationToken.None);
        reply.Ok.Should().BeTrue();
        await RunAutomationsAsync(host);

        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "Hey Peery, weekly game?", Arg.Any<CancellationToken>());
        await host.Sender.Received(1).SendDirectMessageAsync(Other, "Hey Otto, weekly game?", Arg.Any<CancellationToken>());
        (await RunsAsync(host)).Where(run => run.RuleName == "nudge").Should().OnlyContain(run => run.Depth == 1);
    }

    [Fact]
    public async Task Chains_StopAtTheirDepth()
    {
        await using var host = await HostAsync(
            options => options.Control.Automation.MaxChainDepth = 2,
            """{ "Name": "loop", "On": "Custom:loop", "Do": [{ "Raise": "loop" }] }""",
            """{ "Name": "start", "On": "Manual", "Do": [{ "Raise": "loop" }] }""");

        await host.Get<AutomationService>().RunAsync(Manager, "start", null, null, CancellationToken.None);
        await RunAutomationsAsync(host);

        var runs = await RunsAsync(host);
        runs.Should().HaveCount(3);
        runs.Single(run => run.Depth == 2).State.Should().Be(AutomationRunState.Failed);
    }

    [Fact]
    public async Task StopOtherRules_LetsTheFirstMatchingRuleWin()
    {
        await using var host = await HostAsync(
            """{ "Name": "vip", "On": "FriendRequestAccepted", "Priority": 10, "StopOtherRules": true, "When": "player.level >= 20", "Do": [{ "SendDm": "VIP" }] }""",
            """{ "Name": "default", "On": "FriendRequestAccepted", "Do": [{ "SendDm": "Normal" }] }""");

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await host.ProcessAsync(new FriendRequestAccepted(Other));
        await RunAutomationsAsync(host);

        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "VIP", Arg.Any<CancellationToken>());
        await host.Sender.DidNotReceive().SendDirectMessageAsync(Peer, "Normal", Arg.Any<CancellationToken>());
        await host.Sender.Received(1).SendDirectMessageAsync(Other, "Normal", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DryRun_OnlyReports()
    {
        await using var host = await HostAsync("""
            { "Name": "trial", "On": "FriendRequestAccepted", "DryRun": true, "Do": [{ "Wait": "1d" }, { "SendDm": "Hi" }, { "AcceptFriendRequest": true }] }
            """);

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await RunAutomationsAsync(host);

        await host.Sender.DidNotReceiveWithAnyArgs().SendDirectMessageAsync(default!, default!, default);
        await host.Api.DidNotReceiveWithAnyArgs().AcceptFriendRequestAsync(default!, default);
        var report = (await QueuedAsync<AutomationReport>(host)).Should().ContainSingle().Subject;
        report.DryRun.Should().BeTrue();
        report.Lines.Should().HaveCount(3).And.OnlyContain(line => line.StartsWith("🧪", StringComparison.Ordinal) || line.StartsWith("⏭️", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FriendActions_CheckTheListsFirst()
    {
        await using var host = await HostAsync("""
            { "Name": "friendly", "On": "FriendRequestReceived", "Report": "All", "Do": [{ "AcceptFriendRequest": true }, { "SendFriendRequest": true }] }
            """);
        host.Api.GetFriendshipsAsync(Arg.Any<CancellationToken>()).Returns(Friendships(incoming: [Peer]));
        host.Api.AcceptFriendRequestAsync(Peer, Arg.Any<CancellationToken>()).Returns(Result.Success());

        await host.ProcessAsync(new FriendRequestReceived(Peer));
        await RunAutomationsAsync(host);

        await host.Api.Received(1).AcceptFriendRequestAsync(Peer, Arg.Any<CancellationToken>());
        await host.Api.DidNotReceiveWithAnyArgs().SendFriendRequestAsync(default!, default);
        var report = (await QueuedAsync<AutomationReport>(host)).Single();
        report.Lines.Should().Contain(line => line.StartsWith("⏭️ Send a friend request", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClubApplications_AreAnnounced_AndCanBeAnsweredByARule()
    {
        await using var host = await HostAsync(
            options =>
            {
                options.GeoGuessr.Club.Enabled = true;
                options.Events[EventKeys.ClubApplicationReceived].Enabled = true;
            },
            """{ "Name": "auto-accept", "On": "ClubApplicationReceived", "When": "player.level >= 20", "Do": [{ "AcceptClubApplication": true }, { "SendFriendRequest": true }] }""");
        host.Get<AccountContext>().Set(Own, "club1");
        host.Api.AcceptClubApplicationAsync("club1", Peer, Arg.Any<CancellationToken>()).Returns(Result.Success());
        host.Api.SendFriendRequestAsync(Peer, Arg.Any<CancellationToken>()).Returns(Result.Success());

        await host.ProcessAsync(new ClubApplicationsFetched(ClubApplicationSet.From([new ClubApplicant(Other, "Otto")])));
        await host.ProcessAsync(new ClubApplicationsFetched(ClubApplicationSet.From([new ClubApplicant(Other, "Otto"), new ClubApplicant(Peer, "Peery")])));
        await RunAutomationsAsync(host);

        (await QueuedAsync<EventCard>(host)).Should().ContainSingle().Which.ActorUserId.Should().Be(Peer, "the first snapshot is a silent baseline");
        await host.Api.Received(1).AcceptClubApplicationAsync("club1", Peer, Arg.Any<CancellationToken>());
        await host.Api.Received(1).SendFriendRequestAsync(Peer, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Schedules_RecordABaseline_ThenRunAtTheirTime()
    {
        await using var host = await HostAsync("""
            { "Name": "daily", "On": "Schedule", "Cron": "0 18 * * *", "Do": [{ "PostDiscord": "Daily reminder at {{ event.scheduled_at | date.to_string '%H:%M' }}" }] }
            """);
        var scheduler = host.Get<AutomationScheduler>();

        await scheduler.TickAsync(CancellationToken.None);
        (await RunsAsync(host)).Should().BeEmpty("a new schedule waits for its next occurrence");

        host.Time.Advance(TimeSpan.FromHours(6));
        await scheduler.TickAsync(CancellationToken.None);
        await scheduler.TickAsync(CancellationToken.None);
        await RunAutomationsAsync(host);

        (await RunsAsync(host)).Should().ContainSingle();
        (await QueuedAsync<AutomationPost>(host)).Should().ContainSingle().Which.Text.Should().Contain("18:00");
    }

    [Fact]
    public async Task ASchedule_MissedForLong_RunsOnceAndOnlyIfRecent()
    {
        await using var host = await HostAsync("""
            { "Name": "hourly", "On": "Schedule", "Cron": "0 * * * *", "MaxAge": "30m", "Do": [{ "PostDiscord": "tick" }] }
            """);
        var scheduler = host.Get<AutomationScheduler>();
        await scheduler.TickAsync(CancellationToken.None);

        host.Time.Advance(TimeSpan.FromHours(5) + TimeSpan.FromMinutes(10));
        await scheduler.TickAsync(CancellationToken.None);
        await RunAutomationsAsync(host);

        (await RunsAsync(host)).Should().ContainSingle().Which.State.Should().Be(AutomationRunState.Completed);
    }

    [Fact]
    public async Task ForumNote_AndPost_GoToTheOutbox()
    {
        await using var host = await HostAsync("""
            { "Name": "notes", "On": "FriendRequestAccepted", "Do": [
              { "ForumNote": "Became friends with **{{ player.nick }}**" },
              { "PostDiscord": { "Title": "New friend", "Text": "{{ player.nick }}", "ChannelId": 99, "PingRoleIds": [5] } }
            ] }
            """);

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await RunAutomationsAsync(host);

        var note = (await QueuedAsync<AutomationForumNote>(host)).Should().ContainSingle().Subject;
        note.PartnerUserId.Should().Be(Peer);
        AutomationMarkup.Strip(note.Text).Should().Be("Became friends with **Peery**");
        var post = (await QueuedAsync<AutomationPost>(host)).Should().ContainSingle().Subject;
        post.ChannelId.Should().Be(99);
        post.PingRoleIds.Should().Equal(5UL);
    }

    [Fact]
    public async Task ScriptErrors_FailTheRunWithTheirPath()
    {
        await using var host = await HostAsync("""
            { "Name": "broken", "On": "FriendRequestAccepted", "Do": [{ "SendDm": "{{ for i in 1..5000 }}x{{ end }}" }] }
            """);

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await RunAutomationsAsync(host);

        var run = (await RunsAsync(host)).Single();
        run.State.Should().Be(AutomationRunState.Failed);
        run.Outcome.Should().StartWith("Do[0]");
    }

    [Fact]
    public async Task FinishedRuns_KeepNoEventData()
    {
        await using var host = await HostAsync("""{ "Name": "reply", "On": "DmMessage", "Do": [{ "SendDm": "ok" }] }""");

        await host.ProcessAsync(new DirectMessageReceived(Dm(Peer, "private words", host.Time.GetUtcNow())));
        await RunAutomationsAsync(host);

        var run = (await RunsAsync(host)).Single();
        run.EventJson.Should().Be("{}");
        run.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task GeoGuessrOutage_WhileReadingThePlayer_PostponesTheRun()
    {
        await using var host = await HostAsync("""
            { "Name": "welcome", "On": "FriendRequestAccepted", "When": "player.level > 1", "Do": [{ "SendDm": "Hi {{ player.nick }}!" }] }
            """);
        host.Api.GetPlayerProfileAsync(Peer, Arg.Any<CancellationToken>())
            .ThrowsAsync(new GeoGuessrUnavailableException("down", new HttpRequestException()));

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await RunAutomationsAsync(host);

        var run = (await RunsAsync(host)).Single();
        run.State.Should().Be(AutomationRunState.Pending);
        run.Attempts.Should().Be(1);

        host.Api.GetPlayerProfileAsync(Peer, Arg.Any<CancellationToken>()).Returns(Profile(Peer, "Peery", 30));
        host.Time.Advance(TimeSpan.FromSeconds(30));
        await RunAutomationsAsync(host);

        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "Hi Peery!", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheapPlayerFields_DontReadTheProfile()
    {
        await using var host = await HostAsync(Welcome);

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await RunAutomationsAsync(host);

        await host.Api.DidNotReceiveWithAnyArgs().GetPlayerProfileAsync(default!, default);
        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "Hi Peery!", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActionsAreAudited_UnderTheRulesName()
    {
        await using var host = await HostAsync(Welcome);

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await RunAutomationsAsync(host);

        var scope = host.Services.CreateAsyncScope();
        await using (scope)
        {
            var database = scope.ServiceProvider.GetRequiredService<GeoGuessrAccountShadowBot.Infrastructure.Persistence.ShadowDbContext>();
            var audit = database.ControlAudits.Should().ContainSingle().Subject;
            audit.Action.Should().Be("auto:welcome:dm");
            audit.Target.Should().Be(Peer);
            audit.Outcome.Should().Be("success");
        }
    }
}

public sealed class AutomationServiceTests
{
    private const string Peer = ShadowTestHost.PeerId;
    private const ulong Manager = 700;
    private const ulong ManagerRole = 701;

    private static async Task<ShadowTestHost> HostAsync(Action<ShadowOptions>? configure = null)
    {
        var host = await ShadowTestHost.CreateAsync(options =>
        {
            options.Control.Automation.Enabled = true;
            options.Control.Automation.ManagerRoleIds = [ManagerRole];
            configure?.Invoke(options);
        });

        host.Roles.GetRoleIdsAsync(ShadowTestHost.AuthorizedUser, Arg.Any<CancellationToken>()).Returns([ShadowTestHost.AuthorizedRole]);
        host.Roles.GetRoleIdsAsync(Manager, Arg.Any<CancellationToken>()).Returns([ManagerRole]);
        host.Sender.SendDirectMessageAsync(default!, default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(Result.Success());
        host.Api.GetPlayerNickAsync(Peer, Arg.Any<CancellationToken>()).Returns("Peery");
        return host;
    }

    private const string Rule = """{ "Name": "welcome", "On": "FriendRequestAccepted", "Do": [{ "SendDm": "Hi" }] }""";

    [Fact]
    public async Task OnlyManagers_MaySaveRules()
    {
        await using var host = await HostAsync();
        var service = host.Get<AutomationService>();

        (await service.SaveAsync(ShadowTestHost.AuthorizedUser, Rule, null, CancellationToken.None)).Ok.Should().BeFalse();
        (await service.SaveAsync(Manager, Rule, null, CancellationToken.None)).Ok.Should().BeTrue();

        host.Get<AutomationRuleRegistry>().RulesFor(EventKeys.FriendRequestAccepted).Should().ContainSingle();
        (await service.ListAsync(ShadowTestHost.AuthorizedUser, CancellationToken.None)).Text.Should().Contain("welcome");
        (await service.ListAsync(999, CancellationToken.None)).Ok.Should().BeFalse();
    }

    [Fact]
    public async Task SavedRules_RunOnEvents()
    {
        await using var host = await HostAsync();
        await host.Get<AutomationService>().SaveAsync(Manager, Rule, null, CancellationToken.None);

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await host.Get<AutomationWorker>().RunDueAsync(CancellationToken.None);

        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "Hi", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidRules_ListEveryProblem()
    {
        await using var host = await HostAsync(options => options.Events[EventKeys.FriendRequestAccepted].Enabled = false);

        var reply = await host.Get<AutomationService>().SaveAsync(Manager, Rule, null, CancellationToken.None);

        reply.Ok.Should().BeFalse();
        reply.Text.Should().Contain("switched off");
    }

    [Fact]
    public async Task Creating_DoesntOverwrite_AndEditing_DoesntRename()
    {
        await using var host = await HostAsync();
        var service = host.Get<AutomationService>();
        await service.SaveAsync(Manager, Rule, null, CancellationToken.None);

        (await service.SaveAsync(Manager, Rule, null, CancellationToken.None)).Text.Should().Contain("already exists");
        (await service.SaveAsync(Manager, Rule.Replace("welcome", "other", StringComparison.Ordinal), "welcome", CancellationToken.None)).Text.Should().Contain("can't rename");
        (await service.SaveAsync(Manager, Rule.Replace("\"Hi\"", "\"Hello\"", StringComparison.Ordinal), "welcome", CancellationToken.None)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Disabling_CancelsWaitingRuns()
    {
        await using var host = await HostAsync();
        var service = host.Get<AutomationService>();
        await service.SaveAsync(Manager, """{ "Name": "slow", "On": "FriendRequestAccepted", "Do": [{ "Wait": "1h" }, { "SendDm": "Hi" }] }""", null, CancellationToken.None);
        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await host.Get<AutomationWorker>().RunDueAsync(CancellationToken.None);

        var reply = await service.SetEnabledAsync(Manager, "slow", enabled: false, CancellationToken.None);
        host.Time.Advance(TimeSpan.FromHours(1));
        await host.Get<AutomationWorker>().RunDueAsync(CancellationToken.None);

        reply.Text.Should().Contain("Cancelled 1");
        await host.Sender.DidNotReceiveWithAnyArgs().SendDirectMessageAsync(default!, default!, default);
        (await service.ShowAsync(Manager, "slow", CancellationToken.None)).Text.Should().Contain("\"Enabled\": false");
    }

    [Fact]
    public async Task Test_ShowsWhatWouldHappen_WithoutDoingIt()
    {
        await using var host = await HostAsync();
        var service = host.Get<AutomationService>();
        await service.SaveAsync(Manager, """
            { "Name": "reply", "On": "DmMessage", "When": "event.text | string.contains 'hi'", "Do": [{ "Wait": "5m" }, { "SendDm": "You said {{ event.text }}" }] }
            """, null, CancellationToken.None);

        var reply = await service.TestAsync(ShadowTestHost.AuthorizedUser, "reply", Peer, "hi there", CancellationToken.None);

        reply.Ok.Should().BeTrue();
        AutomationMarkup.Strip(reply.Text).Should().Contain("Wait 5 min").And.Contain("Send a DM to Peery: You said hi there");
        await host.Sender.DidNotReceiveWithAnyArgs().SendDirectMessageAsync(default!, default!, default);
        (await host.Get<AutomationService>().RunsAsync(Manager, null, CancellationToken.None)).Text.Should().Be("No runs yet.");
    }

    [Fact]
    public async Task ManualRuns_SkipLimits_AndReport()
    {
        await using var host = await HostAsync();
        var service = host.Get<AutomationService>();
        await service.SaveAsync(Manager, """{ "Name": "hello", "On": "Manual", "OncePerPlayer": true, "Do": [{ "SendDm": "Hello" }] }""", null, CancellationToken.None);

        (await service.RunAsync(ShadowTestHost.AuthorizedUser, "hello", Peer, null, CancellationToken.None)).Ok.Should().BeFalse();
        (await service.RunAsync(Manager, "hello", $"https://www.geoguessr.com/user/{Peer}", null, CancellationToken.None)).Ok.Should().BeTrue();
        (await service.RunAsync(Manager, "hello", Peer, null, CancellationToken.None)).Ok.Should().BeTrue();
        await host.Get<AutomationWorker>().RunDueAsync(CancellationToken.None);

        await host.Sender.Received(2).SendDirectMessageAsync(Peer, "Hello", Arg.Any<CancellationToken>());
        (await host.ReadQueuedMessagesAsync()).OfType<AutomationReport>().Should().HaveCount(2);
        (await service.RunAsync(Manager, "hello", "not a player", null, CancellationToken.None)).Text.Should().Contain("isn't a GeoGuessr user id");
    }

    [Fact]
    public async Task ConfigRules_CanOnlyBeSwitched()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Control:Automation:Rules:0:Name"] = "fixed",
                ["Control:Automation:Rules:0:On"] = "FriendRequestAccepted",
                ["Control:Automation:Rules:0:Do:0:SendDm"] = "Hi"
            })
            .Build();
        await using var host = await ShadowTestHost.CreateAsync(options =>
        {
            options.Control.Automation.Enabled = true;
            options.Control.Automation.ManagerRoleIds = [ManagerRole];
        }, configuration);
        host.Roles.GetRoleIdsAsync(Manager, Arg.Any<CancellationToken>()).Returns([ManagerRole]);
        var service = host.Get<AutomationService>();

        (await service.DeleteAsync(Manager, "fixed", CancellationToken.None)).Ok.Should().BeFalse();
        (await service.GetEditableAsync(Manager, "fixed", CancellationToken.None)).Refusal.Should().NotBeNull();
        (await service.SaveAsync(Manager, """{ "Name": "fixed", "On": "Manual", "Do": [{ "Stop": true }] }""", null, CancellationToken.None)).Ok.Should().BeFalse();

        (await service.SetEnabledAsync(Manager, "fixed", enabled: false, CancellationToken.None)).Ok.Should().BeTrue();
        host.Get<AutomationRuleRegistry>().RulesFor(EventKeys.FriendRequestAccepted).Should().BeEmpty();
        (await service.SetEnabledAsync(Manager, "fixed", enabled: true, CancellationToken.None)).Ok.Should().BeTrue();
        host.Get<AutomationRuleRegistry>().RulesFor(EventKeys.FriendRequestAccepted).Should().ContainSingle();
    }

    [Fact]
    public async Task Cancel_EndsARun()
    {
        await using var host = await HostAsync();
        var service = host.Get<AutomationService>();
        await service.SaveAsync(Manager, """{ "Name": "slow", "On": "Manual", "Do": [{ "Wait": "1h" }, { "SendDm": "Hi" }] }""", null, CancellationToken.None);
        await service.RunAsync(Manager, "slow", Peer, null, CancellationToken.None);
        await host.Get<AutomationWorker>().RunDueAsync(CancellationToken.None);

        (await service.CancelAsync(Manager, 1, CancellationToken.None)).Ok.Should().BeTrue();
        (await service.CancelAsync(Manager, 1, CancellationToken.None)).Ok.Should().BeFalse();
        host.Time.Advance(TimeSpan.FromHours(2));
        await host.Get<AutomationWorker>().RunDueAsync(CancellationToken.None);

        await host.Sender.DidNotReceiveWithAnyArgs().SendDirectMessageAsync(default!, default!, default);
    }
}

public sealed class AutomationOptionsValidatorTests
{
    private static ConfiguredAutomationRules Rules(params (string Key, string Value)[] values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value))).Build());

    [Fact]
    public void ConfiguredRules_AreCheckedAgainstTheRestOfTheConfiguration()
    {
        var rules = Rules(
            ("Control:Automation:Rules:0:Name", "reply"),
            ("Control:Automation:Rules:0:On", "ClubChat"),
            ("Control:Automation:Rules:0:Do:0:SendClubMessage", "hi"),
            ("Control:Automation:Rules:1:Name", "broken"),
            ("Control:Automation:Rules:1:On", "Never"));
        var options = TestOptions.Valid();
        options.Control.Automation.Enabled = true;

        var result = new AutomationOptionsValidator(rules).Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(failure => failure.Contains("Rules:1", StringComparison.Ordinal) && failure.Contains("isn't a trigger", StringComparison.Ordinal));
        result.Failures.Should().Contain(failure => failure.Contains("Automation reply", StringComparison.Ordinal) && failure.Contains("switched off", StringComparison.Ordinal));
        result.Failures.Should().Contain(failure => failure.Contains("GeoGuessr:Club:Enabled", StringComparison.Ordinal));
    }

    [Fact]
    public void SwitchingAutomationsOff_StillRequiresRulesThatParse()
    {
        var valid = Rules(
            ("Control:Automation:Rules:0:Name", "r"),
            ("Control:Automation:Rules:0:On", "ClubChat"),
            ("Control:Automation:Rules:0:Do:0:SendClubMessage", "hi"));
        var broken = Rules(
            ("Control:Automation:Rules:0:Name", "r"),
            ("Control:Automation:Rules:0:On", "Never"));

        new AutomationOptionsValidator(valid).Validate(null, TestOptions.Valid()).Succeeded.Should().BeTrue();
        new AutomationOptionsValidator(broken).Validate(null, TestOptions.Valid()).Failed.Should().BeTrue();
    }

    [Fact]
    public void TheDefaults_AreValid()
    {
        new AutomationOptionsValidator(Rules()).Validate(null, TestOptions.Valid()).Succeeded.Should().BeTrue();
    }
}
