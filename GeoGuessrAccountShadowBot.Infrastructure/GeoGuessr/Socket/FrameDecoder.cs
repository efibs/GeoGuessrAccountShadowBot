// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Frozen;
using System.Text.Json;
using GeoGuessrAccountShadowBot.Application.Reconciliation;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;
using GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Http;

namespace GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Socket;

/// <summary>What a received frame means to the bot.</summary>
internal abstract record DecodedFrame
{
    /// <summary>The frame carries an event.</summary>
    public sealed record Event(ShadowEvent ShadowEvent) : DecodedFrame;

    /// <summary>Something changed, and HTTP has to say what. These frames carry no details.</summary>
    public sealed record Doorbell(ReconcileScope Scope) : DecodedFrame;

    public sealed record Subscribed(string? Topic) : DecodedFrame;

    public sealed record SubscribeDenied(string? Topic) : DecodedFrame;

    /// <summary>A known frame the bot has no use for.</summary>
    public sealed record Ignored : DecodedFrame;

    /// <summary>A frame nobody has seen yet, logged so that protocol changes get noticed.</summary>
    public sealed record Unknown(string Code) : DecodedFrame;

    public sealed record Malformed : DecodedFrame;
}

/// <summary>
/// Turns socket frames into events. A frame's payload is a JSON document encoded as a string, so it
/// gets parsed a second time. Frames also carry an <c>accessToken</c> field, which is never read.
/// </summary>
internal static class FrameDecoder
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly DecodedFrame Ignored = new DecodedFrame.Ignored();

    private static readonly FrozenDictionary<string, DecodedFrame> Doorbells = new Dictionary<string, DecodedFrame>
    {
        ["NewFriendRequest"] = new DecodedFrame.Doorbell(ReconcileScope.Friends),
        ["FriendsUpdated"] = new DecodedFrame.Doorbell(ReconcileScope.Friends),
        ["NotificationUpdate"] = new DecodedFrame.Doorbell(ReconcileScope.Notifications),
        ["BadgesUnlocked"] = new DecodedFrame.Doorbell(ReconcileScope.Notifications),
        ["ClubMemberJoined"] = new DecodedFrame.Doorbell(ReconcileScope.Club),
        ["ClubMemberLeft"] = new DecodedFrame.Doorbell(ReconcileScope.Club),
        ["ClubMemberRoleChanged"] = new DecodedFrame.Doorbell(ReconcileScope.Club)
    }.ToFrozenDictionary();

    private static readonly FrozenSet<string> IgnoredCodes = FrozenSet.ToFrozenSet(
    [
        "AccountUpdate", "ClubUpdated", "ClubMissionBoardUpdated", "ClubMemberCameOnline", "ClubMemberWentOffline",
        "ClubMemberActivityChanged", "PollUpdated", "PollDeleted", "FeedReactionAdded"
    ]);

    public static DecodedFrame Decode(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var codeElement) || codeElement.GetString() is not { } code)
            {
                return new DecodedFrame.Malformed();
            }

            var topic = root.TryGetProperty("topic", out var topicElement) ? topicElement.GetString() : null;
            var payload = root.TryGetProperty("payload", out var payloadElement) && payloadElement.ValueKind == JsonValueKind.String
                ? payloadElement.GetString()
                : null;

            if (Doorbells.TryGetValue(code, out var doorbell))
            {
                return doorbell;
            }

            return code switch
            {
                "ChatMessage" => DecodeChatMessage(topic, payload),
                "FriendCameOnline" => DecodePresence(payload, true),
                "FriendWentOffline" => DecodePresence(payload, false),
                "MissionBoardProgressed" => DecodeMissionBoard(payload),
                "Subscribed" => new DecodedFrame.Subscribed(topic),
                "SubscribeDenied" => new DecodedFrame.SubscribeDenied(topic),
                _ => IgnoredCodes.Contains(code) ? Ignored : new DecodedFrame.Unknown(code)
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or NullReferenceException)
        {
            return new DecodedFrame.Malformed();
        }
    }

    private static DecodedFrame DecodeChatMessage(string? topic, string? payload)
    {
        var dto = Deserialize<ChatMessageDto>(payload);
        var message = ChatMessageMapper.ToDomain(dto);

        // The topic is chat:<context>:<level>:<id>, and the id is the club for a club chat.
        var parts = topic?.Split(':') ?? [];
        return parts is ["chat", "Friend", ..]
            ? new DecodedFrame.Event(new DirectMessageReceived(message))
            : parts is ["chat", "Club", _, var clubId, ..]
                ? new DecodedFrame.Event(new ClubChatMessageReceived(clubId, message))
                : Ignored;
    }

    private static DecodedFrame DecodePresence(string? payload, bool isOnline)
    {
        var dto = Deserialize<PresencePayload>(payload);
        return new DecodedFrame.Event(new FriendPresenceChanged(dto.FriendId, isOnline, null));
    }

    private static DecodedFrame DecodeMissionBoard(string? payload)
    {
        var dto = Deserialize<MissionBoardPayload>(payload);
        return new DecodedFrame.Event(new ClubMissionBoardProgressed(dto.ContributorUserId, dto.BoardCleared));
    }

    private static T Deserialize<T>(string? payload) where T : class =>
        JsonSerializer.Deserialize<T>(payload ?? throw new JsonException("The frame has no payload."), SerializerOptions)
        ?? throw new JsonException("The payload is null.");

    private sealed record PresencePayload(string FriendId);

    private sealed record MissionBoardPayload(string? ContributorUserId, bool BoardCleared);
}
