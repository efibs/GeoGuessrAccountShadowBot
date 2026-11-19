// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Pipeline;
using GeoGuessrAccountShadowBot.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Automation;

/// <summary>
/// Runs automation runs one at a time, oldest first. A run that waits doesn't hold the worker: it
/// is saved with the time to look at it again, which also makes waits survive restarts.
/// </summary>
public sealed partial class AutomationWorker(
    IServiceScopeFactory scopes,
    AutomationEngine engine,
    AutomationSignal signal,
    AccountContext account,
    IOptions<ShadowOptions> options,
    TimeProvider timeProvider,
    ILogger<AutomationWorker> logger,
    WorkerSupervision supervision) : SupervisedBackgroundService(supervision)
{
    private const int BatchSize = 25;
    private const int MaxCrashes = 3;

    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromDays(1);
    private static readonly TimeSpan KeepFinishedFor = TimeSpan.FromDays(30);

    private readonly Dictionary<long, int> _crashes = [];
    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;

    /// <summary>Runs a batch of what is due now.</summary>
    /// <returns>How many runs were looked at. A full batch means more may be due right away.</returns>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<long> due;
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var automations = scope.ServiceProvider.GetRequiredService<IAutomationStore>();
            due = await automations.ReadDueRunIdsAsync(timeProvider.GetUtcNow(), BatchSize, cancellationToken).ConfigureAwait(false);
        }

        foreach (var runId in due)
        {
            try
            {
                await engine.ExecuteAsync(runId, cancellationToken).ConfigureAwait(false);
                _crashes.Remove(runId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var crashes = _crashes[runId] = _crashes.GetValueOrDefault(runId) + 1;
                LogRunCrashed(exception, runId, crashes);
                if (crashes >= MaxCrashes)
                {
                    _crashes.Remove(runId);
                    await engine.FailAsync(runId, "The run failed repeatedly. See the logs.", cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return due.Count;
    }

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Control.Automation.Enabled)
        {
            return;
        }

        await account.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            var more = false;
            try
            {
                more = await RunDueAsync(stoppingToken).ConfigureAwait(false) == BatchSize;
                await PruneIfDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogPassFailed(exception);
            }

            if (!more)
            {
                await signal.WaitAsync(IdlePoll, stoppingToken).ConfigureAwait(false);
            }
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
            var automations = scope.ServiceProvider.GetRequiredService<IAutomationStore>();
            await automations.PruneRunsAsync(now - KeepFinishedFor, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Automation run {RunId} threw, {Crashes} times so far")]
    private partial void LogRunCrashed(Exception exception, long runId, int crashes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An automation pass failed and will run again")]
    private partial void LogPassFailed(Exception exception);
}

/// <summary>
/// Starts schedule rules. The first sight of a schedule only records the time, so a new rule or a
/// changed cron waits for its next occurrence. After downtime, a missed occurrence runs once if
/// it's within the rule's MaxAge, however many were missed.
/// </summary>
public sealed partial class AutomationScheduler(
    IServiceScopeFactory scopes,
    AutomationRuleRegistry registry,
    AutomationSignal signal,
    AccountContext account,
    IOptions<ShadowOptions> options,
    TimeProvider timeProvider,
    ILogger<AutomationScheduler> logger,
    WorkerSupervision supervision) : SupervisedBackgroundService(supervision)
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);

    public static string StateKey(string ruleName) => $"automation:schedule:{ruleName}";

    /// <summary>Starts every schedule that is due.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var started = 0;

        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();
            var factory = scope.ServiceProvider.GetRequiredService<AutomationRunFactory>();

            foreach (var rule in registry.RulesFor(AutomationTriggerSpec.SchedulePrefix))
            {
                var key = StateKey(rule.Name);
                var state = await store.ReadStateAsync<ScheduleState>(key, cancellationToken).ConfigureAwait(false);
                var cron = rule.Trigger.Name;

                if (state is null || state.Cron != cron || state.TimeZone != rule.Trigger.TimeZone.Id)
                {
                    await store.WriteStateAsync(key, new ScheduleState(cron, rule.Trigger.TimeZone.Id, now), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Only the latest missed occurrence counts, so a long downtime starts one run.
                var missed = rule.Trigger.Cron!
                    .GetOccurrences(state.LastCheckedAt.UtcDateTime, now.UtcDateTime, rule.Trigger.TimeZone, fromInclusive: false, toInclusive: true)
                    .Select(occurrence => (DateTime?)occurrence)
                    .LastOrDefault();
                if (missed is not { } occurrence)
                {
                    continue;
                }

                var occurredAt = new DateTimeOffset(occurrence, TimeSpan.Zero);
                var fire = new AutomationFire(AutomationTriggerSpec.SchedulePrefix, null, occurredAt, new System.Text.Json.Nodes.JsonObject
                {
                    ["scheduled_at"] = occurredAt
                }, $"schedule:{rule.Name}:{occurredAt.ToUnixTimeSeconds()}");
                started += factory.Create(fire, [rule], depth: 0).Count;

                await store.WriteStateAsync(key, state with { LastCheckedAt = now }, cancellationToken).ConfigureAwait(false);
            }

            await store.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (started > 0)
        {
            LogStarted(started);
            signal.Wake();
        }
    }

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Control.Automation.Enabled)
        {
            return;
        }

        await account.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TickInterval, timeProvider);
        do
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogTickFailed(exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>What a schedule last saw. A change of cron or zone starts it over.</summary>
    public sealed record ScheduleState(string Cron, string TimeZone, DateTimeOffset LastCheckedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Started {Count} scheduled automation runs")]
    private partial void LogStarted(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking automation schedules failed and will run again")]
    private partial void LogTickFailed(Exception exception);
}
