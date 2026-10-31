// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Frozen;
using System.Reflection;
using GeoGuessrAccountShadowBot.Domain.Events;
using Microsoft.Extensions.DependencyInjection;

namespace GeoGuessrAccountShadowBot.Application.Pipeline;

/// <summary>
/// Finds the handler for an event. The handlers are discovered once at startup, and from then on
/// dispatch is a dictionary lookup rather than reflection.
/// </summary>
public sealed class EventRouter
{
    private readonly FrozenDictionary<Type, IEventRoute> _routes;

    public EventRouter(IEnumerable<Type> eventTypes) =>
        _routes = eventTypes.ToFrozenDictionary(
            type => type,
            type => (IEventRoute)Activator.CreateInstance(typeof(EventRoute<>).MakeGenericType(type))!);

    /// <summary>The event types that have a handler, for tests that check nothing was forgotten.</summary>
    public IEnumerable<Type> EventTypes => _routes.Keys;

    public static IEnumerable<HandlerRegistration> DiscoverHandlers(Assembly assembly) =>
        from type in assembly.GetTypes()
        where type is { IsClass: true, IsAbstract: false }
        from contract in type.GetInterfaces()
        where contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IShadowEventHandler<>)
        select new HandlerRegistration(contract.GetGenericArguments()[0], contract, type);

    public ValueTask DispatchAsync(ShadowEvent shadowEvent, IServiceProvider services, CancellationToken cancellationToken) =>
        _routes.TryGetValue(shadowEvent.GetType(), out var route)
            ? route.InvokeAsync(shadowEvent, services, cancellationToken)
            : throw new InvalidOperationException($"No handler is registered for {shadowEvent.GetType().Name}.");

    private interface IEventRoute
    {
        ValueTask InvokeAsync(ShadowEvent shadowEvent, IServiceProvider services, CancellationToken cancellationToken);
    }

    private sealed class EventRoute<TEvent> : IEventRoute where TEvent : ShadowEvent
    {
        public ValueTask InvokeAsync(ShadowEvent shadowEvent, IServiceProvider services, CancellationToken cancellationToken) =>
            services.GetRequiredService<IShadowEventHandler<TEvent>>().HandleAsync((TEvent)shadowEvent, cancellationToken);
    }
}

/// <summary>
/// A handler found by <see cref="EventRouter.DiscoverHandlers"/>.
/// </summary>
public sealed record HandlerRegistration(Type EventType, Type Contract, Type Implementation);
