// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using GeoGuessrAccountShadowBot.Application.Automation;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GeoGuessrAccountShadowBot.Infrastructure.Persistence;

/// <summary>
/// Keeps everything in one change tracker, so <see cref="CommitAsync"/> writes one transaction.
/// </summary>
internal sealed class EfShadowStore(ShadowDbContext database, TimeProvider timeProvider, OutboxFilter outbox)
    : IShadowStore, IAutomationStore, IExternalEventStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The conversion the model applies to every <see cref="DateTimeOffset"/>, for raw SQL.</summary>
    private static readonly DateTimeOffsetToBinaryConverter FetchedAtConverter = new();

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

    public void Enqueue(OutboxMessage message)
    {
        if (!outbox.Accepts(message))
        {
            return;
        }

        database.Outbox.Add(new OutboxRow
        {
            CreatedAt = timeProvider.GetUtcNow(),
            Payload = JsonSerializer.Serialize(message, SerializerOptions)
        });
    }

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

    public void AddPendingReply(string peerId, string textHash, ulong threadId, ulong messageId, DateTimeOffset createdAt, string? automationRule = null) =>
        database.PendingReplies.Add(new PendingReplyRow
        {
            PeerId = peerId,
            TextHash = textHash,
            ThreadId = threadId,
            MessageId = messageId,
            CreatedAt = createdAt,
            AutomationRule = automationRule
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
        var row = await database.PlayerNames.AsNoTracking()
            .FirstOrDefaultAsync(name => name.UserId == userId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : new PlayerName(row.UserId, row.Nick, row.FetchedAt);
    }

    /// <summary>
    /// Upserts right away rather than at <see cref="CommitAsync"/>. The outbox dispatcher and the
    /// automation worker look up the same new player at the same moment, and a cache insert in the
    /// unit of work made the loser's commit fail after its card had already reached Discord.
    /// </summary>
    public async Task SavePlayerNameAsync(PlayerName name, CancellationToken cancellationToken = default)
    {
        var fetchedAt = (long)FetchedAtConverter.ConvertToProvider(name.FetchedAt)!;
        await database.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO "PlayerNames" ("UserId", "Nick", "FetchedAt") VALUES ({name.UserId}, {name.Nick}, {fetchedAt})
             ON CONFLICT ("UserId") DO UPDATE SET "Nick" = excluded."Nick", "FetchedAt" = excluded."FetchedAt"
             """,
            cancellationToken).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<StoredAutomationRule>> ReadRulesAsync(CancellationToken cancellationToken = default)
    {
        var rows = await database.AutomationRules.AsNoTracking().OrderBy(row => row.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToRule).ToArray();
    }

    public async Task<StoredAutomationRule?> FindRuleAsync(string name, CancellationToken cancellationToken = default)
    {
        var row = await database.AutomationRules.FindAsync([name], cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToRule(row);
    }

    public async Task SaveRuleAsync(StoredAutomationRule rule, CancellationToken cancellationToken = default)
    {
        var row = await database.AutomationRules.FindAsync([rule.Name], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            database.AutomationRules.Add(new AutomationRuleRow { Name = rule.Name, Json = rule.Json, UpdatedBy = rule.UpdatedBy, UpdatedAt = rule.UpdatedAt });
            return;
        }

        row.Json = rule.Json;
        row.UpdatedBy = rule.UpdatedBy;
        row.UpdatedAt = rule.UpdatedAt;
    }

    public async Task DeleteRuleAsync(string name, CancellationToken cancellationToken = default)
    {
        if (await database.AutomationRules.FindAsync([name], cancellationToken).ConfigureAwait(false) is { } row)
        {
            database.AutomationRules.Remove(row);
        }
    }

    public IAutomationRunHandle AddRun(AutomationRun run)
    {
        var row = new AutomationRunRow
        {
            RuleName = run.RuleName,
            RuleJson = run.RuleJson,
            Trigger = run.Trigger,
            GroupKey = run.GroupKey,
            EventJson = run.EventJson,
            CreatedAt = run.CreatedAt,
            OccurredAt = run.OccurredAt,
            ResumeAt = run.ResumeAt,
            LogJson = "[]"
        };
        Copy(run, row);
        database.AutomationRuns.Add(row);
        return new RunHandle(row);
    }

    public async Task<AutomationRun?> FindRunAsync(long id, CancellationToken cancellationToken = default)
    {
        var row = await database.AutomationRuns.FindAsync([id], cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToRun(row);
    }

    public async Task UpdateRunAsync(AutomationRun run, CancellationToken cancellationToken = default)
    {
        var row = await database.AutomationRuns.FindAsync([run.Id], cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"Automation run {run.Id} doesn't exist.");
        Copy(run, row);
    }

    public Task SetApprovalMessageAsync(long runId, ulong channelId, ulong messageId, CancellationToken cancellationToken = default) =>
        database.AutomationRuns
            .Where(row => row.Id == runId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.ApprovalChannelId, channelId)
                .SetProperty(row => row.ApprovalMessageId, messageId), cancellationToken);

    public async Task<IReadOnlyList<long>> ReadDueRunIdsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default) =>
        await database.AutomationRuns
            .AsNoTracking()
            .Where(row => row.FinishedAt == null && row.ResumeAt <= now)
            .OrderBy(row => row.Id)
            .Take(limit)
            .Select(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<AutomationRun>> ReadOpenRunsAsync(string? ruleName, CancellationToken cancellationToken = default)
    {
        var rows = await database.AutomationRuns
            .AsNoTracking()
            .Where(row => row.FinishedAt == null && (ruleName == null || row.RuleName == ruleName))
            .OrderBy(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ToRun).ToArray();
    }

    public async Task<IReadOnlyList<AutomationRun>> ReadOpenRunsInGroupAsync(string groupKey, long exceptId, CancellationToken cancellationToken = default)
    {
        var rows = await database.AutomationRuns
            .Where(row => row.GroupKey == groupKey && row.Id != exceptId && row.FinishedAt == null)
            .OrderBy(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ToRun).ToArray();
    }

    public async Task<IReadOnlyList<AutomationRun>> ReadRecentRunsAsync(string? ruleName, int limit, CancellationToken cancellationToken = default)
    {
        var rows = await database.AutomationRuns
            .AsNoTracking()
            .Where(row => ruleName == null || row.RuleName == ruleName)
            .OrderByDescending(row => row.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ToRun).ToArray();
    }

    public Task<int> CountAdmittedRunsAsync(string ruleName, string? subjectUserId, DateTimeOffset since, CancellationToken cancellationToken = default) =>
        database.AutomationRuns
            .AsNoTracking()
            .Where(row => row.RuleName == ruleName && !row.DryRun && row.AdmittedAt != null && row.AdmittedAt >= since)
            .Where(row => subjectUserId == null || row.SubjectUserId == subjectUserId)
            .CountAsync(cancellationToken);

    public async Task<DateTimeOffset?> FindLastRunForPlayerAsync(string ruleName, string subjectUserId, CancellationToken cancellationToken = default)
    {
        var row = await database.AutomationPlayerRuns.FindAsync([ruleName, subjectUserId], cancellationToken).ConfigureAwait(false);
        return row?.LastRunAt;
    }

    public async Task RecordRunForPlayerAsync(string ruleName, string subjectUserId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var row = await database.AutomationPlayerRuns.FindAsync([ruleName, subjectUserId], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            database.AutomationPlayerRuns.Add(new AutomationPlayerRunRow { RuleName = ruleName, SubjectUserId = subjectUserId, LastRunAt = at });
            return;
        }

        row.LastRunAt = at;
    }

    public Task PruneRunsAsync(DateTimeOffset finishedBefore, CancellationToken cancellationToken = default) =>
        database.AutomationRuns
            .Where(row => row.FinishedAt != null && row.FinishedAt < finishedBefore)
            .ExecuteDeleteAsync(cancellationToken);

    public void AddExternalEvent(string trigger, string? subjectUserId, DateTimeOffset occurredAt, DateTimeOffset recordedAt, string dataJson) =>
        database.ExternalEvents.Add(new ExternalEventRow
        {
            Trigger = trigger,
            SubjectUserId = subjectUserId,
            OccurredAt = occurredAt,
            RecordedAt = recordedAt,
            DataJson = dataJson
        });

    public async Task<IReadOnlyList<ExternalEvent>> ReadExternalEventsAsync(
        long after, IReadOnlyCollection<string> triggers, int limit, CancellationToken cancellationToken = default)
    {
        var rows = await database.ExternalEvents
            .AsNoTracking()
            .Where(row => row.Id > after && triggers.Contains(row.Trigger))
            .OrderBy(row => row.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(row => new ExternalEvent(row.Id, row.Trigger, row.SubjectUserId, row.OccurredAt, row.RecordedAt, row.DataJson)).ToArray();
    }

    public async Task<ExternalEventBounds> ReadExternalEventBoundsAsync(CancellationToken cancellationToken = default)
    {
        var bounds = await database.ExternalEvents
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(group => new { Oldest = group.Min(row => row.Id), Newest = group.Max(row => row.Id) })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return bounds is null ? new ExternalEventBounds(0, 0) : new ExternalEventBounds(bounds.Oldest, bounds.Newest);
    }

    public async Task PruneExternalEventsAsync(DateTimeOffset recordedBefore, CancellationToken cancellationToken = default)
    {
        var newest = await database.ExternalEvents.MaxAsync(row => (long?)row.Id, cancellationToken).ConfigureAwait(false) ?? 0;
        await database.ExternalEvents
            .Where(row => row.RecordedAt < recordedBefore && row.Id < newest)
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
        new(row.Id, row.PeerId, row.TextHash, row.ThreadId, row.MessageId, row.CreatedAt, row.AutomationRule);

    private static StoredAutomationRule ToRule(AutomationRuleRow row) => new(row.Name, row.Json, row.UpdatedBy, row.UpdatedAt);

    private static void Copy(AutomationRun run, AutomationRunRow row)
    {
        row.SubjectUserId = run.SubjectUserId;
        row.EventJson = run.EventJson;
        row.Depth = run.Depth;
        row.Manual = run.Manual;
        row.DryRun = run.DryRun;
        row.RequestedBy = run.RequestedBy;
        row.State = (int)run.State;
        row.ProgramCounter = run.ProgramCounter;
        row.ResumeAt = run.ResumeAt;
        row.AdmittedAt = run.AdmittedAt;
        row.FinishedAt = run.FinishedAt;
        row.Attempts = run.Attempts;
        row.Outcome = run.Outcome;
        row.LogJson = JsonSerializer.Serialize(run.Log, SerializerOptions);

        // The approval card's location belongs to SetApprovalMessageAsync alone.
    }

    private static AutomationRun ToRun(AutomationRunRow row) => new()
    {
        Id = row.Id,
        RuleName = row.RuleName,
        RuleJson = row.RuleJson,
        Trigger = row.Trigger,
        GroupKey = row.GroupKey,
        SubjectUserId = row.SubjectUserId,
        EventJson = row.EventJson,
        Depth = row.Depth,
        Manual = row.Manual,
        DryRun = row.DryRun,
        RequestedBy = row.RequestedBy,
        State = (AutomationRunState)row.State,
        ProgramCounter = row.ProgramCounter,
        CreatedAt = row.CreatedAt,
        OccurredAt = row.OccurredAt,
        ResumeAt = row.ResumeAt,
        AdmittedAt = row.AdmittedAt,
        FinishedAt = row.FinishedAt,
        Attempts = row.Attempts,
        Outcome = row.Outcome,
        Log = JsonSerializer.Deserialize<List<string>>(row.LogJson, SerializerOptions) ?? [],
        ApprovalChannelId = row.ApprovalChannelId,
        ApprovalMessageId = row.ApprovalMessageId
    };

    private sealed class RunHandle(AutomationRunRow row) : IAutomationRunHandle
    {
        public long Id => row.Id;
    }
}
