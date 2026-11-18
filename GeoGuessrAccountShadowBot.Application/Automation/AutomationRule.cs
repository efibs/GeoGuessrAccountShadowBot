// SPDX-License-Identifier: GPL-2.0-or-later

using Cronos;
using GeoGuessrAccountShadowBot.Application.Notifications;

namespace GeoGuessrAccountShadowBot.Application.Automation;

/// <summary>Where a rule was defined.</summary>
public enum AutomationRuleOrigin
{
    /// <summary>In the configuration. Discord can switch it off, but not change it.</summary>
    Configuration,

    /// <summary>Created from Discord, and stored in the database.</summary>
    Discord
}

/// <summary>What starts a rule.</summary>
public enum AutomationTriggerKind
{
    /// <summary>An event on the account, named by its key in <c>Events</c>.</summary>
    Event,

    /// <summary>A cron schedule.</summary>
    Schedule,

    /// <summary>Only the run command.</summary>
    Manual,

    /// <summary>An event raised by another rule.</summary>
    Custom
}

/// <summary>When a rule reports its runs to Discord.</summary>
public enum AutomationReportMode
{
    None,
    Failures,
    All
}

/// <summary>What starts a rule.</summary>
/// <param name="Kind">The kind of trigger.</param>
/// <param name="Name">The event key, or the name of a custom event.</param>
/// <param name="Cron">The schedule, for <see cref="AutomationTriggerKind.Schedule"/>.</param>
/// <param name="TimeZone">The time zone the schedule is read in.</param>
public sealed record AutomationTriggerSpec(AutomationTriggerKind Kind, string Name, CronExpression? Cron, TimeZoneInfo TimeZone)
{
    public const string SchedulePrefix = "Schedule";
    public const string ManualName = "Manual";
    public const string CustomPrefix = "Custom:";

    /// <summary>The key runs are matched by, such as <c>DmMessage</c> or <c>Custom:vip</c>.</summary>
    public string Key => Kind switch
    {
        AutomationTriggerKind.Event => Name,
        AutomationTriggerKind.Schedule => SchedulePrefix,
        AutomationTriggerKind.Manual => ManualName,
        _ => CustomPrefix + Name
    };
}

/// <summary>
/// How often a rule may run. Every limit is checked when a run starts, against runs that started.
/// </summary>
/// <param name="OncePerPlayer">Run at most once per player, ever, or within <paramref name="OncePerPlayerWithin"/>.</param>
/// <param name="OncePerPlayerWithin">The window for <paramref name="OncePerPlayer"/>, or null for ever.</param>
/// <param name="PlayerCooldown">The least time between two runs for the same player.</param>
/// <param name="Cooldown">The least time between two runs of the rule.</param>
/// <param name="MaxRunsPerHour">The most runs that may start in an hour.</param>
public sealed record AutomationLimits(
    bool OncePerPlayer,
    TimeSpan? OncePerPlayerWithin,
    TimeSpan? PlayerCooldown,
    TimeSpan? Cooldown,
    int? MaxRunsPerHour)
{
    public static readonly AutomationLimits None = new(false, null, null, null, null);
}

/// <summary>
/// A rule, compiled from its JSON into a flat program. Branches become jumps, so a run that waits
/// or needs approval resumes from a single number.
/// </summary>
public sealed record AutomationRule(
    string Name,
    string? Description,
    bool Enabled,
    bool DryRun,
    AutomationTriggerSpec Trigger,
    AutomationScript? When,
    TimeSpan MaxAge,
    AutomationLimits Limits,
    AutomationReportMode Report,
    ulong? ReportChannelId,
    int Priority,
    bool StopOtherRules,
    IReadOnlyList<AutomationStep> Program,
    string Json,
    AutomationRuleOrigin Origin)
{
    /// <summary>Whether any script reads the player, which costs a profile lookup.</summary>
    public bool ReadsPlayer { get; } = (When?.ReadsPlayer ?? false) || Program.Any(step => step.Scripts.Any(script => script.ReadsPlayer));

    public bool NeedsApproval => Program.OfType<ApprovalStep>().Any();
}

