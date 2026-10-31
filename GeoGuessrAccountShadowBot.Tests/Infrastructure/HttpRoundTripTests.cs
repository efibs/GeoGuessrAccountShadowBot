// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Text;
using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Model;
using GeoGuessrAccountShadowBot.Infrastructure;
using GeoGuessrAccountShadowBot.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Tests.Infrastructure;

/// <summary>
/// Sends real HTTP through the real client stack to a local server. The JSON matches responses
/// observed live, in shape, so these prove the DTOs, routes and verbs rather than a mock of them.
/// </summary>
public sealed class HttpRoundTripTests : IAsyncLifetime
{
    private const string Own = ShadowTestHost.OwnUserId;
    private const string Peer = ShadowTestHost.PeerId;

    private readonly Dictionary<string, (int Status, string Body)> _responses = [];
    private readonly List<string> _requests = [];
    private readonly List<string?> _cookies = [];
    private readonly AccountContext _account = new();

    private WebApplication _server = null!;
    private ServiceProvider _services = null!;

    private IGeoGuessrApi Api => _services.GetRequiredService<IGeoGuessrApi>();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        _server = builder.Build();
        _server.Run(context =>
        {
            var request = $"{context.Request.Method} {context.Request.Path}{context.Request.QueryString}";
            _requests.Add(request);
            _cookies.Add(context.Request.Headers.Cookie);

            if (!_responses.TryGetValue(request, out var response))
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return context.Response.WriteAsync($"unexpected request: {request}");
            }

            context.Response.StatusCode = response.Status;
            context.Response.ContentType = "application/json";
            return response.Body.Length == 0 ? Task.CompletedTask : context.Response.WriteAsync(response.Body, Encoding.UTF8);
        });
        await _server.StartAsync();

        var address = _server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var options = TestOptions.Valid();
        options.GeoGuessr.ApiBaseUrl = new Uri(address);

        _account.Set(Own, null);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(_account);
        services.AddSingleton<IOptions<ShadowOptions>>(Options.Create(options));
        services.AddSingleton(TimeProvider.System);
        services.AddShadowGeoGuessrHttp();
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _server.DisposeAsync();
    }

    private void Given(string request, string body, int status = 200) => _responses[request] = (status, body);

    private static string MessageJson(string id, string text) => $$"""
        {"id":"{{id}}","payloadType":"Text","textPayload":"{{text}}","invitePayload":null,"recipientType":"User",
         "recipientId":"{{Own}}","sourceType":"User","sourceId":"{{Peer}}","sentAt":"2026-10-31T00:21:22.1050000Z",
         "roomId":"{{Own}}:{{Peer}}","context":"Friend","clubPayload":null,"pollPayload":null,
         "highlightPayload":null,"reactions":null,"isBlocked":false}
        """;

    [Fact]
    public async Task OwnUserId_ReadsTheProfile()
    {
        Given("GET /api/v3/profiles", $$"""{"user":{"nick":"n","id":"{{Own}}","deviceToken":"secret"},"isBanned":false}""");

        (await Api.GetOwnUserIdAsync()).Should().Be(Own);
        _cookies.Should().ContainSingle().Which.Should().Be("_ncfa=test-token");
    }

    [Fact]
    public async Task Friendships_ReadAllThreeLists()
    {
        Given("GET /api/v3/social/friendships", $$"""{"friendIds":["{{Own}}"],"receivedFriendRequests":["r"],"sentFriendRequests":["s"],"receivedFriendRequestMessages":{} }""");

        var friendships = await Api.GetFriendshipsAsync();

        friendships.Friends.Should().Equal(Own);
        friendships.Incoming.Should().Equal("r");
        friendships.Outgoing.Should().Equal("s");
    }

    [Fact]
    public async Task Notifications_SendPagingAndReadTheType()
    {
        Given("GET /api/v4/notifications?count=8&page=2", $$"""[{"id":"n1","type":9,"sentBy":{"id":"{{Peer}}","nick":"nick","isVerified":false},"sentAt":"2026-10-31T00:14:53.1000000Z","isRead":false,"payload":"{}"}]""");

        var notification = (await Api.GetNotificationsAsync(2, 8)).Single();

        notification.Type.Should().Be(NotificationType.FriendRequestAccepted);
        notification.SentByNick.Should().Be("nick");
    }

    [Fact]
    public async Task ChatRooms_MergeUnreadsAndSummary()
    {
        var other = "cccccccccccccccccccccccc";
        Given("GET /api/v4/chat/unreads", $$"""{"{{Peer}}":{"roomId":"{{Own}}:{{Peer}}","revision":"current","messages":[{{MessageJson("m1", "unread")}}],"hasUnreads":true,"unreadCount":1,"messageCount":1} }""");
        Given("GET /api/v3/social/friends/summary?fast=true&page=0", $$"""{"friends":[],"friendsCount":0,"hasMore":false,"friendChatRooms":[{"roomId":"{{Own}}:{{other}}","messages":[],"messageCount":0}],"requests":[]}""");

        var rooms = await Api.GetChatRoomsAsync();

        rooms.Select(room => room.PeerId).Should().BeEquivalentTo([Peer, other]);
        var message = rooms.Single(room => room.PeerId == Peer).Messages.Single();
        message.Text.Should().Be("unread");
        message.SentAt.Should().Be(new DateTimeOffset(2026, 10, 31, 0, 21, 22, 105, TimeSpan.Zero));
    }

    [Fact]
    public async Task ChatHistory_ReadsTheRoom()
    {
        Given($"GET /api/v4/chat/{Peer}", $$"""{"roomId":"{{Own}}:{{Peer}}","messages":[{{MessageJson("m1", "hello")}}],"messageCount":1}""");

        (await Api.GetChatHistoryAsync(Peer)).Single().Id.Should().Be("m1");
    }

    [Fact]
    public async Task ChatHistory_OfAStrangerIsEmpty()
    {
        Given($"GET /api/v4/chat/{Peer}", "", 404);

        (await Api.GetChatHistoryAsync(Peer)).Should().BeEmpty();
    }

    [Fact]
    public async Task PlayerNick_ReadsTheNestedName()
    {
        Given($"GET /api/v4/player-identities/{Peer}", """{"player":{"id":"x","nick":"The Nick","isDeleted":false},"avatar":{"equipped":[]}}""");

        (await Api.GetPlayerNickAsync(Peer)).Should().Be("The Nick");
    }

    private static string UserJson(string club = "null", string customImage = "null") => $$"""
        {"nick":"The Nick","created":"2026-02-20T10:00:00.000Z","isProUser":true,"type":"Unlimited","isVerified":false,
         "pin":{"url":"pin/abc.png","anchor":"center-center","isDefault":false},"customImage":{{customImage}},"url":"/user/{{Peer}}",
         "id":"{{Peer}}","countryCode":"de","progress":{"xp":1,"level":28},"isBanned":false,"chatBan":false,
         "suspendedUntil":null,"club":{{club}} }
        """;

    private void GivenNoRanked()
    {
        Given($"GET /api/v4/ranked-system/progress/{Peer}", """{"divisionNumber":null,"divisionName":null,"rating":null,"tier":null,"guessedFirstRate":0,"winStreak":0,"latestGames":[],"bestCountries":[],"worstCountries":[]}""");
        Given($"GET /api/v4/ranked-system/peak-rating/{Peer}", "", 404);
    }

    [Fact]
    public async Task Profile_CombinesAllItsSources()
    {
        Given($"GET /api/v3/users/{Peer}", UserJson("""{"tag":"TAG1","clubId":"club-1","level":20}"""));
        Given("GET /api/v4/clubs/club-1", """{"clubId":"club-1","name":"Test Club","members":[],"tag":"TAG1"}""");
        Given($"GET /api/v4/ranked-system/progress/{Peer}", """{"divisionName":"Gold II","rating":932,"latestGames":[true,false]}""");
        Given($"GET /api/v4/ranked-system/peak-rating/{Peer}", """{"peakOverallRating":1010}""");

        var profile = (await Api.GetPlayerProfileAsync(Peer))!;

        profile.Nick.Should().Be("The Nick");
        profile.CountryCode.Should().Be("de");
        profile.Level.Should().Be(28);
        profile.IsPro.Should().BeTrue();
        profile.ImagePath.Should().Be("pin/abc.png");
        profile.Club.Should().Be(new PlayerClub("club-1", "TAG1", 20, "Test Club"));
        profile.Ranked!.Rating.Should().Be(932);
        profile.Ranked.PeakRating.Should().Be(1010);
        profile.Ranked.RecentGames.Should().Equal(true, false);
    }

    [Fact]
    public async Task Profile_PrefersACustomImage()
    {
        Given($"GET /api/v3/users/{Peer}", UserJson(customImage: "\"custom/abc.png\""));
        GivenNoRanked();

        (await Api.GetPlayerProfileAsync(Peer))!.ImagePath.Should().Be("custom/abc.png");
    }

    [Fact]
    public async Task Profile_WorksWithoutAClubOrRanking()
    {
        Given($"GET /api/v3/users/{Peer}", UserJson());
        GivenNoRanked();

        var profile = (await Api.GetPlayerProfileAsync(Peer))!;

        profile.Club.Should().BeNull();
        profile.Ranked!.Rating.Should().BeNull();
    }

    [Fact]
    public async Task Profile_SurvivesFailingDetails()
    {
        Given($"GET /api/v3/users/{Peer}", UserJson("""{"tag":"TAG1","clubId":"club-1","level":20}"""));
        Given("GET /api/v4/clubs/club-1", "", 403);
        Given($"GET /api/v4/ranked-system/progress/{Peer}", "", 403);
        Given($"GET /api/v4/ranked-system/peak-rating/{Peer}", "", 403);

        var profile = (await Api.GetPlayerProfileAsync(Peer))!;

        profile.Club!.Tag.Should().Be("TAG1");
        profile.Club.Name.Should().BeNull();
        profile.Ranked.Should().BeNull();
    }

    [Fact]
    public async Task Profile_OfAnUnknownPlayerIsNull()
    {
        Given($"GET /api/v3/users/{Peer}", "", 404);

        (await Api.GetPlayerProfileAsync(Peer)).Should().BeNull();
    }

    [Fact]
    public async Task OwnClub_IsNullWithoutAClub()
    {
        Given("GET /api/v4/clubs/my", "", 204);

        (await Api.GetOwnClubIdAsync()).Should().BeNull();
    }

    [Fact]
    public async Task OwnClub_ReadsTheId()
    {
        Given("GET /api/v4/clubs/my", """{"clubId":"club-1","name":"n","joinToken":"secret"}""");

        (await Api.GetOwnClubIdAsync()).Should().Be("club-1");
    }

    [Fact]
    public async Task ClubMembers_ReadTheNestedUser()
    {
        Given("GET /api/v4/clubs/club-1/members", """[{"user":{"userId":"u1","nick":"n","avatar":"a"},"role":2,"joinedAt":"2026-01-01T00:00:00Z"}]""");

        (await Api.GetClubMembersAsync("club-1")).Single().Should().Be(new ClubMember("u1", "n", 2));
    }

    [Fact]
    public async Task Ranked_ToleratesAnUnrankedAccount()
    {
        Given($"GET /api/v4/ranked-system/progress/{Own}", """{"divisionNumber":null,"divisionName":null,"rating":null,"tier":null,"guessedFirstRate":0.5,"winStreak":0,"latestGames":[],"bestCountries":[],"worstCountries":[]}""");
        Given("GET /api/v4/season-pass/me", """{"seasonId":"s1","points":3,"claimedRewardIds":[]}""");

        (await Api.GetRankedAsync(Own)).Should().Be(new RankedSnapshot(null, null, null, "s1"));
    }

    [Fact]
    public async Task Accept_IsAPut()
    {
        Given($"PUT /api/v3/social/friends/{Peer}?context=", """{"message":"ok"}""");

        (await Api.AcceptFriendRequestAsync(Peer)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Decline_IsADelete()
    {
        Given($"DELETE /api/v3/social/friends/{Peer}", """{"message":"ok"}""");

        (await Api.DeclineFriendRequestAsync(Peer)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task RejectedSession_RaisesItsOwnException()
    {
        Given("GET /api/v3/profiles", "", 401);

        var act = () => Api.GetOwnUserIdAsync();

        await act.Should().ThrowAsync<GeoGuessrUnauthorizedException>();
        _requests.Should().ContainSingle("a rejected token can't be fixed by retrying");
    }

    [Fact]
    public async Task RejectedSession_IsRecognisedOnCallsThatReturnAResponseObject()
    {
        Given($"PUT /api/v3/social/friends/{Peer}?context=", "", 401);
        Given($"GET /api/v4/chat/{Peer}", "", 401);

        var accept = () => Api.AcceptFriendRequestAsync(Peer);
        var history = () => Api.GetChatHistoryAsync(Peer);

        await accept.Should().ThrowAsync<GeoGuessrUnauthorizedException>();
        await history.Should().ThrowAsync<GeoGuessrUnauthorizedException>();
    }

    [Fact]
    public async Task TransientFailure_IsRetriedForReads_ThenReportedAsUnavailable()
    {
        Given("GET /api/v3/profiles", "", 503);

        var act = () => Api.GetOwnUserIdAsync();

        await act.Should().ThrowAsync<GeoGuessrUnavailableException>();
        _requests.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public async Task ServerError_OnAResponseObject_IsReportedAsUnavailable()
    {
        Given($"GET /api/v4/chat/{Peer}", "", 400);

        var act = () => Api.GetChatHistoryAsync(Peer);

        await act.Should().ThrowAsync<GeoGuessrUnavailableException>();
    }

    [Fact]
    public async Task UnreadableJson_IsReportedAsUnavailable()
    {
        Given("GET /api/v3/social/friendships", "<html>not json</html>");

        var act = () => Api.GetFriendshipsAsync();

        await act.Should().ThrowAsync<GeoGuessrUnavailableException>();
    }

    [Fact]
    public async Task TransientFailure_IsNeverRetriedForChanges()
    {
        Given($"PUT /api/v3/social/friends/{Peer}?context=", "", 503);

        var result = await Api.AcceptFriendRequestAsync(Peer);

        result.IsSuccess.Should().BeFalse();
        _requests.Should().ContainSingle("repeating an accept or decline could act twice");
    }
}
