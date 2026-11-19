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
/// Polls the club's pending applications, since GeoGuessr sends no frame for a new one. It stays
/// idle unless the event is switched on, and stops for good if the account may not see them.
/// </summary>
public sealed partial class ClubApplicationsPollingWorker(
    IGeoGuessrApi api,
    IEventPublisher events,
    AccountContext account,
    IOptions<ShadowOptions> options,
    TimeProvider timeProvider,
    ILogger<ClubApplicationsPollingWorker> logger,
    WorkerSupervision supervision) : SupervisedBackgroundService(supervision)
{
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.IsEventEnabled(EventKeys.ClubApplicationReceived))
        {
            return;
        }

        await account.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);
        if (account.ClubId is not { } clubId)
        {
            return;
        }

        using var timer = new PeriodicTimer(settings.Polling.ClubApplicationsInterval, timeProvider);
        do
        {
            try
            {
                if (await api.GetClubApplicationsAsync(clubId, stoppingToken).ConfigureAwait(false) is not { } applications)
                {
                    LogNotPermitted();
                    await events.PublishAsync(new OperatorAlertRaised(
                        "Club applications can't be read",
                        "GeoGuessr only shows applications to members who may manage them. Give the account that permission and restart the bot.",
                        false), stoppingToken).ConfigureAwait(false);
                    return;
                }

                await events.PublishAsync(new ClubApplicationsFetched(applications), stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogPollFailed(exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the club applications failed and will run again later")]
    private partial void LogPollFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The account may not manage club applications, so they won't be polled")]
    private partial void LogNotPermitted();
}
