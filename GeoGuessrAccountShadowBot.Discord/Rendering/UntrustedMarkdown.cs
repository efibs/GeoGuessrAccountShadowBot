// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using GeoGuessrAccountShadowBot.Domain.Model;

namespace GeoGuessrAccountShadowBot.Discord.Rendering;

/// <summary>
/// Makes text from GeoGuessr inert in Discord. Discord.Net's <c>Format.Sanitize</c> isn't enough,
/// because it leaves headings, list markers, masked links, mentions and timestamps working. This is
/// one pass over the text rather than a second layer, so nothing gets escaped twice.
/// </summary>
public static class UntrustedMarkdown
{
    private const string EscapedCharacters = "\\*_~`|>:/.<[]()@#-!+=";

    public static string Escape(UntrustedText text) => Escape(text.Value);

    public static string Escape(string text)
    {
        var result = new StringBuilder(text.Length + 16);

        foreach (var character in Clean(text))
        {
            if (EscapedCharacters.Contains(character))
            {
                result.Append('\\');
            }

            result.Append(character);
        }

        return result.ToString();
    }

    /// <summary>
    /// Removes control characters, and the invisible direction overrides that can make a message
    /// read differently from what it says. Line breaks stay.
    /// </summary>
    public static string Clean(string text)
    {
        var result = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            if (character is '\n' or '\t' || !(char.IsControl(character) || IsDirectionControl(character)))
            {
                result.Append(character);
            }
        }

        return result.ToString();
    }

    private static bool IsDirectionControl(char character) => character is
        '\u061C' or '\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');
}
