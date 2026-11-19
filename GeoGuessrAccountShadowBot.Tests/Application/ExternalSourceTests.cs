// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application.Automation;
using GeoGuessrAccountShadowBot.Application.Handlers;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Pipeline;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Application.Sources;
using GeoGuessrAccountShadowBot.Domain;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;
using GeoGuessrAccountShadowBot.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace GeoGuessrAccountShadowBot.Tests.Application;

/// <summary>
/// Rules that read other instances, and events that come from them, on the real engine and a real
/// database, with only the source itself scripted.
/// </summary>
public sealed class ExternalSourceTests
{
    private const string Peer = ShadowTestHost.PeerId;
    private const string Other = "cccccccccccccccccccccccc";
    private const string Token = "a-client-token-that-is-long-enough-to-pass";

    /// <summary>The rules of issue #25, written against a source named dragon.</summary>
    private const string Befriend = """
        { "Name": "befriend-dragon-applicants", "On": "Source:dragon:ClubApplicationReceived", "OncePerPlayer": true, "MaxAge": "2d",
          "Do": [{ "SendFriendRequest": true }] }
        """;

    private const string Accepted = """
        { "Name": "friend-request-accepted", "On": "FriendRequestAccepted", "Do": [
          { "If": "player.has_applied", "Then": [{ "SendDm": "Your application to the Den" }],
            "Else": [{ "If": "sources.dragon.player.has_applied", "Then": [{ "SendDm": "Your application to Dragon" }] }] }
        ] }
        """;

