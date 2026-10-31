// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application.Pipeline;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using NetArchTest.Rules;

namespace GeoGuessrAccountShadowBot.Tests.Architecture;

public sealed class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(ShadowEvent).Assembly;
    private static readonly Assembly Application = typeof(EventRouter).Assembly;
    private static readonly Assembly Infrastructure = typeof(GeoGuessrAccountShadowBot.Infrastructure.DependencyInjection).Assembly;
    private static readonly Assembly Discord = typeof(GeoGuessrAccountShadowBot.Discord.DependencyInjection).Assembly;

    private static void ShouldNotDependOn(Assembly assembly, params string[] forbidden)
    {
        var result = Types.InAssembly(assembly).Should().NotHaveDependencyOnAny(forbidden).GetResult();

        (result.FailingTypeNames ?? []).Should().BeEmpty();
    }

    [Fact]
    public void Domain_DependsOnNothing()
    {
        ShouldNotDependOn(Domain, "GeoGuessrAccountShadowBot.Application", "GeoGuessrAccountShadowBot.Infrastructure", "GeoGuessrAccountShadowBot.Discord", "Microsoft.Extensions", "Discord");
    }

    [Fact]
    public void Application_DoesNotKnowTheOutsideWorld()
    {
        ShouldNotDependOn(Application, "GeoGuessrAccountShadowBot.Infrastructure", "GeoGuessrAccountShadowBot.Discord", "Discord", "Refit", "Microsoft.EntityFrameworkCore", "System.Net.WebSockets");
    }

    [Fact]
    public void Infrastructure_AndDiscord_StayApart()
    {
        ShouldNotDependOn(Infrastructure, "GeoGuessrAccountShadowBot.Discord", "Discord");
        ShouldNotDependOn(Discord, "GeoGuessrAccountShadowBot.Infrastructure", "Refit", "Microsoft.EntityFrameworkCore");
    }

    [Fact]
    public void Handlers_DoNoNetworkCalls()
    {
        var offenders = EventRouter.DiscoverHandlers(Application)
            .Select(handler => handler.Implementation)
            .Where(type => type.GetConstructors().SelectMany(constructor => constructor.GetParameters())
                .Any(parameter => parameter.ParameterType == typeof(IGeoGuessrApi)
                                  || parameter.ParameterType == typeof(IGeoGuessrChatSender)
                                  || parameter.ParameterType == typeof(IDiscordPublisher)));

        offenders.Should().BeEmpty("a slow call in a handler stalls every other event");
    }

    [Fact]
    public void ConcreteClasses_AreSealed()
    {
        foreach (var assembly in new[] { Domain, Application, Infrastructure, Discord })
        {
            var result = Types.InAssembly(assembly)
                .That().AreClasses().And().AreNotAbstract().And().DoNotHaveNameMatching("<|Migration|Snapshot|Program")
                .And().DoNotResideInNamespace("GeoGuessrAccountShadowBot.Infrastructure.Persistence.Migrations")
                .Should().BeSealed().GetResult();

            (result.FailingTypeNames ?? []).Should().BeEmpty();
        }
    }
}

public sealed class EventCatalogTests
{
    private static readonly Assembly Domain = typeof(ShadowEvent).Assembly;

    [Fact]
    public void EveryEvent_HasAHandler()
    {
        var events = Domain.GetTypes().Where(type => type is { IsAbstract: false } && type.IsSubclassOf(typeof(ShadowEvent)));
        var handled = EventRouter.DiscoverHandlers(typeof(EventRouter).Assembly).Select(handler => handler.EventType);

        events.Should().BeEquivalentTo(handled);
    }

    [Fact]
    public void Configuration_ListsEverySwitch()
    {
        var configured = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.reference.json"))
            .Build()
            .GetSection("Events")
            .GetChildren()
            .Select(section => section.Key);

        configured.Should().BeEquivalentTo(EventKeys.All);
    }

    [Fact]
    public void EveryConstant_IsInTheCatalog()
    {
        var constants = typeof(EventKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!);

        constants.Should().BeEquivalentTo(EventKeys.All);
    }

    [Fact]
    public void ReferenceConfiguration_IsValidOnceSecretsAreSet()
    {
        TestOptions.Valid().Events.Should().NotBeEmpty();
    }
}
