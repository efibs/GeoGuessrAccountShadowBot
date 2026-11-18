// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cronos;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Domain.Events;

namespace GeoGuessrAccountShadowBot.Application.Automation;

/// <summary>The rule, or every problem found in it.</summary>
public sealed record AutomationParseResult(AutomationRule? Rule, IReadOnlyList<string> Errors)
{
    public bool IsSuccess => Rule is not null;
}

/// <summary>
/// Reads a rule from JSON, or from the same shape in the configuration, where every value is a
/// string. Every problem is reported with its path at once, so fixing a rule takes one round.
/// </summary>
public static partial class AutomationRuleParser
{
    public const int MaxSteps = 50;

    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxWait = TimeSpan.FromDays(7);
    public static readonly TimeSpan DefaultApprovalTimeout = TimeSpan.FromDays(1);

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly string[] RuleKeys =
    [
        "Name", "Description", "Enabled", "DryRun", "On", "Cron", "TimeZone", "When", "MaxAge",
        "OncePerPlayer", "PlayerCooldown", "Cooldown", "MaxRunsPerHour", "Priority", "StopOtherRules",
        "Report", "ReportChannelId", "Do"
    ];

    private static readonly Dictionary<string, PlayerActionKind> PlayerActions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SendFriendRequest"] = PlayerActionKind.SendFriendRequest,
        ["AcceptFriendRequest"] = PlayerActionKind.AcceptFriendRequest,
        ["DeclineFriendRequest"] = PlayerActionKind.DeclineFriendRequest,
        ["AcceptClubApplication"] = PlayerActionKind.AcceptClubApplication,
        ["DeclineClubApplication"] = PlayerActionKind.DeclineClubApplication
    };

    /// <summary>Every action name, for messages and documentation.</summary>
    public static readonly IReadOnlyList<string> ActionNames =
    [
        "SendDm", "SendClubMessage", .. PlayerActions.Keys, "PostDiscord", "ForumNote",
        "Wait", "RequireApproval", "Raise", "Stop", "If"
    ];

    public static AutomationParseResult Parse(string json, AutomationRuleOrigin origin)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: DocumentOptions);
        }
        catch (JsonException exception)
        {
            return new AutomationParseResult(null, [$"The rule isn't valid JSON: {exception.Message}"]);
        }

        return Parse(node, origin);
    }

    public static AutomationParseResult Parse(JsonNode? node, AutomationRuleOrigin origin)
    {
        var reader = new Reader();
        var rule = reader.ReadRule(node, origin);
        return reader.Errors.Count == 0 && rule is not null
            ? new AutomationParseResult(rule, [])
            : new AutomationParseResult(null, reader.Errors);
    }

    /// <summary>Whether a name may be used for a rule or a custom event.</summary>
    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    /// <summary>
    /// Reads durations as <c>hh:mm:ss</c>, <c>d.hh:mm:ss</c>, or a number with a unit, such as
    /// <c>30s</c>, <c>5m</c>, <c>2h</c> or <c>1d</c>.
    /// </summary>
    public static bool TryParseDuration(string text, out TimeSpan duration)
    {
        text = text.Trim();
        if (ShortDurationPattern().Match(text) is { Success: true } match)
        {
            var amount = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            duration = match.Groups[2].Value switch
            {
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "h" => TimeSpan.FromHours(amount),
                _ => TimeSpan.FromDays(amount)
            };
            return true;
        }

        return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out duration) && duration >= TimeSpan.Zero;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,47}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*([smhd])$")]
    private static partial Regex ShortDurationPattern();

    /// <summary>Collects errors while reading, so that one bad value doesn't hide the next.</summary>
    private sealed class Reader
    {
        public List<string> Errors { get; } = [];

        private void Error(string path, string message) => Errors.Add($"{path}: {message}");

        public AutomationRule? ReadRule(JsonNode? node, AutomationRuleOrigin origin)
        {
            if (node is not JsonObject root)
            {
                Errors.Add("The rule must be a JSON object.");
                return null;
            }

            var properties = Properties(root, "rule", RuleKeys);

            var name = String(properties, "Name", "Name");
            if (name is null)
            {
                Error("Name", "is required.");
            }
            else if (!IsValidName(name))
            {
                Error("Name", "must be 1 to 48 lowercase letters, digits or dashes, starting with a letter or digit.");
            }

            var trigger = ReadTrigger(properties);
            var when = Script(properties, "When", "When", expression: true);
            var maxAge = Duration(properties, "MaxAge", "MaxAge") ?? DefaultMaxAge;
            var limits = ReadLimits(properties);
            var report = ReadReport(properties);

            var program = new List<AutomationStep>();
            if (!properties.TryGetValue("Do", out var steps) || steps is null)
            {
                Error("Do", "is required, and lists what the rule does.");
            }
            else
            {
                var errorsBefore = Errors.Count;
                CompileSteps(steps, "Do", program);
                if (program.Count == 0 && Errors.Count == errorsBefore)
                {
                    Error("Do", "must have at least one step.");
                }
                else if (program.Count > MaxSteps)
                {
                    Error("Do", $"compiles to {program.Count} steps, and at most {MaxSteps} are allowed.");
                }
            }

            if (name is null || trigger is null)
            {
                return null;
            }

            return new AutomationRule(
                name,
                String(properties, "Description", "Description"),
                Bool(properties, "Enabled", "Enabled") ?? true,
                Bool(properties, "DryRun", "DryRun") ?? false,
                trigger,
                when,
                maxAge,
                limits,
                report,
                ULong(properties, "ReportChannelId", "ReportChannelId"),
                Int(properties, "Priority", "Priority") ?? 0,
                Bool(properties, "StopOtherRules", "StopOtherRules") ?? false,
                program,
                root.ToJsonString(WriteOptions),
                origin);
        }

        private AutomationTriggerSpec? ReadTrigger(Dictionary<string, JsonNode?> properties)
        {
            var on = String(properties, "On", "On");
            var cronText = String(properties, "Cron", "Cron");
            var zoneText = String(properties, "TimeZone", "TimeZone");

            var zone = TimeZoneInfo.Utc;
            if (zoneText is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(zoneText, out zone))
            {
                Error("TimeZone", $"'{zoneText}' isn't a known time zone. Use an IANA name such as Europe/Berlin.");
                zone = TimeZoneInfo.Utc;
            }

            if (on is null)
            {
                Error("On", "is required. Use an event key, Schedule, Manual or Custom:<name>.");
                return null;
            }

            if (!string.Equals(on, AutomationTriggerSpec.SchedulePrefix, StringComparison.OrdinalIgnoreCase) && cronText is not null)
            {
                Error("Cron", "only applies when On is Schedule.");
            }

            if (string.Equals(on, AutomationTriggerSpec.SchedulePrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (cronText is null)
                {
                    Error("Cron", "is required for a schedule, such as \"0 18 * * *\" for 18:00 every day.");
                    return null;
                }

                try
                {
                    return new AutomationTriggerSpec(AutomationTriggerKind.Schedule, cronText, CronExpression.Parse(cronText, CronFormat.Standard), zone);
                }
                catch (CronFormatException exception)
                {
                    Error("Cron", $"isn't a valid five-field cron expression: {exception.Message}");
                    return null;
                }
            }

            if (string.Equals(on, AutomationTriggerSpec.ManualName, StringComparison.OrdinalIgnoreCase))
            {
                return new AutomationTriggerSpec(AutomationTriggerKind.Manual, AutomationTriggerSpec.ManualName, null, zone);
            }

            if (on.StartsWith(AutomationTriggerSpec.CustomPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var custom = on[AutomationTriggerSpec.CustomPrefix.Length..];
                if (!IsValidName(custom))
                {
                    Error("On", "needs a custom event name of lowercase letters, digits or dashes after Custom:.");
                    return null;
                }

                return new AutomationTriggerSpec(AutomationTriggerKind.Custom, custom, null, zone);
            }

            var key = EventKeys.All.FirstOrDefault(candidate => string.Equals(candidate, on, StringComparison.OrdinalIgnoreCase));
            if (key is null || !AutomationTriggers.EventTriggers.Contains(key))
            {
                Error("On", $"'{on}' isn't a trigger. Use Schedule, Manual, Custom:<name> or one of: {string.Join(", ", AutomationTriggers.EventTriggers.Order(StringComparer.Ordinal))}.");
                return null;
            }

            return new AutomationTriggerSpec(AutomationTriggerKind.Event, key, null, zone);
        }

        private AutomationLimits ReadLimits(Dictionary<string, JsonNode?> properties)
        {
            var once = false;
            TimeSpan? onceWithin = null;
            if (properties.TryGetValue("OncePerPlayer", out var onceNode) && onceNode is not null)
            {
                if (AsBool(onceNode) is { } flag)
                {
                    once = flag;
                }
                else if (AsString(onceNode) is { } text && TryParseDuration(text, out var window))
                {
                    once = true;
                    onceWithin = window;
                }
                else
                {
                    Error("OncePerPlayer", "must be true, false, or a duration such as \"30d\".");
                }
            }

            var maxRuns = Int(properties, "MaxRunsPerHour", "MaxRunsPerHour");
            if (maxRuns is < 1)
            {
                Error("MaxRunsPerHour", "must be at least 1.");
            }

            return new AutomationLimits(
                once,
                onceWithin,
                Duration(properties, "PlayerCooldown", "PlayerCooldown"),
                Duration(properties, "Cooldown", "Cooldown"),
                maxRuns);
        }

        private AutomationReportMode ReadReport(Dictionary<string, JsonNode?> properties)
        {
            var text = String(properties, "Report", "Report");
            if (text is null)
            {
                return AutomationReportMode.Failures;
            }

            if (Enum.TryParse<AutomationReportMode>(text, ignoreCase: true, out var mode) && Enum.IsDefined(mode))
            {
                return mode;
            }

            Error("Report", "must be None, Failures or All.");
            return AutomationReportMode.Failures;
        }

        private void CompileSteps(JsonNode node, string path, List<AutomationStep> program)
        {
            if (node is not JsonArray array)
            {
                // The configuration has no way to tell a one-element array from an object, and a
                // single step is common enough to accept on its own.
                if (node is JsonObject)
                {
                    CompileStep(node, $"{path}[0]", program);
                    return;
                }

                Error(path, "must be a list of steps.");
                return;
            }

            for (var index = 0; index < array.Count; index++)
            {
                CompileStep(array[index], $"{path}[{index}]", program);
            }
        }

        private void CompileStep(JsonNode? node, string path, List<AutomationStep> program)
        {
            if (node is not JsonObject step)
            {
                Error(path, $"must be an object such as {{ \"SendDm\": \"Hi\" }}. Steps are: {string.Join(", ", ActionNames)}.");
                return;
            }

            if (step.Any(property => string.Equals(property.Key, "If", StringComparison.OrdinalIgnoreCase)))
            {
                CompileIf(step, path, program);
                return;
            }

            if (step.Count != 1)
            {
                Error(path, "must have exactly one key, naming the step.");
                return;
            }

            var (action, value) = (step.First().Key, step.First().Value);
            var at = $"{path}.{action}";
            var compiled = action.ToUpperInvariant() switch
            {
                "SENDDM" => ReadSendDm(value, at, path),
                "SENDCLUBMESSAGE" => ReadTextOnly(value, at) is { } text ? new SendClubMessageStep(path, text) : null,
                "POSTDISCORD" => ReadPostDiscord(value, at, path),
                "FORUMNOTE" => ReadForumNote(value, at, path),
                "WAIT" => ReadWait(value, at, path),
                "REQUIREAPPROVAL" => ReadApproval(value, at, path),
                "RAISE" => ReadRaise(value, at, path),
                "STOP" => new StopStep(path, AsBool(value) is null ? AsString(value) : null),
                _ when PlayerActions.TryGetValue(action, out var kind) => new PlayerActionStep(path, kind, ReadTarget(value, at)),
                _ => Unknown(at, action)
            };

            if (compiled is not null)
            {
                program.Add(compiled);
            }
        }

        private AutomationStep? Unknown(string path, string action)
        {
            Error(path, $"'{action}' isn't a step. Steps are: {string.Join(", ", ActionNames)}.");
            return null;
        }

        private void CompileIf(JsonObject step, string path, List<AutomationStep> program)
        {
            var properties = Properties(step, path, ["If", "Then", "Else"]);
            var condition = Script(properties, "If", $"{path}.If", expression: true);
            if (condition is null)
            {
                Error($"{path}.If", "needs a condition.");
            }

            var branch = program.Count;
            program.Add(new JumpStep(path, -1));

            if (properties.TryGetValue("Then", out var then) && then is not null)
            {
                CompileSteps(then, $"{path}.Then", program);
            }
            else
            {
                Error($"{path}.Then", "is required.");
            }

            var elseStart = program.Count;
            if (properties.TryGetValue("Else", out var otherwise) && otherwise is not null)
            {
                var skipElse = program.Count;
                program.Add(new JumpStep(path, -1));
                elseStart = program.Count;
                CompileSteps(otherwise, $"{path}.Else", program);
                program[skipElse] = new JumpStep(path, program.Count);
            }

            if (condition is not null)
            {
                program[branch] = new JumpUnlessStep(path, condition, elseStart);
            }
        }

        private SendDmStep? ReadSendDm(JsonNode? value, string at, string path)
        {
            if (value is JsonObject options)
            {
                var properties = Properties(options, at, ["Text", "To"]);
                var text = Script(properties, "Text", $"{at}.Text", expression: false);
                if (text is null)
                {
                    Error($"{at}.Text", "is required.");
                    return null;
                }

                return new SendDmStep(path, text, Script(properties, "To", $"{at}.To", expression: false));
            }

            return ReadTextOnly(value, at) is { } shorthand ? new SendDmStep(path, shorthand, null) : null;
        }

        private PostDiscordStep? ReadPostDiscord(JsonNode? value, string at, string path)
        {
            if (value is not JsonObject options)
            {
                return ReadTextOnly(value, at) is { } shorthand ? new PostDiscordStep(path, shorthand, null, null, [], CardStyle.Info) : null;
            }

            var properties = Properties(options, at, ["Text", "Title", "ChannelId", "PingRoleIds", "Style"]);
            var text = Script(properties, "Text", $"{at}.Text", expression: false);
            if (text is null)
            {
                Error($"{at}.Text", "is required.");
            }

            var style = CardStyle.Info;
            if (String(properties, "Style", $"{at}.Style") is { } styleText
                && !(Enum.TryParse(styleText, ignoreCase: true, out style) && Enum.IsDefined(style)))
            {
                Error($"{at}.Style", "must be Info, Success, Warning or Danger.");
            }

            return text is null
                ? null
                : new PostDiscordStep(
                    path,
                    text,
                    Script(properties, "Title", $"{at}.Title", expression: false),
                    ULong(properties, "ChannelId", $"{at}.ChannelId"),
                    ULongs(properties, "PingRoleIds", $"{at}.PingRoleIds"),
                    style);
        }

        private ForumNoteStep? ReadForumNote(JsonNode? value, string at, string path)
        {
            if (value is not JsonObject options)
            {
                return ReadTextOnly(value, at) is { } shorthand ? new ForumNoteStep(path, shorthand, null) : null;
            }

            var properties = Properties(options, at, ["Text", "Player"]);
            var text = Script(properties, "Text", $"{at}.Text", expression: false);
            if (text is null)
            {
                Error($"{at}.Text", "is required.");
                return null;
            }

            return new ForumNoteStep(path, text, Script(properties, "Player", $"{at}.Player", expression: false));
        }

        private WaitStep? ReadWait(JsonNode? value, string at, string path)
        {
            if (AsString(value) is { } text && TryParseDuration(text, out var duration))
            {
                if (duration > MaxWait)
                {
                    Error(at, $"can be at most {MaxWait.TotalDays} days.");
                    return null;
                }

                return new WaitStep(path, duration);
            }

            Error(at, "must be a duration such as \"30s\", \"10m\" or \"00:10:00\".");
            return null;
        }

        private ApprovalStep? ReadApproval(JsonNode? value, string at, string path)
        {
            if (value is not JsonObject options)
            {
                if (value is null || AsBool(value) == true)
                {
                    return new ApprovalStep(path, null, DefaultApprovalTimeout, null);
                }

                return ReadTextOnly(value, at) is { } prompt ? new ApprovalStep(path, prompt, DefaultApprovalTimeout, null) : null;
            }

            var properties = Properties(options, at, ["Prompt", "Timeout", "ChannelId"]);
            var timeout = Duration(properties, "Timeout", $"{at}.Timeout") ?? DefaultApprovalTimeout;
            if (timeout > MaxWait)
            {
                Error($"{at}.Timeout", $"can be at most {MaxWait.TotalDays} days.");
            }

            return new ApprovalStep(
                path,
                Script(properties, "Prompt", $"{at}.Prompt", expression: false),
                timeout,
                ULong(properties, "ChannelId", $"{at}.ChannelId"));
        }

        private RaiseStep? ReadRaise(JsonNode? value, string at, string path)
        {
            if (value is not JsonObject options)
            {
                var shorthand = AsString(value);
                if (shorthand is null || !IsValidName(shorthand))
                {
                    Error(at, "must name a custom event of lowercase letters, digits or dashes.");
                    return null;
                }

                return new RaiseStep(path, shorthand, new Dictionary<string, AutomationScript>(), null, null);
            }

            var properties = Properties(options, at, ["Event", "Data", "ForEach", "Player"]);
            var name = String(properties, "Event", $"{at}.Event");
            if (name is null || !IsValidName(name))
            {
                Error($"{at}.Event", "must name a custom event of lowercase letters, digits or dashes.");
            }

            var data = new Dictionary<string, AutomationScript>(StringComparer.Ordinal);
            if (properties.TryGetValue("Data", out var dataNode) && dataNode is not null)
            {
                if (dataNode is not JsonObject dataObject)
                {
                    Error($"{at}.Data", "must be an object of names and templates.");
                }
                else
                {
                    foreach (var (key, template) in dataObject)
                    {
                        if (!DataKeyPattern().IsMatch(key))
                        {
                            Error($"{at}.Data.{key}", "must be a lowercase name with letters, digits or underscores.");
                        }
                        else if (ReadTextOnly(template, $"{at}.Data.{key}") is { } script)
                        {
                            data[key] = script;
                        }
                    }
                }
            }

            AutomationAudience? audience = null;
            if (String(properties, "ForEach", $"{at}.ForEach") is { } audienceText)
            {
                if (Enum.TryParse<AutomationAudience>(audienceText, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                {
                    audience = parsed;
                }
                else
                {
                    Error($"{at}.ForEach", $"must be one of: {string.Join(", ", Enum.GetNames<AutomationAudience>())}.");
                }
            }

            var player = Script(properties, "Player", $"{at}.Player", expression: false);
            if (audience is not null && player is not null)
            {
                Error(at, "can't have both ForEach and Player.");
            }

            return name is null ? null : new RaiseStep(path, name, data, audience, player);
        }

        /// <summary>A target is the run's player unless a template names another one.</summary>
        private AutomationScript? ReadTarget(JsonNode? value, string at)
        {
            if (value is null || AsBool(value) == true)
            {
                return null;
            }

            if (value is JsonObject options)
            {
                var properties = Properties(options, at, ["Player"]);
                return Script(properties, "Player", $"{at}.Player", expression: false);
            }

            return ReadTextOnly(value, at);
        }

        private AutomationScript? ReadTextOnly(JsonNode? value, string at)
        {
            var text = AsString(value);
            if (string.IsNullOrWhiteSpace(text))
            {
                Error(at, "needs text.");
                return null;
            }

            return ParseScript(text, at, expression: false);
        }

        private AutomationScript? Script(Dictionary<string, JsonNode?> properties, string key, string path, bool expression)
        {
            if (!properties.TryGetValue(key, out var node) || node is null)
            {
                return null;
            }

            var text = AsString(node);
            if (string.IsNullOrWhiteSpace(text))
            {
                Error(path, "must be non-empty text.");
                return null;
            }

            return ParseScript(text, path, expression);
        }

        private AutomationScript? ParseScript(string text, string path, bool expression)
        {
            var script = expression
                ? AutomationScript.ParseExpression(text, out var error)
                : AutomationScript.ParseTemplate(text, out error);
            if (script is null)
            {
                Error(path, $"doesn't parse: {error}");
            }

            return script;
        }

        /// <summary>Reads an object's keys case-insensitively, and reports keys nobody reads.</summary>
        private Dictionary<string, JsonNode?> Properties(JsonObject node, string path, IReadOnlyCollection<string> known)
        {
            var properties = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in node)
            {
                var match = known.FirstOrDefault(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    Error(path, $"'{key}' isn't a known property. Known: {string.Join(", ", known)}.");
                }
                else if (!properties.TryAdd(match, value))
                {
                    Error(path, $"'{key}' appears twice.");
                }
            }

            return properties;
        }

        private string? String(Dictionary<string, JsonNode?> properties, string key, string path)
        {
            if (!properties.TryGetValue(key, out var node) || node is null)
            {
                return null;
            }

            if (AsString(node) is { } text)
            {
                return text;
            }

            Error(path, "must be text.");
            return null;
        }

        private bool? Bool(Dictionary<string, JsonNode?> properties, string key, string path)
        {
            if (!properties.TryGetValue(key, out var node) || node is null)
            {
                return null;
            }

            if (AsBool(node) is { } flag)
            {
                return flag;
            }

            Error(path, "must be true or false.");
            return null;
        }

        private int? Int(Dictionary<string, JsonNode?> properties, string key, string path)
        {
            if (!properties.TryGetValue(key, out var node) || node is null)
            {
                return null;
            }

            if (node is JsonValue value && value.TryGetValue<int>(out var number))
            {
                return number;
            }

            if (AsString(node) is { } text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }

            Error(path, "must be a whole number.");
            return null;
        }

        private ulong? ULong(Dictionary<string, JsonNode?> properties, string key, string path)
        {
            if (!properties.TryGetValue(key, out var node) || node is null)
            {
                return null;
            }

            if (AsULong(node) is { } id)
            {
                return id;
            }

            Error(path, "must be a Discord id.");
            return null;
        }

        private List<ulong> ULongs(Dictionary<string, JsonNode?> properties, string key, string path)
        {
            if (!properties.TryGetValue(key, out var node) || node is null)
            {
                return [];
            }

            // The configuration turns arrays into objects keyed 0, 1 and so on.
            IEnumerable<JsonNode?> items = node switch
            {
                JsonArray array => array,
                JsonObject indexed => indexed.Select(entry => entry.Value),
                _ => [node]
            };

            var ids = new List<ulong>();
            foreach (var item in items)
            {
                if (AsULong(item) is { } id)
                {
                    ids.Add(id);
                }
                else
                {
                    Error(path, "must list Discord ids.");
                }
            }

            return ids;
        }

        private TimeSpan? Duration(Dictionary<string, JsonNode?> properties, string key, string path)
        {
            if (String(properties, key, path) is not { } text)
            {
                return null;
            }

            if (TryParseDuration(text, out var duration))
            {
                return duration;
            }

            Error(path, "must be a duration such as \"30s\", \"10m\", \"2h\", \"1d\" or \"00:10:00\".");
            return null;
        }

        private static string? AsString(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static bool? AsBool(JsonNode? node)
        {
            if (node is not JsonValue value)
            {
                return null;
            }

            if (value.TryGetValue<bool>(out var flag))
            {
                return flag;
            }

            return value.TryGetValue<string>(out var text) && bool.TryParse(text, out flag) ? flag : null;
        }

        private static ulong? AsULong(JsonNode? node)
        {
            if (node is not JsonValue value)
            {
                return null;
            }

            if (value.TryGetValue<ulong>(out var id))
            {
                return id;
            }

            // Discord ids exceed what JavaScript numbers hold, so people often quote them.
            return value.TryGetValue<string>(out var text) && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) ? id : null;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,31}$")]
    private static partial Regex DataKeyPattern();
}
