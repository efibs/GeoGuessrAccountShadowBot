// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;
using System.Net.Sockets;
using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Api;
using GeoGuessrAccountShadowBot.Application.Automation;
using GeoGuessrAccountShadowBot.Application.Handlers;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Application.Sources;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;
using GeoGuessrAccountShadowBot.Infrastructure;
using GeoGuessrAccountShadowBot.Infrastructure.Sources;
using GeoGuessrAccountShadowBot.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GeoGuessrAccountShadowBot.Tests.Infrastructure;

/// <summary>
/// One instance serving its API on a real port, and the real client of another instance reading
/// it, so the wire format, the tokens and the event stream are tested as they run.
/// </summary>
public sealed class ExternalApiRoundTripTests : IAsyncLifetime
{
    private const string Peer = ShadowTestHost.PeerId;
    private const string Other = "cccccccccccccccccccccccc";
    private const string MainToken = "token-of-the-main-instance-0123456789";
    private const string BlindToken = "token-of-a-client-that-sees-nothing-0123";

    private ShadowTestHost _host = null!;
    private ExternalApiServer _server = null!;
    private ServiceProvider _clients = null!;

    public async Task InitializeAsync()
    {
        _host = await ShadowTestHost.CreateAsync(options =>
        {
            options.ExternalApi.Enabled = true;
            options.ExternalApi.Port = 0;
            options.ExternalApi.Clients["main"] = new ExternalApiClientOptions
            {
                Token = MainToken,
                Events = [EventKeys.FriendRequestAccepted],
                Data = [ExternalApiData.Players, ExternalApiData.Account]
            };
            options.ExternalApi.Clients["blind"] = new ExternalApiClientOptions { Token = BlindToken };
        });

        await _host.WithStoreAsync(async store =>
        {
            await store.WriteStateAsync(SnapshotKeys.ClubApplications, new ClubApplicationSet(
                ImmutableSortedDictionary<string, ClubApplicant>.Empty.Add(Peer, new ClubApplicant(Peer, "Peery"))));
            await store.CommitAsync();
            return 0;
        });

        _server = new ExternalApiServer(
            Microsoft.Extensions.Options.Options.Create(_host.Options),
            _host.Get<ExternalApiService>(),
            NullLoggerFactory.Instance,
            TimeProvider.System,
            NullLogger<ExternalApiServer>.Instance);
        await _server.StartAsync(CancellationToken.None);

        _clients = new ServiceCollection().AddLogging().AddShadowSources().BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        await _server.DisposeAsync();
        await _clients.DisposeAsync();
        await _host.DisposeAsync();
    }

    private ShadowBotSource Client(string token, int? port = null) => new(
        "dragon",
        new SourceOptions { Url = new Uri($"http://127.0.0.1:{port ?? _server.Port}"), Token = token },
        _clients.GetRequiredService<IHttpClientFactory>(),
        NullLogger<ShadowBotSource>.Instance);

    [Fact]
    public async Task Player_IsReadFromTheSourcesSnapshots()
    {
        var dragon = Client(MainToken);

        var applicant = await dragon.ReadSectionAsync("player", Peer);
        var stranger = await dragon.ReadSectionAsync("player", Other);

        applicant["has_applied"]!.GetValue<bool>().Should().BeTrue();
        applicant["is_friend"]!.GetValue<bool>().Should().BeFalse();
        stranger["has_applied"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Account_IsServed()
    {
        var account = await Client(MainToken).ReadSectionAsync("account", null);

        account["id"]!.GetValue<string>().Should().Be(ShadowTestHost.OwnUserId);
    }

    [Fact]
    public async Task AWrongToken_IsRefused()
    {
        var read = () => Client("not-the-token-of-anyone-at-all-0123456").ReadSectionAsync("account", null);

        (await read.Should().ThrowAsync<ExternalSourceRefusedException>()).Which.Message.Should().Contain("doesn't accept the token");
    }

    [Fact]
    public async Task DataTheClientMayNotRead_IsRefused_AndSaysWhatToAllow()
    {
        var lists = () => Client(MainToken).ReadAudienceAsync(AutomationAudience.ClubApplications);
        var player = () => Client(BlindToken).ReadSectionAsync("player", Peer);
        var events = async () =>
        {
            await foreach (var _ in Client(BlindToken).StreamEventsAsync(null))
            {
            }
        };

        (await lists.Should().ThrowAsync<ExternalSourceRefusedException>()).Which.Message.Should().Contain("Add Lists");
        (await player.Should().ThrowAsync<ExternalSourceRefusedException>()).Which.Message.Should().Contain("Add Players");
        (await events.Should().ThrowAsync<ExternalSourceRefusedException>()).Which.Message.Should().Contain("Events there");
    }

    [Fact]
    public async Task AnUnreachableSource_IsUnavailable_NotRefused()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var unused = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var read = () => Client(MainToken, unused).ReadSectionAsync("account", null);

        await read.Should().ThrowAsync<ExternalSourceUnavailableException>();
    }

    [Fact]
    public async Task Events_Stream_AsTheyHappen_AndResumeAfterTheLastOneSeen()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dragon = Client(MainToken);

        await _host.ProcessAsync(new FriendRequestAccepted(Peer));

        long seen;
        await using (var first = dragon.StreamEventsAsync(null, timeout.Token).GetAsyncEnumerator(timeout.Token))
        {
            (await first.MoveNextAsync()).Should().BeTrue();
            var hello = first.Current.Should().BeOfType<SourceHello>().Subject;
            hello.Newest.Should().Be(1);

            // Events not allowed to this client aren't sent, and allowed ones arrive live.
            await _host.ProcessAsync(new FriendRequestReceived(Other));
            await _host.ProcessAsync(new FriendRequestAccepted(Other));

            (await first.MoveNextAsync()).Should().BeTrue();
            var live = first.Current.Should().BeOfType<SourceEvent>().Subject;
            live.Trigger.Should().Be(EventKeys.FriendRequestAccepted);
            live.PlayerId.Should().Be(Other);
            seen = live.Id;
        }

        await _host.ProcessAsync(new FriendRequestAccepted("dddddddddddddddddddddddd"));

        await using var second = dragon.StreamEventsAsync(seen, timeout.Token).GetAsyncEnumerator(timeout.Token);
        (await second.MoveNextAsync()).Should().BeTrue();
        second.Current.Should().BeOfType<SourceHello>();
        (await second.MoveNextAsync()).Should().BeTrue();
        second.Current.Should().BeOfType<SourceEvent>().Which.PlayerId.Should().Be("dddddddddddddddddddddddd");
    }

    [Fact]
    public async Task Stopping_EndsOpenStreams_WithoutHoldingUpTheStop()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = Client(MainToken).StreamEventsAsync(null, timeout.Token).GetAsyncEnumerator(timeout.Token);
        (await stream.MoveNextAsync()).Should().BeTrue();

        var started = TimeProvider.System.GetTimestamp();
        await _server.StopAsync(timeout.Token);

        TimeProvider.System.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(5));
        var next = async () => await stream.MoveNextAsync();
        await next.Should().NotThrowAsync<OperationCanceledException>();
    }
}
