// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;
using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application.Connection;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Application.Reconciliation;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;
using GeoGuessrAccountShadowBot.Tests.TestSupport;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GeoGuessrAccountShadowBot.Tests.Application;

public sealed class ReconciliationTests
{
    private const string Own = ShadowTestHost.OwnUserId;
    private const string Peer = ShadowTestHost.PeerId;

    private static async Task<ShadowTestHost> CreateHostAsync()
    {
        var host = await ShadowTestHost.CreateAsync();
        host.Api.GetFriendshipsAsync(Arg.Any<CancellationToken>()).Returns(new FriendshipSet([], [], []));
        host.Api.GetChatRoomsAsync(Arg.Any<CancellationToken>()).Returns([]);
        host.Api.GetNotificationsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        return host;
    }

    private static Task RunAsync(ShadowTestHost host, ReconcileScope scope = ReconcileScope.All, bool detectMissed = false) =>
        RunAndProcessAsync(host, scope, detectMissed);

    private static async Task RunAndProcessAsync(ShadowTestHost host, ReconcileScope scope, bool detectMissed)
    {
        await host.Get<ReconciliationCoordinator>().RunAsync(scope, detectMissed, CancellationToken.None);
        await host.ProcessQueuedAsync();
    }

    private static ChatMessage Message(ShadowTestHost host, string id, TimeSpan age, string text = "hi") => new(
        id, ChatPayloadType.Text, text, Peer, Own, false, host.Time.GetUtcNow() - age, $"{Own}:{Peer}");

    private static void GivenRoom(ShadowTestHost host, params ChatMessage[] newestFirst) =>
        host.Api.GetChatRoomsAsync(Arg.Any<CancellationToken>())
            .Returns([new ChatRoomSummary(Peer, newestFirst.Length, newestFirst)]);

    private static AccountNotification Notification(ShadowTestHost host, string id, NotificationType type, TimeSpan? age = null) =>
        new(id, type, Peer, "nick", host.Time.GetUtcNow() - (age ?? TimeSpan.Zero));

    [Fact]
    public async Task FirstRun_RecordsChatSilently()
    {
        await using var host = await CreateHostAsync();
        GivenRoom(host, Message(host, "m1", TimeSpan.FromHours(1)));

        await RunAsync(host);

        (await host.ReadQueuedMessagesAsync()).OfType<ChatLine>().Should().BeEmpty();
    }

    [Fact]
    public async Task LaterRun_CatchesUpOnlyWhatIsNew()
    {
        await using var host = await CreateHostAsync();
        var old = Message(host, "m1", TimeSpan.FromHours(1));
        GivenRoom(host, old);
        await RunAsync(host);

        GivenRoom(host, Message(host, "m2", TimeSpan.FromMinutes(1), "while offline"), old);
        await RunAsync(host);

        var line = (await host.ReadQueuedMessagesAsync()).OfType<ChatLine>().Should().ContainSingle().Subject;
        line.Text.Value.Should().Be("while offline");
    }

    [Fact]
    public async Task CatchUp_DeliversOldestFirst()
    {
        await using var host = await CreateHostAsync();
        await RunAsync(host);

        GivenRoom(host, Message(host, "m3", TimeSpan.FromMinutes(1), "third"), Message(host, "m2", TimeSpan.FromMinutes(2), "second"), Message(host, "m1", TimeSpan.FromMinutes(3), "first"));
        await RunAsync(host);

        (await host.ReadQueuedMessagesAsync()).OfType<ChatLine>().Select(line => line.Text.Value)
            .Should().Equal("first", "second", "third");
    }

    [Fact]
    public async Task IncompleteRoom_ReadsTheFullHistory()
    {
        await using var host = await CreateHostAsync();
        await RunAsync(host);

        var message = Message(host, "m1", TimeSpan.FromMinutes(1), "from history");
        host.Api.GetChatRoomsAsync(Arg.Any<CancellationToken>()).Returns([new ChatRoomSummary(Peer, 5, [])]);
        host.Api.GetChatHistoryAsync(Peer, Arg.Any<CancellationToken>()).Returns([message]);
        await RunAsync(host);

        (await host.ReadQueuedMessagesAsync()).OfType<ChatLine>().Should().ContainSingle();
    }

    [Fact]
    public async Task VeryOldMessages_AreRecordedSilently()
    {
        await using var host = await CreateHostAsync();
        await RunAsync(host);

        GivenRoom(host, Message(host, "m1", TimeSpan.FromDays(30)));
        await RunAsync(host);

        (await host.ReadQueuedMessagesAsync()).OfType<ChatLine>().Should().BeEmpty();
    }

