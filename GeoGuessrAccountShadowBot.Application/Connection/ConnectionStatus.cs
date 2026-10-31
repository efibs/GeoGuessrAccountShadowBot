// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Domain.Events;

namespace GeoGuessrAccountShadowBot.Application.Connection;

/// <summary>
/// The live connection state, and how the rest of the bot talks to whoever runs the connection.
/// </summary>
public sealed class ConnectionStatus(TimeProvider timeProvider)
{
    private readonly Lock _lock = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ConnectionState _current = new(ConnectionPhase.Reconnecting);

    /// <summary>Raised when something outside the connection knows it needs replacing.</summary>
    public event Action? ReconnectRequested;

    /// <summary>Raised when an HTTP call found the token rejected.</summary>
    public event Action? UnauthorizedReported;

    public ConnectionState Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>When the next attempt to reconnect happens, for telling people how long to wait.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    public void Update(ConnectionState state)
    {
        TaskCompletionSource changed;
        lock (_lock)
        {
            _current = state;
            NextAttemptAt = state.Phase == ConnectionPhase.Polling && state.Reason != PollingReason.SocketDisabled
                ? timeProvider.GetUtcNow() + state.Delay
                : null;
            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
    }

    /// <summary>Waits for the socket to open, returning false if it doesn't within the timeout.</summary>
    public async Task<bool> WaitUntilConnectedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);

        try
        {
            while (true)
            {
                Task changed;
                lock (_lock)
                {
                    if (_current.Phase == ConnectionPhase.Connected)
                    {
                        return true;
                    }

                    if (_current.Phase != ConnectionPhase.Reconnecting)
                    {
                        return false;
                    }

                    changed = _changed.Task;
                }

                await changed.WaitAsync(linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public void RequestReconnect() => ReconnectRequested?.Invoke();

    public void ReportUnauthorized() => UnauthorizedReported?.Invoke();
}

/// <summary>
/// Decides which connection changes deserve a message, so that operators hear about real problems
/// and not about every brief reconnect.
/// </summary>
public sealed class ConnectionAnnouncer(TimeProvider timeProvider)
{
    private static readonly TimeSpan QuietOutage = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RepeatAfter = TimeSpan.FromHours(6);
    private static readonly TimeSpan RepeatAuthFailureAfter = TimeSpan.FromHours(24);

    private DateTimeOffset? _downSince;
    private DateTimeOffset _lastAlertAt = DateTimeOffset.MinValue;
    private string? _lastAlertTitle;
    private bool _alertOpen;

    /// <summary>Returns the alert the new state calls for, if any.</summary>
    public OperatorAlertRaised? Observe(ConnectionState state)
    {
        var now = timeProvider.GetUtcNow();

        if (state.Phase == ConnectionPhase.Connected)
        {
            _downSince = null;
            if (!_alertOpen)
            {
                return null;
            }

            _alertOpen = false;
            return new OperatorAlertRaised("Connected", "The GeoGuessr socket is back. Anything missed is being caught up.", false);
        }

        _downSince ??= now;

        return state.Phase switch
        {
            ConnectionPhase.AuthFailed => Alert(
                "GeoGuessr token rejected",
                "The `_ncfa` token no longer works, so nothing is being mirrored. Replace it and restart the bot.",
                ping: true,
                RepeatAuthFailureAfter),
            ConnectionPhase.Polling => DescribePolling(state),
            ConnectionPhase.Reconnecting when now - _downSince >= QuietOutage => Alert(
                "GeoGuessr socket down",
                "Reconnecting. Catch-up over HTTP continues in the meantime.",
                ping: false,
                RepeatAfter),
            _ => null
        };
    }

    private OperatorAlertRaised? DescribePolling(ConnectionState state) => state.Reason switch
    {
        PollingReason.Superseded => Alert(
            "Yielded the GeoGuessr socket",
            "Another connection took over. Mirroring continues slowly over HTTP, and replies are refused until the socket is back.",
            ping: false,
            RepeatAfter),
        PollingReason.Blocked => Alert(
            "GeoGuessr socket blocked",
            "The socket handshake was refused although the token is valid. Mirroring continues over HTTP, and replies are refused.",
            ping: true,
            RepeatAfter),
        PollingReason.ClientOutdated => Alert(
            "GeoGuessr client version outdated",
            "GeoGuessr asked this client to reload, so `GeoGuessr:ClientVersion` is probably stale.",
            ping: true,
            RepeatAfter),
        _ => null
    };

    private OperatorAlertRaised? Alert(string title, string detail, bool ping, TimeSpan repeatAfter)
    {
        var now = timeProvider.GetUtcNow();
        if (_lastAlertTitle == title && now - _lastAlertAt < repeatAfter)
        {
            return null;
        }

        _lastAlertTitle = title;
        _lastAlertAt = now;
        _alertOpen = true;
        return new OperatorAlertRaised(title, detail, ping);
    }
}
