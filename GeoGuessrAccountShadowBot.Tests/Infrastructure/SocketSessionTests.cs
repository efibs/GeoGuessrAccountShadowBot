// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Socket;
using GeoGuessrAccountShadowBot.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Tests.Infrastructure;

public sealed class SocketSessionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The validator only allows wss to geoguessr.com, and this test server is local ws.</summary>
    private static ClientWebSocketConnector Connector(Uri socketUrl, int maxFrameBytes = 1024 * 1024)
    {
        var options = TestOptions.Valid();
        options.GeoGuessr.SocketUrl = socketUrl;
        options.GeoGuessr.Socket.MaxFrameBytes = maxFrameBytes;
        return new ClientWebSocketConnector(Options.Create(options));
    }

    private static async Task<List<string>> ReadAllAsync(ISocketSession session)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var frames = new List<string>();
        await foreach (var frame in session.ReadFramesAsync(timeout.Token))
        {
            frames.Add(frame);
        }

        return frames;
    }

    private static async Task CloseAsync(WebSocket socket, int code, string? description)
    {
        await socket.CloseOutputAsync((WebSocketCloseStatus)code, description, CancellationToken.None);
        var buffer = new byte[64];
        while (socket.State == WebSocketState.CloseSent)
        {
            await socket.ReceiveAsync(buffer, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Handshake_SendsTheSessionTheWayTheSiteDoes()
    {
        await using var server = await FakeGeoGuessrServer.StartAsync(async (_, socket) =>
        {
            if (socket is not null)
            {
                await CloseAsync(socket, 1000, null);
            }
        });

        var session = await Connector(server.SocketUrl).ConnectAsync(true, CancellationToken.None);
        await using (session)
        {
            await ReadAllAsync(session);
        }

        server.LastHandshakeHeaders!["Cookie"].Should().Be("_ncfa=test-token");
        server.LastHandshakeHeaders!["Origin"].Should().Be("https://www.geoguessr.com");
        server.LastHandshakeHeaders!["User-Agent"].Should().Contain("GeoGuessrAccountShadowBot");
        server.LastQuery.Should().Contain("c=web-").And.Contain("tabId=").And.Contain("visibility=visible").And.Contain("reconnect=true");
    }

    [Fact]
    public async Task FirstConnection_DoesNotClaimToBeAReconnect()
    {
        await using var server = await FakeGeoGuessrServer.StartAsync(async (_, socket) =>
        {
            if (socket is not null)
            {
                await CloseAsync(socket, 1000, null);
            }
        });

        var session = await Connector(server.SocketUrl).ConnectAsync(false, CancellationToken.None);
        await using (session)
        {
            await ReadAllAsync(session);
        }

        server.LastQuery.Should().NotContain("reconnect");
    }

    [Fact]
    public async Task FragmentedFrame_ArrivesWhole()
    {
        var text = new string('x', 100_000);
        await using var server = await FakeGeoGuessrServer.StartAsync(async (_, socket) =>
        {
            if (socket is not null)
            {
                await FakeGeoGuessrServer.SendTextAsync(socket, text, fragmentSize: 7_000);
                await CloseAsync(socket, 1000, null);
            }
        });

        var session = await Connector(server.SocketUrl).ConnectAsync(false, CancellationToken.None);
        await using (session)
        {
            (await ReadAllAsync(session)).Should().ContainSingle().Which.Should().Be(text);
        }
    }

    [Fact]
    public async Task MultibyteTextAcrossFragments_SurvivesIntact()
    {
        var text = string.Concat(Enumerable.Repeat("✅ü", 6_000));
        await using var server = await FakeGeoGuessrServer.StartAsync(async (_, socket) =>
        {
            if (socket is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await socket.SendAsync(bytes.AsMemory(0, 16_385), WebSocketMessageType.Text, false, CancellationToken.None);
                await socket.SendAsync(bytes.AsMemory(16_385), WebSocketMessageType.Text, true, CancellationToken.None);
                await CloseAsync(socket, 1000, null);
            }
        });

        var session = await Connector(server.SocketUrl).ConnectAsync(false, CancellationToken.None);
        await using (session)
        {
            (await ReadAllAsync(session)).Should().ContainSingle().Which.Should().Be(text);
        }
    }

    [Fact]
    public async Task OversizedFrame_EndsTheSessionInsteadOfGrowingForever()
    {
        await using var server = await FakeGeoGuessrServer.StartAsync(async (_, socket) =>
        {
            if (socket is not null)
            {
                await FakeGeoGuessrServer.SendTextAsync(socket, new string('x', 200_000));
                await socket.ReceiveAsync(new byte[16], CancellationToken.None);
            }
        });

        var session = await Connector(server.SocketUrl, maxFrameBytes: 50_000).ConnectAsync(false, CancellationToken.None);
        await using (session)
        {
            (await ReadAllAsync(session)).Should().BeEmpty();
            session.Closure!.Description.Should().Be("frame too big");
        }
    }

    [Fact]
    public async Task BinaryFrames_AreSkipped()
    {
        await using var server = await FakeGeoGuessrServer.StartAsync(async (_, socket) =>
        {
            if (socket is not null)
            {
                await socket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, CancellationToken.None);
                await FakeGeoGuessrServer.SendTextAsync(socket, "after");
                await CloseAsync(socket, 1000, null);
            }
        });

        var session = await Connector(server.SocketUrl).ConnectAsync(false, CancellationToken.None);
        await using (session)
        {
            (await ReadAllAsync(session)).Should().Equal("after");
        }
    }

    [Theory]
    [InlineData(4100, "superseded")]
    [InlineData(1012, null)]
    [InlineData(1001, "Unauthorized")]
    public async Task ServerClose_ReportsItsCodeAndReason(int code, string? description)
    {
        await using var server = await FakeGeoGuessrServer.StartAsync(async (_, socket) =>
        {
            if (socket is not null)
            {
                await CloseAsync(socket, code, description);
            }
        });

        var session = await Connector(server.SocketUrl).ConnectAsync(false, CancellationToken.None);
        await using (session)
        {
            await ReadAllAsync(session);
            session.Closure!.Code.Should().Be(code);
            (session.Closure.Description ?? "").Should().Be(description ?? "");
        }
    }

    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status403Forbidden)]
    public async Task RefusedHandshake_ReportsItsStatus(int status)
    {
        await using var server = await FakeGeoGuessrServer.StartAsync((context, _) =>
        {
            context.Response.StatusCode = status;
            return context.Response.WriteAsync("no");
        });

        var act = () => Connector(server.SocketUrl).ConnectAsync(false, CancellationToken.None);

        (await act.Should().ThrowAsync<SocketHandshakeException>()).Which.StatusCode.Should().Be(status);
    }

    [Fact]
    public async Task RefusedHandshake_ReportsRetryAfter()
    {
        await using var server = await FakeGeoGuessrServer.StartAsync((context, _) =>
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "120";
            return context.Response.WriteAsync("slow down");
        });

        var act = () => Connector(server.SocketUrl).ConnectAsync(false, CancellationToken.None);

        (await act.Should().ThrowAsync<SocketHandshakeException>()).Which.RetryAfter.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task Send_ReachesTheServer()
    {
        var received = new TaskCompletionSource<string>();
        await using var server = await FakeGeoGuessrServer.StartAsync(async (_, socket) =>
        {
            if (socket is not null)
            {
                var buffer = new byte[1024];
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                received.SetResult(Encoding.UTF8.GetString(buffer, 0, result.Count));
                await CloseAsync(socket, 1000, null);
            }
        });

        var session = await Connector(server.SocketUrl).ConnectAsync(false, CancellationToken.None);
        await using (session)
        {
            await session.SendAsync(OutboundFrames.HeartBeat, CancellationToken.None);
            (await received.Task.WaitAsync(TestTimeout)).Should().Be(OutboundFrames.HeartBeat);
        }
    }
}
