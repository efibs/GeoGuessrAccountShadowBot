// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Socket;

/// <summary>
/// The topics the bot uses. The inbox subscription uses the account's own id, but sending a direct
/// message addresses the partner's id.
/// </summary>
internal static class SocketTopics
{
    public static string Self(string userId) => $"self:{userId}";

    public static string FriendChat(string userId) => $"chat:Friend:TextMessages:{userId}";

    public static string ClubChat(string clubId) => $"chat:Club:TextMessages:{clubId}";

    public static string Club(string clubId) => $"club:{clubId}";
}

/// <summary>
/// Builds the frames the bot sends.
/// </summary>
internal static class OutboundFrames
{
    private const string Client = "web";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string HeartBeat { get; } = JsonSerializer.Serialize(new Frame("HeartBeat", null, null, null), SerializerOptions);

    public static string Subscribe(string topic) => JsonSerializer.Serialize(new Frame("Subscribe", topic, null, Client), SerializerOptions);

    public static string ChatMessage(string topic, string text) => JsonSerializer.Serialize(new Frame("ChatMessage", topic, text, Client), SerializerOptions);

    private sealed record Frame(string Code, string? Topic, string? Payload, string? Client);
}
