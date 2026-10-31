// SPDX-License-Identifier: GPL-2.0-or-later

namespace GeoGuessrAccountShadowBot.Domain.Model;

/// <summary>
/// Text controlled by another GeoGuessr user, such as a nickname or chat message. The wrapper keeps
/// it from being treated as a plain string, so it can't reach Discord without being escaped.
/// </summary>
public readonly record struct UntrustedText(string Value)
{
    public override string ToString() => Value;
}
