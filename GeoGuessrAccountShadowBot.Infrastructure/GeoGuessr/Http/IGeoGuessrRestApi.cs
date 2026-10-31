// SPDX-License-Identifier: GPL-2.0-or-later

using Refit;

namespace GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Http;

/// <summary>
/// The undocumented GeoGuessr endpoints the bot uses. See Documentation/Protocol.md for which of
/// them were observed live.
/// </summary>
internal interface IGeoGuessrRestApi
{
    [Get("/api/v3/profiles")]
    Task<ProfileResponse> GetProfileAsync(CancellationToken cancellationToken);

    [Get("/api/v3/social/friendships")]
    Task<FriendshipsResponse> GetFriendshipsAsync(CancellationToken cancellationToken);

    [Get("/api/v4/notifications")]
    Task<List<NotificationDto>> GetNotificationsAsync(int count, int page, CancellationToken cancellationToken);

    [Get("/api/v3/social/friends/summary?fast=true")]
    Task<FriendsSummaryResponse> GetFriendsSummaryAsync(int page, CancellationToken cancellationToken);

    [Get("/api/v4/chat/unreads")]
    Task<Dictionary<string, ChatRoomDto>> GetUnreadsAsync(CancellationToken cancellationToken);

    [Get("/api/v4/chat/{peerId}")]
    Task<IApiResponse<ChatRoomDto>> GetChatRoomAsync(string peerId, CancellationToken cancellationToken);

    [Get("/api/v4/player-identities/{userId}")]
    Task<IApiResponse<PlayerIdentityResponse>> GetPlayerIdentityAsync(string userId, CancellationToken cancellationToken);

    [Get("/api/v3/users/{userId}")]
    Task<IApiResponse<UserDto>> GetUserAsync(string userId, CancellationToken cancellationToken);

    [Get("/api/v4/ranked-system/peak-rating/{userId}")]
    Task<IApiResponse<RankedPeakRatingDto>> GetRankedPeakRatingAsync(string userId, CancellationToken cancellationToken);

    [Get("/api/v4/clubs/{clubId}")]
    Task<IApiResponse<ClubDto>> GetClubAsync(string clubId, CancellationToken cancellationToken);

    [Get("/api/v4/clubs/my")]
    Task<IApiResponse<MyClubResponse>> GetOwnClubAsync(CancellationToken cancellationToken);

    [Get("/api/v4/clubs/{clubId}/members")]
    Task<List<ClubMemberDto>> GetClubMembersAsync(string clubId, CancellationToken cancellationToken);

    [Get("/api/v4/clubs/{clubId}/chat")]
    Task<ChatRoomDto> GetClubChatAsync(string clubId, CancellationToken cancellationToken);

    [Get("/api/v4/ranked-system/progress/{userId}")]
    Task<RankedProgressDto> GetRankedProgressAsync(string userId, CancellationToken cancellationToken);

    [Get("/api/v4/ranked-system/progress/{userId}")]
    Task<IApiResponse<RankedProgressDto>> TryGetRankedProgressAsync(string userId, CancellationToken cancellationToken);

    [Get("/api/v4/season-pass/me")]
    Task<IApiResponse<SeasonPassDto>> GetSeasonPassAsync(CancellationToken cancellationToken);

    /// <summary>Accepts an incoming request. POST on the same path would send a new one instead.</summary>
    [Put("/api/v3/social/friends/{userId}?context=")]
    Task<IApiResponse> AcceptFriendRequestAsync(string userId, CancellationToken cancellationToken);

    [Delete("/api/v3/social/friends/{userId}")]
    Task<IApiResponse> DeclineFriendRequestAsync(string userId, CancellationToken cancellationToken);
}
