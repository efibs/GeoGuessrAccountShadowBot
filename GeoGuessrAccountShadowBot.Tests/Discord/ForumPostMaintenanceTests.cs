// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using AwesomeAssertions;
using Discord;
using Discord.Net;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Discord.Output;
using GeoGuessrAccountShadowBot.Discord.Rendering;
using GeoGuessrAccountShadowBot.Domain.Model;
using GeoGuessrAccountShadowBot.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GeoGuessrAccountShadowBot.Tests.Discord;

public sealed class ForumPostMaintenanceTests
{
    private const string Peer = ShadowTestHost.PeerId;
    private const ulong ThreadId = 70;
    private const ulong BotId = 900;

    private static readonly UntrustedText OldNick = new("Old Name");
    private static readonly UntrustedText NewNick = new("New Name");

    private readonly IThreadChannel _thread = Substitute.For<IThreadChannel>();
    private readonly IUserMessage _starter = Substitute.For<IUserMessage>();

    public ForumPostMaintenanceTests()
    {
        _thread.Id.Returns(ThreadId);
        _thread.GetMessageAsync(ThreadId, Arg.Any<CacheMode>(), Arg.Any<RequestOptions>()).Returns(_starter);
        var bot = Author(BotId);
        _starter.Author.Returns(bot);
        _starter.Content.Returns(CardRenderer.WithProfileLink("first message", Peer));
    }

    private static IUser Author(ulong id)
    {
        var author = Substitute.For<IUser>();
        author.Id.Returns(id);
        return author;
    }

    private static HttpException DiscordError(HttpStatusCode status)
    {
        var request = Substitute.For<IRequest>();
        return new HttpException(status, request);
    }

    private async Task<PartnerThread?> RefreshAsync(ShadowTestHost host, UntrustedText nick, TimeSpan sinceLastCheck)
    {
        var mapped = new PartnerThread(Peer, ThreadId, OldNick.Value, host.Time.GetUtcNow() - sinceLastCheck);

        return await host.WithStoreAsync(async store =>
        {
            await store.SavePartnerThreadAsync(mapped);
            await store.CommitAsync();

            var maintenance = new ForumPostMaintenance(store, host.Time, NullLogger<ForumPostMaintenance>.Instance);
            await maintenance.RefreshIfDueAsync(_thread, mapped, nick, BotId, CancellationToken.None);
            await store.CommitAsync();

            return await store.FindPartnerThreadAsync(Peer);
        });
    }

    [Fact]
    public async Task BeforeTheCheckIsDue_NothingHappens()
    {
        await using var host = await ShadowTestHost.CreateAsync();

        await RefreshAsync(host, NewNick, TimeSpan.FromHours(23));

        await _thread.DidNotReceiveWithAnyArgs().ModifyAsync(default!);
        await _starter.DidNotReceiveWithAnyArgs().ModifyAsync(default!);
    }

    [Fact]
    public async Task Rename_UpdatesTheTitleAndRemembersIt()
    {
        await using var host = await ShadowTestHost.CreateAsync();

        var saved = await RefreshAsync(host, NewNick, TimeSpan.FromHours(25));

        await _thread.ReceivedWithAnyArgs(1).ModifyAsync(default!);
        saved!.TitleNick.Should().Be(NewNick.Value);
        saved.TitleCheckedAt.Should().Be(host.Time.GetUtcNow());
    }

    [Fact]
    public async Task UnchangedName_LeavesTheTitleAlone()
    {
        await using var host = await ShadowTestHost.CreateAsync();

        var saved = await RefreshAsync(host, OldNick, TimeSpan.FromHours(25));

        await _thread.DidNotReceiveWithAnyArgs().ModifyAsync(default!);
        saved!.TitleCheckedAt.Should().Be(host.Time.GetUtcNow());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task FailedRename_NeverFailsTheDelivery_AndStaysDue(HttpStatusCode status)
    {
        await using var host = await ShadowTestHost.CreateAsync();
        _thread.ModifyAsync(default!).ThrowsAsyncForAnyArgs(DiscordError(status));

        var saved = await RefreshAsync(host, NewNick, TimeSpan.FromHours(25));

        saved!.TitleNick.Should().Be(OldNick.Value);
        saved.TitleCheckedAt.Should().BeBefore(host.Time.GetUtcNow() - TimeSpan.FromHours(24));
    }

    [Fact]
    public async Task OlderPost_GetsTheProfileLink()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        _starter.Content.Returns("first message");
        MessageProperties? edit = null;
        await _starter.ModifyAsync(Arg.Do<Action<MessageProperties>>(change =>
        {
            edit = new MessageProperties();
            change(edit);
        }), Arg.Any<RequestOptions>());

        await RefreshAsync(host, OldNick, TimeSpan.FromHours(25));

        edit!.Content.Value.Should().Be($"[GeoGuessr profile](https://www.geoguessr.com/user/{Peer})\nfirst message");
        edit.AllowedMentions.Value.AllowedTypes.Should().Be(AllowedMentionTypes.None);
    }

    [Fact]
    public async Task PostWithALink_IsNotEdited()
    {
        await using var host = await ShadowTestHost.CreateAsync();

        await RefreshAsync(host, OldNick, TimeSpan.FromHours(25));

        await _starter.DidNotReceiveWithAnyArgs().ModifyAsync(default!);
    }

    [Fact]
    public async Task AFirstMessageByAnyoneElse_IsNotEdited()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        var person = Author(BotId + 1);
        _starter.Author.Returns(person);
        _starter.Content.Returns("written by a person");

        await RefreshAsync(host, OldNick, TimeSpan.FromHours(25));

        await _starter.DidNotReceiveWithAnyArgs().ModifyAsync(default!);
    }
}

public sealed class ProfileLinkTests
{
    [Fact]
    public void FullFirstMessage_StillFitsDiscordsLimit()
    {
        var text = new string('x', MessageChunker.DefaultLimit);

        CardRenderer.WithProfileLink(text, new string('a', 24)).Length.Should().BeLessThanOrEqualTo(2000);
    }

    [Fact]
    public void LinkOfAnotherPlayer_DoesNotCount()
    {
        var text = CardRenderer.WithProfileLink("hello", "aaaa");

        CardRenderer.HasProfileLink(text, "aaaa").Should().BeTrue();
        CardRenderer.HasProfileLink(text, "bbbb").Should().BeFalse();
    }

    [Fact]
    public void OddUserId_CannotBreakOutOfTheLink()
    {
        CardRenderer.WithProfileLink("hello", "x) [evil](http://evil.example").Should().Contain("x%29%20%5Bevil%5D");
    }
}
