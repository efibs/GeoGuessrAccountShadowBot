// SPDX-License-Identifier: GPL-2.0-or-later

using Discord;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Domain.Model;

namespace GeoGuessrAccountShadowBot.Discord.Rendering;

/// <summary>
/// The text of a card, kept apart from Discord's embed type so that it can be snapshot tested.
/// </summary>
public sealed record RenderedCard(string Title, string? Description, uint Color, DateTimeOffset? Timestamp)
{
    /// <summary>A line above the title, saying what the card is about.</summary>
    public string? Author { get; init; }

    /// <summary>Where the title links to.</summary>
    public string? Url { get; init; }

    public string? ThumbnailUrl { get; init; }

    public IReadOnlyList<RenderedField> Fields { get; init; } = [];

    public Embed ToEmbed()
    {
        var builder = new EmbedBuilder().WithTitle(Title).WithColor(new Color(Color));

        if (Author is not null)
        {
            builder.WithAuthor(Author);
        }

        if (Url is not null)
        {
            builder.WithUrl(Url);
        }

        if (ThumbnailUrl is not null)
        {
            builder.WithThumbnailUrl(ThumbnailUrl);
        }

        foreach (var field in Fields)
        {
            builder.AddField(field.Name, field.Value, field.IsInline);
        }

        if (Description is not null)
        {
            builder.WithDescription(Description);
        }

        if (Timestamp is { } timestamp)
        {
            builder.WithTimestamp(timestamp);
        }

        return builder.Build();
    }
}

/// <summary>
/// One labelled value on a card.
/// </summary>
public sealed record RenderedField(string Name, string Value, bool IsInline = true);

/// <summary>
/// Renders outbox messages. These are pure functions, and they're the only place GeoGuessr text
/// turns into Discord text.
/// </summary>
public static class CardRenderer
{
    private const int MaxDescriptionLength = 3900;

    public const uint InfoColor = 0x5865F2;

    private const string ProfileBaseUrl = "https://www.geoguessr.com/user/";

    private const uint SuccessColor = 0x57F287;
    private const uint WarningColor = 0xFEE75C;
    private const uint DangerColor = 0xED4245;

    public static RenderedCard Render(EventCard card, UntrustedText? actorNick)
    {
        var lines = new List<string>();

        if (actorNick is { } nick)
        {
            lines.Add($"**Player:** {UntrustedMarkdown.Escape(nick)}");
        }

        if (card.Detail is not null)
        {
            lines.Add(card.Detail);
        }

        if (card.Body is { } body)
        {
            lines.Add(UntrustedMarkdown.Escape(body));
        }

        var description = lines.Count == 0 ? null : Truncate(string.Join('\n', lines));
        return new RenderedCard(card.Title, description, ColorOf(card.Style), card.OccurredAt);
    }

    public static RenderedCard RenderFriendRequest(UntrustedText nick) =>
        new("New friend request", $"**Player:** {UntrustedMarkdown.Escape(nick)}", InfoColor, null);

    /// <summary>A card with enough about the sender to decide on their request.</summary>
    public static RenderedCard RenderFriendRequest(PlayerProfile profile, DateTimeOffset now) => ProfileCardRenderer.Render(profile, now);

    /// <summary>
    /// Closes a card, keeping the profile details on it. They were true when the decision was made,
    /// so nothing has to be looked up again.
    /// </summary>
    public static Embed CloseFriendRequest(IEmbed card, bool becameFriend) => card.ToEmbedBuilder()
        .WithAuthor(becameFriend ? "Friend request accepted" : "Friend request declined")
        .WithColor(new Color(becameFriend ? SuccessColor : WarningColor))
        .Build();

    public static RenderedCard RenderFriendRequestOutcome(UntrustedText nick, bool becameFriend) => new(
        becameFriend ? "Friend request accepted" : "Friend request declined",
        $"**Player:** {UntrustedMarkdown.Escape(nick)}",
        becameFriend ? SuccessColor : WarningColor,
        null);

    public static RenderedCard RenderStatus(StatusMessage status) =>
        new(status.Title, status.Detail, status.Ping ? DangerColor : InfoColor, null);

    /// <summary>
    /// Renders one mirrored chat message. A late message shows when it was sent, so that catch-up
    /// after an outage can't be mistaken for something happening now.
    /// </summary>
    public static string RenderChatLine(ChatLine line, UntrustedText partnerNick, DateTimeOffset now)
    {
        var author = line.Origin == ChatLineOrigin.Partner
            ? UntrustedMarkdown.Escape(partnerNick)
            : "This account, via another login";
        var isLate = now - line.SentAt > TimeSpan.FromMinutes(1);
        var time = isLate ? $" (<t:{line.SentAt.ToUnixTimeSeconds()}:f>)" : "";

        return $"**{author}**{time}\n{UntrustedMarkdown.Escape(line.Text)}";
    }

    /// <summary>
    /// Puts a link to the partner's profile above a post's first message. Titles can't hold links.
    /// A message from the chunker leaves room for this within Discord's 2000 characters.
    /// </summary>
    public static string WithProfileLink(string text, string userId) =>
        $"[GeoGuessr profile]({ProfileUrl(userId)})\n{text}";

    public static bool HasProfileLink(string text, string userId) =>
        text.Contains(ProfileUrl(userId), StringComparison.Ordinal);

    public static string ProfileUrl(string userId) => ProfileBaseUrl + Uri.EscapeDataString(userId);

    private static uint ColorOf(CardStyle style) => style switch
    {
        CardStyle.Success => SuccessColor,
        CardStyle.Warning => WarningColor,
        CardStyle.Danger => DangerColor,
        _ => InfoColor
    };

    private static string Truncate(string text)
    {
        if (text.Length <= MaxDescriptionLength)
        {
            return text;
        }

        // Cutting through an escape would leave a stray backslash, so the chunker picks the spot.
        return $"{MessageChunker.Chunk(text, MaxDescriptionLength)[0]}…";
    }
}
