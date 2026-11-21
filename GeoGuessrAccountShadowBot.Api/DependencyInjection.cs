// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.DependencyInjection;

namespace GeoGuessrAccountShadowBot.Api;

/// <summary>
/// Registers the external API, which other instances read this one through. It only listens
/// while <c>ExternalApi:Enabled</c> is on.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddShadowExternalApi(this IServiceCollection services)
    {
        services.AddHostedService<ExternalApiServer>();
        return services;
    }
}
