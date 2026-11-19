// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics.CodeAnalysis;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Events;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Pipeline;

/// <summary>
/// The per-event working state of the processor. Events raised by a handler inherit the silence of
/// the event that caused them, so a silent first sync can't notify through a derived event. Cards
/// of events whose <see cref="EventOptions.Card"/> is off are dropped here, so no handler has to
/// check it, and the event itself still runs its rules.
/// </summary>
public sealed class EventContext(IShadowStore store, IOptions<ShadowOptions> options) : IEventContext
{
    private readonly Queue<ShadowEvent> _queue = new();
    private bool _silent;

    public void Begin(ShadowEvent shadowEvent)
    {
        _silent = false;
        _queue.Clear();
        _queue.Enqueue(shadowEvent);
    }

    public bool TryTakeNext([NotNullWhen(true)] out ShadowEvent? next)
    {
        if (!_queue.TryDequeue(out next))
        {
            return false;
        }

        _silent = next.Silent;
        return true;
    }

    public void Notify(OutboxMessage message)
    {
        if (!_silent && (CardKey(message) is not { } key || options.Value.IsCardEnabled(key)))
        {
            store.Enqueue(message);
        }
    }

    /// <summary>The event whose card a message is, or null for what isn't a card.</summary>
    private static string? CardKey(OutboxMessage message) => message switch
    {
        EventCard card => card.EventKey,
        FriendRequestCard or FriendRequestOutcome => EventKeys.FriendRequestReceived,
        _ => null
    };

    public void Raise(ShadowEvent shadowEvent) =>
        _queue.Enqueue(_silent ? shadowEvent with { Silent = true } : shadowEvent);
}
