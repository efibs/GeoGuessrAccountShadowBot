// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Pipeline;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Reconciliation;

/// <summary>
/// Polls the account's ranked state, the one thing GeoGuessr doesn't push. It stays idle unless a
/// ranked event is switched on.
/// </summary>
public sealed partial class RankedPollingWorker(
    IGeoGuessrApi api,
    IEventPublisher events,
    AccountContext account,
    IOptions<ShadowOptions> options,
    TimeProvider timeProvider,
    ILogger<RankedPollingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.IsEventEnabled(EventKeys.RankedDivisionChanged) && !settings.IsEventEnabled(EventKeys.SeasonChanged))
        {
            return;
        }

        await account.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(settings.Polling.RankedInterval, timeProvider);
        do
        {
            try
            {
                var snapshot = await api.GetRankedAsync(account.UserId, stoppingToken).ConfigureAwait(false);
                await events.PublishAsync(new RankedFetched(snapshot), stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogPollFailed(exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the ranked state failed and will run again later")]
    private partial void LogPollFailed(Exception exception);
}
