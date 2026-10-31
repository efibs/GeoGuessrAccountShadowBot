// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;
using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Pipeline;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;
using GeoGuessrAccountShadowBot.Infrastructure.Persistence;
using GeoGuessrAccountShadowBot.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace GeoGuessrAccountShadowBot.Tests.Application;

public sealed class EventPipelineTests
{
    private const string Own = ShadowTestHost.OwnUserId;
    private const string Peer = ShadowTestHost.PeerId;

    private static ChatMessage Incoming(string id = "m1", string text = "hello", DateTimeOffset? sentAt = null) => new(
        id, ChatPayloadType.Text, text, Peer, Own, false, sentAt ?? new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero), $"{Own}:{Peer}");

    private static ChatMessage Outgoing(string id = "m2", string text = "hello") =>
        Incoming(id, text) with { SourceId = Own, RecipientId = Peer };

    private static FriendshipSet Friendships(string[]? friends = null, string[]? incoming = null, string[]? outgoing = null) => new(
        (friends ?? []).ToImmutableSortedSet(),
        (incoming ?? []).ToImmutableSortedSet(),
        (outgoing ?? []).ToImmutableSortedSet());

    [Fact]
    public async Task IncomingMessage_IsQueuedForThePartnersPost()
    {
        await using var host = await ShadowTestHost.CreateAsync();

        await host.ProcessAsync(new DirectMessageReceived(Incoming()));

        var line = (await host.ReadQueuedMessagesAsync()).Should().ContainSingle().Which.Should().BeOfType<ChatLine>().Subject;
        line.PartnerUserId.Should().Be(Peer);
        line.Origin.Should().Be(ChatLineOrigin.Partner);
        line.Text.Value.Should().Be("hello");
    }

    [Fact]
    public async Task TheSameMessageFromTwoSources_IsOnlyQueuedOnce()
    {
        await using var host = await ShadowTestHost.CreateAsync();

        await host.ProcessAsync(new DirectMessageReceived(Incoming()));
        await host.ProcessAsync(new DirectMessageReceived(Incoming()));

        (await host.ReadQueuedMessagesAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task OutgoingMessageFromAnotherLogin_IsMirrored()
    {
        await using var host = await ShadowTestHost.CreateAsync();

        await host.ProcessAsync(new DirectMessageReceived(Outgoing()));

        var line = (await host.ReadQueuedMessagesAsync()).Should().ContainSingle().Which.Should().BeOfType<ChatLine>().Subject;
        line.Origin.Should().Be(ChatLineOrigin.OtherLogin);
        line.PartnerUserId.Should().Be(Peer);
    }

    [Fact]
    public async Task EchoOfABotReply_IsConfirmedAndNotMirrored()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        await host.WithStoreAsync(async store =>
        {
            store.AddPendingReply(Peer, GeoGuessrAccountShadowBot.Application.Handlers.ChatText.Hash("hello"), 70, 80, host.Time.GetUtcNow());
            await store.CommitAsync();
            return true;
        });

        await host.ProcessAsync(new DirectMessageReceived(Outgoing()));

        var feedback = (await host.ReadQueuedMessagesAsync()).Should().ContainSingle().Which.Should().BeOfType<ReplyFeedback>().Subject;
        feedback.Should().Be(new ReplyFeedback(70, 80, ReplyFeedbackKind.Delivered, null));
    }

    [Fact]
    public async Task SecondIdenticalOutgoingMessage_IsMirroredBecauseOnlyOneReplyWasPending()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        await host.WithStoreAsync(async store =>
        {
            store.AddPendingReply(Peer, GeoGuessrAccountShadowBot.Application.Handlers.ChatText.Hash("hello"), 70, 80, host.Time.GetUtcNow());
            await store.CommitAsync();
            return true;
        });

        await host.ProcessAsync(new DirectMessageReceived(Outgoing("m2")));
        await host.ProcessAsync(new DirectMessageReceived(Outgoing("m3")));

        (await host.ReadQueuedMessagesAsync()).Select(message => message.GetType()).Should().Equal(typeof(ReplyFeedback), typeof(ChatLine));
    }

    [Theory]
    [InlineData(ChatPayloadType.Reaction)]
    [InlineData(ChatPayloadType.Divider)]
    [InlineData(ChatPayloadType.Disconnect)]
    [InlineData(ChatPayloadType.Unknown)]
    public async Task PayloadsNobodyWrote_AreNotMirrored(ChatPayloadType type)
    {
        await using var host = await ShadowTestHost.CreateAsync();

        await host.ProcessAsync(new DirectMessageReceived(Incoming() with { PayloadType = type }));

        (await host.ReadQueuedMessagesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task SwitchedOffEvent_IsRecordedButNotQueued()
    {
        await using var host = await ShadowTestHost.CreateAsync(options => options.Events[EventKeys.DmMessage].Enabled = false);

        await host.ProcessAsync(new DirectMessageReceived(Incoming()));

        (await host.ReadQueuedMessagesAsync()).Should().BeEmpty();
        (await host.WithStoreAsync(store => store.FindProcessedAsync([new DirectMessageReceived(Incoming()).DedupeKey])))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task SilentEvent_UpdatesStateWithoutNotifying()
    {
        await using var host = await ShadowTestHost.CreateAsync();

        await host.ProcessAsync(new DirectMessageReceived(Incoming()) { Silent = true });

        (await host.ReadQueuedMessagesAsync()).Should().BeEmpty();
        (await host.WithStoreAsync(store => store.FindProcessedAsync([new DirectMessageReceived(Incoming()).DedupeKey])))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task FirstFriendshipSnapshot_AnnouncesNoExistingRequests()
    {
        await using var host = await ShadowTestHost.CreateAsync();

        await host.ProcessAsync(new FriendshipsFetched(Friendships(friends: ["a"], incoming: ["b", "c"])));

        var queued = await host.ReadQueuedMessagesAsync();
        queued.Should().ContainSingle().Which.Should().BeOfType<StatusMessage>();
    }

    [Fact]
    public async Task NewRequest_GetsACard()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        await host.ProcessAsync(new FriendshipsFetched(Friendships()));

        await host.ProcessAsync(new FriendshipsFetched(Friendships(incoming: [Peer])));

        (await host.ReadQueuedMessagesAsync()).OfType<FriendRequestCard>().Should().ContainSingle().Which.UserId.Should().Be(Peer);
    }

    [Fact]
    public async Task AcceptedOutgoingRequest_IsAnnounced()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        await host.ProcessAsync(new FriendshipsFetched(Friendships(outgoing: [Peer])));

        await host.ProcessAsync(new FriendshipsFetched(Friendships(friends: [Peer])));

        (await host.ReadQueuedMessagesAsync()).OfType<EventCard>().Should().ContainSingle()
            .Which.EventKey.Should().Be(EventKeys.FriendRequestAccepted);
    }

    [Fact]
    public async Task NotificationAndSnapshotOfTheSameAcceptance_AnnounceItOnce()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        await host.ProcessAsync(new FriendshipsFetched(Friendships(outgoing: [Peer])));
        var notification = new AccountNotification("n1", NotificationType.FriendRequestAccepted, Peer, null, host.Time.GetUtcNow());

        await host.ProcessAsync(new AccountNotificationReceived(notification, NotificationClassifier.Classify(notification.Type).SwitchKey));
        await host.ProcessAsync(new FriendshipsFetched(Friendships(friends: [Peer])));
        await host.ProcessAsync(new FriendshipsFetched(Friendships(friends: [Peer])));

        (await host.ReadQueuedMessagesAsync()).OfType<EventCard>().Should().ContainSingle();
    }

    [Fact]
    public async Task AnsweredRequest_ClosesItsCardWithoutAnotherAnnouncement()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        await host.ProcessAsync(new FriendshipsFetched(Friendships(incoming: [Peer])));

        await host.ProcessAsync(new FriendshipsFetched(Friendships(friends: [Peer])));

        var queued = await host.ReadQueuedMessagesAsync();
        queued.OfType<FriendRequestOutcome>().Should().ContainSingle().Which.BecameFriend.Should().BeTrue();
        queued.OfType<EventCard>().Should().BeEmpty();
    }

    [Fact]
    public async Task GlitchySnapshot_ChangesNothing()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        string[] friends = ["a", "b", "c", "d", "e", "f"];
        await host.ProcessAsync(new FriendshipsFetched(Friendships(friends: friends)));

        await host.ProcessAsync(new FriendshipsFetched(Friendships()));
        await host.ProcessAsync(new FriendshipsFetched(Friendships(friends: friends)));

        (await host.ReadQueuedMessagesAsync()).OfType<EventCard>().Should().BeEmpty();
    }

    [Fact]
    public async Task RankedChange_IsAnnouncedOnlyAfterTheFirstSnapshot()
    {
        await using var host = await ShadowTestHost.CreateAsync(options =>
        {
            options.Events[EventKeys.RankedDivisionChanged].Enabled = true;
            options.Events[EventKeys.SeasonChanged].Enabled = true;
        });

        await host.ProcessAsync(new RankedFetched(new RankedSnapshot(1, "Bronze", 100, "s1")));
        (await host.ReadQueuedMessagesAsync()).Should().BeEmpty();

        await host.ProcessAsync(new RankedFetched(new RankedSnapshot(2, "Silver", 200, "s2")));

        (await host.ReadQueuedMessagesAsync()).OfType<EventCard>().Select(card => card.EventKey)
            .Should().BeEquivalentTo([EventKeys.RankedDivisionChanged, EventKeys.SeasonChanged]);
    }

    [Fact]
    public async Task ClubRosterChanges_ReachTheirOwnSwitches()
    {
        await using var host = await ShadowTestHost.CreateAsync(options =>
        {
            options.GeoGuessr.Club.Enabled = true;
            options.Events[EventKeys.ClubMemberJoined].Enabled = true;
        });

        await host.ProcessAsync(new ClubRosterFetched(ClubRoster.From([new("a", "n", 1), new("leaves", "n", 1)])));
        await host.ProcessAsync(new ClubRosterFetched(ClubRoster.From([new("a", "n", 1), new("joins", "n", 1)])));

        (await host.ReadQueuedMessagesAsync()).OfType<EventCard>().Should().ContainSingle()
            .Which.ActorUserId.Should().Be("joins");
    }

    [Fact]
    public async Task PoisonEvent_IsDroppedAndReported()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        var unhandled = new UnroutedEvent();

        await host.ProcessAsync(unhandled);
        await host.ProcessAsync(unhandled);

        var queued = await host.ReadQueuedMessagesAsync();
        queued.Should().ContainSingle().Which.Should().BeOfType<StatusMessage>();
    }

    [Fact]
    public async Task PresenceFlicker_IsNotAnnounced()
    {
        await using var host = await ShadowTestHost.CreateAsync(options => options.Events[EventKeys.FriendPresence].Enabled = true);

        await host.ProcessAsync(new FriendPresenceChanged(Peer, true, null));
        await host.ProcessAsync(new FriendPresenceChanged(Peer, false, null));
        await host.ProcessAsync(new FriendPresenceChanged(Peer, true, null));
        host.Time.Advance(TimeSpan.FromMinutes(2));
        await host.ProcessAsync(new FriendPresenceChanged(Peer, false, null));

        (await host.ReadQueuedMessagesAsync()).Should().HaveCount(2);
    }

    /// <summary>A key is needed, because the poison path marks keys as processed.</summary>
    private sealed record UnroutedEvent : ShadowEvent
    {
        public override string DedupeKey => "poison";
    }
}

