// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Frozen;
using System.Text.Json.Nodes;
using GeoGuessrAccountShadowBot.Application.Handlers;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;

namespace GeoGuessrAccountShadowBot.Application.Automation;

/// <summary>
/// An event, as the rules see it.
/// </summary>
/// <param name="Trigger">The trigger key, such as <c>DmMessage</c>.</param>
/// <param name="SubjectUserId">The player it's about, who becomes <c>player</c>, if any.</param>
/// <param name="OccurredAt">When it happened, if GeoGuessr says. Otherwise it's when the bot found out.</param>
/// <param name="Data">What becomes <c>event</c> in scripts.</param>
/// <param name="GroupKey">Identifies the event, so rules with StopOtherRules can stop the others.</param>
public sealed record AutomationFire(string Trigger, string? SubjectUserId, DateTimeOffset? OccurredAt, JsonObject Data, string GroupKey);

/// <summary>
/// Maps events to triggers. Messages the account sent itself never trigger anything, which keeps
/// a rule from answering its own messages.
/// </summary>
public static class AutomationTriggers
{
    /// <summary>The event keys a rule may use as its trigger.</summary>
    public static readonly FrozenSet<string> EventTriggers = EventKeys.All;

    /// <summary>The fields each trigger puts into <c>event</c>, for documentation and help.</summary>
    public static readonly FrozenDictionary<string, string[]> EventFields = new Dictionary<string, string[]>
    {
        [EventKeys.DmMessage] = ["text", "payload_type", "sent_at"],
        [EventKeys.ClubChat] = ["text", "payload_type", "sent_at", "club_id"],
        [EventKeys.FriendRequestReceived] = [],
        [EventKeys.FriendRequestAccepted] = [],
        [EventKeys.FriendPresence] = ["online", "activity"],
        [EventKeys.ClubMemberJoined] = ["nick", "role"],
        [EventKeys.ClubMemberLeft] = ["nick", "role"],
        [EventKeys.ClubRoleChanged] = ["nick", "role"],
        [EventKeys.ClubMissionBoard] = ["board_cleared"],
        [EventKeys.ClubApplicationReceived] = ["nick"],
        [EventKeys.RankedDivisionChanged] = ["before", "after", "before_number", "after_number", "rating"],
        [EventKeys.SeasonChanged] = ["season_id"]
    }.ToFrozenDictionary();

    /// <summary>The fields of a notification trigger, such as <c>InviteGame</c>.</summary>
    public static readonly string[] NotificationFields = ["notification_id", "type", "sent_by_nick", "sent_at"];

