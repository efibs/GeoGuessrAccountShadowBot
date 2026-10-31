// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Connection;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Pipeline;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Reconciliation;

/// <summary>What a reconciliation run covers.</summary>
[Flags]
public enum ReconcileScope
{
    None = 0,
    Friends = 1,
    Notifications = 2,
    Chat = 4,
    Club = 8,
    All = Friends | Notifications | Chat | Club
}

/// <summary>
/// The scopes that start with a silent baseline.
/// </summary>
public static class BaselineScopes
{
    public const string Notifications = "notifications";
    public const string Chat = "chat";
    public const string ClubChat = "club-chat";

    public static string StateKey(string scope) => $"baseline:{scope}";
}

/// <summary>
/// Compares GeoGuessr's HTTP state with what was already processed, and feeds anything new into the
/// same pipeline the socket uses. That makes one code path responsible for every event, whether it
/// arrived live, after downtime, or while the socket was yielded.
/// </summary>
public sealed partial class ReconciliationCoordinator(
    IGeoGuessrApi api,
    IEventPublisher events,
    IServiceScopeFactory scopes,
    AccountContext account,
    ConnectionStatus connection,
    IOptions<ShadowOptions> options,
    TimeProvider timeProvider,
    ILogger<ReconciliationCoordinator> logger)
{
    /// <summary>Anything this old that should have come over the socket means the socket went deaf.</summary>
    private static readonly TimeSpan MissedEventAge = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _singleFlight = new(1, 1);

    /// <summary>
    /// Runs one reconciliation at a time. Each scope fails on its own, so a bad endpoint can't stop
    /// the others.
    /// </summary>
    /// <param name="scope">What to reconcile.</param>
    /// <param name="detectMissed">Treat old unseen messages as proof the socket lost its subscription.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    public async Task RunAsync(ReconcileScope scope, bool detectMissed, CancellationToken cancellationToken)
    {
        await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (scope.HasFlag(ReconcileScope.Friends))
            {
                await RunPassAsync("friends", ReconcileFriendsAsync, cancellationToken).ConfigureAwait(false);
            }

            if (scope.HasFlag(ReconcileScope.Notifications))
            {
                await RunPassAsync("notifications", ReconcileNotificationsAsync, cancellationToken).ConfigureAwait(false);
            }

            if (scope.HasFlag(ReconcileScope.Chat) && options.Value.IsEventEnabled(EventKeys.DmMessage))
            {
                await RunPassAsync("chat", ct => ReconcileChatAsync(detectMissed, ct), cancellationToken).ConfigureAwait(false);
            }

            if (scope.HasFlag(ReconcileScope.Club) && account.ClubId is { } clubId)
            {
                await RunPassAsync("club", ct => ReconcileClubAsync(clubId, ct), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    private async Task RunPassAsync(string name, Func<CancellationToken, Task> pass, CancellationToken cancellationToken)
    {
        try
        {
            await pass(cancellationToken).ConfigureAwait(false);
        }
        catch (GeoGuessrUnauthorizedException)
        {
            connection.ReportUnauthorized();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogPassFailed(exception, name);
        }
    }

    private async Task ReconcileFriendsAsync(CancellationToken cancellationToken)
    {
        var friendships = await api.GetFriendshipsAsync(cancellationToken).ConfigureAwait(false);
        await events.PublishAsync(new FriendshipsFetched(friendships), cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileNotificationsAsync(CancellationToken cancellationToken)
    {
        var silent = !await IsBaselineDoneAsync(BaselineScopes.Notifications, cancellationToken).ConfigureAwait(false);
        var pending = await ReadUnseenNotificationsAsync(cancellationToken).ConfigureAwait(false);

        // The list is newest first, and people should read things in the order they happened.
        foreach (var notification in pending.OrderBy(item => item.SentAt))
        {
            var route = NotificationClassifier.Classify(notification.Type);
            var shadowEvent = new AccountNotificationReceived(notification, route.SwitchKey)
            {
                Silent = silent || IsTooOld(notification.SentAt)
            };
            await events.PublishAsync(shadowEvent, cancellationToken).ConfigureAwait(false);
        }

        if (pending.Any(item => NotificationClassifier.RingsFriendshipDoorbell(item.Type)))
        {
            await RunPassAsync("friends", ReconcileFriendsAsync, cancellationToken).ConfigureAwait(false);
        }

        if (silent)
        {
            await events.PublishAsync(new BaselineCompleted(BaselineScopes.Notifications, pending.Count), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Walks pages until one is entirely known. Stopping at the first known id would be wrong,
    /// because entries can be deleted or reordered.
    /// </summary>
    private async Task<List<AccountNotification>> ReadUnseenNotificationsAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value.Polling;
        var unseen = new List<AccountNotification>();

        for (var page = 0; page < settings.MaxNotificationPages; page++)
        {
            var notifications = await api.GetNotificationsAsync(page, settings.NotificationPageSize, cancellationToken).ConfigureAwait(false);
            if (notifications.Count == 0)
            {
                return unseen;
            }

            var keys = notifications.ToDictionary(item => new AccountNotificationReceived(item, null).DedupeKey);
            var known = await FindProcessedAsync(keys.Keys, cancellationToken).ConfigureAwait(false);
            if (known.Count == keys.Count)
            {
                return unseen;
            }

            unseen.AddRange(keys.Where(entry => !known.Contains(entry.Key)).Select(entry => entry.Value));
        }

        LogNotificationPagesExhausted(settings.MaxNotificationPages);
        return unseen;
    }

    private async Task ReconcileChatAsync(bool detectMissed, CancellationToken cancellationToken)
    {
        var silent = !await IsBaselineDoneAsync(BaselineScopes.Chat, cancellationToken).ConfigureAwait(false);
        var rooms = await api.GetChatRoomsAsync(cancellationToken).ConfigureAwait(false);
        var recorded = 0;
        var missed = false;

        foreach (var room in rooms)
        {
            var messages = room.Messages.Count >= room.MessageCount
                ? room.Messages
                : await api.GetChatHistoryAsync(room.PeerId, cancellationToken).ConfigureAwait(false);

            var byKey = messages.ToDictionary(message => new DirectMessageReceived(message).DedupeKey);
            var known = await FindProcessedAsync(byKey.Keys, cancellationToken).ConfigureAwait(false);

            // Rooms and histories come back newest first.
            foreach (var (key, message) in byKey.Where(entry => !known.Contains(entry.Key)).OrderBy(entry => entry.Value.SentAt))
            {
                missed |= timeProvider.GetUtcNow() - message.SentAt > MissedEventAge;
                await events.PublishAsync(
                    new DirectMessageReceived(message) { Silent = silent || IsTooOld(message.SentAt) },
                    cancellationToken).ConfigureAwait(false);
                recorded++;
            }
        }

        if (silent)
        {
            await events.PublishAsync(new BaselineCompleted(BaselineScopes.Chat, recorded), cancellationToken).ConfigureAwait(false);
        }
        else if (missed && detectMissed && connection.Current.Phase == ConnectionPhase.Connected)
        {
            LogSocketMissedMessages();
            connection.RequestReconnect();
        }
    }

    private async Task ReconcileClubAsync(string clubId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        string[] rosterEvents = [EventKeys.ClubMemberJoined, EventKeys.ClubMemberLeft, EventKeys.ClubRoleChanged];

        if (rosterEvents.Any(settings.IsEventEnabled))
        {
            var members = await api.GetClubMembersAsync(clubId, cancellationToken).ConfigureAwait(false);
            await events.PublishAsync(new ClubRosterFetched(ClubRoster.From(members)), cancellationToken).ConfigureAwait(false);
        }

        if (!settings.IsEventEnabled(EventKeys.ClubChat))
        {
            return;
        }

        var silent = !await IsBaselineDoneAsync(BaselineScopes.ClubChat, cancellationToken).ConfigureAwait(false);
        var messages = await api.GetClubChatAsync(clubId, cancellationToken).ConfigureAwait(false);
        var byKey = messages.ToDictionary(message => new ClubChatMessageReceived(clubId, message).DedupeKey);
        var known = await FindProcessedAsync(byKey.Keys, cancellationToken).ConfigureAwait(false);

        foreach (var (_, message) in byKey.Where(entry => !known.Contains(entry.Key)).OrderBy(entry => entry.Value.SentAt))
        {
            await events.PublishAsync(
                new ClubChatMessageReceived(clubId, message) { Silent = silent || IsTooOld(message.SentAt) },
                cancellationToken).ConfigureAwait(false);
        }

        if (silent)
        {
            await events.PublishAsync(new BaselineCompleted(BaselineScopes.ClubChat, messages.Count), cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsTooOld(DateTimeOffset at) => timeProvider.GetUtcNow() - at > options.Value.Polling.MaxCatchUpAge;

    private async Task<bool> IsBaselineDoneAsync(string scope, CancellationToken cancellationToken)
    {
        var serviceScope = scopes.CreateAsyncScope();
        await using (serviceScope.ConfigureAwait(false))
        {
            var store = serviceScope.ServiceProvider.GetRequiredService<IShadowStore>();
            var state = await store.ReadStateAsync<BaselineState>(BaselineScopes.StateKey(scope), cancellationToken).ConfigureAwait(false);
            return state is not null;
        }
    }

    private async Task<IReadOnlySet<string>> FindProcessedAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();
            return await store.FindProcessedAsync(keys, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Pass} reconciliation failed and will run again later")]
    private partial void LogPassFailed(Exception exception, string pass);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopped reading notifications after {Pages} pages, so older ones may be missing")]
    private partial void LogNotificationPagesExhausted(int pages);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Catch-up found messages the socket should have delivered, so it is being replaced")]
    private partial void LogSocketMissedMessages();
}

/// <summary>
/// Marks a baseline as done.
/// </summary>
public sealed record BaselineState(DateTimeOffset CompletedAt);