public sealed class OutboxDispatcherTests
{
    private static readonly StatusMessage First = new("first", "detail", false);
    private static readonly StatusMessage Second = new("second", "detail", false);

    private static Task QueueAsync(ShadowTestHost host, params OutboxMessage[] messages) => host.WithStoreAsync(async store =>
    {
        foreach (var message in messages)
        {
            store.Enqueue(message);
        }

        await store.CommitAsync();
        return true;
    });

    [Fact]
    public async Task Delivers_OldestFirst()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        await QueueAsync(host, First, Second);

        await host.DrainOutboxAsync();

        host.Publisher.Delivered.Should().Equal(First, Second);
        (await host.ReadOutboxAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task TransientFailure_KeepsOrderAndRetriesLater()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        await QueueAsync(host, First, Second);

        host.Publisher.Failure = _ => new DeliveryResult(DeliveryStatus.Transient, "discord is down");
        await host.DrainOutboxAsync();
        host.Publisher.Delivered.Should().BeEmpty();

        host.Publisher.Failure = _ => null;
        await host.DrainOutboxAsync();

        host.Publisher.Delivered.Should().Equal(First, Second);
    }

    [Fact]
    public async Task PermanentFailure_SkipsThatMessageOnly()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        await QueueAsync(host, First, Second);
        host.Publisher.Failure = message => message == First ? new DeliveryResult(DeliveryStatus.Permanent, "channel gone") : null;

