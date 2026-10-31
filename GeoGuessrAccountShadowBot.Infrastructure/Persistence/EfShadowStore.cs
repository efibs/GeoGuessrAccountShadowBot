// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace GeoGuessrAccountShadowBot.Infrastructure.Persistence;

/// <summary>
/// Keeps everything in one change tracker, so <see cref="CommitAsync"/> writes one transaction.
/// </summary>
internal sealed class EfShadowStore(ShadowDbContext database, TimeProvider timeProvider) : IShadowStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<bool> TryMarkProcessedAsync(string key, CancellationToken cancellationToken = default)
    {
        if (await database.ProcessedEvents.FindAsync([key], cancellationToken).ConfigureAwait(false) is not null)
        {
            return false;
        }

        database.ProcessedEvents.Add(new ProcessedEventRow { Key = key, ProcessedAt = timeProvider.GetUtcNow() });
        return true;
    }

    public async Task<IReadOnlySet<string>> FindProcessedAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        var known = await database.ProcessedEvents
            .AsNoTracking()
            .Where(row => keys.Contains(row.Key))
            .Select(row => row.Key)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return known.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<T?> ReadStateAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        var row = await database.States.FindAsync([key], cancellationToken).ConfigureAwait(false);
        return row is null ? null : JsonSerializer.Deserialize<T>(row.Json, SerializerOptions);
    }

    public async Task WriteStateAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class
    {
        var json = JsonSerializer.Serialize(value, SerializerOptions);
        var row = await database.States.FindAsync([key], cancellationToken).ConfigureAwait(false);

        if (row is null)
        {
            database.States.Add(new StateRow { Key = key, Json = json });
        }
        else
        {
            row.Json = json;
        }
    }

    public async Task DeleteStateAsync(string key, CancellationToken cancellationToken = default)
    {
        if (await database.States.FindAsync([key], cancellationToken).ConfigureAwait(false) is { } row)
        {
            database.States.Remove(row);
        }
    }

    public void Enqueue(OutboxMessage message) => database.Outbox.Add(new OutboxRow
    {
        CreatedAt = timeProvider.GetUtcNow(),
        Payload = JsonSerializer.Serialize(message, SerializerOptions)
    });

    public async Task<IReadOnlyList<OutboxEntry>> ReadOutboxAsync(int limit, CancellationToken cancellationToken = default)
    {
        var rows = await database.Outbox
            .AsNoTracking()
            .Where(row => row.DeliveredAt == null && row.FailedAt == null)
            .OrderBy(row => row.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(row => new OutboxEntry(row.Id, JsonSerializer.Deserialize<OutboxMessage>(row.Payload!, SerializerOptions)!))
            .ToArray();
    }

    public async Task MarkDeliveredAsync(long id, CancellationToken cancellationToken = default)
    {
        var row = await RequireOutboxRowAsync(id, cancellationToken).ConfigureAwait(false);
        row.DeliveredAt = timeProvider.GetUtcNow();
        row.Payload = null;
    }

    public async Task MarkFailedAsync(long id, string reason, CancellationToken cancellationToken = default)
    {
        var row = await RequireOutboxRowAsync(id, cancellationToken).ConfigureAwait(false);
        row.FailedAt = timeProvider.GetUtcNow();
        row.FailureReason = reason;
        row.Payload = null;
    }

    public async Task<PartnerThread?> FindPartnerThreadAsync(string partnerUserId, CancellationToken cancellationToken = default)
    {
        var row = await database.PartnerThreads.FindAsync([partnerUserId], cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToThread(row);
    }

    public async Task<PartnerThread?> FindPartnerThreadByThreadIdAsync(ulong threadId, CancellationToken cancellationToken = default)
    {
        var row = await database.PartnerThreads
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.ThreadId == threadId, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToThread(row);
    }

    public async Task SavePartnerThreadAsync(PartnerThread thread, CancellationToken cancellationToken = default)
    {
        var row = await database.PartnerThreads.FindAsync([thread.PartnerUserId], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            database.PartnerThreads.Add(new PartnerThreadRow
            {
                PartnerUserId = thread.PartnerUserId,
                ThreadId = thread.ThreadId,
                TitleNick = thread.TitleNick,
                TitleCheckedAt = thread.TitleCheckedAt
            });
            return;
        }

        row.ThreadId = thread.ThreadId;
        row.TitleNick = thread.TitleNick;
        row.TitleCheckedAt = thread.TitleCheckedAt;
    }

    public async Task RemovePartnerThreadAsync(ulong threadId, CancellationToken cancellationToken = default)
    {
        var row = await database.PartnerThreads
            .FirstOrDefaultAsync(item => item.ThreadId == threadId, cancellationToken)
            .ConfigureAwait(false);
        if (row is not null)
        {
            database.PartnerThreads.Remove(row);
        }
    }

    public async Task<FriendRequestCardLocation?> FindFriendRequestCardAsync(string userId, CancellationToken cancellationToken = default)
    {
        var row = await database.FriendRequestCards.FindAsync([userId], cancellationToken).ConfigureAwait(false);
        return row is null ? null : new FriendRequestCardLocation(row.UserId, row.ChannelId, row.MessageId);
    }

    public async Task SaveFriendRequestCardAsync(FriendRequestCardLocation card, CancellationToken cancellationToken = default)
    {
        var row = await database.FriendRequestCards.FindAsync([card.UserId], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            database.FriendRequestCards.Add(new FriendRequestCardRow { UserId = card.UserId, ChannelId = card.ChannelId, MessageId = card.MessageId });
            return;
        }

        row.ChannelId = card.ChannelId;
        row.MessageId = card.MessageId;
    }

    public async Task RemoveFriendRequestCardAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (await database.FriendRequestCards.FindAsync([userId], cancellationToken).ConfigureAwait(false) is { } row)
        {
            database.FriendRequestCards.Remove(row);
        }
    }

    public void AddPendingReply(string peerId, string textHash, ulong threadId, ulong messageId, DateTimeOffset createdAt) =>
        database.PendingReplies.Add(new PendingReplyRow
        {
            PeerId = peerId,
            TextHash = textHash,
            ThreadId = threadId,
            MessageId = messageId,
            CreatedAt = createdAt
        });

    public async Task<PendingReply?> FindPendingReplyAsync(string peerId, string textHash, CancellationToken cancellationToken = default)
    {
        var row = await database.PendingReplies
            .AsNoTracking()
            .Where(item => item.PeerId == peerId && item.TextHash == textHash)
            .OrderBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToPendingReply(row);
    }

    public async Task<IReadOnlyList<PendingReply>> ReadPendingRepliesOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var rows = await database.PendingReplies
            .AsNoTracking()
            .Where(item => item.CreatedAt <= cutoff)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ToPendingReply).ToArray();
    }

    public async Task RemovePendingReplyAsync(long id, CancellationToken cancellationToken = default)
    {
        if (await database.PendingReplies.FindAsync([id], cancellationToken).ConfigureAwait(false) is { } row)
        {
            database.PendingReplies.Remove(row);
        }
    }

    public async Task<PlayerName?> FindPlayerNameAsync(string userId, CancellationToken cancellationToken = default)
    {
        var row = await database.PlayerNames.FindAsync([userId], cancellationToken).ConfigureAwait(false);
        return row is null ? null : new PlayerName(row.UserId, row.Nick, row.FetchedAt);
    }

    public async Task SavePlayerNameAsync(PlayerName name, CancellationToken cancellationToken = default)
    {
        var row = await database.PlayerNames.FindAsync([name.UserId], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            database.PlayerNames.Add(new PlayerNameRow { UserId = name.UserId, Nick = name.Nick, FetchedAt = name.FetchedAt });
            return;
        }

        row.Nick = name.Nick;
        row.FetchedAt = name.FetchedAt;
    }

    public void AddAudit(ControlAudit audit) => database.ControlAudits.Add(new ControlAuditRow
    {
        At = audit.At,
        DiscordUserId = audit.DiscordUserId,
        Action = audit.Action,
        Target = audit.Target,
        Outcome = audit.Outcome
    });

    public async Task PruneAsync(DateTimeOffset processedBefore, DateTimeOffset deliveredBefore, CancellationToken cancellationToken = default)
    {
        await database.ProcessedEvents
            .Where(row => row.ProcessedAt < processedBefore)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        await database.Outbox
            .Where(row => row.DeliveredAt < deliveredBefore || row.FailedAt < deliveredBefore)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task CommitAsync(CancellationToken cancellationToken = default) => database.SaveChangesAsync(cancellationToken);

    private async Task<OutboxRow> RequireOutboxRowAsync(long id, CancellationToken cancellationToken) =>
        await database.Outbox.FindAsync([id], cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Outbox entry {id} doesn't exist.");

    private static PartnerThread ToThread(PartnerThreadRow row) =>
        new(row.PartnerUserId, row.ThreadId, row.TitleNick, row.TitleCheckedAt);

    private static PendingReply ToPendingReply(PendingReplyRow row) =>
        new(row.Id, row.PeerId, row.TextHash, row.ThreadId, row.MessageId, row.CreatedAt);
}
