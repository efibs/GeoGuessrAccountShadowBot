// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;

namespace GeoGuessrAccountShadowBot.Domain.Model;

/// <summary>
/// Someone waiting for the club to answer their application.
/// </summary>
public sealed record ClubApplicant(string UserId, string Nick);

/// <summary>
/// The pending applications to the club at one moment. GeoGuessr sends no frame for a new
/// application, so comparing two of these is how the bot learns about one.
/// </summary>
public sealed record ClubApplicationSet(ImmutableSortedDictionary<string, ClubApplicant> Pending)
{
    public static ClubApplicationSet From(IEnumerable<ClubApplicant> applicants) =>
        new(applicants.DistinctBy(applicant => applicant.UserId).ToImmutableSortedDictionary(applicant => applicant.UserId, applicant => applicant));

    /// <summary>The applications that weren't pending in the earlier snapshot.</summary>
    public IReadOnlyList<ClubApplicant> NewSince(ClubApplicationSet previous) =>
        Pending.Values.Where(applicant => !previous.Pending.ContainsKey(applicant.UserId)).ToArray();
}
