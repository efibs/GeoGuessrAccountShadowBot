// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Options;

namespace GeoGuessrAccountShadowBot.Application.Connection;

/// <summary>What the connection is doing.</summary>
public enum ConnectionPhase
{
    /// <summary>Trying to open the socket.</summary>
    Reconnecting,

    Connected,

    /// <summary>Off the socket on purpose, catching up over HTTP instead.</summary>
    Polling,

    /// <summary>The token was rejected, so everything waits until it's replaced.</summary>
    AuthFailed
}

/// <summary>Why the bot is off the socket.</summary>
public enum PollingReason
{
    None,

    /// <summary>GeoGuessr closed the socket with 4100.</summary>
    Superseded,

    /// <summary>The handshake was refused although the token is valid, probably by Cloudflare.</summary>
    Blocked,

    /// <summary>GeoGuessr closed the socket with 4200, so the client version is probably stale.</summary>
    ClientOutdated,

    /// <summary>The socket is switched off in the configuration.</summary>
    SocketDisabled
}

/// <summary>
/// The state of the connection. <see cref="Delay"/> is how long to wait before the next attempt.
/// </summary>
public sealed record ConnectionState(
    ConnectionPhase Phase,
    PollingReason Reason = PollingReason.None,
    int Attempt = 0,
    int YieldCount = 0,
    TimeSpan Delay = default,
    bool IsReconnectAfterRestart = false);

/// <summary>Something that happened to the connection.</summary>
public abstract record ConnectionSignal
{
    public sealed record Opened : ConnectionSignal;

    /// <summary>The socket ended. A null code means it failed without a closing handshake.</summary>
    public sealed record Closed(int? Code, string? Description, TimeSpan ConnectedFor) : ConnectionSignal;

    /// <summary>The server refused the handshake with an HTTP status.</summary>
    public sealed record HandshakeRefused(int Status, TimeSpan? RetryAfter) : ConnectionSignal;

    /// <summary>The handshake failed before the server answered.</summary>
    public sealed record ConnectFailed : ConnectionSignal;

    /// <summary>The wait in <see cref="ConnectionPhase.Polling"/> is over.</summary>
    public sealed record RetryDue : ConnectionSignal;
}

/// <summary>
/// The result of a signal.
/// </summary>
/// <param name="Next">The next state.</param>
/// <param name="VerifyAuth">
/// The token may be the cause, so it must be checked first. If it's rejected, the bot goes to
/// <see cref="ConnectionPolicy.AuthFailed"/> instead of <paramref name="Next"/>.
/// </param>
public sealed record ConnectionTransition(ConnectionState Next, bool VerifyAuth = false);

/// <summary>
/// Decides how the connection reacts to each signal. It has no side effects, so every close code
/// can be tested without a socket.
/// </summary>
public sealed class ConnectionPolicy(SocketOptions socket, YieldOptions yieldOptions)
{
    public const int CloseGoingAway = 1001;
    public const int CloseServerRestart = 1012;
    public const int CloseSuperseded = 4100;
    public const int CloseReload = 4200;

    private const int FirstApplicationCloseCode = 4000;

    private static readonly TimeSpan MinimumDelayAfterUnknownClose = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BlockedRetry = TimeSpan.FromHours(1);
    private static readonly TimeSpan BlockedRetryMax = TimeSpan.FromHours(6);
    private static readonly TimeSpan ClientOutdatedRetry = TimeSpan.FromMinutes(30);

    public static ConnectionState AuthFailed { get; } = new(ConnectionPhase.AuthFailed);

    public ConnectionState Initial() => socket.Enabled
        ? new ConnectionState(ConnectionPhase.Reconnecting)
        : new ConnectionState(ConnectionPhase.Polling, PollingReason.SocketDisabled);

