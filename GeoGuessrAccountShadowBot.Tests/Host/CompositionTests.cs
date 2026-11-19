// SPDX-License-Identifier: GPL-2.0-or-later

using AwesomeAssertions;
using Discord.Interactions;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Discord.Input;
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
    public void SettingsFiles_AreFoundNextToTheExecutable_WhateverTheWorkingDirectory()
    {
        var builder = ShadowHost.CreateBuilder([]);

        Path.TrimEndingDirectorySeparator(builder.Environment.ContentRootPath)
            .Should().Be(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
    }

    [Fact]
    public void EveryHostedService_CanBeBuilt()
    {
        using var host = BuildHost();

        host.Services.GetServices<IHostedService>().Should().HaveCountGreaterThan(5);
    }

    /// <summary>
    /// Discord.Net checks handlers only when a module is added on Ready. A module it refuses
    /// throws there, and stops every module and the command registration after it (issue #21).
    /// </summary>
    [Fact]
    public async Task EveryInteractionModule_IsAcceptedByDiscordNet()
    {
        using var host = BuildHost(("Control:Automation:Enabled", "true"));
        var interactions = host.Services.GetRequiredService<InteractionService>();

        // Every module in the assembly, so a new one is checked without anyone listing it here.
        var modules = await interactions.AddModulesAsync(typeof(AutomationModule).Assembly, host.Services);

        modules.Select(module => module.Name).Should().Contain(
            [nameof(FriendRequestModule), nameof(ShadowCommandsModule), nameof(AutomationModule), nameof(AutomationInteractionsModule)]);
        interactions.ModalCommands.Select(command => command.Name).Should().BeEquivalentTo(["au:create", "au:edit:*"]);
        interactions.ComponentCommands.Select(command => command.Name).Should().Contain(["au:ok:*", "au:no:*"]);
    }

    /// <summary>
    /// A worker that throws stops the whole host, and the restart resends what was in flight
    /// (issue #19). Only the health probe may do that, because a failing probe should restart the
    /// container.
    /// </summary>
    [Fact]
    public void EveryWorker_SurvivesItsOwnCrash()
    {
        using var host = BuildHost(("Control:Automation:Enabled", "true"));

        var unsupervised = host.Services.GetServices<IHostedService>()
            .OfType<BackgroundService>()
            .Where(service => service is not GeoGuessrAccountShadowBot.Application.Pipeline.SupervisedBackgroundService)
            .Select(service => service.GetType().Name);

        unsupervised.Should().BeEquivalentTo(["HealthFileWorker"]);
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

    /// <summary>A headless instance has no Discord at all, so nothing may wait for it.</summary>
    [Fact]
    public void Headless_BuildsWithoutAnythingOfDiscord()
    {
        using var host = BuildHost(
            ("Discord:Enabled", "false"), ("Discord:Token", ""), ("Discord:GuildId", "0"), ("Forum:ChannelId", ""),
            ("Control:Automation:Enabled", "true"));

        host.Services.GetRequiredService<IOptions<ShadowOptions>>().Value.IsHeadless.Should().BeTrue();
        host.Services.GetServices<IHostedService>().Select(service => service.GetType().Name)
            .Should().NotContain(["DiscordBotService", "OutboxDispatcher"])
            .And.Contain(["SourceEventWorker", "ExternalApiServer"]);
        host.Services.GetRequiredService<IGuildRoleReader>().Should().BeOfType<GeoGuessrAccountShadowBot.Application.Control.NoGuildRoles>();
    }

    [Fact]
    public async Task ExternalApi_AndSources_StartAndStop_ThroughTheRealGraph()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var host = BuildHost(
            ("ExternalApi:Enabled", "true"),
            ("ExternalApi:Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("ExternalApi:Clients:main:Token", "token-of-the-main-instance-0123456789"),
            ("ExternalApi:Clients:main:Data:0", "Players"),
            ("Sources:dragon:Url", "http://dragon-headless:8080"),
            ("Sources:dragon:Token", "token-of-the-main-instance-0123456789"),
            ("Sources:dragon:UnhealthyAfter", ""));

        // Empty means the health check never fails on this source, as documented.
        host.Services.GetRequiredService<IOptions<ShadowOptions>>().Value.Sources["dragon"].UnhealthyAfter.Should().BeNull();
        host.Services.GetRequiredService<GeoGuessrAccountShadowBot.Application.Sources.ExternalSources>().Find("dragon").Should().NotBeNull();

        var server = host.Services.GetServices<IHostedService>().OfType<GeoGuessrAccountShadowBot.Api.ExternalApiServer>().Single();
        await server.StartAsync(CancellationToken.None);
        server.Port.Should().Be(port);
        await server.StopAsync(CancellationToken.None);
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
