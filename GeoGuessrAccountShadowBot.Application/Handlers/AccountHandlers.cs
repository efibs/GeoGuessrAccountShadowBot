// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Pipeline;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Application.Reconciliation;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;

namespace GeoGuessrAccountShadowBot.Application.Handlers;

/// <summary>
/// Announces an entry of the account's notification list.
/// </summary>
public sealed class AccountNotificationHandler(IEventContext context) : IShadowEventHandler<AccountNotificationReceived>
{
    public ValueTask HandleAsync(AccountNotificationReceived shadowEvent, CancellationToken cancellationToken)
    {
        var notification = shadowEvent.Notification;
        var route = NotificationClassifier.Classify(notification.Type);

        if (route.SwitchKey is { } key)
        {
            context.Notify(new EventCard(key, route.Style, route.Title, null, notification.SentById, null, notification.SentAt));
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Compares a ranked snapshot with the stored one.
/// </summary>
public sealed class RankedFetchedHandler(IShadowStore store, IEventContext context) : IShadowEventHandler<RankedFetched>
{
    private const string StateKey = "ranked";

    public async ValueTask HandleAsync(RankedFetched shadowEvent, CancellationToken cancellationToken)
    {
        var current = shadowEvent.Snapshot;
        var previous = await store.ReadStateAsync<RankedSnapshot>(StateKey, cancellationToken).ConfigureAwait(false);

        if (previous is not null)
        {
            if (current.DivisionChangedFrom(previous))
            {
                context.Raise(new RankedDivisionChanged(previous, current));
            }

            if (current.SeasonChangedFrom(previous))
            {
                context.Raise(new SeasonChanged(current));
            }
        }

        await store.WriteStateAsync(StateKey, current, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Announces a move between ranked divisions.
/// </summary>
public sealed class RankedDivisionChangedHandler(IEventContext context) : IShadowEventHandler<RankedDivisionChanged>
{
    public ValueTask HandleAsync(RankedDivisionChanged shadowEvent, CancellationToken cancellationToken)
    {
        var before = shadowEvent.Previous.DivisionName ?? "Unranked";
        var after = shadowEvent.Current.DivisionName ?? "Unranked";
        var rating = shadowEvent.Current.Rating is { } value ? $" (rating {value})" : "";

        context.Notify(new EventCard(
            EventKeys.RankedDivisionChanged, CardStyle.Info, "Ranked division changed", null, null, new UntrustedText($"{before} → {after}{rating}"), null));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Announces a new competitive season.
/// </summary>
public sealed class SeasonChangedHandler(IEventContext context) : IShadowEventHandler<SeasonChanged>
{
    public ValueTask HandleAsync(SeasonChanged shadowEvent, CancellationToken cancellationToken)
    {
        context.Notify(new EventCard(EventKeys.SeasonChanged, CardStyle.Info, "A new season started", null, null, null, null));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Records that a scope finished its silent first run.
/// </summary>
public sealed class BaselineCompletedHandler(IShadowStore store, IEventContext context, TimeProvider timeProvider)
    : IShadowEventHandler<BaselineCompleted>
{
    public async ValueTask HandleAsync(BaselineCompleted shadowEvent, CancellationToken cancellationToken)
    {
        var state = new BaselineState(timeProvider.GetUtcNow());
        await store.WriteStateAsync(BaselineScopes.StateKey(shadowEvent.Scope), state, cancellationToken).ConfigureAwait(false);

        context.Notify(new StatusMessage(
            "First sync finished",
            $"Recorded {shadowEvent.RecordedItems} existing items from `{shadowEvent.Scope}` without announcing them.",
            false));
    }
}

/// <summary>
/// Forwards an operator alert to the status channel.
/// </summary>
public sealed class OperatorAlertHandler(IEventContext context) : IShadowEventHandler<OperatorAlertRaised>
{
    public ValueTask HandleAsync(OperatorAlertRaised shadowEvent, CancellationToken cancellationToken)
    {
        context.Notify(new StatusMessage(shadowEvent.Title, shadowEvent.Detail, shadowEvent.Ping));
        return ValueTask.CompletedTask;
    }
}
