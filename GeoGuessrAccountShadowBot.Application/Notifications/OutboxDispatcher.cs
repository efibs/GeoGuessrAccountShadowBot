// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GeoGuessrAccountShadowBot.Application.Notifications;

/// <summary>
/// Wakes the dispatcher when something was queued, so that it doesn't have to poll quickly.
/// </summary>
public sealed class OutboxSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already awake.
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _signal.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// Delivers queued messages to Discord strictly oldest first, so a conversation can't arrive out of
/// order. A crash between Discord accepting a message and the row being marked causes one
/// duplicate. That is the price of never losing a notification, since Discord.Net offers no way to
/// make a send idempotent.
/// </summary>
public sealed partial class OutboxDispatcher(
    IServiceScopeFactory scopes,
    IDiscordReadiness readiness,
    OutboxSignal signal,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private const int MaxTransientBackoffSeconds = 300;

    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromDays(1);
    private static readonly TimeSpan KeepProcessedKeysFor = TimeSpan.FromDays(90);
    private static readonly TimeSpan KeepDeliveredFor = TimeSpan.FromDays(7);

    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;

    /// <summary>Delivers everything that is queued now, stopping early while Discord is failing.</summary>
    /// <returns>How long to wait before retrying, or null if the queue was drained.</returns>
    public async Task<TimeSpan?> DrainAsync(int transientFailures, CancellationToken cancellationToken)
    {
        while (true)
        {
            IReadOnlyList<OutboxEntry> entries;
            var readScope = scopes.CreateAsyncScope();
            await using (readScope.ConfigureAwait(false))
            {
                var store = readScope.ServiceProvider.GetRequiredService<IShadowStore>();
                entries = await store.ReadOutboxAsync(BatchSize, cancellationToken).ConfigureAwait(false);
            }

            if (entries.Count == 0)
            {
                return null;
            }

            foreach (var entry in entries)
            {
                var result = await DeliverAsync(entry, cancellationToken).ConfigureAwait(false);
                if (result.Status == DeliveryStatus.Transient)
                {
                    LogDeliveryPostponed(entry.Message.GetType().Name, result.Reason);
                    return TimeSpan.FromSeconds(Math.Min(MaxTransientBackoffSeconds, Math.Pow(2, transientFailures + 1)));
                }
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await readiness.WaitUntilReadyAsync(stoppingToken).ConfigureAwait(false);

        var transientFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var retryAfter = await DrainAsync(transientFailures, stoppingToken).ConfigureAwait(false);
            await PruneIfDueAsync(stoppingToken).ConfigureAwait(false);

            transientFailures = retryAfter is null ? 0 : transientFailures + 1;
            if (retryAfter is { } delay)
            {
                await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await signal.WaitAsync(IdlePoll, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<DeliveryResult> DeliverAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();
            var publisher = scope.ServiceProvider.GetRequiredService<IDiscordPublisher>();

            var result = await publisher.PublishAsync(entry.Message, cancellationToken).ConfigureAwait(false);

            switch (result.Status)
            {
                case DeliveryStatus.Delivered:
                    await store.MarkDeliveredAsync(entry.Id, cancellationToken).ConfigureAwait(false);
                    break;
                case DeliveryStatus.Permanent:
                    LogDeliveryFailed(entry.Message.GetType().Name, result.Reason);
                    await store.MarkFailedAsync(entry.Id, result.Reason ?? "unknown", cancellationToken).ConfigureAwait(false);
                    break;
                case DeliveryStatus.Transient:
                    return result;
            }

            await store.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
    }

    private async Task PruneIfDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now < _nextPrune)
        {
            return;
        }

        _nextPrune = now + PruneEvery;

        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();
            await store.PruneAsync(now - KeepProcessedKeysFor, now - KeepDeliveredFor, cancellationToken).ConfigureAwait(false);
            await store.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivering a {MessageType} to Discord will be retried: {Reason}")]
    private partial void LogDeliveryPostponed(string messageType, string? reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "A {MessageType} could not be delivered to Discord: {Reason}")]
    private partial void LogDeliveryFailed(string messageType, string? reason);
}
