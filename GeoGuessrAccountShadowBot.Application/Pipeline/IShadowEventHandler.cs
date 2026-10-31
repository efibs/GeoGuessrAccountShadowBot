// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Domain.Events;

namespace GeoGuessrAccountShadowBot.Application.Pipeline;

/// <summary>
/// Handles one event type. Handlers run one at a time inside a transaction, so they may only touch
/// the store and <see cref="IEventContext"/>. A network call here would stall every other event.
/// </summary>
public interface IShadowEventHandler<in TEvent> where TEvent : ShadowEvent
{
    ValueTask HandleAsync(TEvent shadowEvent, CancellationToken cancellationToken);
}

/// <summary>
/// What a handler can produce. Both go into the current transaction.
/// </summary>
public interface IEventContext
{
    /// <summary>Queues a Discord delivery, unless the current event is silent.</summary>
    void Notify(OutboxMessage message);

    /// <summary>Runs another event after this one, in the same transaction.</summary>
    void Raise(ShadowEvent shadowEvent);
}
