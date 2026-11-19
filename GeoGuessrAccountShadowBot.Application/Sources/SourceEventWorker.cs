// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Automation;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Pipeline;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Sources;

/// <summary>
/// Follows the event stream of every source and starts the rules listening to it. The position in
/// a source's feed is saved in the transaction that creates the runs, so after a restart or an
/// outage on either side, each event starts its rules exactly once.
/// </summary>
public sealed partial class SourceEventWorker(
    IServiceScopeFactory scopes,
    ExternalSources sources,
    SourceStatusTracker status,
    AutomationRuleRegistry registry,
    AutomationSignal signal,
    IEventPublisher events,
    AccountContext account,
    IOptions<ShadowOptions> options,
    TimeProvider timeProvider,
    ILogger<SourceEventWorker> logger,
    WorkerSupervision supervision) : SupervisedBackgroundService(supervision)
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RefusedRetry = TimeSpan.FromMinutes(5);

    public static string CursorKey(string source) => $"source:{source}:cursor";

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        var followed = sources.All.Where(source => source.HasEvents).ToArray();
        if (!options.Value.Control.Automation.Enabled || followed.Length == 0)
        {
            return;
        }

        await account.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);
        await Task.WhenAll(followed.Select(source => FollowAsync(source, stoppingToken))).ConfigureAwait(false);
    }

    /// <summary>Keeps one source's stream open, reconnecting with a backoff for as long as the bot runs.</summary>
    public async Task FollowAsync(IExternalSource source, CancellationToken cancellationToken)
    {
        status.Start(source.Name);
        var failures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await ReadStreamAsync(source, () => failures = 0, cancellationToken).ConfigureAwait(false);
                await AlertAsync(status.Down(source.Name, "The event stream ended.", refused: false), cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(1);
            }
            catch (ExternalSourceRefusedException exception)
            {
                LogRefused(source.Name, exception.Message);
                await AlertAsync(status.Down(source.Name, exception.Message, refused: true), cancellationToken).ConfigureAwait(false);
                delay = RefusedRetry;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                failures++;
                LogStreamFailed(source.Name, failures, exception.Message);
                await AlertAsync(status.Down(source.Name, Describe(exception), refused: false), cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(failures, 10))));
            }

            await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads one connection's worth of events.</summary>
    /// <param name="onConnected">Called once the source greeted, which resets the backoff.</param>
    public async Task ReadStreamAsync(IExternalSource source, Action onConnected, CancellationToken cancellationToken)
    {
        var cursor = await ReadCursorAsync(source.Name, cancellationToken).ConfigureAwait(false);
        await foreach (var item in source.StreamEventsAsync(cursor, cancellationToken).ConfigureAwait(false))
        {
            switch (item)
            {
                case SourceHello hello:
                    onConnected();
                    await AlertAsync(status.Connected(source.Name), cancellationToken).ConfigureAwait(false);
                    cursor = await GreetAsync(source.Name, cursor, hello, cancellationToken).ConfigureAwait(false);
                    break;

                case SourceEvent sourceEvent when cursor is { } last && sourceEvent.Id > last:
                    await StartRulesAsync(source.Name, sourceEvent, cancellationToken).ConfigureAwait(false);
                    cursor = sourceEvent.Id;
                    break;
            }
        }
    }

    /// <summary>
    /// Settles where to read from. A first connection starts at the source's newest event, as a
    /// first sync records without notifying, so old events don't start rules.
    /// </summary>
    private async Task<long> GreetAsync(string name, long? cursor, SourceHello hello, CancellationToken cancellationToken)
    {
        if (cursor is { } last && last <= hello.Newest)
        {
            if (hello.Oldest > 0 && last < hello.Oldest - 1)
            {
                LogGap(name, last, hello.Oldest);
                await AlertAsync(new OperatorAlertRaised(
                    $"Events from {name} may be missing",
                    $"This instance was away longer than {name} keeps its events (ExternalApi:EventRetention there), so rules may have missed some.",
                    false), cancellationToken).ConfigureAwait(false);
            }

            return last;
        }

        if (cursor is not null)
        {
            // The source has fewer events than this instance saw, so its database started over.
            LogStartedOver(name, cursor.Value, hello.Newest);
            await AlertAsync(new OperatorAlertRaised(
                $"Source {name} started over",
                "Its event feed is behind what this instance already saw, so its database was probably replaced. Following it from its newest event.",
                false), cancellationToken).ConfigureAwait(false);
        }

        await SaveCursorAsync(name, hello.Newest, cancellationToken).ConfigureAwait(false);
        return hello.Newest;
    }

    private async Task StartRulesAsync(string name, SourceEvent sourceEvent, CancellationToken cancellationToken)
    {
        var trigger = ExternalSourceNames.TriggerKey(name, sourceEvent.Trigger);
        var rules = registry.RulesFor(trigger);
        var started = 0;

        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            if (rules.Count > 0)
            {
                var data = sourceEvent.Data.DeepClone().AsObject();
                data["source"] = name;
                var fire = new AutomationFire(
                    trigger,
                    AutomationEngine.ParseUserId(sourceEvent.PlayerId),
                    sourceEvent.OccurredAt,
                    data,
                    $"source:{name}:{sourceEvent.Id}");
                started = scope.ServiceProvider.GetRequiredService<AutomationRunFactory>().Create(fire, rules, depth: 0).Count;
            }

            var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();
            await store.WriteStateAsync(CursorKey(name), new SourceCursor(sourceEvent.Id), cancellationToken).ConfigureAwait(false);
            await store.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        status.EventReceived(name, sourceEvent.RecordedAt);
        if (started > 0)
        {
            LogStarted(name, sourceEvent.Trigger, started);
            signal.Wake();
        }
    }

    private async Task<long?> ReadCursorAsync(string name, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();
            return (await store.ReadStateAsync<SourceCursor>(CursorKey(name), cancellationToken).ConfigureAwait(false))?.Id;
        }
    }

    private async Task SaveCursorAsync(string name, long id, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();
            await store.WriteStateAsync(CursorKey(name), new SourceCursor(id), cancellationToken).ConfigureAwait(false);
            await store.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AlertAsync(OperatorAlertRaised? alert, CancellationToken cancellationToken)
    {
        if (alert is not null)
        {
            await events.PublishAsync(alert, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Describe(Exception exception) => exception switch
    {
        ExternalSourceUnavailableException => exception.Message,
        _ => "Reading its events failed. See the logs."
    };

    /// <summary>The last event of a source that this instance handled.</summary>
    public sealed record SourceCursor(long Id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Event {Trigger} from source {Source} started {Count} automation runs")]
    private partial void LogStarted(string source, string trigger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Following source {Source} failed {Failures} times in a row: {Reason}")]
    private partial void LogStreamFailed(string source, int failures, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Source {Source} refused this instance: {Reason}")]
    private partial void LogRefused(string source, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Source {Source} no longer has the events after {Cursor}, its oldest is {Oldest}")]
    private partial void LogGap(string source, long cursor, long oldest);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Source {Source} is at event {Newest}, behind {Cursor} seen here, so it started over")]
    private partial void LogStartedOver(string source, long cursor, long newest);
}
