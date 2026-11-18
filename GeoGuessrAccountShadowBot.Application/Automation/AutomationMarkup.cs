// SPDX-License-Identifier: GPL-2.0-or-later

namespace GeoGuessrAccountShadowBot.Application.Automation;

/// <summary>
/// Marks the parts of automation text that came from outside the rule, such as a nickname or a
/// message. Rule authors may write Discord markdown, but the values put into their templates must
/// stay inert, so the Discord layer escapes every marked span and keeps the rest as written.
/// </summary>
public static class AutomationMarkup
{
    /// <summary>Private use characters, which can't come from GeoGuessr because they're stripped.</summary>
    public const char Open = '';

    public const char Close = '';

    /// <summary>Marks a value as untrusted.</summary>
    public static string Untrusted(string? text) => $"{Open}{Strip(text ?? "")}{Close}";

    /// <summary>Removes the markers, for text that goes to GeoGuessr or into a log.</summary>
    public static string Strip(string text) =>
        text.Contains(Open, StringComparison.Ordinal) || text.Contains(Close, StringComparison.Ordinal)
            ? text.Replace(Open.ToString(), "", StringComparison.Ordinal).Replace(Close.ToString(), "", StringComparison.Ordinal)
            : text;
}
