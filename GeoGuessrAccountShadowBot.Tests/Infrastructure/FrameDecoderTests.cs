// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.RegularExpressions;
using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application.Reconciliation;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;
using GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Socket;

namespace GeoGuessrAccountShadowBot.Tests.Infrastructure;

/// <summary>
/// The fixtures are real frames, captured live and scrubbed by the probe.
/// </summary>
public sealed class FrameDecoderTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "frames", $"{name}.json"));

    [Fact]
    public void ChatMessage_DecodesARealFrame()
    {
        var frame = FrameDecoder.Decode(Fixture("ChatMessage")).Should().BeOfType<DecodedFrame.Event>().Subject;

        var message = frame.ShadowEvent.Should().BeOfType<DirectMessageReceived>().Subject.Message;
        message.Id.Should().Be("m000000001");
        message.PayloadType.Should().Be(ChatPayloadType.Text);
        message.SourceId.Should().NotBe(message.RecipientId);
        message.RoomId.Should().Contain(":");
        message.IsSystemMessage.Should().BeFalse();
    }

    [Theory]
    [InlineData("NewFriendRequest", ReconcileScope.Friends)]
    [InlineData("FriendsUpdated", ReconcileScope.Friends)]
    [InlineData("NotificationUpdate", ReconcileScope.Notifications)]
    public void PayloadlessFrames_AreDoorbells(string name, ReconcileScope scope)
    {
        FrameDecoder.Decode(Fixture(name)).Should().Be(new DecodedFrame.Doorbell(scope));
    }

    [Theory]
    [InlineData("FriendCameOnline", true)]
    [InlineData("FriendWentOffline", false)]
    public void Presence_DecodesARealFrame(string name, bool isOnline)
    {
        var frame = FrameDecoder.Decode(Fixture(name)).Should().BeOfType<DecodedFrame.Event>().Subject;

        var presence = frame.ShadowEvent.Should().BeOfType<FriendPresenceChanged>().Subject;
        presence.IsOnline.Should().Be(isOnline);
        presence.FriendId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Subscribed_DecodesARealFrame()
    {
        FrameDecoder.Decode(Fixture("Subscribed")).Should().BeOfType<DecodedFrame.Subscribed>()
            .Which.Topic.Should().StartWith("chat:Friend:TextMessages:");
    }

    [Fact]
    public void SubscribeDenied_NamesTheTopic()
    {
        FrameDecoder.Decode("""{"code":"SubscribeDenied","topic":"club:1"}""")
            .Should().Be(new DecodedFrame.SubscribeDenied("club:1"));
    }

    [Fact]
    public void ClubChat_CarriesTheClubFromTheTopic()
    {
        var payload = """{\"id\":\"m1\",\"payloadType\":\"Text\",\"textPayload\":\"hi\",\"sourceType\":\"User\",\"sourceId\":\"a\",\"recipientId\":\"c\",\"sentAt\":\"2026-01-01T00:00:00Z\",\"roomId\":\"c\"}""";

        var frame = FrameDecoder.Decode($$"""{"code":"ChatMessage","topic":"chat:Club:TextMessages:club-1","payload":"{{payload}}"}""");

        frame.Should().BeOfType<DecodedFrame.Event>().Which.ShadowEvent
            .Should().BeOfType<ClubChatMessageReceived>().Which.ClubId.Should().Be("club-1");
    }

    [Fact]
    public void SystemSource_IsMarked()
    {
        var payload = """{\"id\":\"m1\",\"payloadType\":\"Club\",\"sourceType\":\"System\",\"sourceId\":\"a\",\"recipientId\":\"c\",\"sentAt\":\"2026-01-01T00:00:00Z\",\"roomId\":\"r\"}""";

        var frame = FrameDecoder.Decode($$"""{"code":"ChatMessage","topic":"chat:Friend:TextMessages:a","payload":"{{payload}}"}""");

        frame.Should().BeOfType<DecodedFrame.Event>().Which.ShadowEvent
            .Should().BeOfType<DirectMessageReceived>().Which.Message.IsSystemMessage.Should().BeTrue();
    }

    [Fact]
    public void UnknownPayloadType_DecodesRatherThanFails()
    {
        var payload = """{\"id\":\"m1\",\"payloadType\":\"Hologram\",\"sourceType\":\"User\",\"sourceId\":\"a\",\"recipientId\":\"c\",\"sentAt\":\"2026-01-01T00:00:00Z\",\"roomId\":\"r\"}""";

        var frame = FrameDecoder.Decode($$"""{"code":"ChatMessage","topic":"chat:Friend:TextMessages:a","payload":"{{payload}}"}""");

        frame.Should().BeOfType<DecodedFrame.Event>().Which.ShadowEvent
            .Should().BeOfType<DirectMessageReceived>().Which.Message.PayloadType.Should().Be(ChatPayloadType.Unknown);
    }

    [Fact]
    public void MissionBoard_ReadsItsPayload()
    {
        var frame = FrameDecoder.Decode("""{"code":"MissionBoardProgressed","payload":"{\"contributorUserId\":\"u1\",\"boardCleared\":true}"}""");

        frame.Should().BeOfType<DecodedFrame.Event>().Which.ShadowEvent
            .Should().Be(new ClubMissionBoardProgressed("u1", true));
    }

    [Fact]
    public void UnknownCode_IsReportedByName()
    {
        FrameDecoder.Decode("""{"code":"BrandNewThing"}""").Should().Be(new DecodedFrame.Unknown("BrandNewThing"));
    }

    [Fact]
    public void KnownUselessFrame_IsIgnored()
    {
        FrameDecoder.Decode("""{"code":"AccountUpdate"}""").Should().BeOfType<DecodedFrame.Ignored>();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"topic":"x"}""")]
    [InlineData("""{"code":"ChatMessage"}""")]
    [InlineData("""{"code":"ChatMessage","topic":"chat:Friend:TextMessages:a","payload":"not json"}""")]
    [InlineData("""{"code":"FriendCameOnline","payload":null}""")]
    public void BrokenFrames_AreMalformedRatherThanExceptions(string frame)
    {
        FrameDecoder.Decode(frame).Should().BeOfType<DecodedFrame.Malformed>();
    }
}

public sealed partial class FixtureSafetyTests
{
    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*", SearchOption.AllDirectories))
        {
            data.Add(path);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_HoldsNoRealData(string path)
    {
        var text = File.ReadAllText(path);

        text.Should().NotContain("_ncfa");
        text.Should().NotContain("joinToken");
        RealIdRegex().IsMatch(text).Should().BeFalse("ids must be scrubbed to the zero-padded fakes");
    }

    /// <summary>A 24 character id that doesn't start with the fakes' zeros, and isn't an escaped quote.</summary>
    [GeneratedRegex(@"(?<![0-9a-f\\u])(?!0{10})[0-9a-f]{24}(?![0-9a-f])")]
    private static partial Regex RealIdRegex();
}

public sealed class OutboundFramesTests
{
    [Fact]
    public void Subscribe_MatchesTheSitesFormat()
    {
        OutboundFrames.Subscribe("self:abc").Should().Be("""{"code":"Subscribe","topic":"self:abc","client":"web"}""");
    }

    [Fact]
    public void HeartBeat_CarriesOnlyItsCode()
    {
        OutboundFrames.HeartBeat.Should().Be("""{"code":"HeartBeat"}""");
    }

    [Fact]
    public void ChatMessage_SendsPlainTextAsThePayload()
    {
        OutboundFrames.ChatMessage(SocketTopics.FriendChat("peer"), "hi")
            .Should().Be("""{"code":"ChatMessage","topic":"chat:Friend:TextMessages:peer","payload":"hi","client":"web"}""");
    }
}
