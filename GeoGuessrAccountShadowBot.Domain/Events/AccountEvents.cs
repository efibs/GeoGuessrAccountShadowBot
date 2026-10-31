// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Domain.Model;

namespace GeoGuessrAccountShadowBot.Domain.Events;

/// <summary>
/// An entry from the account's notification list. Which switch applies depends on the notification
/// type, so one record serves every kind of invite and account event.
/// </summary>
public sealed record AccountNotificationReceived(AccountNotification Notification, string? NotificationSwitchKey) : ShadowEvent
{
    public override string DedupeKey => $"notif:{Notification.Id}";

    public override string? SwitchKey => NotificationSwitchKey;
}

/// <summary>
/// A fresh ranked snapshot, which its handler compares with the stored one.
/// </summary>
public sealed record RankedFetched(RankedSnapshot Snapshot) : ShadowEvent;

/// <summary>
/// The account moved to another ranked division.
/// </summary>
public sealed record RankedDivisionChanged(RankedSnapshot Previous, RankedSnapshot Current) : ShadowEvent
{
    public override string SwitchKey => EventKeys.RankedDivisionChanged;
}

/// <summary>
/// A new competitive season began.
/// </summary>
public sealed record SeasonChanged(RankedSnapshot Current) : ShadowEvent
{
    public override string SwitchKey => EventKeys.SeasonChanged;
}
