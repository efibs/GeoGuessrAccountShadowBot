// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Domain.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GeoGuessrAccountShadowBot.Application.Pipeline;

/// <summary>
/// A failure that no restart can fix, such as a database of another account. It passes through
/// <see cref="SupervisedBackgroundService"/> and stops the bot, which is the point.
/// </summary>
public sealed class FatalShadowException(string message) : Exception(message);

/// <summary>
/// A worker that restarts after a crash instead of stopping the host. A host restart is never the
/// cure: it resends whatever was in flight, as issue #19 showed, and a worker that crashes on the
/// same input crashes again after it. Each crash is logged and reported to the status channel.
/// Only a <see cref="FatalShadowException"/> still stops the host.
/// </summary>
public abstract class SupervisedBackgroundService(WorkerSupervision supervision) : BackgroundService
{
    /// <summary>The worker's loop. Returning ends the worker for good.</summary>
    protected abstract Task RunAsync(CancellationToken stoppingToken);

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var name = GetType().Name;
        var crashes = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAt = supervision.Time.GetUtcNow();
            try
            {
                await RunAsync(stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not FatalShadowException)
            {
                crashes = supervision.Time.GetUtcNow() - startedAt >= WorkerSupervision.HealthyRun ? 1 : crashes + 1;
                var delay = WorkerSupervision.Backoff(crashes);
                supervision.ReportCrash(name, exception, crashes, delay);

                try
                {
                    await Task.Delay(delay, supervision.Time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}

/// <summary>
/// What every <see cref="SupervisedBackgroundService"/> shares: the clock for its backoff, and where
/// its crashes are reported.
/// </summary>
public sealed partial class WorkerSupervision(TimeProvider time, IEventPublisher events, ILogger<WorkerSupervision> logger)
{
    /// <summary>A worker that ran this long before crashing starts its backoff over.</summary>
    public static readonly TimeSpan HealthyRun = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    public TimeProvider Time => time;

    /// <summary>1 s after the first crash, doubling up to five minutes.</summary>
    public static TimeSpan Backoff(int crashes) =>
        TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(crashes - 1, 16))));

    public void ReportCrash(string worker, Exception exception, int crashes, TimeSpan restartIn)
    {
        LogCrashed(exception, worker, crashes, restartIn);

        // Only the first crash of a series is announced, so a worker stuck in a loop can't flood
        // the status channel. The logs have every one.
        if (crashes == 1)
        {
            events.TryPublish(new OperatorAlertRaised(
                "A background worker crashed",
                $"{worker} failed with {exception.GetType().Name} and restarts by itself. If this repeats, see the logs.",
                false));
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{Worker} crashed (crash {Crashes} in a row) and restarts in {RestartIn}")]
    private partial void LogCrashed(Exception exception, string worker, int crashes, TimeSpan restartIn);
}
