// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GeoGuessrAccountShadowBot.Tests.Infrastructure;

/// <summary>
/// A real local WebSocket server, so tests exercise a real ClientWebSocket. Kestrel only speaks
/// plain ws here, which is why these tests build the session around the client directly.
/// </summary>
internal sealed class FakeGeoGuessrServer : IAsyncDisposable
{
    private readonly WebApplication _application;

    private FakeGeoGuessrServer(WebApplication application) => _application = application;

    public Uri SocketUrl { get; private set; } = null!;

    public Dictionary<string, string>? LastHandshakeHeaders { get; private set; }

    public string? LastQuery { get; private set; }

    public static async Task<FakeGeoGuessrServer> StartAsync(Func<HttpContext, WebSocket?, Task> handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        var application = builder.Build();
        application.UseWebSockets();

        var server = new FakeGeoGuessrServer(application);
        application.Map("/ws", async context =>
        {
            // Kestrel recycles the header collection after the request, so it has to be copied.
            server.LastHandshakeHeaders = context.Request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString());
            server.LastQuery = context.Request.QueryString.Value;

            // The handler sees each request twice: first to refuse the handshake if it wants to.
            await handler(context, null);
            if (!context.Response.HasStarted && context.Response.StatusCode == StatusCodes.Status200OK)
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await handler(context, socket);
            }
        });

        await application.StartAsync();

        var address = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        server.SocketUrl = new Uri($"{address.Replace("http://", "ws://", StringComparison.Ordinal)}/ws");
        return server;
    }

    public static async Task SendTextAsync(WebSocket socket, string text, int fragmentSize = int.MaxValue)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var offset = 0; offset < bytes.Length; offset += fragmentSize)
        {
            var count = Math.Min(fragmentSize, bytes.Length - offset);
            var isLast = offset + count >= bytes.Length;
            await socket.SendAsync(bytes.AsMemory(offset, count), WebSocketMessageType.Text, isLast, CancellationToken.None);

            if (isLast)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync() => await _application.DisposeAsync();
}
