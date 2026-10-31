// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net.WebSockets;
using GeoGuessrAccountShadowBot.Application;
using GeoGuessrAccountShadowBot.Application.Connection;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Pipeline;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Application.Reconciliation;
using GeoGuessrAccountShadowBot.Domain.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Socket;

/// <summary>
/// Keeps the socket alive by carrying out what <see cref="ConnectionPolicy"/> decides. All decisions
/// live in the policy, and this class only performs them.
/// </summary>
internal sealed partial class ConnectionSupervisor(
    ISocketConnector connector,
    CurrentSocket current,
    AccountInitializer accountInitializer,
    AccountContext account,
    IGeoGuessrApi api,
    ConnectionPolicy policy,
    ConnectionStatus status,
    ConnectionAnnouncer announcer,
    IEventPublisher events,
    ReconciliationCoordinator reconciliation,
    ReconcileRequests reconcileRequests,
    IOptions<ShadowOptions> options,
    TimeProvider timeProvider,
    ILogger<ConnectionSupervisor> logger) : BackgroundService
{
    private const int MaxJitterMilliseconds = 250;

    private static readonly TimeSpan AuthRecheckInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan InitializationRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CatchUpBudget = TimeSpan.FromSeconds(30);

    private readonly HashSet<string> _reportedUnknownCodes = [];
    private readonly Lock _interruptLock = new();
    private CancellationTokenSource _interrupt = new();
    private bool _authSuspect;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        status.ReconnectRequested += Interrupt;
        status.UnauthorizedReported += OnUnauthorizedReported;

        try
        {
            await InitializeAccountAsync(stoppingToken).ConfigureAwait(false);

            var state = policy.Initial();
            await ApplyAsync(state, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                state = state.Phase switch
                {
                    ConnectionPhase.Reconnecting => await RunSocketAsync(state, stoppingToken).ConfigureAwait(false),
                    ConnectionPhase.Polling => await WaitOutPollingAsync(state, stoppingToken).ConfigureAwait(false),
                    _ => await WaitForValidTokenAsync(stoppingToken).ConfigureAwait(false)
                };

                if (ConsumeAuthSuspicion() && !await IsTokenValidAsync(stoppingToken).ConfigureAwait(false))
                {
                    state = ConnectionPolicy.AuthFailed;
                }

                await ApplyAsync(state, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            status.ReconnectRequested -= Interrupt;
            status.UnauthorizedReported -= OnUnauthorizedReported;
        }
    }

    private async Task InitializeAccountAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await accountInitializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (GeoGuessrUnauthorizedException)
            {
                await ApplyAsync(ConnectionPolicy.AuthFailed, cancellationToken).ConfigureAwait(false);
                await Task.Delay(AuthRecheckInterval, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (GeoGuessrUnavailableException exception)
            {
                LogInitializationFailed(exception);
                await Task.Delay(InitializationRetry, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<ConnectionState> RunSocketAsync(ConnectionState state, CancellationToken cancellationToken)
    {
        await DelayAsync(state.Delay + Jitter(), cancellationToken).ConfigureAwait(false);

        ISocketSession session;
        try
        {
            session = await connector.ConnectAsync(state.IsReconnectAfterRestart, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketHandshakeException exception)
        {
            LogHandshakeRefused(exception.StatusCode);
            return await TransitionAsync(state, new ConnectionSignal.HandshakeRefused(exception.StatusCode, exception.RetryAfter), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or HttpRequestException)
        {
            LogConnectFailed(exception);
            return await TransitionAsync(state, new ConnectionSignal.ConnectFailed(), cancellationToken).ConfigureAwait(false);
        }

        var openedAt = timeProvider.GetUtcNow();
        await using (session.ConfigureAwait(false))
        {
            current.Session = session;
            state = policy.Next(state, new ConnectionSignal.Opened()).Next;
            await ApplyAsync(state, cancellationToken).ConfigureAwait(false);

            try
            {
                await ReadUntilClosedAsync(session, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                current.Session = null;
            }
        }

        var connectedFor = timeProvider.GetUtcNow() - openedAt;
        LogSocketClosed(session.Closure?.Code, session.Closure?.Description, connectedFor);

        var closed = new ConnectionSignal.Closed(session.Closure?.Code, session.Closure?.Description, connectedFor);
        return await TransitionAsync(state, closed, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadUntilClosedAsync(ISocketSession session, CancellationToken cancellationToken)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, NewInterruptToken());
        using var gate = new FrameGate(events);

        Task catchUp = Task.CompletedTask;
        Task heartbeat = Task.CompletedTask;
        try
        {
            foreach (var topic in Topics())
            {
                await session.SendAsync(OutboundFrames.Subscribe(topic), connection.Token).ConfigureAwait(false);
            }

            catchUp = CatchUpAsync(gate, connection.Token);
            heartbeat = HeartbeatAsync(session, connection.Token);

            await foreach (var frame in session.ReadFramesAsync(connection.Token).ConfigureAwait(false))
            {
                await HandleFrameAsync(frame, gate, connection.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (connection.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Replaced on request. The session's Closure stays empty, which reads as a transport failure.
        }
        catch (WebSocketException exception)
        {
            LogConnectFailed(exception);
        }
        finally
        {
            await connection.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(IgnoreCancellation(catchUp), IgnoreCancellation(heartbeat)).ConfigureAwait(false);
        }
    }

    private IEnumerable<string> Topics()
    {
        yield return SocketTopics.Self(account.UserId);
        yield return SocketTopics.FriendChat(account.UserId);

        if (account.ClubId is { } clubId)
        {
            yield return SocketTopics.Club(clubId);
            yield return SocketTopics.ClubChat(clubId);
        }
    }

    private async Task CatchUpAsync(FrameGate gate, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(CatchUpBudget);

        try
        {
            await reconciliation.RunAsync(ReconcileScope.All, false, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogCatchUpTimedOut();
        }

        await gate.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HeartbeatAsync(ISocketSession session, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.Value.GeoGuessr.Socket.HeartbeatInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await session.SendAsync(OutboundFrames.HeartBeat, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // The read loop sees the same failure and reports it.
        }
    }

    private async ValueTask HandleFrameAsync(string text, FrameGate gate, CancellationToken cancellationToken)
    {
        switch (FrameDecoder.Decode(text))
        {
            case DecodedFrame.Event { ShadowEvent: FriendPresenceChanged presence }:
                // Presence can't be caught up anyway, so under pressure it's the one thing to drop.
                events.TryPublish(presence);
                break;
            case DecodedFrame.Event decoded:
                await gate.PassAsync(decoded.ShadowEvent, cancellationToken).ConfigureAwait(false);
                break;
            case DecodedFrame.Doorbell doorbell:
                reconcileRequests.Request(doorbell.Scope);
                break;
            case DecodedFrame.SubscribeDenied denied:
                LogSubscribeDenied(denied.Topic);
                await events.PublishAsync(
                    new OperatorAlertRaised("GeoGuessr refused a subscription", $"The socket refused `{denied.Topic}`, so those events only arrive through catch-up.", false),
                    cancellationToken).ConfigureAwait(false);
                break;
            case DecodedFrame.Unknown unknown when _reportedUnknownCodes.Add(unknown.Code):
                LogUnknownFrame(unknown.Code);
                break;
            case DecodedFrame.Malformed:
                LogMalformedFrame();
                break;
        }
    }

    private async Task<ConnectionState> WaitOutPollingAsync(ConnectionState state, CancellationToken cancellationToken)
    {
        reconcileRequests.Request(ReconcileScope.All);

        var wait = state.Reason == PollingReason.SocketDisabled ? Timeout.InfiniteTimeSpan : state.Delay;
        await DelayAsync(wait, cancellationToken).ConfigureAwait(false);

        return state.Reason == PollingReason.SocketDisabled
            ? state
            : policy.Next(state, new ConnectionSignal.RetryDue()).Next;
    }

    private async Task<ConnectionState> WaitForValidTokenAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(AuthRecheckInterval, timeProvider, cancellationToken).ConfigureAwait(false);

        return await IsTokenValidAsync(cancellationToken).ConfigureAwait(false)
            ? policy.Initial()
            : ConnectionPolicy.AuthFailed;
    }

    private async Task<ConnectionState> TransitionAsync(ConnectionState state, ConnectionSignal signal, CancellationToken cancellationToken)
    {
        var transition = policy.Next(state, signal);
        if (transition.VerifyAuth && !await IsTokenValidAsync(cancellationToken).ConfigureAwait(false))
        {
            return ConnectionPolicy.AuthFailed;
        }

        return transition.Next;
    }

    private async Task<bool> IsTokenValidAsync(CancellationToken cancellationToken)
    {
        try
        {
            await api.GetOwnUserIdAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (GeoGuessrUnauthorizedException)
        {
            return false;
        }
        catch (GeoGuessrUnavailableException exception)
        {
            // Unreachable says nothing about the token, so don't stop everything over it.
            LogAuthCheckFailed(exception);
            return true;
        }
    }

    private async Task ApplyAsync(ConnectionState state, CancellationToken cancellationToken)
    {
        status.Update(state);
        if (announcer.Observe(state) is { } alert)
        {
            await events.PublishAsync(alert, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits, ending early if the bot is interrupted by a reconnect request or a token problem.</summary>
    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, NewInterruptToken());
        try
        {
            await Task.Delay(delay, timeProvider, wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Interrupted, so the main loop decides what comes next.
        }
    }

    private CancellationToken NewInterruptToken()
    {
        lock (_interruptLock)
        {
            _interrupt.Dispose();
            _interrupt = new CancellationTokenSource();
            return _interrupt.Token;
        }
    }

    private void Interrupt()
    {
        lock (_interruptLock)
        {
            _interrupt.Cancel();
        }
    }

    private void OnUnauthorizedReported()
    {
        Volatile.Write(ref _authSuspect, true);
        Interrupt();
    }

    private bool ConsumeAuthSuspicion() => Interlocked.Exchange(ref _authSuspect, false);

    private TimeSpan Jitter() => TimeSpan.FromMilliseconds(Random.Shared.Next(MaxJitterMilliseconds));

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected while a connection ends.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the account at startup, retrying")]
    private partial void LogInitializationFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The socket handshake was refused with status {Status}")]
    private partial void LogHandshakeRefused(int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not connect the socket")]
    private partial void LogConnectFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "The socket closed with {Code} [{Description}] after {ConnectedFor}")]
    private partial void LogSocketClosed(int? code, string? description, TimeSpan connectedFor);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Catch-up took too long, so live frames are being released")]
    private partial void LogCatchUpTimedOut();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The socket refused the subscription to {Topic}")]
    private partial void LogSubscribeDenied(string? topic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Received an unknown frame with code {Code}. GeoGuessr may have changed its protocol")]
    private partial void LogUnknownFrame(string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Received a frame that could not be decoded")]
    private partial void LogMalformedFrame();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check the token, and assuming it is still valid")]
    private partial void LogAuthCheckFailed(Exception exception);
}
