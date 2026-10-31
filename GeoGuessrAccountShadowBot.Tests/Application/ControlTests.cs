// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;
using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application;
using GeoGuessrAccountShadowBot.Application.Connection;
using GeoGuessrAccountShadowBot.Application.Control;
using GeoGuessrAccountShadowBot.Application.Handlers;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain;
using GeoGuessrAccountShadowBot.Domain.Model;
using GeoGuessrAccountShadowBot.Infrastructure.Persistence;
using GeoGuessrAccountShadowBot.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace GeoGuessrAccountShadowBot.Tests.Application;

public sealed class DmReplyServiceTests
{
    private const string Peer = ShadowTestHost.PeerId;
    private const ulong ThreadId = 70;
    private const ulong MessageId = 80;

    private static async Task<ShadowTestHost> CreateHostAsync(Action<ShadowOptions>? configure = null)
    {
        var host = await ShadowTestHost.CreateAsync(options =>
        {
            options.Control.DmReply.Enabled = true;
            options.Discord.MessageContentIntent = true;
            configure?.Invoke(options);
        });

        host.Roles.GetRoleIdsAsync(ShadowTestHost.AuthorizedUser, Arg.Any<CancellationToken>()).Returns([ShadowTestHost.AuthorizedRole]);
        host.Sender.SendDirectMessageAsync(default!, default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(Result.Success());

        await host.WithStoreAsync(async store =>
        {
            await store.SavePartnerThreadAsync(new PartnerThread(Peer, ThreadId, "nick", host.Time.GetUtcNow()));
            await store.CommitAsync();
            return true;
        });

        return host;
    }

    private static ForumReply Reply(string text = "hello", ulong author = ShadowTestHost.AuthorizedUser, bool hasOtherContent = false, ulong threadId = ThreadId) =>
        new(threadId, MessageId, author, text, hasOtherContent);

    private static Task HandleAsync(ShadowTestHost host, ForumReply reply) =>
        host.Get<DmReplyService>().HandleAsync(reply, CancellationToken.None);

    private static async Task ShouldBeRefusedAsync(ShadowTestHost host, ReplyFeedbackKind kind, string reasonPart)
    {
        var feedback = (await host.ReadQueuedMessagesAsync()).Should().ContainSingle().Which.Should().BeOfType<ReplyFeedback>().Subject;
        feedback.Kind.Should().Be(kind);
        feedback.Reason.Should().Contain(reasonPart);
        await host.Sender.DidNotReceiveWithAnyArgs().SendDirectMessageAsync(default!, default!, default);
    }

    [Fact]
    public async Task AuthorizedReply_IsSentAndAwaitsItsEcho()
    {
        await using var host = await CreateHostAsync();

        await HandleAsync(host, Reply());

        await host.Sender.Received(1).SendDirectMessageAsync(Peer, "hello", Arg.Any<CancellationToken>());
        var pending = await host.WithStoreAsync(store => store.FindPendingReplyAsync(Peer, ChatText.Hash("hello")));
        pending!.MessageId.Should().Be(MessageId);
    }

    [Fact]
    public async Task PendingRow_IsCommittedBeforeTheSend()
    {
        await using var host = await CreateHostAsync();
        PendingReply? seenDuringSend = null;
        host.Sender.SendDirectMessageAsync(default!, default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(async _ =>
        {
            seenDuringSend = await host.WithStoreAsync(store => store.FindPendingReplyAsync(Peer, ChatText.Hash("hello")));
            return Result.Success();
        });

        await HandleAsync(host, Reply());

        seenDuringSend.Should().NotBeNull();
    }

    [Fact]
    public async Task IgnorePrefix_StaysInDiscord()
    {
        await using var host = await CreateHostAsync();

        await HandleAsync(host, Reply("// staff note"));

        (await host.ReadQueuedMessagesAsync()).Should().BeEmpty();
        await host.Sender.DidNotReceiveWithAnyArgs().SendDirectMessageAsync(default!, default!, default);
    }

    [Fact]
    public async Task SwitchedOff_DoesNothing()
    {
        await using var host = await CreateHostAsync(options => options.Control.DmReply.Enabled = false);

        await HandleAsync(host, Reply());

        await host.Sender.DidNotReceiveWithAnyArgs().SendDirectMessageAsync(default!, default!, default);
    }

    [Fact]
    public async Task UnauthorizedAuthor_IsRefusedAndAudited()
    {
        await using var host = await CreateHostAsync();
        host.Roles.GetRoleIdsAsync(999, Arg.Any<CancellationToken>()).Returns([]);

        await HandleAsync(host, Reply(author: 999));

        await ShouldBeRefusedAsync(host, ReplyFeedbackKind.Refused, "role");
        var scope = host.Services.CreateAsyncScope();
        await using (scope)
        {
            var audit = await scope.ServiceProvider.GetRequiredService<ShadowDbContext>().ControlAudits.SingleAsync();
            audit.Outcome.Should().Be("control.forbidden");
            audit.DiscordUserId.Should().Be(999);
        }
    }

    [Fact]
    public async Task UnlinkedPost_IsRefused()
    {
        await using var host = await CreateHostAsync();

        await HandleAsync(host, Reply(threadId: 1234));

        await ShouldBeRefusedAsync(host, ReplyFeedbackKind.Refused, "isn't linked");
    }

    [Fact]
    public async Task TooLongReply_IsRefusedRatherThanCut()
    {
        await using var host = await CreateHostAsync();

        await HandleAsync(host, Reply(new string('x', 301)));

        await ShouldBeRefusedAsync(host, ReplyFeedbackKind.Refused, "300");
    }

    [Fact]
    public async Task ExactlyTheLimit_IsSent()
    {
        await using var host = await CreateHostAsync();

        await HandleAsync(host, Reply(new string('x', 300)));

        await host.Sender.ReceivedWithAnyArgs(1).SendDirectMessageAsync(default!, default!, default);
    }

    [Fact]
    public async Task Attachments_AreRefused()
    {
        await using var host = await CreateHostAsync();

        await HandleAsync(host, Reply(hasOtherContent: true));

        await ShouldBeRefusedAsync(host, ReplyFeedbackKind.Refused, "only carry text");
    }

    [Fact]
    public async Task EmptyMessage_IsRefused()
    {
        await using var host = await CreateHostAsync();

        await HandleAsync(host, Reply("   "));

        await ShouldBeRefusedAsync(host, ReplyFeedbackKind.Refused, "no text");
    }

    [Fact]
    public async Task RateLimit_RefusesTheNextMessageAndRecoversAfterAMinute()
    {
        await using var host = await CreateHostAsync(options => options.Control.DmReply.MaxPerMinute = 1);

        await HandleAsync(host, Reply("one"));
        await HandleAsync(host, Reply("two"));
        await host.Sender.ReceivedWithAnyArgs(1).SendDirectMessageAsync(default!, default!, default);

        host.Time.Advance(TimeSpan.FromMinutes(1));
        await HandleAsync(host, Reply("three"));

        await host.Sender.ReceivedWithAnyArgs(2).SendDirectMessageAsync(default!, default!, default);
    }

    [Fact]
    public async Task WhileYielded_RefusesAndNamesTheNextAttempt()
    {
        await using var host = await CreateHostAsync();
        host.Get<ConnectionStatus>().Update(new ConnectionState(ConnectionPhase.Polling, PollingReason.Superseded, Delay: TimeSpan.FromMinutes(15)));

        await HandleAsync(host, Reply());

        await ShouldBeRefusedAsync(host, ReplyFeedbackKind.NotSent, "<t:");
        (await host.WithStoreAsync(store => store.FindPendingReplyAsync(Peer, ChatText.Hash("hello")))).Should().BeNull();
    }

    [Fact]
    public async Task WhileAuthFailed_Refuses()
    {
        await using var host = await CreateHostAsync();
        host.Get<ConnectionStatus>().Update(ConnectionPolicy.AuthFailed);

        await HandleAsync(host, Reply());

        await ShouldBeRefusedAsync(host, ReplyFeedbackKind.NotSent, "isn't connected");
    }

    [Fact]
    public async Task WhileReconnecting_WaitsForTheSocket()
    {
        await using var host = await CreateHostAsync();
        var status = host.Get<ConnectionStatus>();
        status.Update(new ConnectionState(ConnectionPhase.Reconnecting));

        var handling = HandleAsync(host, Reply());
        await Task.Delay(20);
        status.Update(new ConnectionState(ConnectionPhase.Connected));
        await handling.WaitAsync(TimeSpan.FromSeconds(5));

        await host.Sender.ReceivedWithAnyArgs(1).SendDirectMessageAsync(default!, default!, default);
    }

    [Fact]
    public async Task FailedSend_LeavesNothingPending()
    {
        await using var host = await CreateHostAsync();
        host.Sender.SendDirectMessageAsync(default!, default!, Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs((Result)new Error(ErrorKind.Unavailable, "chat.offline", "offline"));

        await HandleAsync(host, Reply());

        (await host.WithStoreAsync(store => store.FindPendingReplyAsync(Peer, ChatText.Hash("hello")))).Should().BeNull();
        (await host.ReadQueuedMessagesAsync()).OfType<ReplyFeedback>().Should().ContainSingle().Which.Kind.Should().Be(ReplyFeedbackKind.NotSent);
    }
}

public sealed class PendingReplyMonitorTests
{
    private const string Own = ShadowTestHost.OwnUserId;
    private const string Peer = ShadowTestHost.PeerId;

    private static async Task<ShadowTestHost> CreateHostWithPendingAsync()
    {
        var host = await ShadowTestHost.CreateAsync();
        await host.WithStoreAsync(async store =>
        {
            store.AddPendingReply(Peer, ChatText.Hash("hello"), 70, 80, host.Time.GetUtcNow());
            await store.CommitAsync();
            return true;
        });
        host.Time.Advance(TimeSpan.FromSeconds(20));
        return host;
    }

    private static Task SettleAsync(ShadowTestHost host) =>
        host.Services.GetServices<IHostedService>().OfType<PendingReplyMonitor>().Single().SettleExpiredAsync(CancellationToken.None);

    [Fact]
    public async Task MessageFoundInHistory_CountsAsDelivered()
    {
        await using var host = await CreateHostWithPendingAsync();
        host.Api.GetChatHistoryAsync(Peer, Arg.Any<CancellationToken>()).Returns(
            [new ChatMessage("m1", ChatPayloadType.Text, "hello", Own, Peer, false, host.Time.GetUtcNow(), "room")]);

        await SettleAsync(host);

        (await host.ReadQueuedMessagesAsync()).OfType<ReplyFeedback>().Should().ContainSingle().Which.Kind.Should().Be(ReplyFeedbackKind.Delivered);
    }

    [Fact]
    public async Task MessageNotInHistory_IsReportedAsUnconfirmed()
    {
        await using var host = await CreateHostWithPendingAsync();
        host.Api.GetChatHistoryAsync(Peer, Arg.Any<CancellationToken>()).Returns([]);

        await SettleAsync(host);

        (await host.ReadQueuedMessagesAsync()).OfType<ReplyFeedback>().Should().ContainSingle().Which.Kind.Should().Be(ReplyFeedbackKind.Unconfirmed);
        (await host.WithStoreAsync(store => store.FindPendingReplyAsync(Peer, ChatText.Hash("hello")))).Should().BeNull();
    }

    [Fact]
    public async Task RecentPending_IsLeftAlone()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        await host.WithStoreAsync(async store =>
        {
            store.AddPendingReply(Peer, ChatText.Hash("hello"), 70, 80, host.Time.GetUtcNow());
            await store.CommitAsync();
            return true;
        });

        await SettleAsync(host);

        (await host.ReadQueuedMessagesAsync()).Should().BeEmpty();
    }
}

public sealed class AccountControlServiceTests
{
    private const string Requester = "cccccccccccccccccccccccc";

    private static async Task<ShadowTestHost> CreateHostAsync(string[] incoming)
    {
        var host = await ShadowTestHost.CreateAsync(options =>
        {
            options.Control.FriendRequestButtons.Enabled = true;
            options.Control.ClubChat.Enabled = true;
            options.GeoGuessr.Club.Enabled = true;
        });

        host.Roles.GetRoleIdsAsync(ShadowTestHost.AuthorizedUser, Arg.Any<CancellationToken>()).Returns([ShadowTestHost.AuthorizedRole]);
        host.Api.GetFriendshipsAsync(Arg.Any<CancellationToken>())
            .Returns(new FriendshipSet([], incoming.ToImmutableSortedSet(), []));
        host.Api.AcceptFriendRequestAsync(default!, default).ReturnsForAnyArgs(Result.Success());
        host.Api.DeclineFriendRequestAsync(default!, default).ReturnsForAnyArgs(Result.Success());
        host.Sender.SendClubMessageAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());
        return host;
    }

    private static AccountControlService Control(ShadowTestHost host) => host.Get<AccountControlService>();

    [Fact]
    public async Task Accept_AcceptsAPendingRequest()
    {
        await using var host = await CreateHostAsync([Requester]);

        var result = await Control(host).AcceptFriendRequestAsync(ShadowTestHost.AuthorizedUser, Requester, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await host.Api.Received(1).AcceptFriendRequestAsync(Requester, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Decline_OnAStaleCard_NeverTouchesTheFriendship()
    {
        await using var host = await CreateHostAsync([]);

        var result = await Control(host).DeclineFriendRequestAsync(ShadowTestHost.AuthorizedUser, Requester, CancellationToken.None);

        result.Error!.Code.Should().Be("friend-request.not_pending");
        await host.Api.DidNotReceiveWithAnyArgs().DeclineFriendRequestAsync(default!, default);
    }

    [Fact]
    public async Task Decline_NeedsARole()
    {
        await using var host = await CreateHostAsync([Requester]);
        host.Roles.GetRoleIdsAsync(999, Arg.Any<CancellationToken>()).Returns([]);

        var result = await Control(host).DeclineFriendRequestAsync(999, Requester, CancellationToken.None);

        result.Error!.Kind.Should().Be(ErrorKind.Forbidden);
        await host.Api.DidNotReceiveWithAnyArgs().DeclineFriendRequestAsync(default!, default);
    }

    [Fact]
    public async Task ClubSay_Posts()
    {
        await using var host = await CreateHostAsync([]);
        host.Get<AccountContext>().Set(ShadowTestHost.OwnUserId, "club1");

        var result = await Control(host).SendClubMessageAsync(ShadowTestHost.AuthorizedUser, "hello club", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await host.Sender.Received(1).SendClubMessageAsync("club1", "hello club", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("club1", "   ", "club-chat.length")]
    [InlineData(null, "hello", "club-chat.no_club")]
    public async Task ClubSay_RefusesWhatCannotBeSent(string? clubId, string text, string expectedCode)
    {
        await using var host = await CreateHostAsync([]);
        host.Get<AccountContext>().Set(ShadowTestHost.OwnUserId, clubId);

        var result = await Control(host).SendClubMessageAsync(ShadowTestHost.AuthorizedUser, text, CancellationToken.None);

        result.Error!.Code.Should().Be(expectedCode);
    }

    [Fact]
    public async Task ClubSay_RefusesWhileOffline()
    {
        await using var host = await CreateHostAsync([]);
        host.Get<AccountContext>().Set(ShadowTestHost.OwnUserId, "club1");
        host.Get<ConnectionStatus>().Update(new ConnectionState(ConnectionPhase.Polling, PollingReason.Superseded));

        var result = await Control(host).SendClubMessageAsync(ShadowTestHost.AuthorizedUser, "hello", CancellationToken.None);

        result.Error!.Kind.Should().Be(ErrorKind.Unavailable);
    }

    [Fact]
    public async Task EmptyRoleList_AllowsNobody()
    {
        await using var host = await CreateHostAsync([Requester]);
        host.Options.Control.AuthorizedRoleIds = [];

        var result = await Control(host).AcceptFriendRequestAsync(ShadowTestHost.AuthorizedUser, Requester, CancellationToken.None);

        result.Error!.Kind.Should().Be(ErrorKind.Forbidden);
        await host.Roles.DidNotReceiveWithAnyArgs().GetRoleIdsAsync(default);
    }
}

public sealed class AccountInitializerTests
{
    private const string Own = ShadowTestHost.OwnUserId;

    [Fact]
    public async Task RefusesADatabaseOfAnotherAccount()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        host.Api.GetOwnUserIdAsync(Arg.Any<CancellationToken>()).Returns(Own);
        await host.Get<AccountInitializer>().InitializeAsync(CancellationToken.None);

        host.Api.GetOwnUserIdAsync(Arg.Any<CancellationToken>()).Returns("dddddddddddddddddddddddd");
        var act = () => host.Get<AccountInitializer>().InitializeAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*different GeoGuessr account*");
    }

    [Fact]
    public async Task LooksUpTheClubOnlyWhenEnabled()
    {
        await using var host = await ShadowTestHost.CreateAsync(options => options.GeoGuessr.Club.Enabled = true);
        host.Api.GetOwnUserIdAsync(Arg.Any<CancellationToken>()).Returns(Own);
        host.Api.GetOwnClubIdAsync(Arg.Any<CancellationToken>()).Returns("club1");

        await host.Get<AccountInitializer>().InitializeAsync(CancellationToken.None);

        host.Get<AccountContext>().ClubId.Should().Be("club1");
    }
}
