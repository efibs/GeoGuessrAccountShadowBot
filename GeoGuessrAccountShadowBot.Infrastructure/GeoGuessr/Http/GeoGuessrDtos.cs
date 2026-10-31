// SPDX-License-Identifier: GPL-2.0-or-later

namespace GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Http;

// Only the fields the bot uses are declared. Responses also carry things it must never hold on to,
// such as club join tokens and the account's device token.

internal sealed record ProfileResponse(ProfileUser User);

internal sealed record ProfileUser(string Id);

internal sealed record FriendshipsResponse(
    List<string> FriendIds,
    List<string> ReceivedFriendRequests,
    List<string> SentFriendRequests);

internal sealed record NotificationDto(string Id, int Type, NotificationSenderDto? SentBy, DateTimeOffset SentAt);

internal sealed record NotificationSenderDto(string? Id, string? Nick);

internal sealed record FriendsSummaryResponse(bool HasMore, List<ChatRoomDto>? FriendChatRooms);

internal sealed record ChatRoomDto(string? RoomId, int MessageCount, List<ChatMessageDto>? Messages);

internal sealed record ChatMessageDto(
    string Id,
    string? PayloadType,
    string? TextPayload,
    string? SourceType,
    string SourceId,
    string RecipientId,
    DateTimeOffset SentAt,
    string RoomId);

internal sealed record PlayerIdentityResponse(PlayerIdentityDto? Player);

internal sealed record PlayerIdentityDto(string? Nick);

internal sealed record MyClubResponse(string? ClubId);

internal sealed record ClubMemberDto(ClubMemberUserDto User, int Role);

internal sealed record ClubMemberUserDto(string UserId, string Nick);

internal sealed record RankedProgressDto(int? DivisionNumber, string? DivisionName, int? Rating);

internal sealed record SeasonPassDto(string? SeasonId);
