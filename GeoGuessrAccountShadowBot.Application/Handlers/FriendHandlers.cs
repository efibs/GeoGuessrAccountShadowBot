// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Pipeline;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;
using Microsoft.Extensions.Logging;

namespace GeoGuessrAccountShadowBot.Application.Handlers;

/// <summary>
/// Compares a friendship snapshot with the stored one. GeoGuessr's friend frames carry no payload,
/// so this diff is the only producer of friend events, and that keeps them from firing twice.
/// </summary>
public sealed partial class FriendshipsFetchedHandler(
    IShadowStore store,
    IEventContext context,
    ILogger<FriendshipsFetchedHandler> logger) : IShadowEventHandler<FriendshipsFetched>
{
    private const string StateKey = "friendships";

    public async ValueTask HandleAsync(FriendshipsFetched shadowEvent, CancellationToken cancellationToken)
    {
        var current = shadowEvent.Friendships;
        var previous = await store.ReadStateAsync<FriendshipSet>(StateKey, cancellationToken).ConfigureAwait(false);

        if (previous is null)
        {
            context.Notify(new StatusMessage(
                "Friends recorded",
                $"Tracking {current.Friends.Count} friends. {current.Incoming.Count} incoming requests were already pending and won't be announced.",
                false));
        }
        else if (current.LooksLikeGlitchComparedTo(previous))
        {
            LogSnapshotIgnored(previous.Friends.Count, current.Friends.Count);
            return;
        }
        else
        {
            var diff = current.DiffFrom(previous);

            foreach (var userId in diff.NewIncoming)
            {
                context.Raise(new FriendRequestReceived(userId));
            }

            foreach (var userId in diff.Accepted)
            {
                context.Raise(new FriendRequestAccepted(userId));
            }

            foreach (var userId in diff.ResolvedIncoming)
            {
                context.Raise(new FriendRequestResolved(userId, current.Friends.Contains(userId)));
            }
        }

        await store.WriteStateAsync(StateKey, current, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignored a friendship snapshot that went from {Before} to {After} friends")]
    private partial void LogSnapshotIgnored(int before, int after);
}

/// <summary>
/// Announces an incoming friend request.
/// </summary>
public sealed class FriendRequestReceivedHandler(IEventContext context) : IShadowEventHandler<FriendRequestReceived>
{
    public ValueTask HandleAsync(FriendRequestReceived shadowEvent, CancellationToken cancellationToken)
    {
        context.Notify(new FriendRequestCard(shadowEvent.UserId));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Announces that someone accepted the account's friend request.
/// </summary>
public sealed class FriendRequestAcceptedHandler(IEventContext context) : IShadowEventHandler<FriendRequestAccepted>
{
    public ValueTask HandleAsync(FriendRequestAccepted shadowEvent, CancellationToken cancellationToken)
    {
        context.Notify(new EventCard(
            EventKeys.FriendRequestAccepted, CardStyle.Success, "Friend request accepted", null, shadowEvent.UserId, null, null));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Closes the card of a friend request that was answered, from here or from another login.
/// </summary>
public sealed class FriendRequestResolvedHandler(IEventContext context) : IShadowEventHandler<FriendRequestResolved>
{
    public ValueTask HandleAsync(FriendRequestResolved shadowEvent, CancellationToken cancellationToken)
    {
        context.Notify(new FriendRequestOutcome(shadowEvent.UserId, shadowEvent.BecameFriend));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Announces friend presence, ignoring the brief offline-online flicker GeoGuessr sends when
/// someone merely reloads a page.
/// </summary>
public sealed class FriendPresenceHandler(IEventContext context, PresenceTracker tracker, TimeProvider timeProvider)
    : IShadowEventHandler<FriendPresenceChanged>
{
    public ValueTask HandleAsync(FriendPresenceChanged shadowEvent, CancellationToken cancellationToken)
    {
        if (tracker.ShouldAnnounce(shadowEvent.FriendId, shadowEvent.IsOnline, timeProvider.GetUtcNow()))
        {
            var title = shadowEvent.IsOnline ? "A friend came online" : "A friend went offline";
            var detail = shadowEvent.Activity is null ? null : $"Activity: {shadowEvent.Activity}";
            context.Notify(new EventCard(EventKeys.FriendPresence, CardStyle.Info, title, detail, shadowEvent.FriendId, null, null));
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Remembers the last announced presence of each friend, so that flicker can be recognised.
/// </summary>
public sealed class PresenceTracker
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (bool IsOnline, DateTimeOffset At)> _announced = new();

    public bool ShouldAnnounce(string friendId, bool isOnline, DateTimeOffset now)
    {
        if (_announced.TryGetValue(friendId, out var last) && (last.IsOnline == isOnline || now - last.At < QuietPeriod))
        {
            return false;
        }

        _announced[friendId] = (isOnline, now);
        return true;
    }
}
