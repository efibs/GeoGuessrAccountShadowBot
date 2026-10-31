// SPDX-License-Identifier: GPL-2.0-or-later

using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GeoGuessrAccountShadowBot.Tests.Application;

public sealed class PlayerDirectoryTests
{
    private const string UserId = ShadowTestHost.PeerId;

    private static async Task<string> GetNickAsync(ShadowTestHost host)
    {
        var scope = host.Services.CreateAsyncScope();
        await using (scope)
        {
            var directory = scope.ServiceProvider.GetRequiredService<PlayerDirectory>();
            var nick = await directory.GetNickAsync(UserId, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<GeoGuessrAccountShadowBot.Application.Ports.IShadowStore>().CommitAsync();
            return nick.Value;
        }
    }

    [Fact]
    public async Task CachesNames()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        host.Api.GetPlayerNickAsync(UserId, Arg.Any<CancellationToken>()).Returns("nick");

        await GetNickAsync(host);
        (await GetNickAsync(host)).Should().Be("nick");

        await host.Api.Received(1).GetPlayerNickAsync(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshesAfterADay()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        host.Api.GetPlayerNickAsync(UserId, Arg.Any<CancellationToken>()).Returns("old", "new");

        await GetNickAsync(host);
        host.Time.Advance(TimeSpan.FromHours(25));

        (await GetNickAsync(host)).Should().Be("new");
    }

    [Fact]
    public async Task FailedLookup_FallsBackToAShortId()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        host.Api.GetPlayerNickAsync(UserId, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException());

        (await GetNickAsync(host)).Should().Be("player-bbbbbbbb");
    }

    [Fact]
    public async Task FailedRefresh_KeepsTheLastKnownName()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        host.Api.GetPlayerNickAsync(UserId, Arg.Any<CancellationToken>()).Returns("known");
        await GetNickAsync(host);
        host.Time.Advance(TimeSpan.FromHours(25));
        host.Api.GetPlayerNickAsync(UserId, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException());

        (await GetNickAsync(host)).Should().Be("known");
    }
}
