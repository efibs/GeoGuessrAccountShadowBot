// SPDX-License-Identifier: GPL-2.0-or-later

using AwesomeAssertions;
using Discord;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Discord.Input;
using GeoGuessrAccountShadowBot.Discord.Output;
using GeoGuessrAccountShadowBot.Discord.Rendering;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;
using static VerifyXunit.Verifier;

namespace GeoGuessrAccountShadowBot.Tests.Discord;

public sealed class UntrustedMarkdownTests
{
    [Fact]
    public Task HostileText_BecomesInert()
    {
        string[] hostile =
        [
            "@everyone",
            "@here free nitro",
            "<@123456789>",
            "<@&123456789>",
            "<#123456789>",
            "<t:0:R>",
            "# big heading",
            "## also a heading",
            "-# small text",
            "- list item",
            "1. numbered",
            "> quote",
            ">>> long quote",
            "[totally safe](http://evil.example)",
            "https://evil.example/path",
            "**bold** __underline__ ~~strike~~ ||spoiler||",
            "`code` ```block```",
            "\\*already escaped\\*",
            "normal text, ok?"
        ];

        return Verify(hostile.Select(text => new { text, escaped = UntrustedMarkdown.Escape(text) }));
    }

    [Theory]
    [InlineData("abc‮def", "abcdef")]
    [InlineData("abc‏def", "abcdef")]
    [InlineData("abc⁦def⁩", "abcdef")]
    [InlineData("abc\u0000def", "abcdef")]
    [InlineData("abc\u001Bdef", "abcdef")]
    public void InvisibleControls_AreRemoved(string text, string expected)
    {
        UntrustedMarkdown.Clean(text).Should().Be(expected);
    }

    [Fact]
    public void LineBreaksAndEmoji_Stay()
    {
        UntrustedMarkdown.Clean("line one\nline two ✅").Should().Be("line one\nline two ✅");
    }
}

public sealed class ForumTitleTests
{
    [Fact]
    public void Title_KeepsAnOrdinaryName()
    {
        ForumTitle.Create(new UntrustedText("Some Player"), "id").Should().Be("Some Player");
    }

    [Fact]
    public void Title_FlattensLinesAndWhitespace()
    {
        ForumTitle.Create(new UntrustedText("  two\n\nlines\t here "), "id").Should().Be("two lines here");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("‮‏")]
    public void BlankTitle_FallsBackToTheId(string nick)
    {
        ForumTitle.Create(new UntrustedText(nick), "bbbbbbbbbbbbbbbbbbbbbbbb").Should().Be("player-bbbbbbbb");
    }

    [Fact]
    public void LongTitle_IsCut()
    {
        ForumTitle.Create(new UntrustedText(new string('x', 300)), "id").Should().HaveLength(100);
    }
}

public sealed class MessageChunkerTests
{
    [Fact]
    public void ShortText_StaysWhole()
    {
        MessageChunker.Chunk("hello").Should().Equal("hello");
    }

    [Fact]
    public void LongText_FitsTheLimit()
    {
        var chunks = MessageChunker.Chunk(new string('x', 5_000));

        chunks.Should().AllSatisfy(chunk => chunk.Length.Should().BeLessThanOrEqualTo(MessageChunker.DefaultLimit));
        string.Concat(chunks).Should().HaveLength(5_000);
    }

    [Fact]
    public void PrefersALineBreak()
    {
        var text = new string('a', 80) + "\n" + new string('b', 40);

        MessageChunker.Chunk(text, 100).Should().Equal(new string('a', 80), new string('b', 40));
    }

    [Fact]
    public void NeverSeparatesABackslashFromWhatItEscapes()
    {
        var text = UntrustedMarkdown.Escape(new string('@', 500));

        foreach (var limit in Enumerable.Range(50, 40))
        {
            MessageChunker.Chunk(text, limit).Should().AllSatisfy(chunk =>
                (chunk.Length - chunk.TrimEnd('\\').Length).Should().Be(0));
        }
    }

    [Fact]
    public void NeverSplitsAnEmoji()
    {
        var text = string.Concat(Enumerable.Repeat("😀", 100));

        foreach (var limit in Enumerable.Range(10, 20))
        {
            MessageChunker.Chunk(text, limit).Should().AllSatisfy(chunk =>
                char.IsHighSurrogate(chunk[^1]).Should().BeFalse());
        }
    }
}

