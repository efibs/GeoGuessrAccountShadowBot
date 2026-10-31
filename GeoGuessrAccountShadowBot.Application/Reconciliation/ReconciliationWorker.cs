// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Connection;
using GeoGuessrAccountShadowBot.Application.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Reconciliation;

/// <summary>
/// Collects requests to reconcile. Many doorbell frames can arrive at once, and they are merged
/// into one sync.
/// </summary>
public sealed class ReconcileRequests
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private int _pending;

    public void Request(ReconcileScope scope)
    {
        Interlocked.Or(ref _pending, (int)scope);
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);

    public ReconcileScope Take() => (ReconcileScope)Interlocked.Exchange(ref _pending, 0);
}

/// <summary>
/// Runs reconciliation on doorbells and on a timer. The timer is fast while the socket is away and
/// slow while it's connected, where it only guards against missed events.
/// </summary>
public sealed class ReconciliationWorker(
    ReconciliationCoordinator coordinator,
    ReconcileRequests requests,
    ConnectionStatus connection,
    AccountContext account,
    IOptions<ShadowOptions> options,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await account.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);

        var settings = options.Value.Polling;
        var lastRun = timeProvider.GetUtcNow();

        while (!stoppingToken.IsCancellationRequested)
        {
            var isConnected = connection.Current.Phase == ConnectionPhase.Connected;
            var interval = isConnected ? settings.ConnectedSafetyInterval : settings.Interval;
            var wait = lastRun + interval - timeProvider.GetUtcNow();

            await requests.WaitAsync(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, stoppingToken).ConfigureAwait(false);

            if (requests.Take() is var doorbells and not ReconcileScope.None)
            {
                await Task.Delay(settings.DoorbellDebounce, timeProvider, stoppingToken).ConfigureAwait(false);
                await RunIfAllowedAsync(doorbells | requests.Take(), false, stoppingToken).ConfigureAwait(false);
            }

            if (timeProvider.GetUtcNow() - lastRun >= interval)
            {
                await RunIfAllowedAsync(ReconcileScope.All, isConnected, stoppingToken).ConfigureAwait(false);
                lastRun = timeProvider.GetUtcNow();
            }
        }
    }

    private Task RunIfAllowedAsync(ReconcileScope scope, bool detectMissed, CancellationToken cancellationToken) =>
        connection.Current.Phase == ConnectionPhase.AuthFailed
            ? Task.CompletedTask
            : coordinator.RunAsync(scope, detectMissed, cancellationToken);
}
