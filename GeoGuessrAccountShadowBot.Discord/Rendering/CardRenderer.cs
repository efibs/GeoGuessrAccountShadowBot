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
    public Embed ToEmbed()
    {
        var builder = new EmbedBuilder().WithTitle(Title).WithColor(new Color(Color));

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
/// Renders outbox messages. These are pure functions, and they're the only place GeoGuessr text
/// turns into Discord text.
/// </summary>
public static class CardRenderer
{
    private const int MaxDescriptionLength = 3900;

    private const uint InfoColor = 0x5865F2;
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