    private static async Task<ShadowTestHost> HostAsync(Action<ShadowOptions>? configure, params string[] rules)
    {
        var json = $$"""{ "Control": { "Automation": { "Rules": [{{string.Join(",", rules)}}] } } }""";
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

        var host = await ShadowTestHost.CreateAsync(options =>
        {
            options.Control.Automation.Enabled = true;
            options.Sources["dragon"] = new SourceOptions { Url = new Uri("http://dragon:8080"), Token = Token };
            configure?.Invoke(options);
        }, configuration);

        host.Sender.SendDirectMessageAsync(default!, default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(Result.Success());
        host.Api.SendFriendRequestAsync(default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(Result.Success());
        host.Api.GetPlayerNickAsync(default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs("Peery");
        host.Api.GetFriendshipsAsync(Arg.Any<CancellationToken>()).Returns(new FriendshipSet([], [], []));
        return host;
    }

    private static async Task RunAutomationsAsync(ShadowTestHost host)
    {
        var worker = host.Get<AutomationWorker>();
        for (var pass = 0; pass < 20 && await worker.RunDueAsync(CancellationToken.None) > 0; pass++)
        {
        }
    }

    private static async Task<IReadOnlyList<AutomationRun>> RunsAsync(ShadowTestHost host)
    {
        var scope = host.Services.CreateAsyncScope();
        await using (scope)
        {
            return await scope.ServiceProvider.GetRequiredService<IAutomationStore>().ReadRecentRunsAsync(null, 100);
        }
    }

    private static SourceEvent Application(long id, string player, DateTimeOffset at) =>
        new(id, EventKeys.ClubApplicationReceived, player, at, at, new JsonObject { ["nick"] = "Peery" });

    /// <summary>Lets the worker follow a source until the condition holds, then stops it.</summary>
    private static async Task FollowUntilAsync(ShadowTestHost host, FakeSource source, Func<Task<bool>> done)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var worker = host.Services.GetServices<IHostedService>().OfType<SourceEventWorker>().Single();
        var following = worker.FollowAsync(source, stop.Token);

        while (!await done())
        {
            stop.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, stop.Token);
        }

        await stop.CancelAsync();
        await following.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private static async Task<long?> CursorAsync(ShadowTestHost host) =>
        (await host.WithStoreAsync(store => store.ReadStateAsync<SourceEventWorker.SourceCursor>(SourceEventWorker.CursorKey("dragon"))))?.Id;

    [Fact]
    public async Task ApplicationAtTheSource_BefriendsTheApplicant_Once()
    {
        await using var host = await HostAsync(null, Befriend);
        var dragon = host.Sources["dragon"];
        var now = host.Time.GetUtcNow();

        dragon.Send(new SourceHello(1, 5));
        dragon.Send(Application(6, Peer, now));
        await FollowUntilAsync(host, dragon, async () => await CursorAsync(host) == 6);

        await RunAutomationsAsync(host);
        await host.Api.Received(1).SendFriendRequestAsync(Peer, Arg.Any<CancellationToken>());
        var run = (await RunsAsync(host)).Should().ContainSingle().Subject;
        run.Trigger.Should().Be("Source:dragon:ClubApplicationReceived");
        run.State.Should().Be(AutomationRunState.Completed);
    }

    [Fact]
    public async Task FirstConnection_StartsAtTheNewestEvent_SoOldOnesStartNothing()
    {
        await using var host = await HostAsync(null, Befriend);
        var dragon = host.Sources["dragon"];

        dragon.Send(new SourceHello(1, 40));
        await FollowUntilAsync(host, dragon, async () => await CursorAsync(host) == 40);

        dragon.StreamRequests.Should().Equal([null]);
        (await RunsAsync(host)).Should().BeEmpty();
    }

    [Fact]
    public async Task Reconnecting_ResumesAfterTheLastEvent_AndSkipsWhatWasSeen()
    {
        await using var host = await HostAsync(null, Befriend);
        var dragon = host.Sources["dragon"];
        var now = host.Time.GetUtcNow();
        await host.WithStoreAsync(async store =>
        {
            await store.WriteStateAsync(SourceEventWorker.CursorKey("dragon"), new SourceEventWorker.SourceCursor(6));
            await store.CommitAsync();
            return 0;
        });

        dragon.Send(new SourceHello(1, 7));
        dragon.Send(Application(6, Other, now));
        dragon.Send(Application(7, Peer, now));
        await FollowUntilAsync(host, dragon, async () => await CursorAsync(host) == 7);

        dragon.StreamRequests.Should().Equal([6L]);
        (await RunsAsync(host)).Should().ContainSingle().Which.SubjectUserId.Should().Be(Peer);
    }

    [Fact]
    public async Task ADroppedStream_IsReopened_FromWhereItStopped()
    {
        await using var host = await HostAsync(null, Befriend);
        var dragon = host.Sources["dragon"];
        var now = host.Time.GetUtcNow();

        dragon.Send(new SourceHello(0, 0));
        dragon.Send(Application(1, Peer, now));
        var dropped = false;
        await FollowUntilAsync(host, dragon, async () =>
        {
            if (!dropped && await CursorAsync(host) == 1)
            {
                dropped = true;
                dragon.Disconnect();
                dragon.Send(new SourceHello(1, 2));
                dragon.Send(Application(2, Other, now));
            }

            // Lets the backoff before reconnecting pass.
            host.Time.Advance(TimeSpan.FromSeconds(1));
            return await CursorAsync(host) == 2;
        });

        dragon.StreamRequests.Should().Equal([null, 1L]);
        (await RunsAsync(host)).Select(run => run.SubjectUserId).Should().BeEquivalentTo([Peer, Other]);
    }

    [Fact]
    public async Task ASourceThatStartedOver_IsFollowedFromItsNewestEvent()
    {
        await using var host = await HostAsync(null, Befriend);
        var dragon = host.Sources["dragon"];
        await host.WithStoreAsync(async store =>
        {
            await store.WriteStateAsync(SourceEventWorker.CursorKey("dragon"), new SourceEventWorker.SourceCursor(500));
            await store.CommitAsync();
            return 0;
        });

        dragon.Send(new SourceHello(1, 3));
        dragon.Send(Application(4, Peer, host.Time.GetUtcNow()));
        await FollowUntilAsync(host, dragon, async () => await CursorAsync(host) == 4);

        (await RunsAsync(host)).Should().ContainSingle();
        host.Get<EventBus>().Reader.TryRead(out var alert).Should().BeTrue();
        alert.Should().BeOfType<OperatorAlertRaised>().Which.Title.Should().Contain("started over");
    }

    [Fact]
    public async Task AcceptedRequest_BranchesOnTheApplicationAtTheSource()
    {
        await using var host = await HostAsync(null, Accepted);
        host.Sources["dragon"].Players[Peer] = new JsonObject { ["id"] = Peer, ["has_applied"] = true };

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await host.ProcessAsync(new FriendRequestAccepted(Other));
        await RunAutomationsAsync(host);

        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "Your application to Dragon", Arg.Any<CancellationToken>());
        await host.Sender.DidNotReceive().SendDirectMessageAsync(Other, Arg.Any<string>(), Arg.Any<CancellationToken>());
        host.Sources["dragon"].Reads.Should().BeEquivalentTo([$"player:{Peer}", $"player:{Other}"]);

        // Reading the player through a source doesn't cost a GeoGuessr profile lookup.
        await host.Api.DidNotReceiveWithAnyArgs().GetPlayerProfileAsync(default!, default);
    }

    [Fact]
    public async Task UnreachableSource_PutsTheRunOff_UntilItsBack()
    {
        await using var host = await HostAsync(null, Accepted);
        var dragon = host.Sources["dragon"];
        dragon.Players[Peer] = new JsonObject { ["has_applied"] = true };
        dragon.Failure = new ExternalSourceUnavailableException("It couldn't be reached (ConnectionError).");

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await RunAutomationsAsync(host);

        var waiting = (await RunsAsync(host)).Should().ContainSingle().Subject;
        waiting.State.Should().Be(AutomationRunState.Pending);
        waiting.Attempts.Should().Be(1);

        dragon.Failure = null;
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await RunAutomationsAsync(host);

        (await RunsAsync(host)).Single().State.Should().Be(AutomationRunState.Completed);
        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "Your application to Dragon", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefusingSource_FailsTheRun_AndSaysWhy()
    {
        await using var host = await HostAsync(null, Accepted);
        host.Sources["dragon"].Failure = new ExternalSourceRefusedException("It doesn't let this instance read Players.");

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await RunAutomationsAsync(host);

        var run = (await RunsAsync(host)).Should().ContainSingle().Subject;
        run.State.Should().Be(AutomationRunState.Failed);
        run.Outcome.Should().Contain("dragon").And.Contain("Players");
    }

    [Fact]
    public async Task Raise_ForEachOfASourcesGroup_StartsARunPerPlayer()
    {
        const string Hello = """
            { "Name": "hello-applicants", "On": "Manual", "Do": [{ "Raise": { "Event": "hello", "ForEach": "Source:dragon:ClubApplications" } }] }
            """;
        const string Greet = """{ "Name": "greet", "On": "Custom:hello", "Do": [{ "SendDm": "Hi" }] }""";
        await using var host = await HostAsync(null, Hello, Greet);
        host.Sources["dragon"].Audiences[AutomationAudience.ClubApplications] = [Peer, Other, ShadowTestHost.OwnUserId, "not-an-id"];

        var service = host.Get<AutomationService>();
        host.Roles.GetRoleIdsAsync(1, Arg.Any<CancellationToken>()).Returns([5UL]);
        host.Options.Control.Automation.ManagerRoleIds = [5];
        (await service.RunAsync(1, "hello-applicants", null, null, CancellationToken.None)).Ok.Should().BeTrue();
        await RunAutomationsAsync(host);

        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "Hi", Arg.Any<CancellationToken>());
        await host.Sender.Received(1).SendDirectMessageAsync(Other, "Hi", Arg.Any<CancellationToken>());
        (await RunsAsync(host)).Count(run => run.RuleName == "greet").Should().Be(2);
    }

    [Fact]
    public async Task ServedEvents_AreRecorded_OnlyWhenAClientMayHaveThem()
    {
        await using var host = await HostAsync(options =>
        {
            options.ExternalApi.Enabled = true;
            options.ExternalApi.Clients["main"] = new ExternalApiClientOptions { Token = Token, Events = ["friendrequestaccepted", "Custom:vip"] };
        });

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await host.ProcessAsync(new FriendRequestReceived(Other));
        await host.ProcessAsync(new FriendRequestAccepted(Other) { Silent = true });

        var events = await FeedAsync(host);
        events.Should().ContainSingle();
        events[0].Trigger.Should().Be(EventKeys.FriendRequestAccepted);
        events[0].SubjectUserId.Should().Be(Peer);
    }

    [Fact]
    public async Task RaisedCustomEvents_AreServed_ButNotFromDryRuns()
    {
        const string Vip = """{ "Name": "vip", "On": "FriendRequestAccepted", "Do": [{ "Raise": { "Event": "vip", "Data": { "why": "accepted" } } }] }""";
        const string DryVip = """{ "Name": "dry-vip", "On": "FriendRequestAccepted", "DryRun": true, "Do": [{ "Raise": "vip" }] }""";
        await using var host = await HostAsync(options =>
        {
            options.ExternalApi.Enabled = true;
            options.ExternalApi.Clients["main"] = new ExternalApiClientOptions { Token = Token, Events = ["Custom:vip"] };
        }, Vip, DryVip);

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await RunAutomationsAsync(host);

        var served = (await FeedAsync(host)).Should().ContainSingle().Subject;
        served.Trigger.Should().Be("Custom:vip");
        served.SubjectUserId.Should().Be(Peer);
        JsonNode.Parse(served.DataJson)!["why"]!.GetValue<string>().Should().Be("accepted");
    }

    [Fact]
    public async Task Pruning_DropsOldEvents_ButKeepsTheNewest_SoIdsNeverRestart()
    {
        await using var host = await HostAsync(options =>
        {
            options.ExternalApi.Enabled = true;
            options.ExternalApi.Clients["main"] = new ExternalApiClientOptions { Token = Token, Events = [EventKeys.FriendRequestAccepted] };
        });

        await host.ProcessAsync(new FriendRequestAccepted(Peer));
        await host.ProcessAsync(new FriendRequestAccepted(Other));
        host.Time.Advance(TimeSpan.FromDays(8));
        await host.Services.GetServices<IHostedService>().OfType<ExternalFeedPruner>().Single().PruneAsync(CancellationToken.None);

        (await FeedAsync(host)).Should().ContainSingle().Which.SubjectUserId.Should().Be(Other);

        await host.ProcessAsync(new FriendRequestAccepted("dddddddddddddddddddddddd"));
        (await FeedAsync(host)).Select(row => row.Id).Should().Equal([2L, 3L]);
    }

    [Fact]
    public async Task Headless_KeepsNothingForDiscord()
    {
        await using var host = await HostAsync(options => options.Discord.Enabled = false);

        await host.ProcessAsync(new OperatorAlertRaised("Something", "happened", false));
        await host.ProcessAsync(new FriendRequestReceived(Peer));

        (await host.ReadOutboxAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task PlayerRelations_AreServed_FromTheSnapshots()
    {
        await using var host = await HostAsync(options =>
        {
            options.ExternalApi.Enabled = true;
            options.ExternalApi.Clients["main"] = new ExternalApiClientOptions { Token = Token, Data = [ExternalApiData.Players] };
        });
        await host.WithStoreAsync(async store =>
        {
            await store.WriteStateAsync(SnapshotKeys.ClubApplications, new ClubApplicationSet(
                ImmutableSortedDictionary<string, ClubApplicant>.Empty.Add(Peer, new ClubApplicant(Peer, "Peery"))));
            await store.CommitAsync();
            return 0;
        });

        var api = host.Get<ExternalApiService>();
        api.Authenticate("wrong").Should().BeNull();
        api.Authenticate(Token)!.May(ExternalApiData.Players).Should().BeTrue();

        (await api.ReadPlayerAsync(Peer, CancellationToken.None))["has_applied"]!.GetValue<bool>().Should().BeTrue();
        (await api.ReadPlayerAsync(Other, CancellationToken.None))["has_applied"]!.GetValue<bool>().Should().BeFalse();
    }

    private static async Task<IReadOnlyList<ExternalEvent>> FeedAsync(ShadowTestHost host)
    {
        var scope = host.Services.CreateAsyncScope();
        await using (scope)
        {
            return await scope.ServiceProvider.GetRequiredService<IExternalEventStore>()
                .ReadExternalEventsAsync(0, [EventKeys.FriendRequestAccepted, EventKeys.FriendRequestReceived, "Custom:vip"], 100);
        }
    }
}

public sealed class SourceStatusTrackerTests
{
    private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _time = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

    private SourceStatusTracker Tracker()
    {
        var options = TestOptions.Valid();
        options.Sources["dragon"] = new SourceOptions { AlertAfter = TimeSpan.FromMinutes(5), UnhealthyAfter = TimeSpan.FromMinutes(30) };
        return new SourceStatusTracker(Microsoft.Extensions.Options.Options.Create(options), _time);
    }

    [Fact]
    public void ShortOutages_StayQuiet_LongOnesAreAnnouncedOnce_AndTheRecoveryToo()
    {
        var tracker = Tracker();
        tracker.Start("dragon");
        tracker.Connected("dragon").Should().BeNull();

        tracker.Down("dragon", "It answered 502.", refused: false).Should().BeNull();
        _time.Advance(TimeSpan.FromMinutes(4));
        tracker.Down("dragon", "It answered 502.", refused: false).Should().BeNull();
        _time.Advance(TimeSpan.FromMinutes(2));
        tracker.Down("dragon", "It answered 502.", refused: false)!.Title.Should().Be("Source dragon unreachable");
        tracker.Down("dragon", "It answered 502.", refused: false).Should().BeNull();

        tracker.Connected("dragon")!.Title.Should().Be("Source dragon is back");
        tracker.Down("dragon", "Again.", refused: false).Should().BeNull();
    }

    [Fact]
    public void ARefusal_IsAnnouncedAtOnce_WithAPing()
    {
        var tracker = Tracker();
        tracker.Start("dragon");

        tracker.Down("dragon", "It doesn't accept the token.", refused: true).Should().Match<OperatorAlertRaised>(alert => alert.Ping);
    }

    [Fact]
    public void Health_FailsOnlyAfterALongOutage()
    {
        var tracker = Tracker();
        tracker.Start("dragon");
        tracker.Connected("dragon");
        tracker.Down("dragon", "It answered 502.", refused: false);

        _time.Advance(TimeSpan.FromMinutes(29));
        tracker.IsHealthy().Should().BeTrue();
        _time.Advance(TimeSpan.FromMinutes(2));
        tracker.IsHealthy().Should().BeFalse();

        tracker.Connected("dragon");
        tracker.IsHealthy().Should().BeTrue();
    }
}
