// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Domain.Model;

namespace GeoGuessrAccountShadowBot.Discord.Rendering;

/// <summary>
/// Builds a forum post title from a GeoGuessr name. Titles don't render markdown, but they do have
/// to be non-empty, single-line and at most 100 characters.
/// </summary>
public static class ForumTitle
{
    private const int MaxLength = 100;

    public static string Create(UntrustedText nick, string userId)
    {
        var words = UntrustedMarkdown.Clean(nick.Value).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var title = string.Join(' ', words);

        if (title.Length == 0)
        {
            return PlayerDirectory.Fallback(userId).Value;
        }

        return title.Length <= MaxLength ? title : title[..MaxLength].TrimEnd();
    }
}