/// <summary>One instruction of a compiled rule.</summary>
/// <param name="Path">Where it came from, such as <c>Do[1].Then[0]</c>, for messages.</param>
public abstract record AutomationStep(string Path)
{
    /// <summary>The scripts the step evaluates.</summary>
    public virtual IEnumerable<AutomationScript> Scripts => [];

    /// <summary>Whether the step acts as the GeoGuessr account.</summary>
    public virtual bool ActsAsAccount => false;
}

/// <summary>Continues at <paramref name="Target"/> unless the condition holds.</summary>
public sealed record JumpUnlessStep(string Path, AutomationScript Condition, int Target) : AutomationStep(Path)
{
    public override IEnumerable<AutomationScript> Scripts => [Condition];
}

/// <summary>Continues at <paramref name="Target"/>.</summary>
public sealed record JumpStep(string Path, int Target) : AutomationStep(Path);

/// <summary>Pauses the run.</summary>
public sealed record WaitStep(string Path, TimeSpan Duration) : AutomationStep(Path);

/// <summary>Pauses the run until someone approves it in Discord, or ends it if nobody does.</summary>
public sealed record ApprovalStep(string Path, AutomationScript? Prompt, TimeSpan Timeout, ulong? ChannelId) : AutomationStep(Path)
{
    public override IEnumerable<AutomationScript> Scripts => Prompt is null ? [] : [Prompt];
}

/// <summary>Ends the run.</summary>
public sealed record StopStep(string Path, string? Reason) : AutomationStep(Path);

/// <summary>The groups of players a raised event can be sent for, one run each.</summary>
public enum AutomationAudience
{
    Friends,
    FriendRequests,
    ClubMembers,
    ClubApplications
}

/// <summary>Starts the rules listening to a custom event.</summary>
public sealed record RaiseStep(
    string Path,
    string EventName,
    IReadOnlyDictionary<string, AutomationScript> Data,
    AutomationAudience? ForEach,
    AutomationScript? Player) : AutomationStep(Path)
{
    public override IEnumerable<AutomationScript> Scripts => Player is null ? Data.Values : [.. Data.Values, Player];
}

/// <summary>Sends a direct message as the account.</summary>
public sealed record SendDmStep(string Path, AutomationScript Text, AutomationScript? To) : AutomationStep(Path)
{
    public override IEnumerable<AutomationScript> Scripts => To is null ? [Text] : [Text, To];

    public override bool ActsAsAccount => true;
}

/// <summary>Posts in the club chat as the account.</summary>
public sealed record SendClubMessageStep(string Path, AutomationScript Text) : AutomationStep(Path)
{
    public override IEnumerable<AutomationScript> Scripts => [Text];

    public override bool ActsAsAccount => true;
}

/// <summary>The actions on one player.</summary>
public enum PlayerActionKind
{
    SendFriendRequest,
    AcceptFriendRequest,
    DeclineFriendRequest,
    AcceptClubApplication,
    DeclineClubApplication
}

/// <summary>Acts on a player as the account.</summary>
public sealed record PlayerActionStep(string Path, PlayerActionKind Kind, AutomationScript? Player) : AutomationStep(Path)
{
    public override IEnumerable<AutomationScript> Scripts => Player is null ? [] : [Player];

    public override bool ActsAsAccount => true;
}

/// <summary>Posts a card in Discord.</summary>
public sealed record PostDiscordStep(
    string Path,
    AutomationScript Text,
    AutomationScript? Title,
    ulong? ChannelId,
    IReadOnlyList<ulong> PingRoleIds,
    CardStyle Style) : AutomationStep(Path)
{
    public override IEnumerable<AutomationScript> Scripts => Title is null ? [Text] : [Text, Title];
}

/// <summary>Writes a note into a partner's forum post.</summary>
public sealed record ForumNoteStep(string Path, AutomationScript Text, AutomationScript? Player) : AutomationStep(Path)
{
    public override IEnumerable<AutomationScript> Scripts => Player is null ? [Text] : [Text, Player];
}
