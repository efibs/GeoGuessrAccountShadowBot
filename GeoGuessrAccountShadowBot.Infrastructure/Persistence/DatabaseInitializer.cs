// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GeoGuessrAccountShadowBot.Infrastructure.Persistence;

/// <summary>
/// Brings the database up to date. It's registered as the first hosted service, so that nothing
/// touches the database before this has finished.
/// </summary>
internal sealed class DatabaseInitializer(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var database = scope.ServiceProvider.GetRequiredService<ShadowDbContext>();

            var path = new SqliteConnectionStringBuilder(database.Database.GetConnectionString()).DataSource;
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            // A crash during a migration can leave EF's lock row behind, and it would block every
            // later start. Only one instance ever uses this file, so the lock protects nothing.
            await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await database.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS __EFMigrationsLock", cancellationToken).ConfigureAwait(false);

            await database.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

            // Write-ahead logging lets the reconciler read while the pipeline commits.
            await database.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL", cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
