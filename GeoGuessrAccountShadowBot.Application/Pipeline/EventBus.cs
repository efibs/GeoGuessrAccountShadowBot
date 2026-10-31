// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading.Channels;
using GeoGuessrAccountShadowBot.Domain.Events;

namespace GeoGuessrAccountShadowBot.Application.Pipeline;

/// <summary>
/// The way into the pipeline. Durability doesn't live here, it comes from the store. The queue's
/// job is to give every state change a single writer.
/// </summary>
public interface IEventPublisher
{
    /// <summary>Queues an event, waiting if the pipeline is full.</summary>
    ValueTask PublishAsync(ShadowEvent shadowEvent, CancellationToken cancellationToken = default);

    /// <summary>Queues an event unless the pipeline is full. Only for events that can be lost.</summary>
    bool TryPublish(ShadowEvent shadowEvent);
}

/// <summary>
/// The bounded queue between event sources and the single pipeline consumer.
/// </summary>
public sealed class EventBus : IEventPublisher
{
    private const int Capacity = 512;

    private readonly Channel<ShadowEvent> _channel = Channel.CreateBounded<ShadowEvent>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    public ChannelReader<ShadowEvent> Reader => _channel.Reader;

    public ValueTask PublishAsync(ShadowEvent shadowEvent, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(shadowEvent, cancellationToken);

    public bool TryPublish(ShadowEvent shadowEvent) => _channel.Writer.TryWrite(shadowEvent);
}
