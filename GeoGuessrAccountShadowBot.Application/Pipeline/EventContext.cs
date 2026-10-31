// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics.CodeAnalysis;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Events;

namespace GeoGuessrAccountShadowBot.Application.Pipeline;

/// <summary>
/// The per-event working state of the processor. Events raised by a handler inherit the silence of
/// the event that caused them, so a silent first sync can't notify through a derived event.
/// </summary>
public sealed class EventContext(IShadowStore store) : IEventContext
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
        if (!_silent)
        {
            store.Enqueue(message);
        }
    }

    public void Raise(ShadowEvent shadowEvent) =>
        _queue.Enqueue(_silent ? shadowEvent with { Silent = true } : shadowEvent);
}
