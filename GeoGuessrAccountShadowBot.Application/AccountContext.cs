// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Application.Reconciliation;
using GeoGuessrAccountShadowBot.Domain.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application;

/// <summary>
/// Who the bot is shadowing. Everything that needs the account waits for <see cref="Ready"/>.
/// </summary>
public sealed class AccountContext
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string UserId { get; private set; } = "";

    public string? ClubId { get; private set; }

    public Task Ready => _ready.Task;

    public void Set(string userId, string? clubId)
    {
        UserId = userId;
        ClubId = clubId;
        _ready.TrySetResult();
    }
}

/// <summary>
/// Establishes the account at startup. It refuses to continue if the stored data belongs to another
/// account, because swapping the token would otherwise attach new DMs to the old account's threads.
/// </summary>
public sealed class AccountInitializer(
    IGeoGuessrApi api,
    IServiceScopeFactory scopes,
    IOptions<ShadowOptions> options,
    AccountContext account)
{
    private const string AccountStateKey = "account";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var userId = await api.GetOwnUserIdAsync(cancellationToken).ConfigureAwait(false);

        var club = options.Value.GeoGuessr.Club;
        var clubId = club.Enabled
            ? club.ClubId ?? await api.GetOwnClubIdAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();

            var stored = await store.ReadStateAsync<StoredAccount>(AccountStateKey, cancellationToken).ConfigureAwait(false);
            if (stored is not null && stored.UserId != userId)
            {
                throw new InvalidOperationException(
                    "The database belongs to a different GeoGuessr account. Use a new database for this token.");
            }

            await store.WriteStateAsync(AccountStateKey, new StoredAccount(userId), cancellationToken).ConfigureAwait(false);

            // A switched-off scope stops being tracked, so turning it back on must start with a
            // fresh silent baseline rather than a flood of everything that happened meanwhile.
            if (!options.Value.IsEventEnabled(EventKeys.DmMessage))
            {
                await store.DeleteStateAsync(BaselineScopes.StateKey(BaselineScopes.Chat), cancellationToken).ConfigureAwait(false);
            }

            if (!options.Value.IsEventEnabled(EventKeys.ClubChat))
            {
                await store.DeleteStateAsync(BaselineScopes.StateKey(BaselineScopes.ClubChat), cancellationToken).ConfigureAwait(false);
            }

            await store.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        account.Set(userId, clubId);
    }

    private sealed record StoredAccount(string UserId);
}
