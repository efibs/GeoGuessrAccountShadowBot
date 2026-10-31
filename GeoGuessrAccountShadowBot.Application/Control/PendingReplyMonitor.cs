// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Handlers;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Control;

/// <summary>
/// Settles replies whose echo never came. It looks the message up in the chat history once, because
/// a dropped socket can lose the echo of a message that did arrive.
/// </summary>
public sealed partial class PendingReplyMonitor(
    IServiceScopeFactory scopes,
    IGeoGuessrApi api,
    AccountContext account,
    OutboxSignal outboxSignal,
    IOptions<ShadowOptions> options,
    TimeProvider timeProvider,
    ILogger<PendingReplyMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    public async Task SettleExpiredAsync(CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();
            var cutoff = timeProvider.GetUtcNow() - options.Value.Control.DmReply.EchoTimeout;
            var expired = await store.ReadPendingRepliesOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);

            foreach (var pending in expired)
            {
                var kind = await WasDeliveredAsync(pending, cancellationToken).ConfigureAwait(false)
                    ? ReplyFeedbackKind.Delivered
                    : ReplyFeedbackKind.Unconfirmed;
                var reason = kind == ReplyFeedbackKind.Unconfirmed ? "GeoGuessr didn't confirm this message, so it may not have arrived." : null;

                store.Enqueue(new ReplyFeedback(pending.ThreadId, pending.MessageId, kind, reason));
                await store.RemovePendingReplyAsync(pending.Id, cancellationToken).ConfigureAwait(false);
            }

            await store.CommitAsync(cancellationToken).ConfigureAwait(false);

            if (expired.Count > 0)
            {
                outboxSignal.Wake();
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await account.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(CheckInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await SettleExpiredAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogSettleFailed(exception);
            }
        }
    }

    private async Task<bool> WasDeliveredAsync(PendingReply pending, CancellationToken cancellationToken)
    {
        try
        {
            var history = await api.GetChatHistoryAsync(pending.PeerId, cancellationToken).ConfigureAwait(false);
            return history.Any(message => message.IsSentBy(account.UserId)
                                          && message.Text is { } text
                                          && ChatText.Hash(text) == pending.TextHash
                                          && message.SentAt >= pending.CreatedAt - TimeSpan.FromMinutes(1));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogHistoryCheckFailed(exception);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settling pending replies failed and will run again")]
    private partial void LogSettleFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check the history for an unconfirmed reply")]
    private partial void LogHistoryCheckFailed(Exception exception);
}
