// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;
using System.Net;
using GeoGuessrAccountShadowBot.Application;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain;
using GeoGuessrAccountShadowBot.Domain.Model;
using Refit;

namespace GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Http;

/// <summary>
/// Adapts the raw endpoints to the application's view of GeoGuessr. Every call passes through
/// <see cref="CallAsync{T}"/>, so that callers only ever see the two exceptions of the port and
/// nothing above this class has to know about Refit.
/// </summary>
internal sealed class GeoGuessrHttpGateway(IGeoGuessrRestApi api, AccountContext account) : IGeoGuessrApi
{
    private const int MaxSummaryPages = 10;
    private const string RejectedSession = "GeoGuessr rejected the session cookie.";

    public async Task<string> GetOwnUserIdAsync(CancellationToken cancellationToken = default)
    {
        var profile = await CallAsync(() => api.GetProfileAsync(cancellationToken)).ConfigureAwait(false);
        return profile.User.Id;
    }

    public async Task<FriendshipSet> GetFriendshipsAsync(CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(() => api.GetFriendshipsAsync(cancellationToken)).ConfigureAwait(false);
        return new FriendshipSet(
            response.FriendIds.ToImmutableSortedSet(),
            response.ReceivedFriendRequests.ToImmutableSortedSet(),
            response.SentFriendRequests.ToImmutableSortedSet());
    }

    public async Task<IReadOnlyList<AccountNotification>> GetNotificationsAsync(int page, int count, CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(() => api.GetNotificationsAsync(count, page, cancellationToken)).ConfigureAwait(false);
        return response
            .Select(item => new AccountNotification(item.Id, (NotificationType)item.Type, item.SentBy?.Id, item.SentBy?.Nick, item.SentAt))
            .ToArray();
    }

    /// <summary>
    /// Merges the friends summary with the unread list. Messages delivered to a connected socket
    /// don't count as unread, so the unread list alone would miss rooms.
    /// </summary>
    public async Task<IReadOnlyList<ChatRoomSummary>> GetChatRoomsAsync(CancellationToken cancellationToken = default)
    {
        var rooms = new Dictionary<string, ChatRoomSummary>(StringComparer.Ordinal);

        var unreads = await CallAsync(() => api.GetUnreadsAsync(cancellationToken)).ConfigureAwait(false);
        foreach (var (peerId, room) in unreads)
        {
            rooms[peerId] = ToSummary(peerId, room);
        }

        for (var page = 0; page < MaxSummaryPages; page++)
        {
            var currentPage = page;
            var summary = await CallAsync(() => api.GetFriendsSummaryAsync(currentPage, cancellationToken)).ConfigureAwait(false);
            foreach (var room in summary.FriendChatRooms ?? [])
            {
                if (PeerOfRoom(room.RoomId) is { } peerId)
                {
                    rooms[peerId] = ToSummary(peerId, room);
                }
            }

            if (!summary.HasMore)
            {
                break;
            }
        }

        return rooms.Values.ToArray();
    }

    public async Task<IReadOnlyList<ChatMessage>> GetChatHistoryAsync(string peerId, CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(() => api.GetChatRoomAsync(peerId, cancellationToken)).ConfigureAwait(false);
        using (response)
        {
            return response.StatusCode == HttpStatusCode.NotFound ? [] : ToMessages(await ReadAsync(response).ConfigureAwait(false));
        }
    }

    public async Task<string?> GetPlayerNickAsync(string userId, CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(() => api.GetPlayerIdentityAsync(userId, cancellationToken)).ConfigureAwait(false);
        using (response)
        {
            return response.StatusCode == HttpStatusCode.NotFound ? null : (await ReadAsync(response).ConfigureAwait(false))?.Player?.Nick;
        }
    }

    public async Task<PlayerProfile?> GetPlayerProfileAsync(string userId, CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(() => api.GetUserAsync(userId, cancellationToken)).ConfigureAwait(false);
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound || await ReadAsync(response).ConfigureAwait(false) is not { } user)
            {
                return null;
            }

            var club = user.Club is { } userClub
                ? new PlayerClub(userClub.ClubId, userClub.Tag, userClub.Level, await TryReadClubNameAsync(userClub.ClubId, cancellationToken).ConfigureAwait(false))
                : null;

