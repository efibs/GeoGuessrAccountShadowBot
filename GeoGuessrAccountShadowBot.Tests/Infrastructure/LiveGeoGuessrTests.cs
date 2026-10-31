// SPDX-License-Identifier: GPL-2.0-or-later

using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Infrastructure;
using GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Socket;
using GeoGuessrAccountShadowBot.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Tests.Infrastructure;

/// <summary>
/// Runs only when <c>GEOGUESSR_LIVE_NCFA</c> is set, so CI never touches GeoGuessr.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public const string TokenVariable = "GEOGUESSR_LIVE_NCFA";

    public LiveFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(TokenVariable)))
        {
            Skip = $"Set {TokenVariable} to run this against the real GeoGuessr.";
        }
    }
}

/// <summary>
/// The real client stack against the real GeoGuessr, reading only. These catch what mocks and fake
/// servers can't: a response that no longer matches the DTOs, or a change in the socket handshake.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveGeoGuessrTests : IAsyncLifetime
{
    private static readonly TimeSpan SocketTimeout = TimeSpan.FromSeconds(15);

    private readonly AccountContext _account = new();
    private ServiceProvider _services = null!;
    private IOptions<ShadowOptions> _options = null!;

    private IGeoGuessrApi Api => _services.GetRequiredService<IGeoGuessrApi>();

    public async Task InitializeAsync()
    {
        var options = TestOptions.Valid();
        options.GeoGuessr.NcfaToken = Environment.GetEnvironmentVariable(LiveFactAttribute.TokenVariable) ?? "unset";
        _options = Options.Create(options);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_account);
        services.AddSingleton(_options);
        services.AddSingleton(TimeProvider.System);
        services.AddShadowGeoGuessrHttp();
        _services = services.BuildServiceProvider();

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(LiveFactAttribute.TokenVariable)))
        {
            _account.Set(await Api.GetOwnUserIdAsync(), null);
        }
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    [LiveFact]
    public void OwnUserId_IsARealId()
    {
        _account.UserId.Should().MatchRegex("^[0-9a-f]{24}$");
    }

    [LiveFact]
    public async Task Friendships_Decode()
    {
        var friendships = await Api.GetFriendshipsAsync();

        friendships.Friends.Concat(friendships.Incoming).Should().AllSatisfy(id => id.Should().MatchRegex("^[0-9a-f]{24}$"));
    }

    [LiveFact]
    public async Task Notifications_Decode()
    {
        var notifications = await Api.GetNotificationsAsync(0, 20);

        notifications.Should().AllSatisfy(item => item.Id.Should().NotBeNullOrEmpty());
    }

    [LiveFact]
    public async Task ChatRooms_DecodeAndNameThePartner()
    {
        var rooms = await Api.GetChatRoomsAsync();

        rooms.Should().AllSatisfy(room =>
        {
            room.PeerId.Should().MatchRegex("^[0-9a-f]{24}$").And.NotBe(_account.UserId);
            room.Messages.Should().AllSatisfy(message =>
            {
                message.Id.Should().NotBeNullOrEmpty();
                message.PeerOf(_account.UserId).Should().Be(room.PeerId);
            });
        });
    }

    [LiveFact]
    public async Task ChatHistory_MatchesTheRooms()
    {
        var room = (await Api.GetChatRoomsAsync()).FirstOrDefault();
        if (room is null)
        {
            return;
        }

        var history = await Api.GetChatHistoryAsync(room.PeerId);

        history.Select(message => message.Id).Should().Contain(room.Messages.Select(message => message.Id));
        history.Should().BeInDescendingOrder(message => message.SentAt);
    }

    [LiveFact]
    public async Task PlayerNick_Resolves()
    {
        (await Api.GetPlayerNickAsync(_account.UserId)).Should().NotBeNullOrEmpty();
    }

    [LiveFact]
    public async Task OwnClub_DecodesEvenWithoutOne()
    {
        await Api.GetOwnClubIdAsync();
    }

    [LiveFact]
    public async Task Ranked_Decodes()
    {
        (await Api.GetRankedAsync(_account.UserId)).SeasonId.Should().NotBeNullOrEmpty();
    }

    [LiveFact]
    public async Task Socket_ConnectsSubscribesAndGetsTheAck()
    {
        using var timeout = new CancellationTokenSource(SocketTimeout);
        var session = await new ClientWebSocketConnector(_options).ConnectAsync(false, timeout.Token);

        await using (session)
        {
            await session.SendAsync(OutboundFrames.Subscribe(SocketTopics.Self(_account.UserId)), timeout.Token);
            await session.SendAsync(OutboundFrames.Subscribe(SocketTopics.FriendChat(_account.UserId)), timeout.Token);

            await foreach (var frame in session.ReadFramesAsync(timeout.Token))
            {
                if (FrameDecoder.Decode(frame) is DecodedFrame.Subscribed subscribed)
                {
                    subscribed.Topic.Should().Be(SocketTopics.FriendChat(_account.UserId));
                    return;
                }
            }
        }

        Assert.Fail("The socket ended without acknowledging the subscription.");
    }
}
