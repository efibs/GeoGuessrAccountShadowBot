// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Domain.Model;
using GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Http;

namespace GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr;

/// <summary>
/// Maps GeoGuessr's chat message shape, which the socket and the HTTP history share.
/// </summary>
internal static class ChatMessageMapper
{
    private const string SystemSource = "System";

    public static ChatMessage ToDomain(ChatMessageDto dto) => new(
        dto.Id,
        Enum.TryParse<ChatPayloadType>(dto.PayloadType, ignoreCase: true, out var type) ? type : ChatPayloadType.Unknown,
        dto.TextPayload,
        dto.SourceId,
        dto.RecipientId,
        dto.SourceType == SystemSource,
        dto.SentAt,
        dto.RoomId);
}
