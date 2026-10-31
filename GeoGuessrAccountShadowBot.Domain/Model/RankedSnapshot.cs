// SPDX-License-Identifier: GPL-2.0-or-later

namespace GeoGuessrAccountShadowBot.Domain.Model;

/// <summary>
/// The account's competitive standing at one moment. GeoGuessr pushes no ranked events, so changes
/// are found by comparing polled snapshots.
/// </summary>
public sealed record RankedSnapshot(int? DivisionNumber, string? DivisionName, int? Rating, string? SeasonId)
{
    public bool DivisionChangedFrom(RankedSnapshot previous) => DivisionNumber != previous.DivisionNumber;

    /// <summary>A season id going missing is a failed read rather than a new season.</summary>
    public bool SeasonChangedFrom(RankedSnapshot previous) =>
        SeasonId is not null && previous.SeasonId is not null && SeasonId != previous.SeasonId;
}