    public ConnectionTransition Next(ConnectionState state, ConnectionSignal signal) => signal switch
    {
        ConnectionSignal.Opened => new(state with { Phase = ConnectionPhase.Connected, Delay = TimeSpan.Zero, IsReconnectAfterRestart = false }),
        ConnectionSignal.Closed closed => OnClosed(state, closed),
        ConnectionSignal.HandshakeRefused refused => OnHandshakeRefused(state, refused),
        ConnectionSignal.ConnectFailed => new(Backoff(state)),
        ConnectionSignal.RetryDue => new(state with { Phase = ConnectionPhase.Reconnecting, Reason = PollingReason.None, Attempt = 0, Delay = TimeSpan.Zero }),
        _ => throw new ArgumentOutOfRangeException(nameof(signal))
    };

    /// <summary>
    /// The wait after an attempt. It starts like the site's own client, then keeps growing, because
    /// a flat retry suits a tab somebody is looking at, not a bot running unattended.
    /// </summary>
    public TimeSpan BackoffDelay(int attempt) => attempt switch
    {
        <= 0 => TimeSpan.Zero,
        <= 2 => TimeSpan.FromSeconds(1),
        <= 8 => TimeSpan.FromSeconds(3),
        <= 16 => TimeSpan.FromSeconds(5),
        _ => Min(TimeSpan.FromSeconds(15 * Math.Pow(2, Math.Min(attempt - 17, 16))), socket.MaxBackoff)
    };

    private ConnectionTransition OnClosed(ConnectionState state, ConnectionSignal.Closed closed)
    {
        // A connection that lasted proves the last one wasn't a storm, so the counters start over.
        if (closed.ConnectedFor >= socket.StableAfter)
        {
            state = state with { Attempt = 0, YieldCount = 0 };
        }

        switch (closed.Code)
        {
            case CloseServerRestart:
                return new(state with { Phase = ConnectionPhase.Reconnecting, Delay = TimeSpan.Zero, IsReconnectAfterRestart = true });

            case CloseSuperseded:
                return new(state with
                {
                    Phase = ConnectionPhase.Polling,
                    Reason = PollingReason.Superseded,
                    YieldCount = state.YieldCount + 1,
                    Delay = Doubled(yieldOptions.Initial, state.YieldCount, yieldOptions.Max)
                });

            case CloseReload:
                return new(state with { Phase = ConnectionPhase.Polling, Reason = PollingReason.ClientOutdated, Delay = ClientOutdatedRetry });

            case CloseGoingAway when closed.Description == "Unauthorized":
                return new(Backoff(state), VerifyAuth: true);

            case >= FirstApplicationCloseCode:
                var next = Backoff(state);
                return new(next with { Delay = Max(next.Delay, MinimumDelayAfterUnknownClose) });

            default:
                return new(Backoff(state));
        }
    }

    private ConnectionTransition OnHandshakeRefused(ConnectionState state, ConnectionSignal.HandshakeRefused refused)
    {
        switch (refused.Status)
        {
            case 401:
                return new(Backoff(state), VerifyAuth: true);

            case 403:
                var blocked = state with
                {
                    Phase = ConnectionPhase.Polling,
                    Reason = PollingReason.Blocked,
                    YieldCount = state.YieldCount + 1,
                    Delay = Doubled(BlockedRetry, state.YieldCount, BlockedRetryMax)
                };
                return new(blocked, VerifyAuth: true);

            case 429:
                var next = Backoff(state);
                return new(next with { Delay = Max(next.Delay, refused.RetryAfter ?? TimeSpan.Zero) });

            default:
                return new(Backoff(state));
        }
    }

    private ConnectionState Backoff(ConnectionState state)
    {
        var attempt = state.Attempt + 1;
        return state with { Phase = ConnectionPhase.Reconnecting, Reason = PollingReason.None, Attempt = attempt, Delay = BackoffDelay(attempt) };
    }

    private static TimeSpan Doubled(TimeSpan initial, int times, TimeSpan max) =>
        Min(initial * Math.Pow(2, Math.Min(times, 16)), max);

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;

    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first > second ? first : second;
}
