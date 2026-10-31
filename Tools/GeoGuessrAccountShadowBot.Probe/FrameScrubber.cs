// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GeoGuessrAccountShadowBot.Probe;

/// <summary>
/// Turns captured frames into data that is safe to commit as a test fixture. It fails closed: a
/// string survives only if its key is on the allow-list of protocol values, so a field nobody
/// thought about is redacted rather than leaked. Ids become stable fakes, so that frames from one
/// capture still refer to each other consistently.
/// </summary>
internal sealed partial class FrameScrubber
{
    private const string RedactedValue = "<redacted>";

    /// <summary>Keys whose string values are protocol constants, not anyone's data.</summary>
    private static readonly HashSet<string> ProtocolValueKeys =
        ["code", "payloadType", "sourceType", "recipientType", "context", "level", "activityType", "platform"];

    /// <summary>Topic segments that name a channel kind. Any other segment is treated as an id.</summary>
    private static readonly HashSet<string> TopicConstants =
        ["self", "chat", "club", "partyv2", "community-parties", "Friend", "Club", "Party", "InGame", "PostGame",
         "None", "EmotesOnly", "TextMessages", "TeamTextMessages"];

    private readonly Dictionary<string, string> _fakeUserIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fakeMessageIds = new(StringComparer.Ordinal);
    private int _timestampCount;

    /// <summary>Scrubs one frame, including a string-encoded JSON payload.</summary>
    public JsonNode Scrub(string frameJson)
    {
        var frame = JsonNode.Parse(frameJson) ?? throw new JsonException("The frame is null.");
        return ScrubNode(frame, null) ?? throw new JsonException("The frame is null.");
    }

    private JsonNode? ScrubNode(JsonNode? node, string? key) => node switch
    {
        JsonObject value => ScrubObject(value),
        JsonArray value => new JsonArray(value.Select(item => ScrubNode(item, key)).ToArray()),
        JsonValue value => ScrubValue(value, key),
        _ => null
    };

    private JsonObject ScrubObject(JsonObject source)
    {
        var result = new JsonObject();
        foreach (var (key, value) in source)
        {
            // Dictionaries keyed by id would otherwise leak those ids through their keys.
            result[ScrubKey(key, result.Count)] = ScrubNode(value, key);
        }

        return result;
    }

    private JsonNode? ScrubValue(JsonValue value, string? key)
    {
        if (value.TryGetValue<bool>(out var flag))
        {
            return JsonValue.Create(flag);
        }

        if (!value.TryGetValue<string>(out var text))
        {
            // Numbers are counters or enums at best, and scores or ratings at worst.
            return key == "type" ? value.DeepClone() : JsonValue.Create(0);
        }

        return key switch
        {
            "payload" => ScrubPayload(text),
            "topic" or "roomId" => JsonValue.Create(ScrubTopic(text)),
            "id" or "targetMessageId" when !UserIdRegex().IsMatch(text) => JsonValue.Create(FakeMessageId(text)),
            not null when ProtocolValueKeys.Contains(key) => JsonValue.Create(text),
            _ when UserIdRegex().IsMatch(text) => JsonValue.Create(FakeUserId(text)),
            _ when TimestampRegex().IsMatch(text) => JsonValue.Create(NextTimestamp()),
            _ => JsonValue.Create(RedactedValue)
        };
    }

    private JsonNode? ScrubPayload(string text)
    {
        try
        {
            var parsed = JsonNode.Parse(text);
            return JsonValue.Create(ScrubNode(parsed, null)?.ToJsonString() ?? "null");
        }
        catch (JsonException)
        {
            return JsonValue.Create(RedactedValue);
        }
    }

    private string ScrubKey(string key, int position)
    {
        if (UserIdRegex().IsMatch(key))
        {
            return FakeUserId(key);
        }

        return FieldNameRegex().IsMatch(key) ? key : $"key{position}";
    }

    private string ScrubTopic(string topic) => string.Join(':', topic.Split(':').Select(segment =>
    {
        if (TopicConstants.Contains(segment))
        {
            return segment;
        }

        return UserIdRegex().IsMatch(segment) ? FakeUserId(segment) : RedactedValue;
    }));

    private string FakeUserId(string id)
    {
        if (!_fakeUserIds.TryGetValue(id, out var fake))
        {
            fake = (_fakeUserIds.Count + 1).ToString("x24", CultureInfo.InvariantCulture);
            _fakeUserIds[id] = fake;
        }

        return fake;
    }

    private string FakeMessageId(string id)
    {
        if (!_fakeMessageIds.TryGetValue(id, out var fake))
        {
            fake = $"m{_fakeMessageIds.Count + 1:D9}";
            _fakeMessageIds[id] = fake;
        }

        return fake;
    }

    private string NextTimestamp() =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(++_timestampCount)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    [GeneratedRegex("^[0-9a-f]{24}$")]
    private static partial Regex UserIdRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex FieldNameRegex();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}")]
    private static partial Regex TimestampRegex();
}
