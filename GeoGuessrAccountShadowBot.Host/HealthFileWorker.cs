// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Application.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GeoGuessrAccountShadowBot.Host;

/// <summary>
/// Touches a file for as long as the bot works, which the Docker health check reads. That avoids
/// opening a port. A rejected token or a yielded socket doesn't count as unhealthy, because
/// restarting wouldn't fix either, and both are already reported in Discord. A source that has
/// been down longer than its <c>UnhealthyAfter</c> does count, so the outage shows in Docker.
/// </summary>
internal sealed class HealthFileWorker(IServiceScopeFactory scopes, SourceStatusTracker sources, TimeProvider timeProvider) : BackgroundService
{
    private const string HealthFilePath = "/tmp/shadow-healthy";
    private const string ProbeKey = "health-probe";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();
                await store.ReadStateAsync<object>(ProbeKey, stoppingToken).ConfigureAwait(false);
            }

            if (sources.IsHealthy())
            {
                await File.WriteAllTextAsync(HealthFilePath, "", stoppingToken).ConfigureAwait(false);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
