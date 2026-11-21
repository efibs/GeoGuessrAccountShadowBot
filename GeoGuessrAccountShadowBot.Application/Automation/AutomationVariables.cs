// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Model;
using Scriban.Runtime;

namespace GeoGuessrAccountShadowBot.Application.Automation;

/// <summary>
/// What a run knows about its player.
/// </summary>
/// <param name="Id">The GeoGuessr user id.</param>
/// <param name="Nick">The current name, or a fallback if it couldn't be read.</param>
/// <param name="Profile">The public profile, if a script needed it and the player exists.</param>
/// <param name="IsFriend">A friend of the account, by the last friendship snapshot.</param>
/// <param name="HasRequested">Sent the account a friend request that is still pending.</param>
/// <param name="WasRequested">Got a friend request from the account that is still pending.</param>
/// <param name="IsClubMember">In the account's club, by the last roster.</param>
/// <param name="HasApplied">Has a pending application to the club.</param>
public sealed record AutomationPlayer(
    string Id,
    string Nick,
    PlayerProfile? Profile,
    bool IsFriend,
    bool HasRequested,
    bool WasRequested,
    bool IsClubMember,
    bool HasApplied);

/// <summary>
/// Builds the variables scripts see. Everything is copied into plain Scriban objects, so scripts
/// can't reach a .NET object, and names follow Scriban's snake_case.
/// </summary>
public static class AutomationVariables
{
    private const string ProfileBaseUrl = "https://www.geoguessr.com/user/";

    public static ScriptObject Build(
        AutomationRule rule,
        AutomationRun run,
        AutomationPlayer? player,
        AccountContext account,
        DateTimeOffset now,
        ScriptObject? sources = null)
    {
        var data = ParseEvent(run.EventJson);
        data["trigger"] = run.Trigger;
        data["occurred_at"] = run.OccurredAt.UtcDateTime;

        return new ScriptObject
        {
            ["event"] = data,
            ["player"] = player is null ? null : Player(player, now),
            ["rule"] = new ScriptObject { ["name"] = rule.Name, ["description"] = rule.Description },
            ["run"] = new ScriptObject
            {
                ["id"] = run.Id,
                ["trigger"] = run.Trigger,
                ["depth"] = run.Depth,
                ["dry_run"] = run.DryRun,
                ["manual"] = run.Manual
            },
            ["account"] = new ScriptObject { ["id"] = account.UserId, ["club_id"] = account.ClubId },
            ["sources"] = sources ?? [],
            ["now"] = now.UtcDateTime
        };
    }

    public static string ProfileUrl(string userId) => ProfileBaseUrl + Uri.EscapeDataString(userId);

    private static ScriptObject Player(AutomationPlayer player, DateTimeOffset now)
    {
        var result = new ScriptObject
        {
            ["id"] = player.Id,
            ["nick"] = player.Nick,
            ["profile_url"] = ProfileUrl(player.Id),
            ["is_friend"] = player.IsFriend,
            ["has_requested"] = player.HasRequested,
            ["was_requested"] = player.WasRequested,
            ["is_club_member"] = player.IsClubMember,
            ["has_applied"] = player.HasApplied,
            ["found"] = player.Profile is not null
        };

        if (player.Profile is not { } profile)
        {
            return result;
        }

        result["country"] = profile.CountryCode;
        result["level"] = profile.Level;
        result["is_pro"] = profile.IsPro;
        result["account_type"] = profile.AccountType;
        result["created_at"] = profile.CreatedAt?.UtcDateTime;
        result["account_age_days"] = profile.CreatedAt is { } created ? (int)(now - created).TotalDays : null;
        result["banned"] = profile.Standing.IsBanned;
        result["chat_banned"] = profile.Standing.IsChatBanned;
        result["suspended_until"] = profile.Standing.SuspendedUntil?.UtcDateTime;
        result["club"] = profile.Club is { } club
            ? new ScriptObject { ["id"] = club.ClubId, ["tag"] = club.Tag, ["name"] = club.Name, ["level"] = club.Level }
            : null;
        result["ranked"] = profile.Ranked is { } ranked
            ? new ScriptObject
            {
                ["division"] = ranked.Division,
                ["rating"] = ranked.Rating,
                ["peak_rating"] = ranked.PeakRating,
                ["recent_wins"] = ranked.RecentGames.Count(won => won),
                ["recent_games"] = ranked.RecentGames.Count
            }
            : null;
        return result;
    }

    /// <summary>Copies JSON into plain Scriban objects, such as a section read from a source.</summary>
    public static ScriptObject FromJson(JsonObject json) => ToScript(json);

    private static ScriptObject ParseEvent(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonObject root ? ToScript(root) : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ScriptObject ToScript(JsonObject node)
    {
        var result = new ScriptObject();
        foreach (var (key, value) in node)
        {
            result[key] = ToValue(key, value);
        }

        return result;
    }

    private static object? ToValue(string key, JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => ToScript(obj),
        JsonArray array => new ScriptArray(array.Select(item => ToValue(key, item))),
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        JsonValue value when value.TryGetValue<long>(out var number) => number,
        JsonValue value when value.TryGetValue<double>(out var real) => real,
        JsonValue value when value.TryGetValue<string>(out var text) => key.EndsWith("_at", StringComparison.Ordinal)
                                                                      && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.UtcDateTime
            : text,
        _ => node.ToJsonString()
    };
}
