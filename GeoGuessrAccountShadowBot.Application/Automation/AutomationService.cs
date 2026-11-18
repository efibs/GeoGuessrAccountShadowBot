// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeoGuessrAccountShadowBot.Application.Control;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Automation;

/// <summary>
/// An answer for the person who ran a command. Lines may hold values marked with
/// <see cref="AutomationMarkup"/>.
/// </summary>
/// <param name="Ok">Whether the command did what was asked.</param>
/// <param name="Text">What to tell them.</param>
/// <param name="Attachment">A file to attach instead of long text, such as a rule's JSON.</param>
public sealed record AutomationReply(bool Ok, string Text, string? Attachment = null)
{
    public static AutomationReply Refused(Error error) => new(false, error.Message);
}

/// <summary>
/// Managing rules from Discord. Managers may change rules and start runs. Members of the authorized
/// roles may look at rules, test them and answer approvals. Every change is recorded in the audit
/// log like any other action as the account.
/// </summary>
public sealed class AutomationService(
    ControlCommandRunner runner,
    AutomationRuleRegistry registry,
    AutomationEngine engine,
    AutomationSignal signal,
    IServiceScopeFactory scopes,
    IOptions<ShadowOptions> options,
    TimeProvider timeProvider)
{
    private const int RecentRuns = 15;
    private const int MaxInlineLength = 1800;

    private static readonly Error NotAManager =
        new(ErrorKind.Forbidden, "automation.forbidden", "Only members of Control:Automation:ManagerRoleIds may change or run automations.");

    private static readonly Error NotAViewer =
        new(ErrorKind.Forbidden, "automation.forbidden", "You don't have a role that may see automations.");

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private IReadOnlyCollection<ulong> Managers => options.Value.Control.Automation.ManagerRoleIds;

    private IReadOnlyCollection<ulong> Viewers => [.. options.Value.Control.AuthorizedRoleIds, .. Managers];

    /// <summary>The names of every rule, for autocomplete.</summary>
    public IEnumerable<string> RuleNames => registry.All.Select(rule => rule.Name);

    public Task<AutomationReply> ListAsync(ulong user, CancellationToken cancellationToken) =>
        AsViewerAsync(user, () =>
        {
            if (registry.All.Count == 0)
            {
                return Task.FromResult(new AutomationReply(true, "There are no automations yet. Create one with `/automation create`."));
            }

            var text = new StringBuilder();
            foreach (var rule in registry.All)
            {
                var flags = new List<string> { rule.Enabled ? "on" : "off" };
                if (rule.DryRun)
                {
                    flags.Add("dry run");
                }

                if (rule.Origin == AutomationRuleOrigin.Configuration)
                {
                    flags.Add("config");
                }

                text.Append(CultureInfo.InvariantCulture, $"**{rule.Name}** · {rule.Trigger.Key} · {string.Join(", ", flags)}");
                if (registry.Problems.ContainsKey(rule.Name))
                {
                    text.Append(" · ⚠️ has problems");
                }

                text.AppendLine();
                if (rule.Description is { } description)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"> {AutomationMarkup.Untrusted(description)}");
                }
            }

            foreach (var (name, _) in registry.Problems.Where(problem => registry.Find(problem.Key) is null))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"**{name}** · ⚠️ can't be loaded");
            }

            return Task.FromResult(new AutomationReply(true, text.ToString().TrimEnd()));
        }, cancellationToken);

    public Task<AutomationReply> ShowAsync(ulong user, string name, CancellationToken cancellationToken) =>
        AsViewerAsync(user, async () =>
        {
            var json = registry.Find(name)?.Json ?? (await WithAutomationStoreAsync(store => store.FindRuleAsync(name, cancellationToken)).ConfigureAwait(false))?.Json;
            if (json is null)
            {
                return Missing(name);
            }

            var text = new StringBuilder();
            if (registry.Problems.TryGetValue(name, out var problems))
            {
                text.AppendLine("⚠️ This rule can't run as it is:");
                foreach (var problem in problems)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"- {AutomationMarkup.Untrusted(problem)}");
                }
            }

            var runs = await WithAutomationStoreAsync(store => store.ReadRecentRunsAsync(name, 5, cancellationToken)).ConfigureAwait(false);
            if (runs.Count > 0)
            {
                text.AppendLine("Recent runs:");
                text.AppendLine(DescribeRuns(runs));
            }

            var code = $"```json\n{json}\n```";
            return text.Length + code.Length <= MaxInlineLength
                ? new AutomationReply(true, (code + "\n" + text).TrimEnd())
                : new AutomationReply(true, (text.Length == 0 ? "The rule is attached." : text.ToString()).TrimEnd(), json);
        }, cancellationToken);

    /// <summary>The JSON to put into the edit form, or null if the rule can't be edited from Discord.</summary>
    public async Task<(string? Json, AutomationReply? Refusal)> GetEditableAsync(ulong user, string name, CancellationToken cancellationToken)
    {
        if (!await runner.IsAuthorizedAsync(user, Managers, cancellationToken).ConfigureAwait(false))
        {
            return (null, AutomationReply.Refused(NotAManager));
        }

        if (registry.ConfigRules.Any(rule => rule.Name == name))
        {
            return (null, new AutomationReply(false, $"**{name}** is defined in the configuration, so it can only be switched on or off from here."));
        }

        var stored = await WithAutomationStoreAsync(store => store.FindRuleAsync(name, cancellationToken)).ConfigureAwait(false);
        return stored is null ? (null, Missing(name)) : (stored.Json, null);
    }

    /// <summary>Creates a rule, or replaces one when <paramref name="editing"/> names it.</summary>
    public async Task<AutomationReply> SaveAsync(ulong user, string json, string? editing, CancellationToken cancellationToken)
    {
        AutomationReply? reply = null;
        var result = await runner.RunAsync(user, editing is null ? "automation-create" : "automation-edit", editing ?? "new", async ct =>
        {
            var parsed = AutomationRuleParser.Parse(json, AutomationRuleOrigin.Discord);
            var errors = parsed.Errors.ToList();
            if (parsed.Rule is { } candidate)
            {
                errors.AddRange(AutomationRuleChecks.Check(candidate, options.Value));
            }

            if (errors.Count > 0 || parsed.Rule is not { } rule)
            {
                reply = Invalid(errors);
                return new Error(ErrorKind.Validation, "automation.invalid", "The rule is invalid.");
            }

            if (registry.ConfigRules.Any(existing => existing.Name == rule.Name))
            {
                reply = new AutomationReply(false, $"**{rule.Name}** is already defined in the configuration. Pick another name.");
                return new Error(ErrorKind.Validation, "automation.config_name", "The name is taken.");
            }

            if (editing is not null && editing != rule.Name)
            {
                reply = new AutomationReply(false, $"Editing can't rename **{editing}**. Create **{rule.Name}** as a new rule, and delete the old one.");
                return new Error(ErrorKind.Validation, "automation.rename", "Rules can't be renamed.");
            }

            await WithStoresAsync(async (store, automations) =>
            {
                var existing = await automations.FindRuleAsync(rule.Name, ct).ConfigureAwait(false);
                if (editing is null && existing is not null)
                {
                    reply = new AutomationReply(false, $"**{rule.Name}** already exists. Use `/automation edit` to change it.");
                    return;
                }

                if (editing is not null && existing is null)
                {
                    reply = Missing(editing);
                    return;
                }

                await automations.SaveRuleAsync(new StoredAutomationRule(rule.Name, rule.Json, user, timeProvider.GetUtcNow()), ct).ConfigureAwait(false);
                await store.CommitAsync(ct).ConfigureAwait(false);
                await registry.ReloadAsync(store, automations, ct).ConfigureAwait(false);
                reply = new AutomationReply(true, $"Saved **{rule.Name}**. {Summary(rule)}");
            }).ConfigureAwait(false);

            return reply!.Ok ? Result.Success() : new Error(ErrorKind.Validation, "automation.conflict", reply.Text);
        }, cancellationToken, Managers).ConfigureAwait(false);

        return reply ?? AutomationReply.Refused(result.Error!);
    }

    public async Task<AutomationReply> SetEnabledAsync(ulong user, string name, bool enabled, CancellationToken cancellationToken)
    {
        AutomationReply? reply = null;
        var result = await runner.RunAsync(user, enabled ? "automation-enable" : "automation-disable", name, async ct =>
        {
            await WithStoresAsync(async (store, automations) =>
            {
                if (registry.ConfigRules.Any(rule => rule.Name == name))
                {
                    var disabled = (await store.ReadStateAsync<DisabledConfigRules>(DisabledConfigRules.StateKey, ct).ConfigureAwait(false))?.Names ?? [];
                    var names = enabled ? disabled.Where(item => item != name).ToList() : disabled.Append(name).Distinct().ToList();
                    await store.WriteStateAsync(DisabledConfigRules.StateKey, new DisabledConfigRules(names), ct).ConfigureAwait(false);
                }
                else if (await automations.FindRuleAsync(name, ct).ConfigureAwait(false) is { } stored)
                {
                    var node = JsonNode.Parse(stored.Json)!.AsObject();
                    var key = node.Select(property => property.Key).FirstOrDefault(key => string.Equals(key, "Enabled", StringComparison.OrdinalIgnoreCase)) ?? "Enabled";
                    node[key] = enabled;
                    await automations.SaveRuleAsync(stored with { Json = node.ToJsonString(Indented), UpdatedBy = user, UpdatedAt = timeProvider.GetUtcNow() }, ct).ConfigureAwait(false);
                }
                else
                {
                    reply = Missing(name);
                    return;
                }

                await store.CommitAsync(ct).ConfigureAwait(false);
                await registry.ReloadAsync(store, automations, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            if (reply is not null)
            {
                return new Error(ErrorKind.NotFound, "automation.not_found", reply.Text);
            }

            var cancelled = enabled ? 0 : await CancelOpenRunsAsync(name, "The rule was switched off.", ct).ConfigureAwait(false);
            reply = new AutomationReply(true, enabled
                ? $"**{name}** is on."
                : $"**{name}** is off.{(cancelled > 0 ? $" Cancelled {cancelled} unfinished runs." : "")}");
            return Result.Success();
        }, cancellationToken, Managers).ConfigureAwait(false);

        return reply ?? AutomationReply.Refused(result.Error!);
    }

    public async Task<AutomationReply> DeleteAsync(ulong user, string name, CancellationToken cancellationToken)
    {
        AutomationReply? reply = null;
        var result = await runner.RunAsync(user, "automation-delete", name, async ct =>
        {
            if (registry.ConfigRules.Any(rule => rule.Name == name))
            {
                reply = new AutomationReply(false, $"**{name}** is defined in the configuration. Remove it there, or switch it off from here.");
                return new Error(ErrorKind.Validation, "automation.config_rule", reply.Text);
            }

            var found = false;
            await WithStoresAsync(async (store, automations) =>
            {
                found = await automations.FindRuleAsync(name, ct).ConfigureAwait(false) is not null;
                if (found)
                {
                    await automations.DeleteRuleAsync(name, ct).ConfigureAwait(false);
                    await store.CommitAsync(ct).ConfigureAwait(false);
                    await registry.ReloadAsync(store, automations, ct).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            if (!found)
            {
                reply = Missing(name);
                return new Error(ErrorKind.NotFound, "automation.not_found", reply.Text);
            }

            var cancelled = await CancelOpenRunsAsync(name, "The rule was deleted.", ct).ConfigureAwait(false);
            reply = new AutomationReply(true, $"Deleted **{name}**.{(cancelled > 0 ? $" Cancelled {cancelled} unfinished runs." : "")}");
            return Result.Success();
        }, cancellationToken, Managers).ConfigureAwait(false);

        return reply ?? AutomationReply.Refused(result.Error!);
    }

    /// <summary>Shows what a rule would do, without doing anything or recording anything.</summary>
    public Task<AutomationReply> TestAsync(ulong user, string name, string? player, string? text, CancellationToken cancellationToken) =>
        AsViewerAsync(user, async () =>
        {
            if (registry.Find(name) is not { } rule)
            {
                return Missing(name);
            }

            if (!TryReadPlayer(player, out var subject, out var refusal))
            {
                return refusal!;
            }

            var lines = await engine.PreviewAsync(NewRun(rule, subject, text, manual: false, user), cancellationToken).ConfigureAwait(false);
            return new AutomationReply(true, $"Dry run of **{rule.Name}**{(subject is null ? "" : " for that player")}:\n{string.Join('\n', lines)}");
        }, cancellationToken);

    /// <summary>Starts a rule by hand. Its age and limits don't apply, but its condition does.</summary>
    public async Task<AutomationReply> RunAsync(ulong user, string name, string? player, string? text, CancellationToken cancellationToken)
    {
        AutomationReply? reply = null;
        var result = await runner.RunAsync(user, "automation-run", name, async ct =>
        {
            if (registry.Find(name) is not { } rule)
            {
                reply = Missing(name);
                return new Error(ErrorKind.NotFound, "automation.not_found", reply.Text);
            }

            if (!TryReadPlayer(player, out var subject, out var refusal))
            {
                reply = refusal;
                return new Error(ErrorKind.Validation, "automation.player", refusal!.Text);
            }

            long id = 0;
            await WithStoresAsync(async (store, automations) =>
            {
                var handle = automations.AddRun(NewRun(rule, subject, text, manual: true, user));
                await store.CommitAsync(ct).ConfigureAwait(false);
                id = handle.Id;
            }).ConfigureAwait(false);

            signal.Wake();
            reply = new AutomationReply(true, $"Started run #{id} of **{rule.Name}**{(rule.DryRun ? " as a dry run, as the rule says" : "")}. Its report will follow.");
            return Result.Success();
        }, cancellationToken, Managers).ConfigureAwait(false);

        return reply ?? AutomationReply.Refused(result.Error!);
    }

    public Task<AutomationReply> RunsAsync(ulong user, string? name, CancellationToken cancellationToken) =>
        AsViewerAsync(user, async () =>
        {
            var runs = await WithAutomationStoreAsync(store => store.ReadRecentRunsAsync(name, RecentRuns, cancellationToken)).ConfigureAwait(false);
            return runs.Count == 0
                ? new AutomationReply(true, "No runs yet.")
                : new AutomationReply(true, DescribeRuns(runs));
        }, cancellationToken);

    public async Task<AutomationReply> CancelAsync(ulong user, long runId, CancellationToken cancellationToken)
    {
        AutomationReply? reply = null;
        var result = await runner.RunAsync(user, "automation-cancel", runId.ToString(CultureInfo.InvariantCulture), async ct =>
        {
            var run = await WithAutomationStoreAsync(store => store.FindRunAsync(runId, ct)).ConfigureAwait(false);
            if (run is not { IsFinished: false })
            {
                reply = new AutomationReply(false, $"Run #{runId} isn't running.");
                return new Error(ErrorKind.NotFound, "automation.run_not_found", reply.Text);
            }

            await engine.CancelAsync(runId, "Cancelled from Discord.", ct).ConfigureAwait(false);
            reply = new AutomationReply(true, $"Cancelled run #{runId}.");
            return Result.Success();
        }, cancellationToken, Managers).ConfigureAwait(false);

        return reply ?? AutomationReply.Refused(result.Error!);
    }

    /// <summary>Approves or rejects a run waiting for approval.</summary>
    public async Task<AutomationReply> DecideAsync(ulong user, long runId, bool approve, CancellationToken cancellationToken)
    {
        string? problem = null;
        var result = await runner.RunAsync(user, approve ? "automation-approve" : "automation-reject", runId.ToString(CultureInfo.InvariantCulture), async ct =>
        {
            problem = await engine.DecideAsync(runId, approve, user, ct).ConfigureAwait(false);
            return problem is null ? Result.Success() : new Error(ErrorKind.NotFound, "automation.not_waiting", problem);
        }, cancellationToken, Viewers).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return new AutomationReply(true, approve ? "Approved. The run continues." : "Rejected. The run stopped.");
        }

        return new AutomationReply(false, problem ?? result.Error!.Message);
    }

    private AutomationRun NewRun(AutomationRule rule, string? subject, string? text, bool manual, ulong user)
    {
        var now = timeProvider.GetUtcNow();
        var data = new JsonObject { ["requested_by"] = user.ToString(CultureInfo.InvariantCulture) };
        if (text is not null)
        {
            data["text"] = text;
        }

        return new AutomationRun
        {
            RuleName = rule.Name,
            RuleJson = rule.Json,
            Trigger = manual ? AutomationTriggerSpec.ManualName : rule.Trigger.Key,
            GroupKey = $"manual:{Guid.NewGuid():N}",
            SubjectUserId = subject,
            EventJson = data.ToJsonString(),
            Manual = manual,
            DryRun = rule.DryRun,
            RequestedBy = user,
            State = AutomationRunState.Pending,
            CreatedAt = now,
            OccurredAt = now,
            ResumeAt = now
        };
    }

    private async Task<int> CancelOpenRunsAsync(string name, string reason, CancellationToken cancellationToken)
    {
        var open = await WithAutomationStoreAsync(store => store.ReadOpenRunsAsync(name, cancellationToken)).ConfigureAwait(false);
        foreach (var run in open)
        {
            await engine.CancelAsync(run.Id, reason, cancellationToken).ConfigureAwait(false);
        }

        return open.Count;
    }

    private static bool TryReadPlayer(string? player, out string? subject, out AutomationReply? refusal)
    {
        refusal = null;
        subject = null;
        if (string.IsNullOrWhiteSpace(player))
        {
            return true;
        }

        subject = AutomationEngine.ParseUserId(player);
        if (subject is null)
        {
            refusal = new AutomationReply(false, "That isn't a GeoGuessr user id or profile link.");
            return false;
        }

        return true;
    }

    private async Task<AutomationReply> AsViewerAsync(ulong user, Func<Task<AutomationReply>> action, CancellationToken cancellationToken) =>
        await runner.IsAuthorizedAsync(user, Viewers, cancellationToken).ConfigureAwait(false)
            ? await action().ConfigureAwait(false)
            : AutomationReply.Refused(NotAViewer);

    private async Task<T> WithAutomationStoreAsync<T>(Func<IAutomationStore, Task<T>> action)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await action(scope.ServiceProvider.GetRequiredService<IAutomationStore>()).ConfigureAwait(false);
        }
    }

    private async Task WithStoresAsync(Func<IShadowStore, IAutomationStore, Task> action)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await action(scope.ServiceProvider.GetRequiredService<IShadowStore>(), scope.ServiceProvider.GetRequiredService<IAutomationStore>()).ConfigureAwait(false);
        }
    }

    private static AutomationReply Missing(string name) => new(false, $"There is no automation named **{AutomationMarkup.Untrusted(name)}**.");

    private static AutomationReply Invalid(IReadOnlyList<string> errors)
    {
        var text = new StringBuilder("The rule wasn't saved:\n");
        foreach (var error in errors.Take(20))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {AutomationMarkup.Untrusted(error)}");
        }

        if (errors.Count > 20)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"…and {errors.Count - 20} more.");
        }

        return new AutomationReply(false, text.ToString().TrimEnd());
    }

    private static string Summary(AutomationRule rule)
    {
        var parts = new List<string> { $"It runs on {rule.Trigger.Key}", $"has {rule.Program.Count} steps" };
        if (!rule.Enabled)
        {
            parts.Add("is switched off");
        }

        if (rule.DryRun)
        {
            parts.Add("is a dry run, so it only reports what it would do");
        }

        return string.Join(", ", parts) + ". Try it with `/automation test`.";
    }

    private static string DescribeRuns(IEnumerable<AutomationRun> runs) => string.Join('\n', runs.Select(run =>
        $"#{run.Id} **{run.RuleName}** · {run.State}{(run.DryRun ? " · dry run" : "")} · <t:{run.CreatedAt.ToUnixTimeSeconds()}:R>"
        + (run.Outcome is { } outcome ? $"\n> {AutomationMarkup.Untrusted(outcome)}" : "")));
}
