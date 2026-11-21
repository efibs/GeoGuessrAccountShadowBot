// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Automation;
using GeoGuessrAccountShadowBot.Application.Connection;
using GeoGuessrAccountShadowBot.Application.Control;
using GeoGuessrAccountShadowBot.Application.Handlers;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Pipeline;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Application.Reconciliation;
using GeoGuessrAccountShadowBot.Application.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application;

/// <summary>
/// Registers the application layer.
/// </summary>
public static class DependencyInjection
{
    /// <param name="services">The services.</param>
    /// <param name="headless">
    /// Whether the bot runs without Discord. Then nothing delivers to Discord, and nobody has a role
    /// that may act as the account.
    /// </param>
    public static IServiceCollection AddShadowApplication(this IServiceCollection services, bool headless = false)
    {
        services.AddSingleton<IValidateOptions<ShadowOptions>, ShadowOptionsValidator>();
        services.AddSingleton<IValidateOptions<ShadowOptions>, AutomationOptionsValidator>();
        services.AddSingleton<IValidateOptions<ShadowOptions>, ExternalOptionsValidator>();
        services.AddSingleton(TimeProvider.System);

        var handlers = EventRouter.DiscoverHandlers(typeof(DependencyInjection).Assembly).ToArray();
        foreach (var handler in handlers)
        {
            services.AddScoped(handler.Contract, handler.Implementation);
        }

        services.AddSingleton(new EventRouter(handlers.Select(handler => handler.EventType)));
        services.AddSingleton<EventBus>();
        services.AddSingleton<IEventPublisher>(provider => provider.GetRequiredService<EventBus>());
        services.AddSingleton<EventProcessor>();
        services.AddSingleton<WorkerSupervision>();
        services.AddScoped<EventContext>();
        services.AddScoped<IEventContext>(provider => provider.GetRequiredService<EventContext>());

        services.AddSingleton<AccountContext>();
        services.AddSingleton<AccountInitializer>();
        services.AddSingleton<ConnectionStatus>();
        services.AddSingleton<ConnectionAnnouncer>();
        services.AddSingleton(provider =>
        {
            var geoGuessr = provider.GetRequiredService<IOptions<ShadowOptions>>().Value.GeoGuessr;
            return new ConnectionPolicy(geoGuessr.Socket, geoGuessr.Yield);
        });

        services.AddSingleton<PresenceTracker>();
        services.AddSingleton<OutboxSignal>();
        services.AddSingleton<OutboxFilter>();
        services.AddSingleton<ReconcileRequests>();
        services.AddSingleton<ReconciliationCoordinator>();
        services.AddSingleton<ControlCommandRunner>();
        services.AddSingleton<DmReplyService>();
        services.AddSingleton<AccountControlService>();
        services.AddSingleton<PendingReplyMonitor>();
        services.AddScoped<PlayerDirectory>();

        services.AddSingleton<ConfiguredAutomationRules>();
        services.AddSingleton<AutomationRuleRegistry>();
        services.AddSingleton<AutomationSignal>();
        services.AddSingleton<AutomationRateGate>();
        services.AddSingleton<AutomationEngine>();
        services.AddSingleton<AutomationService>();
        services.AddSingleton<AutomationWorker>();
        services.AddSingleton<AutomationScheduler>();
        services.AddScoped<AutomationRunFactory>();
        services.AddScoped<AutomationCapture>();

        services.AddSingleton<ExternalEventFeed>();
        services.AddSingleton<ExternalFeedSignal>();
        services.AddScoped<ExternalEventCapture>();
        services.AddSingleton<ExternalApiService>();
        services.AddSingleton<ExternalSources>();
        services.AddSingleton<SourceStatusTracker>();

        if (headless)
        {
            services.AddSingleton<IGuildRoleReader, NoGuildRoles>();
        }

        services.AddHostedService<EventPipelineWorker>();
        if (!headless)
        {
            services.AddHostedService<OutboxDispatcher>();
        }

        services.AddHostedService<ReconciliationWorker>();
        services.AddHostedService<RankedPollingWorker>();
        services.AddHostedService<ClubApplicationsPollingWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<PendingReplyMonitor>());
        services.AddHostedService(provider => provider.GetRequiredService<AutomationWorker>());
        services.AddHostedService(provider => provider.GetRequiredService<AutomationScheduler>());
        services.AddHostedService<SourceEventWorker>();
        services.AddHostedService<ExternalFeedPruner>();

        return services;
    }
}
