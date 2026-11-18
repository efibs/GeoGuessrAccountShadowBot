// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Options;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Automation;

/// <summary>
/// Checks the automation settings and every configured rule at startup, alongside the rest of the
/// configuration, so that all problems are listed at once.
/// </summary>
public sealed class AutomationOptionsValidator(ConfiguredAutomationRules configured) : IValidateOptions<ShadowOptions>
{
    public ValidateOptionsResult Validate(string? name, ShadowOptions options)
    {
        var settings = options.Control.Automation;
        var errors = new List<string>(configured.Errors);

        if (settings.ManagerRoleIds.Contains(options.Discord.GuildId))
        {
            errors.Add("Control:Automation:ManagerRoleIds must not contain the guild id, which is the @everyone role.");
        }

        if (settings.MaxActionsPerMinute < 1 || settings.MaxActionsPerHour < 1 || settings.MaxDmsPerPlayerPerHour < 1)
        {
            errors.Add("Control:Automation:MaxActionsPerMinute, MaxActionsPerHour and MaxDmsPerPlayerPerHour must be at least 1.");
        }

        if (settings.MaxChainDepth is < 0 or > 10)
        {
            errors.Add("Control:Automation:MaxChainDepth must be between 0 and 10.");
        }

        if (settings.MaxFanOut is < 1 or > 5000)
        {
            errors.Add("Control:Automation:MaxFanOut must be between 1 and 5000.");
        }

        // Rules must always parse, but switching automations off is a valid way to stop them all,
        // so the checks against the rest of the configuration only apply while they're on.
        if (settings.Enabled)
        {
            foreach (var rule in configured.Rules)
            {
                errors.AddRange(AutomationRuleChecks.Check(rule, options).Select(error => $"Automation {rule.Name}: {error}"));
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
