// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Threading.RateLimiting;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Http;
using GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Socket;
using GeoGuessrAccountShadowBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Refit;

namespace GeoGuessrAccountShadowBot.Infrastructure;

/// <summary>
/// Registers GeoGuessr access and persistence.
/// </summary>
public static class DependencyInjection
{
    private const string HttpClientName = "geoguessr";
    private const int RequestsPerSecond = 5;
    private const int MaxQueuedRequests = 100;

    /// <summary>Registers the database. This must come before every other hosted service.</summary>
    public static IServiceCollection AddShadowPersistence(this IServiceCollection services, string databasePath)
    {
        services.AddDbContext<ShadowDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddScoped<IShadowStore, EfShadowStore>();
        services.AddHostedService<DatabaseInitializer>();
        return services;
    }

    public static IServiceCollection AddShadowGeoGuessr(this IServiceCollection services)
    {
        services.AddShadowGeoGuessrHttp();

        services.AddSingleton<CurrentSocket>();
        services.AddSingleton<ISocketConnector, ClientWebSocketConnector>();
        services.AddSingleton<IGeoGuessrChatSender, GeoGuessrChatSender>();
        services.AddHostedService<ConnectionSupervisor>();
        return services;
    }

    /// <summary>Registers the HTTP client stack, which is also what the round-trip tests run on.</summary>
    internal static IServiceCollection AddShadowGeoGuessrHttp(this IServiceCollection services)
    {
        services.AddTransient<GeoGuessrSessionHandler>();

        var client = services.AddHttpClient(HttpClientName, (provider, client) =>
                client.BaseAddress = provider.GetRequiredService<IOptions<ShadowOptions>>().Value.GeoGuessr.ApiBaseUrl)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // The cookie is set by hand and redirects stay off, so it can't follow one elsewhere.
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(15)
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        // The order matters: each attempt of the resilience pipeline passes the session handler.
        client.AddResilienceHandler("geoguessr", AddResilience);
        client.AddHttpMessageHandler<GeoGuessrSessionHandler>();

        // Refit's AddRefitClient needs its optional reflection package, and this doesn't.
        services.AddSingleton(provider => RestService.For<IGeoGuessrRestApi>(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));

        services.AddSingleton<IGeoGuessrApi, GeoGuessrHttpGateway>();
        return services;
    }

    /// <summary>
    /// Retries only requests that can't change the account, because a retried accept or decline
    /// could act twice. The rate limiter sits inside the retry, so that retries use up tokens too.
    /// </summary>
    private static void AddResilience(ResiliencePipelineBuilder<HttpResponseMessage> pipeline)
    {
        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldHandle = arguments => ValueTask.FromResult(
                arguments.Context.GetRequestMessage()?.Method == HttpMethod.Get
                && HttpClientResiliencePredicates.IsTransient(arguments.Outcome))
        });

        pipeline.AddTimeout(TimeSpan.FromSeconds(30));

        pipeline.AddRateLimiter(new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = RequestsPerSecond,
            TokensPerPeriod = RequestsPerSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = MaxQueuedRequests,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        }));
    }
}
