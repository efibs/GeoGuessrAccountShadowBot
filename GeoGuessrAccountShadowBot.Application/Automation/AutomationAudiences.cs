// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Handlers;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Model;

namespace GeoGuessrAccountShadowBot.Application.Automation;

/// <summary>How a player relates to the account, by the last snapshots, which costs no GeoGuessr call.</summary>
public sealed record PlayerRelations(bool IsFriend, bool HasRequested, bool WasRequested, bool IsClubMember, bool HasApplied)
{
    public static async Task<PlayerRelations> ReadAsync(IShadowStore store, string userId, CancellationToken cancellationToken)
    {
        var friendships = await store.ReadStateAsync<FriendshipSet>(SnapshotKeys.Friendships, cancellationToken).ConfigureAwait(false);
        var roster = await store.ReadStateAsync<ClubRoster>(SnapshotKeys.ClubRoster, cancellationToken).ConfigureAwait(false);
        var applications = await store.ReadStateAsync<ClubApplicationSet>(SnapshotKeys.ClubApplications, cancellationToken).ConfigureAwait(false);

        return new PlayerRelations(
            friendships?.Friends.Contains(userId) ?? false,
            friendships?.Incoming.Contains(userId) ?? false,
            friendships?.Outgoing.Contains(userId) ?? false,
            roster?.Members.ContainsKey(userId) ?? false,
            applications?.Pending.ContainsKey(userId) ?? false);
    }
}

/// <summary>The players in each group a Raise can be sent for, by the last snapshots.</summary>
public static class AutomationAudiences
{
    public static async Task<IReadOnlyList<string>> ReadAsync(IShadowStore store, AutomationAudience audience, string ownUserId, CancellationToken cancellationToken)
    {
        IEnumerable<string> ids = audience switch
        {
            AutomationAudience.Friends =>
                (await store.ReadStateAsync<FriendshipSet>(SnapshotKeys.Friendships, cancellationToken).ConfigureAwait(false))?.Friends ?? [],
            AutomationAudience.FriendRequests =>
                (await store.ReadStateAsync<FriendshipSet>(SnapshotKeys.Friendships, cancellationToken).ConfigureAwait(false))?.Incoming ?? [],
            AutomationAudience.ClubMembers =>
                (await store.ReadStateAsync<ClubRoster>(SnapshotKeys.ClubRoster, cancellationToken).ConfigureAwait(false))?.Members.Keys ?? [],
            _ =>
                (await store.ReadStateAsync<ClubApplicationSet>(SnapshotKeys.ClubApplications, cancellationToken).ConfigureAwait(false))?.Pending.Keys ?? []
        };

        return ids.Where(id => id != ownUserId).ToArray();
    }
}
