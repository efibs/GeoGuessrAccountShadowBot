// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application;
using GeoGuessrAccountShadowBot.Application.Connection;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Pipeline;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Application.Reconciliation;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;
using GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Socket;
using GeoGuessrAccountShadowBot.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GeoGuessrAccountShadowBot.Tests.Infrastructure;

/// <summary>
/// Runs the supervisor against a scripted socket. Time only moves when a test moves it, so the
/// tests poll for the real background work to finish and then step the clock.
/// </summary>
public sealed class ConnectionSupervisorTests : IAsyncLifetime
{
    private const string Own = ShadowTestHost.OwnUserId;

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _stop = new();
    private readonly ScriptedConnector _connector = new();
    private ShadowTestHost _host = null!;
    private ConnectionSupervisor _supervisor = null!;

    private ConnectionStatus Status => _host.Get<ConnectionStatus>();

    public async Task InitializeAsync()
    {
        _host = await ShadowTestHost.CreateAsync();
        _host.Api.GetOwnUserIdAsync(Arg.Any<CancellationToken>()).Returns(Own);
        _host.Api.GetFriendshipsAsync(Arg.Any<CancellationToken>()).Returns(new FriendshipSet([], [], []));
        _host.Api.GetChatRoomsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _host.Api.GetNotificationsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        Status.Update(new ConnectionState(ConnectionPhase.Reconnecting));

        _supervisor = new ConnectionSupervisor(
            _connector,
            new CurrentSocket(),
            _host.Get<AccountInitializer>(),
            _host.Get<AccountContext>(),
            _host.Api,
            _host.Get<ConnectionPolicy>(),
            Status,
            _host.Get<ConnectionAnnouncer>(),
            _host.Get<IEventPublisher>(),
            _host.Get<ReconciliationCoordinator>(),
            _host.Get<ReconcileRequests>(),
            _host.Get<IOptions<ShadowOptions>>(),
            _host.Time,
            NullLogger<ConnectionSupervisor>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _stop.CancelAsync();
        await _supervisor.StopAsync(CancellationToken.None);
        await _host.DisposeAsync();
        _stop.Dispose();
    }

    private Task StartAsync() => _supervisor.StartAsync(_stop.Token);

    private async Task<T> EventuallyAsync<T>(Func<T?> read) where T : class
    {
        using var limit = new CancellationTokenSource(WaitLimit);
        while (true)
        {
            if (read() is { } value)
            {
                return value;
            }

            // The supervisor's own delays run on the fake clock, so keep nudging it forward a little.
            _host.Time.Advance(TimeSpan.FromMilliseconds(300));
            await Task.Delay(5, limit.Token);
        }
    }

    private Task EventuallyAsync(Func<bool> condition) => EventuallyAsync(() => condition() ? "done" : null);

    private Task EventuallyPhaseAsync(ConnectionPhase phase) => EventuallyAsync(() => Status.Current.Phase == phase);

    private Task EventuallyOpenAsync(int connections = 1) => EventuallyAsync(() =>
        _connector.Sessions.Count >= connections && Status.Current.Phase == ConnectionPhase.Connected);

    [Fact]
    public async Task Connecting_SubscribesToTheAccountsTopics()
    {
        _connector.Enqueue(new ScriptedSession());
        await StartAsync();
        await EventuallyOpenAsync();

        var session = _connector.Sessions[0];
        await EventuallyAsync(() => session.Sent.Count >= 2);

        session.Sent.Take(2).Should().Equal(
            OutboundFrames.Subscribe($"self:{Own}"),
            OutboundFrames.Subscribe($"chat:Friend:TextMessages:{Own}"));
    }

    [Fact]
    public async Task Connecting_CatchesUpOverHttp()
    {
        _connector.Enqueue(new ScriptedSession());
        await StartAsync();
        await EventuallyOpenAsync();

        await EventuallyAsync(() => _host.Api.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(IGeoGuessrApi.GetChatRoomsAsync)));
    }

    [Fact]
    public async Task HeartBeat_GoesOutOnSchedule()
    {
        var session = new ScriptedSession();
        _connector.Enqueue(session);
        await StartAsync();
        await EventuallyOpenAsync();

        await EventuallyAsync(() =>
        {
            _host.Time.Advance(TimeSpan.FromSeconds(15));
            return session.Sent.Contains(OutboundFrames.HeartBeat);
        });
    }

