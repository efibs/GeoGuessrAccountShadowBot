// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;

namespace GeoGuessrAccountShadowBot.Domain.Model;

/// <summary>
/// The friend state of the account at one moment. GeoGuessr's friend frames carry no payload, so
/// comparing two of these is how the bot learns what changed.
/// </summary>
public sealed record FriendshipSet(
    ImmutableSortedSet<string> Friends,
    ImmutableSortedSet<string> Incoming,
    ImmutableSortedSet<string> Outgoing)
{
    private const int GlitchGuardMinimumFriends = 5;

    /// <summary>What changed between an earlier snapshot and this one.</summary>
    public FriendshipDiff DiffFrom(FriendshipSet previous)
    {
        var newFriends = Friends.Except(previous.Friends);

        // A friend who was never seen as a request was requested and accepted during downtime.
        var accepted = newFriends.Except(previous.Incoming);

        return new FriendshipDiff(
            Incoming.Except(previous.Incoming).ToArray(),
            accepted.ToArray(),
            previous.Incoming.Except(Incoming).ToArray());
    }

    /// <summary>
    /// Whether this snapshot lost so many friends at once that it's more likely a bad response than
    /// a real change. Acting on one would produce a burst of false notifications.
    /// </summary>
    public bool LooksLikeGlitchComparedTo(FriendshipSet previous)
    {
        if (previous.Friends.Count < GlitchGuardMinimumFriends)
        {
            return false;
        }

        return Friends.Count == 0 || previous.Friends.Except(Friends).Count > previous.Friends.Count / 2;
    }
}

/// <summary>
/// The changes between two friendship snapshots.
/// </summary>
/// <param name="NewIncoming">Users who sent the account a request.</param>
/// <param name="Accepted">Users who became friends through a request the account sent.</param>
/// <param name="ResolvedIncoming">Users whose incoming request is gone, accepted or declined.</param>
public sealed record FriendshipDiff(
    IReadOnlyList<string> NewIncoming,
    IReadOnlyList<string> Accepted,
    IReadOnlyList<string> ResolvedIncoming);
