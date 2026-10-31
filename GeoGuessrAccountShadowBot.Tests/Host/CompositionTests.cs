// SPDX-License-Identifier: GPL-2.0-or-later

using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Tests.Host;

/// <summary>
/// Builds the real object graph, with nothing replaced. Tests that mock the edges can't see a
/// registration that only fails when it's resolved, and that kind of bug stops the bot from
/// starting at all.
/// </summary>
public sealed class CompositionTests
{
    private static IHost BuildHost(params (string Key, string Value)[] overrides)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.reference.json"));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GeoGuessr:NcfaToken"] = "test-token",
            ["Discord:Token"] = "test-discord-token",
            ["Discord:GuildId"] = "1",
            ["Discord:DefaultChannelId"] = "2",
            ["Forum:ChannelId"] = "3",
            ["Database:Path"] = Path.Combine(Path.GetTempPath(), $"shadow-{Guid.NewGuid():N}.db")
        });
        builder.Configuration.AddInMemoryCollection(overrides.ToDictionary(item => item.Key, item => (string?)item.Value));

        builder.AddShadowBot();
        return builder.Build();
    }

    [Fact]
    public void EveryHostedService_CanBeBuilt()
    {
        using var host = BuildHost();

        host.Services.GetServices<IHostedService>().Should().HaveCountGreaterThan(5);
    }

    [Fact]
    public void GeoGuessrApi_CanBeBuilt()
    {
        using var host = BuildHost();

        host.Services.GetRequiredService<IGeoGuessrApi>().Should().NotBeNull();
    }

    [Fact]
    public void ScopedServices_CanBeBuilt()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IShadowStore>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IDiscordPublisher>().Should().NotBeNull();
    }

    [Fact]
    public void Configuration_BindsThroughTheRealPath()
    {
        using var host = BuildHost(("Events:FriendPresence:Enabled", "true"), ("Events:FriendPresence:ChannelId", "42"));

        var options = host.Services.GetRequiredService<IOptions<ShadowOptions>>().Value;

        options.Events["FriendPresence"].Should().Match<EventOptions>(item => item.Enabled && item.ChannelId == 42);
        options.Polling.MaxCatchUpAge.Should().Be(TimeSpan.FromDays(7));
    }

    [Fact]
    public void InvalidConfiguration_IsRefusedWithEveryProblemListed()
    {
        using var host = BuildHost(("GeoGuessr:NcfaToken", ""), ("Discord:Token", ""));

        var read = () => host.Services.GetRequiredService<IOptions<ShadowOptions>>().Value;

        read.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().HaveCount(2);
    }
}
