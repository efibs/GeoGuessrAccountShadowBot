// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application;
using GeoGuessrAccountShadowBot.Domain.Model;
using GeoGuessrAccountShadowBot.Host.Logging;
using GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Http;
using GeoGuessrAccountShadowBot.Tests.TestSupport;
using Microsoft.Extensions.Options;
using NSubstitute;
using Refit;

namespace GeoGuessrAccountShadowBot.Tests.Infrastructure;

public sealed class SessionHandlerTests
{
    private sealed class StubTransport(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private static HttpClient Client(StubTransport transport) =>
        new(new GeoGuessrSessionHandler(Options.Create(TestOptions.Valid())) { InnerHandler = transport });

    [Fact]
    public async Task Request_CarriesTheSession()
    {
        var transport = new StubTransport(HttpStatusCode.OK);

        await Client(transport).GetAsync(new Uri("https://www.geoguessr.com/api/v3/profiles"));

        transport.LastRequest!.Headers.GetValues("Cookie").Should().Equal("_ncfa=test-token");
        transport.LastRequest.Headers.GetValues("X-Client").Should().ContainSingle();
    }

    [Fact]
    public async Task OtherHosts_NeverGetTheSession()
    {
        var transport = new StubTransport(HttpStatusCode.OK);

        var act = () => Client(transport).GetAsync(new Uri("https://evil.example/"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        transport.LastRequest.Should().BeNull();
    }
}

public sealed class HttpGatewayTests
{
    private const string Own = ShadowTestHost.OwnUserId;
    private const string Peer = ShadowTestHost.PeerId;

    private static readonly CancellationToken None = CancellationToken.None;

    private readonly IGeoGuessrRestApi _api = Substitute.For<IGeoGuessrRestApi>();
    private readonly GeoGuessrHttpGateway _gateway;

    public HttpGatewayTests()
    {
        var account = new AccountContext();
        account.Set(Own, null);
        _gateway = new GeoGuessrHttpGateway(_api, account);
    }

    private static ChatMessageDto Message(string id) =>
        new(id, "Text", "hi", "User", Peer, Own, DateTimeOffset.UnixEpoch, $"{Own}:{Peer}");

    private void GivenUnreads(params (string Peer, ChatRoomDto Room)[] rooms) =>
        _api.GetUnreadsAsync(None).Returns(rooms.ToDictionary(room => room.Peer, room => room.Room));

    private void GivenSummary(int page, bool hasMore, params ChatRoomDto[] rooms) =>
        _api.GetFriendsSummaryAsync(page, None).Returns(new FriendsSummaryResponse(hasMore, [.. rooms]));

    [Fact]
    public async Task ChatRooms_ComeFromTheSummaryEvenWithNothingUnread()
    {
        GivenUnreads();
        GivenSummary(0, false, new ChatRoomDto($"{Own}:{Peer}", 1, [Message("m1")]));

        var rooms = await _gateway.GetChatRoomsAsync(None);

        rooms.Should().ContainSingle().Which.PeerId.Should().Be(Peer);
    }

    [Fact]
    public async Task ChatRooms_FindThePartnerWhicheverWayTheRoomIdIsOrdered()
    {
        GivenUnreads();
        GivenSummary(0, false, new ChatRoomDto($"{Peer}:{Own}", 0, []));

        (await _gateway.GetChatRoomsAsync(None)).Single().PeerId.Should().Be(Peer);
    }

    [Fact]
    public async Task ChatRooms_MergeSourcesWithoutDuplicates()
    {
        GivenUnreads((Peer, new ChatRoomDto($"{Own}:{Peer}", 1, [Message("m1")])));
        GivenSummary(0, false, new ChatRoomDto($"{Own}:{Peer}", 1, [Message("m1")]));

        (await _gateway.GetChatRoomsAsync(None)).Should().ContainSingle();
    }

    [Fact]
    public async Task ChatRooms_FollowSummaryPages()
    {
        var second = "cccccccccccccccccccccccc";
        GivenUnreads();
        GivenSummary(0, true, new ChatRoomDto($"{Own}:{Peer}", 0, []));
        GivenSummary(1, false, new ChatRoomDto($"{Own}:{second}", 0, []));

        (await _gateway.GetChatRoomsAsync(None)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Notifications_MapTheirType()
    {
        _api.GetNotificationsAsync(8, 0, None).Returns([new NotificationDto("n1", 9, new NotificationSenderDto(Peer, "nick"), DateTimeOffset.UnixEpoch)]);

        var notification = (await _gateway.GetNotificationsAsync(0, 8, None)).Single();

        notification.Type.Should().Be(NotificationType.FriendRequestAccepted);
        notification.SentById.Should().Be(Peer);
    }

    [Fact]
    public async Task Accept_UsesTheAcceptEndpoint()
    {
        var response = Substitute.For<IApiResponse>();
        response.IsSuccessStatusCode.Returns(true);
        _api.AcceptFriendRequestAsync(Peer, None).Returns(response);

        (await _gateway.AcceptFriendRequestAsync(Peer, None)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Accept_IsAPutBecauseAPostWouldSendANewRequest()
    {
        var method = typeof(IGeoGuessrRestApi).GetMethod(nameof(IGeoGuessrRestApi.AcceptFriendRequestAsync))!;

        method.GetCustomAttributes(typeof(PutAttribute), false).Should().ContainSingle();
    }

    [Fact]
    public async Task FailedAnswer_BecomesAnError()
    {
        var response = Substitute.For<IApiResponse>();
        response.IsSuccessStatusCode.Returns(false);
        response.StatusCode.Returns(HttpStatusCode.BadRequest);
        _api.DeclineFriendRequestAsync(Peer, None).Returns(response);

        (await _gateway.DeclineFriendRequestAsync(Peer, None)).Error!.Code.Should().Be("friend-request.decline_failed");
    }

    [Fact]
    public async Task NoClub_MeansNull()
    {
        var response = Substitute.For<IApiResponse<MyClubResponse>>();
        response.StatusCode.Returns(HttpStatusCode.NoContent);
        _api.GetOwnClubAsync(None).Returns(response);

        (await _gateway.GetOwnClubIdAsync(None)).Should().BeNull();
    }

    [Fact]
    public async Task UnknownChatPartner_HasNoHistory()
    {
        var response = Substitute.For<IApiResponse<ChatRoomDto>>();
        response.StatusCode.Returns(HttpStatusCode.NotFound);
        _api.GetChatRoomAsync(Peer, None).Returns(response);

        (await _gateway.GetChatHistoryAsync(Peer, None)).Should().BeEmpty();
    }
}

public sealed class SecretRedactorTests
{
    [Fact]
    public void Redact_RemovesConfiguredSecrets()
    {
        var redactor = new SecretRedactor(["my-secret-token"]);

        redactor.Redact("failed with my-secret-token here").Should().Be("failed with <redacted> here");
    }

    [Fact]
    public void Redact_RemovesEncodedForms()
    {
        var redactor = new SecretRedactor(["a+b/c="]);

        redactor.Redact("value a%2Bb%2Fc%3D").Should().Be("value <redacted>");
    }

    [Fact]
    public void Redact_RemovesAnySessionCookie()
    {
        new SecretRedactor([]).Redact("Cookie: _ncfa=abc123; other=1").Should().Be("Cookie: _ncfa=<redacted>; other=1");
    }

    [Fact]
    public void EmptySecrets_AreIgnored()
    {
        new SecretRedactor(["", null]).Redact("plain text").Should().Be("plain text");
    }
}