            return new PlayerProfile(
                user.Id,
                user.Nick,
                user.CountryCode,
                user.Created,
                user.Progress?.Level,
                user.Type,
                user.IsProUser,
                string.IsNullOrWhiteSpace(user.CustomImage) ? user.Pin?.Url : user.CustomImage,
                new PlayerStanding(user.IsBanned, user.SuspendedUntil, user.ChatBan),
                club,
                await TryReadRankedAsync(userId, cancellationToken).ConfigureAwait(false));
        }
    }

    public async Task<string?> GetOwnClubIdAsync(CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(() => api.GetOwnClubAsync(cancellationToken)).ConfigureAwait(false);
        using (response)
        {
            return response.StatusCode == HttpStatusCode.NoContent ? null : (await ReadAsync(response).ConfigureAwait(false))?.ClubId;
        }
    }

    public async Task<IReadOnlyList<ClubMember>> GetClubMembersAsync(string clubId, CancellationToken cancellationToken = default)
    {
        var members = await CallAsync(() => api.GetClubMembersAsync(clubId, cancellationToken)).ConfigureAwait(false);
        return members.Select(member => new ClubMember(member.User.UserId, member.User.Nick, member.Role)).ToArray();
    }

    public async Task<IReadOnlyList<ChatMessage>> GetClubChatAsync(string clubId, CancellationToken cancellationToken = default)
    {
        var room = await CallAsync(() => api.GetClubChatAsync(clubId, cancellationToken)).ConfigureAwait(false);
        return ToMessages(room);
    }

    public async Task<RankedSnapshot> GetRankedAsync(string userId, CancellationToken cancellationToken = default)
    {
        var progress = await CallAsync(() => api.GetRankedProgressAsync(userId, cancellationToken)).ConfigureAwait(false);

        var season = await CallAsync(() => api.GetSeasonPassAsync(cancellationToken)).ConfigureAwait(false);
        using (season)
        {
            var seasonId = season.IsSuccessStatusCode ? season.Content?.SeasonId : null;
            return new RankedSnapshot(progress.DivisionNumber, progress.DivisionName, progress.Rating, seasonId);
        }
    }

    public async Task<Result> AcceptFriendRequestAsync(string userId, CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(() => api.AcceptFriendRequestAsync(userId, cancellationToken)).ConfigureAwait(false);
        using (response)
        {
            return ToResult(response, "friend-request.accept_failed");
        }
    }

    public async Task<Result> DeclineFriendRequestAsync(string userId, CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(() => api.DeclineFriendRequestAsync(userId, cancellationToken)).ConfigureAwait(false);
        using (response)
        {
            return ToResult(response, "friend-request.decline_failed");
        }
    }

    /// <summary>Reads an optional detail, for which a 404 or an outage just means it stays empty.</summary>
    private static async Task<T?> TryCallAsync<T>(Func<Task<IApiResponse<T>>> call) where T : class
    {
        try
        {
            var response = await CallAsync(call).ConfigureAwait(false);
            using (response)
            {
                return response.IsSuccessStatusCode ? response.Content : null;
            }
        }
        catch (GeoGuessrUnavailableException)
        {
            return null;
        }
    }

    private async Task<string?> TryReadClubNameAsync(string clubId, CancellationToken cancellationToken)
    {
        var club = await TryCallAsync(() => api.GetClubAsync(clubId, cancellationToken)).ConfigureAwait(false);
        return club?.Name;
    }

    private async Task<PlayerRanked?> TryReadRankedAsync(string userId, CancellationToken cancellationToken)
    {
        var progress = await TryCallAsync(() => api.TryGetRankedProgressAsync(userId, cancellationToken)).ConfigureAwait(false);
        var peak = await TryCallAsync(() => api.GetRankedPeakRatingAsync(userId, cancellationToken)).ConfigureAwait(false);

        return progress is null && peak is null
            ? null
            : new PlayerRanked(progress?.DivisionName, progress?.Rating, peak?.PeakOverallRating, progress?.LatestGames ?? []);
    }

    private static async Task<T> CallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            var result = await call().ConfigureAwait(false);
            if (result is IApiResponse { StatusCode: HttpStatusCode.Unauthorized })
            {
                throw new GeoGuessrUnauthorizedException(RejectedSession);
            }

            return result;
        }
        catch (ApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new GeoGuessrUnauthorizedException(RejectedSession);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or GeoGuessrUnauthorizedException))
        {
            throw new GeoGuessrUnavailableException("GeoGuessr couldn't be reached.", exception);
        }
    }

    private static async Task<T?> ReadAsync<T>(IApiResponse<T> response)
    {
        try
        {
            await response.EnsureSuccessfulAsync().ConfigureAwait(false);
            return response.Content;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new GeoGuessrUnavailableException("GeoGuessr answered with an error.", exception);
        }
    }

    private static Result ToResult(IApiResponse response, string code) => response.IsSuccessStatusCode
        ? Result.Success()
        : new Error(ErrorKind.Unexpected, code, $"GeoGuessr answered with status {(int?)response.StatusCode}.");

    private static ChatRoomSummary ToSummary(string peerId, ChatRoomDto room) =>
        new(peerId, room.MessageCount, ToMessages(room));

    private static ChatMessage[] ToMessages(ChatRoomDto? room) =>
        room?.Messages?.Select(ChatMessageMapper.ToDomain).ToArray() ?? [];

    private string? PeerOfRoom(string? roomId) =>
        roomId?.Split(':').FirstOrDefault(id => id != account.UserId);
}
