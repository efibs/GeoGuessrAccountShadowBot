// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Options;

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

        var socket = options.GeoGuessr.Socket.Enabled;
        var club = options.GeoGuessr.Club.Enabled;

        foreach (var step in rule.Program)
        {
            switch (step)
            {
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
}