public sealed class CardRendererTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public Task Card_WithEverything()
    {
        var card = new EventCard(EventKeys.ClubChat, CardStyle.Info, "Club chat", "Some detail", "actor", new UntrustedText("hello **world**"), At);

        return Verify(CardRenderer.Render(card, new UntrustedText("Nick_Name")));
    }

    [Fact]
    public Task Card_WithOnlyATitle()
    {
        var card = new EventCard(EventKeys.SeasonChanged, CardStyle.Warning, "A new season started", null, null, null, null);

        return Verify(CardRenderer.Render(card, null));
    }

    [Theory]
    [InlineData(CardStyle.Success)]
    [InlineData(CardStyle.Danger)]
    public void Card_ColorFollowsItsStyle(CardStyle style)
    {
        var card = new EventCard(EventKeys.BadgeUnlocked, style, "t", null, null, null, null);

        CardRenderer.Render(card, null).Color.Should().NotBe(CardRenderer.Render(card with { Style = CardStyle.Info }, null).Color);
    }

    [Fact]
    public void HostileNames_AreEscapedEverywhere()
    {
        var nick = new UntrustedText("@everyone");

        CardRenderer.RenderFriendRequest(nick).Description.Should().Contain("\\@everyone");
        CardRenderer.RenderFriendRequestOutcome(nick, true).Description.Should().Contain("\\@everyone");
        CardRenderer.Render(new EventCard(EventKeys.BadgeUnlocked, CardStyle.Info, "t", null, null, null, null), nick).Description.Should().Contain("\\@everyone");
    }

    [Fact]
    public void LongBody_FitsAnEmbed()
    {
        var card = new EventCard(EventKeys.ClubChat, CardStyle.Info, "t", null, null, new UntrustedText(new string('@', 3_000)), null);

        var description = CardRenderer.Render(card, null).Description!;

        description.Length.Should().BeLessThanOrEqualTo(4096);
        description.Should().EndWith("…");
    }

    [Fact]
    public Task ChatLine_FromThePartner()
    {
        var line = new ChatLine("peer", ChatLineOrigin.Partner, new UntrustedText("hi # there"), At);

        return Verify(CardRenderer.RenderChatLine(line, new UntrustedText("Nick"), At));
    }

    [Fact]
    public Task ChatLine_FromAnotherLogin()
    {
        var line = new ChatLine("peer", ChatLineOrigin.OtherLogin, new UntrustedText("sent elsewhere"), At);

        return Verify(CardRenderer.RenderChatLine(line, new UntrustedText("Nick"), At));
    }

    [Fact]
    public Task ChatLine_ThatArrivesLateShowsWhenItWasSent()
    {
        var line = new ChatLine("peer", ChatLineOrigin.Partner, new UntrustedText("while you were out"), At);

        return Verify(CardRenderer.RenderChatLine(line, new UntrustedText("Nick"), At.AddHours(2)));
    }
}

public sealed class MentionPolicyTests
{
    [Fact]
    public void Nobody_AllowsNothing()
    {
        var mentions = MentionPolicy.Nobody();

        mentions.AllowedTypes.Should().Be(AllowedMentionTypes.None);
        mentions.RoleIds.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Nobody_NeverReturnsTheSharedInstance()
    {
        MentionPolicy.Nobody().Should().NotBeSameAs(MentionPolicy.Nobody());
    }

    [Fact]
    public void Roles_AllowExactlyThoseRoles()
    {
        var mentions = MentionPolicy.Roles([1, 2]);

        mentions.AllowedTypes.Should().BeNull();
        mentions.RoleIds.Should().Equal(1UL, 2UL);
        mentions.UserIds.Should().BeEmpty();
    }

    [Fact]
    public void NoRoles_MeansNobody()
    {
        MentionPolicy.Roles([]).AllowedTypes.Should().Be(AllowedMentionTypes.None);
        MentionPolicy.PingLine([]).Should().BeNull();
    }

    [Fact]
    public void PingLine_MentionsEachRole()
    {
        MentionPolicy.PingLine([1, 2]).Should().Be("<@&1> <@&2>");
    }
}

public sealed class InboundMessageFilterTests
{
    private const ulong Guild = 1;
    private const ulong Forum = 2;

    [Fact]
    public void UserMessageInTheForum_IsAReply()
    {
        InboundMessageFilter.IsForumReply(new InboundMessageFacts(MessageSource.User, Guild, Forum), Guild, Forum).Should().BeTrue();
    }

    [Theory]
    [InlineData(MessageSource.Bot)]
    [InlineData(MessageSource.Webhook)]
    [InlineData(MessageSource.System)]
    public void OtherSources_NeverAre(MessageSource source)
    {
        InboundMessageFilter.IsForumReply(new InboundMessageFacts(source, Guild, Forum), Guild, Forum).Should().BeFalse();
    }

    [Fact]
    public void OtherForums_AndGuilds_AreIgnored()
    {
        InboundMessageFilter.IsForumReply(new InboundMessageFacts(MessageSource.User, Guild, 99), Guild, Forum).Should().BeFalse();
        InboundMessageFilter.IsForumReply(new InboundMessageFacts(MessageSource.User, 99, Forum), Guild, Forum).Should().BeFalse();
        InboundMessageFilter.IsForumReply(new InboundMessageFacts(MessageSource.User, Guild, Forum), Guild, null).Should().BeFalse();
    }
}

public sealed class FriendRequestButtonIdsTests
{
    [Fact]
    public void Ids_FitDiscordsLimitForARealUserId()
    {
        const int limit = 100;
        var userId = new string('a', 24);

        new[] { FriendRequestButtonIds.Accept(userId), FriendRequestButtonIds.Decline(userId), FriendRequestButtonIds.ConfirmDecline(userId) }
            .Should().AllSatisfy(id => id.Length.Should().BeLessThanOrEqualTo(limit));
    }
}
