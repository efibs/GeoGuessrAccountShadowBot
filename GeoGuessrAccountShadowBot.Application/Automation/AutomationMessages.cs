// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Notifications;

namespace GeoGuessrAccountShadowBot.Application.Automation;

// Text in these messages may hold values marked with AutomationMarkup, which the Discord layer
// escapes. Everything else in them was written by a rule author or by the bot.

/// <summary>A card posted by a rule.</summary>
/// <param name="RuleName">The rule that posted it.</param>
/// <param name="ChannelId">The channel, or null for the report channel.</param>
/// <param name="Title">The card title, or null for the rule name.</param>
/// <param name="Text">The card text, with marked values.</param>
/// <param name="PingRoleIds">The roles the rule pings. Nothing else may be pinged.</param>
/// <param name="Style">The look of the card.</param>
public sealed record AutomationPost(
    string RuleName,
    ulong? ChannelId,
    string? Title,
    string Text,
    IReadOnlyList<ulong> PingRoleIds,
    CardStyle Style) : OutboxMessage;

/// <summary>A note from a rule in a partner's forum post.</summary>
public sealed record AutomationForumNote(string RuleName, string PartnerUserId, string Text) : OutboxMessage;

/// <summary>How a run ended, for its report.</summary>
public enum AutomationRunState
{
    /// <summary>Ready to run when <c>ResumeAt</c> comes.</summary>
    Pending,

    /// <summary>In a Wait step.</summary>
    Waiting,

    /// <summary>Waiting for someone in Discord.</summary>
    AwaitingApproval,

    /// <summary>
    /// An action as the account was started. Finding a run in this state means the bot stopped
    /// during the action, which is never repeated, because it may have happened.
    /// </summary>
    Acting,

    Completed,
    Skipped,
    Failed,
    Cancelled,
    Rejected,
    Expired
}

/// <summary>The outcome of a run, posted when its rule asks for that.</summary>
/// <param name="RuleName">The rule.</param>
/// <param name="RunId">The run.</param>
/// <param name="State">How it ended.</param>
/// <param name="Outcome">Why, in a sentence written by the bot.</param>
/// <param name="SubjectUserId">The player the run was about, if any.</param>
/// <param name="Lines">What the run did, with marked values.</param>
/// <param name="DryRun">The actions were only described.</param>
/// <param name="ChannelId">The channel, or null for the report channel.</param>
public sealed record AutomationReport(
    string RuleName,
    long RunId,
    AutomationRunState State,
    string Outcome,
    string? SubjectUserId,
    IReadOnlyList<string> Lines,
    bool DryRun,
    ulong? ChannelId) : OutboxMessage;

/// <summary>A card with buttons that lets a paused run continue.</summary>
/// <param name="RuleName">The rule.</param>
/// <param name="RunId">The run, which the buttons carry.</param>
/// <param name="SubjectUserId">The player the run is about, if any.</param>
/// <param name="Prompt">The question the rule asks, with marked values.</param>
/// <param name="Planned">What happens on approval, with marked values.</param>
/// <param name="ExpiresAt">When the run ends without an answer.</param>
/// <param name="ChannelId">The channel, or null for the report channel.</param>
public sealed record AutomationApprovalRequest(
    string RuleName,
    long RunId,
    string? SubjectUserId,
    string? Prompt,
    IReadOnlyList<string> Planned,
    DateTimeOffset ExpiresAt,
    ulong? ChannelId) : OutboxMessage;

/// <summary>Closes an approval card.</summary>
/// <param name="RunId">The run.</param>
/// <param name="State">What became of it: pending again when approved, or rejected, expired or cancelled.</param>
/// <param name="DecidedBy">Who answered, if someone did.</param>
public sealed record AutomationApprovalOutcome(long RunId, AutomationRunState State, ulong? DecidedBy) : OutboxMessage;
