// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Pipeline;

/// <summary>
/// Runs one event through its handler. The dedupe key, state changes and queued deliveries all
/// commit in one transaction, so an event is either fully applied or can safely be seen again.
/// </summary>
public sealed partial class EventProcessor(
    IServiceScopeFactory scopes,
    EventRouter router,
    OutboxSignal outboxSignal,
    IOptions<ShadowOptions> options,
    ILogger<EventProcessor> logger)
{
    private const int MaxAttempts = 3;

    public async Task ProcessAsync(ShadowEvent shadowEvent, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await ProcessOnceAsync(shadowEvent, cancellationToken).ConfigureAwait(false);
                outboxSignal.Wake();
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogHandlerFailed(exception, shadowEvent.GetType().Name, attempt);
            }
        }

        await GiveUpAsync(shadowEvent, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessOnceAsync(ShadowEvent shadowEvent, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();
            var context = scope.ServiceProvider.GetRequiredService<EventContext>();

            if (shadowEvent.DedupeKey is { } key && !await store.TryMarkProcessedAsync(key, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            context.Begin(shadowEvent);
            while (context.TryTakeNext(out var current))
            {
                if (IsEnabled(current))
                {
                    await router.DispatchAsync(current, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
                }
            }

            await store.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsEnabled(ShadowEvent shadowEvent) =>
        shadowEvent.SwitchKey is not { } key || options.Value.IsEventEnabled(key);

    /// <summary>
    /// Records a failing event as processed, so that it can't block the queue or come back forever,
    /// and tells the operators about it.
    /// </summary>
    private async Task GiveUpAsync(ShadowEvent shadowEvent, CancellationToken cancellationToken)
    {
        var name = shadowEvent.GetType().Name;
        LogEventDropped(name);

        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();
            if (shadowEvent.DedupeKey is { } key)
            {
                await store.TryMarkProcessedAsync(key, cancellationToken).ConfigureAwait(false);
            }

            store.Enqueue(new StatusMessage("An event could not be processed", $"A {name} event failed {MaxAttempts} times and was dropped. See the logs.", false));
            await store.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        outboxSignal.Wake();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handling {EventType} failed on attempt {Attempt}")]
    private partial void LogHandlerFailed(Exception exception, string eventType, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dropped a {EventType} event after repeated failures")]
    private partial void LogEventDropped(string eventType);
}