    /// <summary>How an event looks to rules, or null if it can't trigger any.</summary>
    public static AutomationFire? Describe(ShadowEvent shadowEvent, string ownUserId)
    {
        var group = shadowEvent.DedupeKey ?? Guid.NewGuid().ToString("N");

        return shadowEvent switch
        {
            DirectMessageReceived { Message: var message } when !message.IsSentBy(ownUserId) && ChatText.Describe(message) is { } text =>
                new AutomationFire(EventKeys.DmMessage, message.SourceId, message.SentAt, Message(message, text), group),

            ClubChatMessageReceived { Message: var message } club when !message.IsSentBy(ownUserId) && ChatText.Describe(message) is { } text =>
                new AutomationFire(EventKeys.ClubChat, message.SourceId, message.SentAt, With(Message(message, text), "club_id", club.ClubId), group),

            FriendRequestReceived request => new AutomationFire(EventKeys.FriendRequestReceived, request.UserId, null, [], group),

            FriendRequestAccepted accepted => new AutomationFire(EventKeys.FriendRequestAccepted, accepted.UserId, null, [], group),

            FriendPresenceChanged presence => new AutomationFire(
                EventKeys.FriendPresence,
                presence.FriendId,
                null,
                new JsonObject { ["online"] = presence.IsOnline, ["activity"] = presence.Activity },
                group),

            AccountNotificationReceived { NotificationSwitchKey: { } key, Notification: var notification } => new AutomationFire(
                key,
                notification.SentById,
                notification.SentAt,
                new JsonObject
                {
                    ["notification_id"] = notification.Id,
                    ["type"] = notification.Type.ToString(),
                    ["sent_by_nick"] = notification.SentByNick,
                    ["sent_at"] = notification.SentAt
                },
                group),

            ClubMemberChanged change => new AutomationFire(
                change.SwitchKey,
                change.Member.UserId,
                null,
                new JsonObject { ["nick"] = change.Member.Nick, ["role"] = change.Member.Role },
                group),

            ClubMissionBoardProgressed board => new AutomationFire(
                EventKeys.ClubMissionBoard, board.ContributorUserId, null, new JsonObject { ["board_cleared"] = board.BoardCleared }, group),

            ClubApplicationReceived application => new AutomationFire(
                EventKeys.ClubApplicationReceived, application.Applicant.UserId, null, new JsonObject { ["nick"] = application.Applicant.Nick }, group),

            RankedDivisionChanged ranked => new AutomationFire(
                EventKeys.RankedDivisionChanged,
                null,
                null,
                new JsonObject
                {
                    ["before"] = ranked.Previous.DivisionName ?? "Unranked",
                    ["after"] = ranked.Current.DivisionName ?? "Unranked",
                    ["before_number"] = ranked.Previous.DivisionNumber,
                    ["after_number"] = ranked.Current.DivisionNumber,
                    ["rating"] = ranked.Current.Rating
                },
                group),

            SeasonChanged season => new AutomationFire(
                EventKeys.SeasonChanged, null, null, new JsonObject { ["season_id"] = season.Current.SeasonId }, group),

            _ => null
        };
    }

    /// <summary>The event keys that are notification types rather than events of their own.</summary>
    public static bool IsNotificationTrigger(string key) => !EventFields.ContainsKey(key);

    private static JsonObject Message(ChatMessage message, string text) => new()
    {
        ["text"] = text,
        ["payload_type"] = message.PayloadType.ToString(),
        ["sent_at"] = message.SentAt
    };

    private static JsonObject With(JsonObject data, string key, string value)
    {
        data[key] = value;
        return data;
    }
}

/// <summary>
/// Reads the rules in <c>Control:Automation:Rules</c>. The configuration flattens JSON into
/// strings, and turns arrays into numbered keys, so the rules are put back together as JSON and
/// read by the same parser as rules from Discord.
/// </summary>
public static class AutomationConfigRules
{
    public const string SectionPath = "Control:Automation:Rules";

    /// <summary>Each rule as JSON, with where it came from for error messages.</summary>
    public static IReadOnlyList<(string Path, JsonNode? Json)> Read(Microsoft.Extensions.Configuration.IConfiguration? configuration)
    {
        if (configuration is null)
        {
            return [];
        }

        return configuration.GetSection(SectionPath).GetChildren()
            .OrderBy(section => int.TryParse(section.Key, out var index) ? index : int.MaxValue)
            .ThenBy(section => section.Key, StringComparer.Ordinal)
            .Select(section => ($"{SectionPath}:{section.Key}", ToJson(section)))
            .ToArray();
    }

    private static JsonNode? ToJson(Microsoft.Extensions.Configuration.IConfigurationSection section)
    {
        var children = section.GetChildren().ToArray();
        if (children.Length == 0)
        {
            return section.Value is null ? null : JsonValue.Create(section.Value);
        }

        if (children.All(child => int.TryParse(child.Key, out _)))
        {
            var array = new JsonArray();
            foreach (var child in children.OrderBy(child => int.Parse(child.Key, System.Globalization.CultureInfo.InvariantCulture)))
            {
                array.Add(ToJson(child));
            }

            return array;
        }

        var obj = new JsonObject();
        foreach (var child in children)
        {
            obj[child.Key] = ToJson(child);
        }

        return obj;
    }
}
