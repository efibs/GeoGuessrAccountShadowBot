// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;

namespace GeoGuessrAccountShadowBot.Domain.Model;

/// <summary>
/// One member of the account's club.
/// </summary>
public sealed record ClubMember(string UserId, string Nick, int Role);

/// <summary>
/// The club's members at one moment. Comparing two rosters finds joins, leaves and role changes,
/// including those that happened while the bot was down.
/// </summary>
public sealed record ClubRoster(ImmutableSortedDictionary<string, ClubMember> Members)
{
    private const int GlitchGuardMinimumMembers = 5;

    public static ClubRoster From(IEnumerable<ClubMember> members) =>
        new(members.ToImmutableSortedDictionary(member => member.UserId, member => member));

    public ClubRosterDiff DiffFrom(ClubRoster previous)
    {
        var joined = Members.Values.Where(member => !previous.Members.ContainsKey(member.UserId)).ToArray();
        var left = previous.Members.Values.Where(member => !Members.ContainsKey(member.UserId)).ToArray();
        var roleChanged = Members.Values
            .Where(member => previous.Members.TryGetValue(member.UserId, out var before) && before.Role != member.Role)
            .ToArray();

        return new ClubRosterDiff(joined, left, roleChanged);
    }

    /// <summary>Whether this roster lost so many members at once that it's more likely a bad response.</summary>
    public bool LooksLikeGlitchComparedTo(ClubRoster previous)
    {
        if (previous.Members.Count < GlitchGuardMinimumMembers)
        {
            return false;
        }

        var remaining = previous.Members.Keys.Count(Members.ContainsKey);
        return Members.Count == 0 || remaining < previous.Members.Count / 2;
    }
}

/// <summary>
/// The changes between two club rosters.
/// </summary>
public sealed record ClubRosterDiff(
    IReadOnlyList<ClubMember> Joined,
    IReadOnlyList<ClubMember> Left,
    IReadOnlyList<ClubMember> RoleChanged);
