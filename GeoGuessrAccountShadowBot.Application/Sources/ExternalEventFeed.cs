// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Frozen;
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
/// Which events the external API serves. Only triggers some client is allowed are recorded, so
/// nothing, and in particular no chat text, is kept for nobody.
/// </summary>
public sealed class ExternalEventFeed
{
    private readonly FrozenSet<string> _served;

    public ExternalEventFeed(IOptions<ShadowOptions> options)
    {
        var api = options.Value.ExternalApi;
        _served = api.Enabled
            ? api.Clients.Values.SelectMany(client => client.Events).Select(Canonical).OfType<string>().ToFrozenSet(StringComparer.Ordinal)
            : FrozenSet<string>.Empty;
    }

    /// <summary>Whether anything is served at all, so events can skip the feed quickly.</summary>
    public bool IsActive => _served.Count > 0;

    public bool IsServed(string trigger) => _served.Contains(trigger);

    /// <summary>
    /// The trigger as rules write it, or null if it can't be served. Schedules, manual runs and
    /// other sources' events aren't events of this account, so they are never passed on.
    /// </summary>
    public static string? Canonical(string trigger)
    {
        if (trigger.StartsWith(AutomationTriggerSpec.CustomPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = trigger[AutomationTriggerSpec.CustomPrefix.Length..];
            return AutomationRuleParser.IsValidName(name) ? AutomationTriggerSpec.CustomPrefix + name : null;
        }

        return EventKeys.All.FirstOrDefault(key => string.Equals(key, trigger, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Wakes every open event stream when something was recorded.</summary>
public sealed class ExternalFeedSignal
{
    private readonly Lock _lock = new();
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Wake()
    {
        TaskCompletionSource woken;
        lock (_lock)
        {
            woken = _next;
            _next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        woken.TrySetResult();
    }

    /// <summary>
    /// Completes on the next wake. Take it before reading the feed, so that an event recorded
    /// between the read and the wait isn't missed.
    /// </summary>
    public Task Next
    {
        get
        {
            lock (_lock)
            {
                return _next.Task;
            }
        }
    }

    /// <summary>Waits for a wake taken from <see cref="Next"/>, or the timeout, whichever comes first.</summary>
    /// <returns>Whether it was woken, rather than timed out.</returns>
    public static async Task<bool> WaitAsync(Task next, TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        try
        {
            await next.WaitAsync(timeout, timeProvider, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}

/// <summary>
/// Records events for the feed, in the unit of work of whatever produced them. Silent events, such
/// as a first sync, aren't recorded, as they start no rules here either.
/// </summary>
public sealed class ExternalEventCapture(ExternalEventFeed feed, IExternalEventStore store, AccountContext account, TimeProvider timeProvider)
{
    /// <returns>Whether anything was recorded.</returns>
    public bool Capture(ShadowEvent shadowEvent) =>
        feed.IsActive && !shadowEvent.Silent && AutomationTriggers.Describe(shadowEvent, account.UserId) is { } fire && Capture(fire);

    /// <returns>Whether anything was recorded.</returns>
    public bool Capture(AutomationFire fire)
    {
        if (!feed.IsServed(fire.Trigger))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        store.AddExternalEvent(fire.Trigger, fire.SubjectUserId, fire.OccurredAt ?? now, now, fire.Data.ToJsonString());
        return true;
    }
}

/// <summary>Removes events older than the retention, once an hour.</summary>
public sealed partial class ExternalFeedPruner(
    IServiceScopeFactory scopes,
    IOptions<ShadowOptions> options,
    TimeProvider timeProvider,
    ILogger<ExternalFeedPruner> logger,
    WorkerSupervision supervision) : SupervisedBackgroundService(supervision)
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public async Task PruneAsync(CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IExternalEventStore>();
            await store.PruneExternalEventsAsync(timeProvider.GetUtcNow() - options.Value.ExternalApi.EventRetention, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ExternalApi.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            try
            {
                await PruneAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogPruneFailed(exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removing old events from the external feed failed and will run again")]
    private partial void LogPruneFailed(Exception exception);
}
