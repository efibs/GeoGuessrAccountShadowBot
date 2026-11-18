// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Automation;

namespace GeoGuessrAccountShadowBot.Application.Ports;

/// <summary>A rule created from Discord.</summary>
public sealed record StoredAutomationRule(string Name, string Json, ulong UpdatedBy, DateTimeOffset UpdatedAt);

/// <summary>
/// One execution of a rule. The rule's JSON is copied in, so a run that waits finishes the rule as
/// it was when it started, even if the rule is edited meanwhile.
/// </summary>
public sealed record AutomationRun
{
    public long Id { get; init; }

    public required string RuleName { get; init; }

    public required string RuleJson { get; init; }

    public required string Trigger { get; init; }

    /// <summary>The event that started it, shared by every run it started.</summary>
    public required string GroupKey { get; init; }

    public string? SubjectUserId { get; init; }

    /// <summary>What scripts see as <c>event</c>. Cleared once the run ends.</summary>
    public required string EventJson { get; init; }

    public int Depth { get; init; }

    public bool Manual { get; init; }

    public bool DryRun { get; init; }

    /// <summary>The Discord user who started it by hand, if anyone did.</summary>
    public ulong? RequestedBy { get; init; }

    public AutomationRunState State { get; init; }

    /// <summary>The next step of the compiled rule.</summary>
    public int ProgramCounter { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>When the worker should look at the run next.</summary>
    public required DateTimeOffset ResumeAt { get; init; }

    /// <summary>When its conditions and limits passed. Limits count only runs with this set.</summary>
    public DateTimeOffset? AdmittedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>How often the run was put off, for example while GeoGuessr was unreachable.</summary>
    public int Attempts { get; init; }

    public string? Outcome { get; init; }

    /// <summary>What the run did so far, as lines with marked values. Cleared once reported.</summary>
    public IReadOnlyList<string> Log { get; init; } = [];

    public ulong? ApprovalChannelId { get; init; }

    public ulong? ApprovalMessageId { get; init; }

    public bool IsFinished => State is AutomationRunState.Completed or AutomationRunState.Skipped or AutomationRunState.Failed
        or AutomationRunState.Cancelled or AutomationRunState.Rejected or AutomationRunState.Expired;
}

/// <summary>A run that was added, whose id is assigned on commit.</summary>
public interface IAutomationRunHandle
{
    long Id { get; }
}

/// <summary>
/// The automation part of the store. It shares the unit of work of <see cref="IShadowStore"/>, so a
/// run is created in the same transaction as the event that started it, and nothing is saved until
/// <see cref="IShadowStore.CommitAsync"/>.
/// </summary>
public interface IAutomationStore
{
    Task<IReadOnlyList<StoredAutomationRule>> ReadRulesAsync(CancellationToken cancellationToken = default);

    Task<StoredAutomationRule?> FindRuleAsync(string name, CancellationToken cancellationToken = default);

    Task SaveRuleAsync(StoredAutomationRule rule, CancellationToken cancellationToken = default);

    Task DeleteRuleAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Queues a new run. Its id is known once the unit of work is committed.</summary>
    IAutomationRunHandle AddRun(AutomationRun run);

    Task<AutomationRun?> FindRunAsync(long id, CancellationToken cancellationToken = default);

    Task UpdateRunAsync(AutomationRun run, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records where a run's approval card was posted. Only these two fields change, because the
    /// delivery runs apart from the worker, which may be changing the run at the same moment.
    /// </summary>
    Task SetApprovalMessageAsync(long runId, ulong channelId, ulong messageId, CancellationToken cancellationToken = default);

    /// <summary>Unfinished runs whose time has come, oldest first.</summary>
    Task<IReadOnlyList<long>> ReadDueRunIdsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default);

    /// <summary>Unfinished runs, of one rule or of all.</summary>
    Task<IReadOnlyList<AutomationRun>> ReadOpenRunsAsync(string? ruleName, CancellationToken cancellationToken = default);

    /// <summary>Unfinished runs started by the same event, other than the given one.</summary>
    Task<IReadOnlyList<AutomationRun>> ReadOpenRunsInGroupAsync(string groupKey, long exceptId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AutomationRun>> ReadRecentRunsAsync(string? ruleName, int limit, CancellationToken cancellationToken = default);

    /// <summary>Counts admitted runs of a rule since a moment, for one player or for all.</summary>
    Task<int> CountAdmittedRunsAsync(string ruleName, string? subjectUserId, DateTimeOffset since, CancellationToken cancellationToken = default);

    /// <summary>
    /// When a rule last ran for a player. Unlike runs, this is never pruned, so a rule that runs
    /// once per player keeps its promise.
    /// </summary>
    Task<DateTimeOffset?> FindLastRunForPlayerAsync(string ruleName, string subjectUserId, CancellationToken cancellationToken = default);

    Task RecordRunForPlayerAsync(string ruleName, string subjectUserId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Removes finished runs that ended before a moment.</summary>
    Task PruneRunsAsync(DateTimeOffset finishedBefore, CancellationToken cancellationToken = default);
}
