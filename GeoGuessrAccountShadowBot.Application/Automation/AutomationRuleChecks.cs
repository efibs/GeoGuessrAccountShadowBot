// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Sources;

namespace GeoGuessrAccountShadowBot.Application.Automation;

/// <summary>
/// Checks a parsed rule against the rest of the configuration, so a rule that can never work is
/// refused when it's written rather than failing when it first runs.
/// </summary>
public static class AutomationRuleChecks
{
    public static IEnumerable<string> Check(AutomationRule rule, ShadowOptions options)
    {
        if (rule.Trigger.Kind == AutomationTriggerKind.Event && !options.IsEventEnabled(rule.Trigger.Name))
        {
            yield return $"On: the event {rule.Trigger.Name} is switched off in Events, so it would never run.";
        }

        foreach (var problem in CheckSources(rule, options))
        {
            yield return problem;
        }

        var headless = options.IsHeadless;
        if (headless && rule.Trigger.Kind == AutomationTriggerKind.Manual)
        {
            yield return "On: manual rules only start from /automation run, which needs Discord, and Discord:Enabled is false.";
        }

        var socket = options.GeoGuessr.Socket.Enabled;
        var club = options.GeoGuessr.Club.Enabled;

        foreach (var step in rule.Program)
        {
            switch (step)
            {
                case PostDiscordStep or ForumNoteStep or ApprovalStep when headless:
                    yield return $"{step.Path}: this step needs Discord, and Discord:Enabled is false.";
                    break;
                case SendDmStep or SendClubMessageStep when !socket:
                    yield return $"{step.Path}: messages are sent over the socket, which needs GeoGuessr:Socket:Enabled.";
                    break;
                case SendClubMessageStep when !club:
                case PlayerActionStep { Kind: PlayerActionKind.AcceptClubApplication or PlayerActionKind.DeclineClubApplication } when !club:
                    yield return $"{step.Path}: club steps need GeoGuessr:Club:Enabled.";
                    break;
                case ForumNoteStep when options.Forum.ChannelId is null:
                    yield return $"{step.Path}: forum notes need Forum:ChannelId.";
                    break;
                case ApprovalStep when options.Control.AuthorizedRoleIds.Count == 0 && options.Control.Automation.ManagerRoleIds.Count == 0:
                    yield return $"{step.Path}: nobody could approve, because Control:AuthorizedRoleIds and Control:Automation:ManagerRoleIds are empty.";
                    break;
            }
        }
    }

    private static IEnumerable<string> CheckSources(AutomationRule rule, ShadowOptions options)
    {
        if (rule.Trigger is { Kind: AutomationTriggerKind.Source, Source: { } triggerSource } && !options.Sources.ContainsKey(triggerSource))
        {
            yield return $"On: there is no source named {triggerSource} in Sources.";
        }

        foreach (var read in rule.SourceReads.Where(read => read.Source is not null))
        {
            if (!options.Sources.ContainsKey(read.Source!))
            {
                yield return $"A script reads sources.{read.Source}, and there is no source named {read.Source} in Sources.";
            }
            else if (read.Section is { } section && !ExternalSourceNames.ShadowBotSections.Contains(section))
            {
                yield return $"A script reads sources.{read.Source}.{section}, and a shadow bot source only has {string.Join(" and ", ExternalSourceNames.ShadowBotSections)}.";
            }
        }

        foreach (var raise in rule.Program.OfType<RaiseStep>().Where(raise => raise.ForEachSource is not null))
        {
            if (!options.Sources.ContainsKey(raise.ForEachSource!))
            {
                yield return $"{raise.Path}.ForEach: there is no source named {raise.ForEachSource} in Sources.";
            }
        }
    }
}