    [Fact]
    public async Task FirstRun_RecordsNotificationsSilently_AndLaterOnesNotify()
    {
        await using var host = await CreateHostAsync();
        host.Api.GetNotificationsAsync(Arg.Is(0), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Notification(host, "n1", NotificationType.GameInvite)]);
        await RunAsync(host);
        (await host.ReadQueuedMessagesAsync()).OfType<EventCard>().Should().BeEmpty();

        host.Api.GetNotificationsAsync(Arg.Is(0), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Notification(host, "n2", NotificationType.UnlockedBadge), Notification(host, "n1", NotificationType.GameInvite)]);
        await RunAsync(host);

        (await host.ReadQueuedMessagesAsync()).OfType<EventCard>().Should().ContainSingle()
            .Which.EventKey.Should().Be(EventKeys.BadgeUnlocked);
    }

    [Fact]
    public async Task Notifications_WalkPagesUntilOneIsFullyKnown()
    {
        await using var host = await CreateHostAsync();
        await RunAsync(host);

        host.Api.GetNotificationsAsync(Arg.Is(0), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Notification(host, "n3", NotificationType.GameInvite)]);
        host.Api.GetNotificationsAsync(Arg.Is(1), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Notification(host, "n2", NotificationType.GameInvite)]);
        await RunAsync(host);

        (await host.ReadQueuedMessagesAsync()).OfType<EventCard>().Should().HaveCount(2);
        await host.Api.Received().GetNotificationsAsync(Arg.Is(2), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Notifications_StopAtTheConfiguredPageLimit()
    {
        await using var host = await CreateHostAsync();
        await RunAsync(host);

        var page = 0;
        host.Api.GetNotificationsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<AccountNotification>)[Notification(host, $"n{page++}", NotificationType.GameInvite)]);
        await RunAsync(host);

        (await host.ReadQueuedMessagesAsync()).OfType<EventCard>().Should().HaveCount(host.Options.Polling.MaxNotificationPages);
    }

    [Fact]
    public async Task FriendNotification_TriggersAFriendshipRead()
    {
        await using var host = await CreateHostAsync();
        await RunAsync(host);
        host.Api.ClearReceivedCalls();

        host.Api.GetNotificationsAsync(Arg.Is(0), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Notification(host, "n1", NotificationType.FriendRequest)]);
        await RunAsync(host, ReconcileScope.Notifications);

        await host.Api.Received(1).GetFriendshipsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneFailingEndpoint_DoesNotStopTheOthers()
    {
        await using var host = await CreateHostAsync();
        host.Api.GetFriendshipsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("down"));

        await RunAsync(host);

        await host.Api.Received().GetChatRoomsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RejectedToken_IsReported()
    {
        await using var host = await CreateHostAsync();
        var reported = false;
        host.Get<ConnectionStatus>().UnauthorizedReported += () => reported = true;
        host.Api.GetFriendshipsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new GeoGuessrUnauthorizedException("rejected"));

        await RunAsync(host);

        reported.Should().BeTrue();
    }

    [Fact]
    public async Task OldUnseenMessageWhileConnected_ReplacesTheSocket()
    {
        await using var host = await CreateHostAsync();
        await RunAsync(host);
        var requested = false;
        host.Get<ConnectionStatus>().ReconnectRequested += () => requested = true;

        GivenRoom(host, Message(host, "m1", TimeSpan.FromMinutes(5)));
        await RunAsync(host, detectMissed: true);

        requested.Should().BeTrue();
    }

    [Fact]
    public async Task CatchUpAfterConnecting_DoesNotMistakeTheOutageForADeafSocket()
    {
        await using var host = await CreateHostAsync();
        await RunAsync(host);
        var requested = false;
        host.Get<ConnectionStatus>().ReconnectRequested += () => requested = true;

        GivenRoom(host, Message(host, "m1", TimeSpan.FromMinutes(5)));
        await RunAsync(host, detectMissed: false);

        requested.Should().BeFalse();
    }

    [Fact]
    public async Task SwitchedOffChat_IsNotRead()
    {
        await using var host = await ShadowTestHost.CreateAsync(options => options.Events[EventKeys.DmMessage].Enabled = false);
        host.Api.GetFriendshipsAsync(Arg.Any<CancellationToken>()).Returns(new FriendshipSet([], [], []));
        host.Api.GetNotificationsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        await RunAsync(host);

        await host.Api.DidNotReceive().GetChatRoomsAsync(Arg.Any<CancellationToken>());
    }
}
