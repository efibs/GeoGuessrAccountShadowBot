// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Hosting;

namespace GeoGuessrAccountShadowBot.Application.Pipeline;

/// <summary>
/// The single consumer of the event queue. Handling events one at a time is what keeps snapshot
/// comparisons free of races.
/// </summary>
public sealed class EventPipelineWorker(EventBus bus, EventProcessor processor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var shadowEvent in bus.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            await processor.ProcessAsync(shadowEvent, stoppingToken).ConfigureAwait(false);
        }
    }
}
