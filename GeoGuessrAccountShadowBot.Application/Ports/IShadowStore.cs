// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Notifications;

namespace GeoGuessrAccountShadowBot.Application.Ports;

/// <summary>A queued Discord delivery.</summary>
public sealed record OutboxEntry(long Id, OutboxMessage Message);

/// <summary>The forum post of one chat partner.</summary>
public sealed record PartnerThread(string PartnerUserId, ulong ThreadId, string TitleNick, DateTimeOffset TitleCheckedAt);

/// <summary>The Discord card of one incoming friend request.</summary>
public sealed record FriendRequestCardLocation(string UserId, ulong ChannelId, ulong MessageId);

/// <summary>A reply that was sent and is waiting to come back on the socket.</summary>
public sealed record PendingReply(long Id, string PeerId, string TextHash, ulong ThreadId, ulong MessageId, DateTimeOffset CreatedAt);

/// <summary>A cached GeoGuessr name.</summary>
public sealed record PlayerName(string UserId, string Nick, DateTimeOffset FetchedAt);

/// <summary>One recorded attempt to act as the account.</summary>
public sealed record ControlAudit(DateTimeOffset At, ulong DiscordUserId, string Action, string Target, string Outcome);

/// <summary>
/// The bot's durable state. One instance is one unit of work: nothing is saved until
/// <see cref="CommitAsync"/>, which is what lets an event, its state change and its notifications
/// land together or not at all.
/// </summary>
public interface IShadowStore
{
    /// <summary>Records the key, and returns false if it was already there.</summary>
    Task<bool> TryMarkProcessedAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Returns which of the keys were already processed.</summary>
    Task<IReadOnlySet<string>> FindProcessedAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default);

    /// <summary>Reads a named JSON value, such as a friendship snapshot.</summary>
    Task<T?> ReadStateAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;

    Task WriteStateAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class;

    Task DeleteStateAsync(string key, CancellationToken cancellationToken = default);

    void Enqueue(OutboxMessage message);

    Task<IReadOnlyList<OutboxEntry>> ReadOutboxAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Marks an entry delivered and drops its payload, so chat text isn't kept at rest.</summary>
    Task MarkDeliveredAsync(long id, CancellationToken cancellationToken = default);

    Task MarkFailedAsync(long id, string reason, CancellationToken cancellationToken = default);

    Task<PartnerThread?> FindPartnerThreadAsync(string partnerUserId, CancellationToken cancellationToken = default);

    Task<PartnerThread?> FindPartnerThreadByThreadIdAsync(ulong threadId, CancellationToken cancellationToken = default);

    Task SavePartnerThreadAsync(PartnerThread thread, CancellationToken cancellationToken = default);

    Task RemovePartnerThreadAsync(ulong threadId, CancellationToken cancellationToken = default);

    Task<FriendRequestCardLocation?> FindFriendRequestCardAsync(string userId, CancellationToken cancellationToken = default);

    Task SaveFriendRequestCardAsync(FriendRequestCardLocation card, CancellationToken cancellationToken = default);

    Task RemoveFriendRequestCardAsync(string userId, CancellationToken cancellationToken = default);

    void AddPendingReply(string peerId, string textHash, ulong threadId, ulong messageId, DateTimeOffset createdAt);

    Task<PendingReply?> FindPendingReplyAsync(string peerId, string textHash, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PendingReply>> ReadPendingRepliesOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);

    Task RemovePendingReplyAsync(long id, CancellationToken cancellationToken = default);

    Task<PlayerName?> FindPlayerNameAsync(string userId, CancellationToken cancellationToken = default);

    Task SavePlayerNameAsync(PlayerName name, CancellationToken cancellationToken = default);

    void AddAudit(ControlAudit audit);

    /// <summary>Removes old dedupe keys and delivered entries.</summary>
    Task PruneAsync(DateTimeOffset processedBefore, DateTimeOffset deliveredBefore, CancellationToken cancellationToken = default);

    Task CommitAsync(CancellationToken cancellationToken = default);
}
