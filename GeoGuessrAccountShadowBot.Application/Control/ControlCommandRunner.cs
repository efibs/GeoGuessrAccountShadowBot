// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Control;

/// <summary>
/// The one gate in front of every action taken as the account. Keeping it here rather than in the
/// Discord layer means no new command or button can forget the check.
/// </summary>
public sealed partial class ControlCommandRunner(
    IGuildRoleReader roles,
    IServiceScopeFactory scopes,
    IOptions<ShadowOptions> options,
    TimeProvider timeProvider,
    ILogger<ControlCommandRunner> logger)
{
    private static readonly Error NotAuthorized =
        new(ErrorKind.Forbidden, "control.forbidden", "You don't have a role that may act as this account.");

    /// <summary>Checks the person's roles, runs the command, and records the attempt either way.</summary>
    public async Task<Result> RunAsync(
        ulong discordUserId,
        string action,
        string target,
        Func<CancellationToken, Task<Result>> command,
        CancellationToken cancellationToken)
    {
        var allowed = await IsAuthorizedAsync(discordUserId, cancellationToken).ConfigureAwait(false);
        var result = allowed ? await command(cancellationToken).ConfigureAwait(false) : NotAuthorized;

        var outcome = result.IsSuccess ? "success" : result.Error!.Code;
        LogControlAction(discordUserId, action, target, outcome);

        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();
            store.AddAudit(new ControlAudit(timeProvider.GetUtcNow(), discordUserId, action, target, outcome));
            await store.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<bool> IsAuthorizedAsync(ulong discordUserId, CancellationToken cancellationToken)
    {
        var authorized = options.Value.Control.AuthorizedRoleIds;
        if (authorized.Count == 0)
        {
            return false;
        }

        var memberRoles = await roles.GetRoleIdsAsync(discordUserId, cancellationToken).ConfigureAwait(false);
        return memberRoles.Any(authorized.Contains);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord user {DiscordUserId} ran {Action} on {Target}: {Outcome}")]
    private partial void LogControlAction(ulong discordUserId, string action, string target, string outcome);
}
