// SPDX-License-Identifier: GPL-2.0-or-later

using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application.Connection;
using GeoGuessrAccountShadowBot.Application.Options;
using Microsoft.Extensions.Time.Testing;

namespace GeoGuessrAccountShadowBot.Tests.Application;

public sealed class ConnectionPolicyTests
{
    private static readonly TimeSpan Brief = TimeSpan.FromSeconds(1);

    private readonly ConnectionPolicy _policy = new(new SocketOptions(), new YieldOptions());
    private readonly ConnectionState _connected = new(ConnectionPhase.Connected);

    private ConnectionTransition Close(int? code, string? description = null, ConnectionState? state = null) =>
        _policy.Next(state ?? _connected, new ConnectionSignal.Closed(code, description, Brief));

    [Fact]
    public void Initial_StartsPollingWhenTheSocketIsDisabled()
    {
        var policy = new ConnectionPolicy(new SocketOptions { Enabled = false }, new YieldOptions());

        policy.Initial().Should().Match<ConnectionState>(state =>
            state.Phase == ConnectionPhase.Polling && state.Reason == PollingReason.SocketDisabled);
    }

    [Fact]
    public void Close1012_ReconnectsImmediatelyAndSaysSo()
    {
        var next = Close(1012).Next;

        next.Phase.Should().Be(ConnectionPhase.Reconnecting);
        next.Delay.Should().Be(TimeSpan.Zero);
        next.IsReconnectAfterRestart.Should().BeTrue();
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1006)]
    [InlineData(1011)]
    [InlineData(null)]
    public void OrdinaryClose_BacksOff(int? code)
    {
        var next = Close(code).Next;

        next.Phase.Should().Be(ConnectionPhase.Reconnecting);
        next.Attempt.Should().Be(1);
    }

    [Fact]
    public void Close4100_YieldsForTheInitialTime()
    {
        var next = Close(4100).Next;

        next.Phase.Should().Be(ConnectionPhase.Polling);
        next.Reason.Should().Be(PollingReason.Superseded);
        next.Delay.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 60)]
    [InlineData(9, 60)]
    public void Close4100_YieldsLongerEachTimeUpToTheLimit(int earlierYields, int expectedMinutes)
    {
        Close(4100, state: _connected with { YieldCount = earlierYields }).Next.Delay
            .Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public void StableConnection_ResetsTheYieldLength()
    {
        var state = _connected with { YieldCount = 3, Attempt = 9 };

        var next = _policy.Next(state, new ConnectionSignal.Closed(4100, null, TimeSpan.FromMinutes(5))).Next;

        next.Delay.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Close4200_ReportsAnOutdatedClient()
    {
        Close(4200).Next.Reason.Should().Be(PollingReason.ClientOutdated);
    }

    [Fact]
    public void Close1001Unauthorized_ChecksTheToken()
    {
        Close(1001, "Unauthorized").VerifyAuth.Should().BeTrue();
        Close(1001, "Bye").VerifyAuth.Should().BeFalse();
    }

    [Fact]
    public void UnknownApplicationClose_WaitsAtLeastThirtySeconds()
    {
        Close(4999).Next.Delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ServerSent4000_IsTreatedAsAnUnknownClose()
    {
        Close(4000).Next.Phase.Should().Be(ConnectionPhase.Reconnecting);
    }

    [Fact]
    public void Handshake401_ChecksTheToken()
    {
        _policy.Next(_connected, new ConnectionSignal.HandshakeRefused(401, null)).VerifyAuth.Should().BeTrue();
    }

    [Fact]
    public void Handshake403_ChecksTheTokenThenPollsAsBlocked()
    {
        var transition = _policy.Next(_connected, new ConnectionSignal.HandshakeRefused(403, null));

        transition.VerifyAuth.Should().BeTrue();
        transition.Next.Reason.Should().Be(PollingReason.Blocked);
        transition.Next.Delay.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Handshake403_RetriesLessOftenEachTimeUpToSixHours()
    {
        var next = _policy.Next(_connected with { YieldCount = 8 }, new ConnectionSignal.HandshakeRefused(403, null)).Next;

        next.Delay.Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public void Handshake429_HonoursRetryAfter()
    {
        var next = _policy.Next(_connected, new ConnectionSignal.HandshakeRefused(429, TimeSpan.FromMinutes(3))).Next;

        next.Delay.Should().Be(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void RetryDue_LeavesPolling()
    {
        var polling = new ConnectionState(ConnectionPhase.Polling, PollingReason.Superseded, Delay: TimeSpan.FromMinutes(15), YieldCount: 2);

        var next = _policy.Next(polling, new ConnectionSignal.RetryDue()).Next;

        next.Phase.Should().Be(ConnectionPhase.Reconnecting);
        next.YieldCount.Should().Be(2);
        next.Delay.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 3)]
    [InlineData(8, 3)]
    [InlineData(9, 5)]
    [InlineData(16, 5)]
    [InlineData(17, 15)]
    [InlineData(18, 30)]
    [InlineData(19, 60)]
    [InlineData(50, 300)]
    public void BackoffDelay_FollowsTheSchedule(int attempt, int expectedSeconds)
    {
        _policy.BackoffDelay(attempt).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }
}

public sealed class ConnectionAnnouncerTests
{
    private readonly FakeTimeProvider _time = new();

    private readonly ConnectionState _reconnecting = new(ConnectionPhase.Reconnecting);
    private readonly ConnectionState _connected = new(ConnectionPhase.Connected);
    private readonly ConnectionState _superseded = new(ConnectionPhase.Polling, PollingReason.Superseded);

    [Fact]
    public void BriefOutage_StaysQuiet()
    {
        var announcer = new ConnectionAnnouncer(_time);

        announcer.Observe(_reconnecting).Should().BeNull();
        _time.Advance(TimeSpan.FromMinutes(1));

        announcer.Observe(_reconnecting).Should().BeNull();
        announcer.Observe(_connected).Should().BeNull();
    }

    [Fact]
    public void LongOutage_IsAnnouncedOnceAndItsEndToo()
    {
        var announcer = new ConnectionAnnouncer(_time);
        announcer.Observe(_reconnecting);
        _time.Advance(TimeSpan.FromMinutes(3));

        announcer.Observe(_reconnecting).Should().NotBeNull();
        announcer.Observe(_reconnecting).Should().BeNull();
        announcer.Observe(_connected)!.Title.Should().Be("Connected");
    }

    [Fact]
    public void RepeatedYields_AreThrottled()
    {
        var announcer = new ConnectionAnnouncer(_time);

        announcer.Observe(_superseded).Should().NotBeNull();
        announcer.Observe(_connected);
        _time.Advance(TimeSpan.FromHours(1));

        announcer.Observe(_superseded).Should().BeNull();
    }

    [Fact]
    public void RejectedToken_PingsAndRepeatsDaily()
    {
        var announcer = new ConnectionAnnouncer(_time);

        announcer.Observe(ConnectionPolicy.AuthFailed)!.Ping.Should().BeTrue();
        _time.Advance(TimeSpan.FromHours(1));
        announcer.Observe(ConnectionPolicy.AuthFailed).Should().BeNull();
        _time.Advance(TimeSpan.FromHours(24));
        announcer.Observe(ConnectionPolicy.AuthFailed).Should().NotBeNull();
    }
}