        await host.DrainOutboxAsync();

        host.Publisher.Delivered.Should().Equal(Second);
        (await host.ReadOutboxAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task EveryMessageType_SurvivesTheDatabase()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        OutboxMessage[] messages =
        [
            new EventCard(EventKeys.BadgeUnlocked, CardStyle.Success, "t", "d", "actor", new UntrustedText("body"), host.Time.GetUtcNow()),
            new ChatLine("peer", ChatLineOrigin.OtherLogin, new UntrustedText("text"), host.Time.GetUtcNow()),
            new FriendRequestCard("user"),
            new FriendRequestOutcome("user", true),
            new StatusMessage("t", "d", true),
            new ReplyFeedback(1, 2, ReplyFeedbackKind.NotSent, "reason")
        ];
        await QueueAsync(host, messages);

        (await host.ReadQueuedMessagesAsync()).Should().Equal(messages);
    }

    [Fact]
    public async Task DeliveredMessage_KeepsNoChatTextAtRest()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        await QueueAsync(host, new ChatLine("peer", ChatLineOrigin.Partner, new UntrustedText("secret"), host.Time.GetUtcNow()));

        await host.DrainOutboxAsync();

        var scope = host.Services.CreateAsyncScope();
        await using (scope)
        {
            scope.ServiceProvider.GetRequiredService<ShadowDbContext>().Outbox.Single().Payload.Should().BeNull();
        }
    }
}
