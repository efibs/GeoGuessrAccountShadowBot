// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Domain.Model;

namespace GeoGuessrAccountShadowBot.Domain.Events;

/// <summary>
/// The ways a club member can change.
/// </summary>
public enum ClubMemberChange
{
    Joined,
    Left,
    RoleChanged
}

/// <summary>
/// A fresh club roster, which its handler compares with the stored one.
/// </summary>
public sealed record ClubRosterFetched(ClubRoster Roster) : ShadowEvent;

/// <summary>
/// A member joined or left the club, or had their role changed.
/// </summary>
public sealed record ClubMemberChanged(ClubMember Member, ClubMemberChange Change) : ShadowEvent
{
    public override string SwitchKey => Change switch
    {
        ClubMemberChange.Joined => EventKeys.ClubMemberJoined,
        ClubMemberChange.Left => EventKeys.ClubMemberLeft,
        _ => EventKeys.ClubRoleChanged
    };
}

/// <summary>
/// The club's mission board moved on. This is only seen live, so it isn't caught up after downtime.
/// </summary>
public sealed record ClubMissionBoardProgressed(string? ContributorUserId, bool BoardCleared) : ShadowEvent
{
    public override string SwitchKey => EventKeys.ClubMissionBoard;
}

/// <summary>
/// The club's pending applications, which their handler compares with the stored ones. Only a
/// member allowed to manage applications can read them.
/// </summary>
public sealed record ClubApplicationsFetched(ClubApplicationSet Applications) : ShadowEvent;

/// <summary>
/// Someone applied to join the club.
/// </summary>
public sealed record ClubApplicationReceived(ClubApplicant Applicant) : ShadowEvent
{
    public override string SwitchKey => EventKeys.ClubApplicationReceived;
}
