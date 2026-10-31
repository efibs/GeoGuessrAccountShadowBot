// SPDX-License-Identifier: GPL-2.0-or-later

namespace GeoGuessrAccountShadowBot.Domain.Events;

/// <summary>
/// A reconciliation scope finished its first run, so later runs may notify. It travels through the
/// pipeline behind the items it follows, which guarantees they were committed first.
/// </summary>
public sealed record BaselineCompleted(string Scope, int RecordedItems) : ShadowEvent;

/// <summary>
/// Something an operator should hear about, such as the socket going down or the token expiring.
/// </summary>
public sealed record OperatorAlertRaised(string Title, string Detail, bool Ping) : ShadowEvent;
