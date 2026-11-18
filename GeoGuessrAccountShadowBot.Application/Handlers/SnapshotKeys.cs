// SPDX-License-Identifier: GPL-2.0-or-later

namespace GeoGuessrAccountShadowBot.Application.Handlers;

/// <summary>
/// Where the snapshot handlers keep their last snapshot. Automations read them to know who is a
/// friend or a club member without asking GeoGuessr.
/// </summary>
public static class SnapshotKeys
{
    public const string Friendships = "friendships";
    public const string ClubRoster = "club-roster";
    public const string ClubApplications = "club-applications";
}
