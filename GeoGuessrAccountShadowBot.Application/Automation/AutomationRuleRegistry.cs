// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Collections.Frozen;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Automation;

/// <summary>
/// The rules in the configuration, parsed once. This depends on the configuration alone, so the
/// options validator can use it without needing the options it validates.
/// </summary>
public sealed class ConfiguredAutomationRules
{
    public ConfiguredAutomationRules(IConfiguration? configuration = null)
    {
        var rules = new List<AutomationRule>();
        var errors = new List<string>();
        foreach (var (path, json) in AutomationConfigRules.Read(configuration))
        {
            var result = AutomationRuleParser.Parse(json, AutomationRuleOrigin.Configuration);
            if (result.Rule is { } rule)
            {
                if (rules.Any(existing => existing.Name == rule.Name))
                {
                    errors.Add($"{path}: the name '{rule.Name}' is used twice.");
                    continue;
                }

                rules.Add(rule);
            }

            errors.AddRange(result.Errors.Select(error => $"{path}: {error}"));
        }

        Rules = rules;
        Errors = errors;
    }

    public IReadOnlyList<AutomationRule> Rules { get; }

    /// <summary>Problems in the rules, which the options validator reports at startup.</summary>
    public IReadOnlyList<string> Errors { get; }
}

/// <summary>Config rules switched off from Discord.</summary>
public sealed record DisabledConfigRules(IReadOnlyList<string> Names)
{
    public const string StateKey = "automation:disabled-config-rules";
}

/// <summary>
/// The rules in effect. Lookups read an immutable snapshot, so the event pipeline never waits for
/// a rule being edited, and a reload replaces the whole set at once.
/// </summary>
public sealed class AutomationRuleRegistry
{
    private readonly ConcurrentDictionary<string, AutomationRule?> _compiled = new(StringComparer.Ordinal);
    private readonly IOptions<ShadowOptions> _options;
    private volatile Snapshot _snapshot;

    public AutomationRuleRegistry(IOptions<ShadowOptions> options, ConfiguredAutomationRules configured)
    {
        _options = options;
        ConfigRules = configured.Rules;
        _snapshot = Build([], new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>The rules from the configuration, as written.</summary>
    public IReadOnlyList<AutomationRule> ConfigRules { get; }

    /// <summary>Every rule, with config rules switched off from Discord shown as disabled.</summary>
    public IReadOnlyList<AutomationRule> All => _snapshot.All;

    /// <summary>Why a stored rule can't run any more, for example because its event was switched off.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Problems => _snapshot.Problems;

    public AutomationRule? Find(string name) => _snapshot.ByName.GetValueOrDefault(name);

    /// <summary>The enabled rules for a trigger, highest priority first.</summary>
    public IReadOnlyList<AutomationRule> RulesFor(string triggerKey) =>
        _snapshot.ByTrigger.TryGetValue(triggerKey, out var rules) ? rules : [];

    /// <summary>Replaces the rules from Discord, and the switches on config rules.</summary>
    public void Load(IReadOnlyList<StoredAutomationRule> stored, IReadOnlySet<string> disabledConfigRules) =>
        _snapshot = Build(stored, disabledConfigRules);

    /// <summary>Reads the stored rules and switches, and puts them in effect.</summary>
    public async Task ReloadAsync(IShadowStore store, IAutomationStore automationStore, CancellationToken cancellationToken)
    {
        var stored = await automationStore.ReadRulesAsync(cancellationToken).ConfigureAwait(false);
        var disabled = await store.ReadStateAsync<DisabledConfigRules>(DisabledConfigRules.StateKey, cancellationToken).ConfigureAwait(false);
        Load(stored, (disabled?.Names ?? []).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Compiles the JSON a run carries. Compiling is deterministic, which is what lets a run's
    /// program counter survive a restart.
    /// </summary>
    public AutomationRule? Compile(string json, AutomationRuleOrigin origin) =>
        _compiled.GetOrAdd(json, text => AutomationRuleParser.Parse(text, origin).Rule);

    private Snapshot Build(IReadOnlyList<StoredAutomationRule> stored, IReadOnlySet<string> disabledConfigRules)
    {
        var rules = new List<AutomationRule>();
        var problems = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var rule in ConfigRules)
        {
            rules.Add(disabledConfigRules.Contains(rule.Name) ? rule with { Enabled = false } : rule);
        }

        foreach (var row in stored)
        {
            if (rules.Any(existing => existing.Name == row.Name))
            {
                problems[row.Name] = ["A rule in the configuration has the same name and takes precedence."];
                continue;
            }

            var result = AutomationRuleParser.Parse(row.Json, AutomationRuleOrigin.Discord);
            if (result.Rule is not { } rule)
            {
                problems[row.Name] = result.Errors;
                continue;
            }

            var issues = AutomationRuleChecks.Check(rule, _options.Value).ToArray();
            if (issues.Length > 0)
            {
                problems[row.Name] = issues;
                rule = rule with { Enabled = false };
            }

            rules.Add(rule);
        }

        rules.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));

        var byTrigger = rules
            .Where(rule => rule.Enabled)
            .GroupBy(rule => rule.Trigger.Key, StringComparer.Ordinal)
            .ToFrozenDictionary(
                group => group.Key,
                group => (IReadOnlyList<AutomationRule>)group.OrderByDescending(rule => rule.Priority).ThenBy(rule => rule.Name, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        return new Snapshot(rules, rules.ToFrozenDictionary(rule => rule.Name, StringComparer.Ordinal), byTrigger, problems);
    }

    private sealed record Snapshot(
        IReadOnlyList<AutomationRule> All,
        FrozenDictionary<string, AutomationRule> ByName,
        FrozenDictionary<string, IReadOnlyList<AutomationRule>> ByTrigger,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Problems);
}