    [Fact]
    public async Task LiveEvents_WaitForCatchUpToFinish()
    {
        var chatRooms = new TaskCompletionSource<IReadOnlyList<ChatRoomSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Api.GetChatRoomsAsync(Arg.Any<CancellationToken>()).Returns(chatRooms.Task);

        var session = new ScriptedSession();
        _connector.Enqueue(session);
        await StartAsync();
        await EventuallyOpenAsync();
        await EventuallyAsync(() => _host.Api.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(IGeoGuessrApi.GetChatRoomsAsync)));

        session.Feed(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "frames", "ChatMessage.json")));
        await Task.Delay(100);

        var bus = _host.Get<EventBus>();
        (await ReadQueuedEventsAsync(bus)).OfType<DirectMessageReceived>().Should().BeEmpty();

        chatRooms.SetResult([]);

        await EventuallyAsync(() => bus.Reader.TryPeek(out var next) && next is not null
            && (next is DirectMessageReceived || DrainUntilChat(bus)));
    }

    private static Task<List<ShadowEvent>> ReadQueuedEventsAsync(EventBus bus)
    {
        var events = new List<ShadowEvent>();
        while (bus.Reader.TryRead(out var shadowEvent))
        {
            events.Add(shadowEvent);
        }

        return Task.FromResult(events);
    }

    private static bool DrainUntilChat(EventBus bus)
    {
        while (bus.Reader.TryPeek(out var next) && next is not DirectMessageReceived)
        {
            bus.Reader.TryRead(out _);
        }

        return bus.Reader.TryPeek(out var head) && head is DirectMessageReceived;
    }

    [Fact]
    public async Task Doorbell_RequestsReconciliation()
    {
        var session = new ScriptedSession();
        _connector.Enqueue(session);
        await StartAsync();
        await EventuallyOpenAsync();

        session.Feed(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "frames", "NewFriendRequest.json")));

        var requests = _host.Get<ReconcileRequests>();
        await EventuallyAsync(() => requests.Take() == ReconcileScope.Friends);
    }

    [Fact]
    public async Task Superseded_YieldsThenReturnsAfterTheWait()
    {
        var first = new ScriptedSession();
        _connector.Enqueue(first);
        _connector.Enqueue(new ScriptedSession());
        await StartAsync();
        await EventuallyOpenAsync();

        first.Close(4100, "superseded");
        await EventuallyPhaseAsync(ConnectionPhase.Polling);
        Status.Current.Reason.Should().Be(PollingReason.Superseded);
        Status.NextAttemptAt.Should().NotBeNull();

        _host.Time.Advance(TimeSpan.FromMinutes(14));
        await Task.Delay(50);
        _connector.Sessions.Should().ContainSingle("the bot must stay off the socket during the yield");

        _host.Time.Advance(TimeSpan.FromMinutes(2));
        await EventuallyOpenAsync(connections: 2);
    }

    [Fact]
    public async Task ServerRestart_ReconnectsAndSaysSo()
    {
        var first = new ScriptedSession();
        _connector.Enqueue(first);
        _connector.Enqueue(new ScriptedSession());
        await StartAsync();
        await EventuallyOpenAsync();

        first.Close(1012, null);
        await EventuallyOpenAsync(connections: 2);

        _connector.RestartFlags.Should().Equal(false, true);
    }

    [Fact]
    public async Task TransportFailure_Reconnects()
    {
        var first = new ScriptedSession();
        _connector.Enqueue(first);
        _connector.Enqueue(new ScriptedSession());
        await StartAsync();
        await EventuallyOpenAsync();

        first.Close(null, null);

        await EventuallyOpenAsync(connections: 2);
    }

    [Fact]
    public async Task RejectedHandshake_WithARejectedToken_StopsEverything()
    {
        _connector.Enqueue(new SocketHandshakeException(401, null, new WebSocketException()));
        await StartAsync();
        await EventuallyAsync(() => _host.Get<AccountContext>().Ready.IsCompleted);

        _host.Api.GetOwnUserIdAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new GeoGuessrUnauthorizedException("rejected"));

        await EventuallyPhaseAsync(ConnectionPhase.AuthFailed);
    }

    [Fact]
    public async Task RefusedHandshake_WithAValidToken_PollsAsBlocked()
    {
        _connector.Enqueue(new SocketHandshakeException(403, null, new WebSocketException()));
        await StartAsync();

        await EventuallyPhaseAsync(ConnectionPhase.Polling);

        Status.Current.Reason.Should().Be(PollingReason.Blocked);
    }

    [Fact]
    public async Task ConnectFailures_KeepRetrying()
    {
        _connector.Enqueue(new WebSocketException());
        _connector.Enqueue(new WebSocketException());
        _connector.Enqueue(new ScriptedSession());
        await StartAsync();

        await EventuallyOpenAsync();
    }

    [Fact]
    public async Task ReconnectRequest_ReplacesTheSocket()
    {
        _connector.Enqueue(new ScriptedSession());
        _connector.Enqueue(new ScriptedSession());
        await StartAsync();
        await EventuallyOpenAsync();
        await EventuallyAsync(() => _connector.Sessions[0].Sent.Count >= 2);

        Status.RequestReconnect();

        await EventuallyOpenAsync(connections: 2);
    }

    [Fact]
    public async Task HttpRejection_WhileConnected_EndsInAuthFailed()
    {
        _connector.Enqueue(new ScriptedSession());
        await StartAsync();
        await EventuallyOpenAsync();
        await EventuallyAsync(() => _connector.Sessions[0].Sent.Count >= 2);

        _host.Api.GetOwnUserIdAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new GeoGuessrUnauthorizedException("rejected"));
        Status.ReportUnauthorized();

        await EventuallyPhaseAsync(ConnectionPhase.AuthFailed);
    }

    [Fact]
    public async Task GeoGuessrDownAtStartup_KeepsRetryingInsteadOfCrashing()
    {
        _host.Api.GetOwnUserIdAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new GeoGuessrUnavailableException("down", new HttpRequestException()));
        _connector.Enqueue(new ScriptedSession());
        await StartAsync();

        await EventuallyAsync(() => _host.Api.ReceivedCalls().Any());
        _host.Time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50);
        _supervisor.ExecuteTask!.IsFaulted.Should().BeFalse();

        _host.Api.GetOwnUserIdAsync(Arg.Any<CancellationToken>()).Returns(Own);
        await EventuallyAsync(() =>
        {
            _host.Time.Advance(TimeSpan.FromSeconds(30));
            return _connector.Sessions.Count > 0;
        });
    }

    [Fact]
    public async Task RejectedTokenAtStartup_WaitsWithoutCrashing()
    {
        _host.Api.GetOwnUserIdAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new GeoGuessrUnauthorizedException("rejected"));
        await StartAsync();

        await EventuallyPhaseAsync(ConnectionPhase.AuthFailed);

        _supervisor.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task AccountOfAnotherDatabase_StopsTheBot()
    {
        await _host.WithStoreAsync(async store =>
        {
            await store.WriteStateAsync("account", new { UserId = "someone-else" });
            await store.CommitAsync();
            return true;
        });

        await StartAsync();
        await EventuallyAsync(() => _supervisor.ExecuteTask?.IsFaulted == true);

        _supervisor.ExecuteTask!.Exception!.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    private sealed class ScriptedConnector : ISocketConnector
    {
        private readonly Queue<object> _script = new();

        public List<ScriptedSession> Sessions { get; } = [];

        public List<bool> RestartFlags { get; } = [];

        public void Enqueue(object step) => _script.Enqueue(step);

        public Task<ISocketSession> ConnectAsync(bool afterServerRestart, CancellationToken cancellationToken)
        {
            RestartFlags.Add(afterServerRestart);

            if (!_script.TryDequeue(out var step))
            {
                return Task.FromException<ISocketSession>(new WebSocketException("the script ran out"));
            }

            if (step is Exception exception)
            {
                return Task.FromException<ISocketSession>(exception);
            }

            var session = (ScriptedSession)step;
            Sessions.Add(session);
            return Task.FromResult<ISocketSession>(session);
        }
    }

    private sealed class ScriptedSession : ISocketSession
    {
        private readonly Channel<string> _frames = Channel.CreateUnbounded<string>();

        public List<string> Sent { get; } = [];

        public SocketClosure? Closure { get; private set; }

        public void Feed(string frame) => _frames.Writer.TryWrite(frame);

        public void Close(int? code, string? description)
        {
            Closure = new SocketClosure(code, description);
            _frames.Writer.TryComplete();
        }

        public Task SendAsync(string frame, CancellationToken cancellationToken)
        {
            lock (Sent)
            {
                Sent.Add(frame);
            }

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken))
            {
                yield return frame;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
