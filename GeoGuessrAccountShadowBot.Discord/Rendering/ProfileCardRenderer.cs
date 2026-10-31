// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;
using GeoGuessrAccountShadowBot.Domain.Model;

namespace GeoGuessrAccountShadowBot.Discord.Rendering;

/// <summary>
/// Renders a player's public profile. Everything here comes from GeoGuessr and is escaped, because
/// a nickname or club name can be anything its owner wants.
/// </summary>
internal static partial class ProfileCardRenderer
{
    private const string ImageBaseUrl = "https://www.geoguessr.com/images/resize:fit:192:0/gravity:ce/plain/";
    private const string ClubBaseUrl = "https://www.geoguessr.com/clubs/";
    private const string Missing = "–";
    private const string Author = "New friend request";

    private const int MaxTitleLength = 256;
    private const int RegionalIndicatorOffset = 0x1F1E6 - 'A';

    public static RenderedCard Render(PlayerProfile profile, DateTimeOffset now)
    {
        var fields = new List<RenderedField>
        {
            new("Member since", profile.CreatedAt is { } created ? $"<t:{created.ToUnixTimeSeconds()}:D>" : Missing),
            new("Level", profile.Level?.ToString(CultureInfo.InvariantCulture) ?? Missing),
            new("Type", FormatType(profile)),
            new("Club", FormatClub(profile.Club)),
            FormatRating(profile.Ranked),
            new("Standing", FormatStanding(profile.Standing, now))
        };

        if (profile.Ranked is { Division: { Length: > 0 } division })
        {
            fields.Add(new RenderedField("Division", UntrustedMarkdown.Escape(division)));
        }

        if (profile.Ranked is { RecentGames.Count: > 0 } ranked)
        {
            fields.Add(new RenderedField("Recent games", string.Concat(ranked.RecentGames.Select(won => won ? "🟩" : "🟥")), IsInline: false));
        }

        return new RenderedCard(FormatTitle(profile), null, CardRenderer.InfoColor, null)
        {
            Author = Author,
            Url = CardRenderer.ProfileUrl(profile.UserId),
            ThumbnailUrl = ImageUrl(profile.ImagePath),
            Fields = fields
        };
    }

    /// <summary>
    /// Builds the picture's address, or nothing if the path looks unlike one GeoGuessr gives out.
    /// Discord refuses a card with a malformed address, and a picture isn't worth losing a card.
    /// </summary>
    public static string? ImageUrl(string? path) =>
        path is not null && ImagePathRegex().IsMatch(path) ? ImageBaseUrl + path : null;

    /// <summary>Turns a two-letter country code into its flag, which Discord shows as an emoji.</summary>
    public static string? Flag(string? countryCode)
    {
        if (countryCode is not { Length: 2 } || !countryCode.All(char.IsAsciiLetter))
        {
            return null;
        }

        return string.Concat(countryCode.ToUpperInvariant().Select(letter => char.ConvertFromUtf32(letter + RegionalIndicatorOffset)));
    }

    private static string FormatTitle(PlayerProfile profile)
    {
        var name = UntrustedMarkdown.Escape(profile.Nick);
        var title = Flag(profile.CountryCode) is { } flag ? $"{flag} {name}" : name;

        return title.Length <= MaxTitleLength ? title : MessageChunker.Chunk(title, MaxTitleLength)[0];
    }

    private static string FormatType(PlayerProfile profile)
    {
        var type = string.IsNullOrWhiteSpace(profile.AccountType) ? Missing : UntrustedMarkdown.Escape(profile.AccountType);
        return profile.IsPro ? $"{type} (Pro)" : type;
    }

    private static string FormatClub(PlayerClub? club)
    {
        if (club is null)
        {
            return "None";
        }

        var label = UntrustedMarkdown.Escape(club.Name ?? club.Tag);
        var url = ClubBaseUrl + Uri.EscapeDataString(club.ClubId);
        return $"[{label}]({url}) (level {club.Level.ToString(CultureInfo.InvariantCulture)})";
    }

    private static RenderedField FormatRating(PlayerRanked? ranked)
    {
        if (ranked?.Rating is { } rating)
        {
            return new RenderedField("Rating", rating.ToString(CultureInfo.InvariantCulture));
        }

        return new RenderedField("Peak rating", ranked?.PeakRating?.ToString(CultureInfo.InvariantCulture) ?? "Unranked");
    }

    private static string FormatStanding(PlayerStanding standing, DateTimeOffset now)
    {
        if (standing.IsBanned)
        {
            return "⛔ Banned";
        }

        if (standing.SuspendedUntil is { } until && until > now)
        {
            return $"⛔ Suspended until <t:{until.ToUnixTimeSeconds()}:f>";
        }

        return standing.IsChatBanned ? "⚠️ Chat banned" : "Good standing";
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+(/[A-Za-z0-9_-]+)*\.(png|jpe?g|webp)$")]
    private static partial Regex ImagePathRegex();
}
