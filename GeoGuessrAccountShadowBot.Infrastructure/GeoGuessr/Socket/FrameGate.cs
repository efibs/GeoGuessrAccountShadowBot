// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Pipeline;
using GeoGuessrAccountShadowBot.Domain.Events;

namespace GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Socket;

/// <summary>
/// Holds live events while catch-up runs after connecting, so that messages from the outage arrive
/// before the ones that came in during catch-up. Overlap between the two is harmless, because the
/// pipeline deduplicates by message id.
/// </summary>
internal sealed class FrameGate(IEventPublisher events) : IDisposable
{
    private const int MaxHeldEvents = 2048;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<ShadowEvent>? _held = [];

    public async ValueTask PassAsync(ShadowEvent shadowEvent, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_held is { Count: < MaxHeldEvents })
            {
                _held.Add(shadowEvent);
                return;
            }

            await events.PublishAsync(shadowEvent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var shadowEvent in _held ?? [])
            {
                await events.PublishAsync(shadowEvent, cancellationToken).ConfigureAwait(false);
            }

            _held = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();
}
