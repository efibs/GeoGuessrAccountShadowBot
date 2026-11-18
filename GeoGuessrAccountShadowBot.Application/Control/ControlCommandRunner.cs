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

    /// <summary>The audit user id of actions an automation took. Discord never hands out id 0.</summary>
    public const ulong AutomationActor = 0;

    /// <summary>Checks the person's roles, runs the command, and records the attempt either way.</summary>
    /// <param name="discordUserId">Who asked.</param>
    /// <param name="action">A stable name for the audit log.</param>
    /// <param name="target">What the action is on, for the audit log.</param>
    /// <param name="command">The action, which only runs for an authorized person.</param>
    /// <param name="cancellationToken">Stops the action.</param>
    /// <param name="allowedRoles">The roles that may run it, instead of <c>Control:AuthorizedRoleIds</c>.</param>
    public async Task<Result> RunAsync(
        ulong discordUserId,
        string action,
        string target,
        Func<CancellationToken, Task<Result>> command,
        CancellationToken cancellationToken,
        IReadOnlyCollection<ulong>? allowedRoles = null)
    {
        var allowed = await IsAuthorizedAsync(discordUserId, allowedRoles ?? options.Value.Control.AuthorizedRoleIds, cancellationToken).ConfigureAwait(false);
        var result = allowed ? await command(cancellationToken).ConfigureAwait(false) : NotAuthorized;

        await RecordAsync(discordUserId, action, target, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Runs an action an automation rule decided on. The authority came from whoever wrote the rule,
    /// so no role is checked, but the action is recorded under the rule's name like any other.
    /// </summary>
    public async Task<Result> RunAsAutomationAsync(
        string ruleName,
        string action,
        string target,
        Func<CancellationToken, Task<Result>> command,
        CancellationToken cancellationToken)
    {
        var result = await command(cancellationToken).ConfigureAwait(false);
        await RecordAsync(AutomationActor, $"auto:{ruleName}:{action}", target, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Whether the person has one of the roles. An empty list allows nobody.</summary>
    public async Task<bool> IsAuthorizedAsync(ulong discordUserId, IReadOnlyCollection<ulong> authorized, CancellationToken cancellationToken)
    {
        if (authorized.Count == 0)
        {
            return false;
        }

        var memberRoles = await roles.GetRoleIdsAsync(discordUserId, cancellationToken).ConfigureAwait(false);
        return memberRoles.Any(authorized.Contains);
    }

    private async Task RecordAsync(ulong discordUserId, string action, string target, Result result, CancellationToken cancellationToken)
    {
        var outcome = result.IsSuccess ? "success" : result.Error!.Code;
        LogControlAction(discordUserId, action, target, outcome);

        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();
            store.AddAudit(new ControlAudit(timeProvider.GetUtcNow(), discordUserId, action, target, outcome));
            await store.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord user {DiscordUserId} ran {Action} on {Target}: {Outcome}")]
    private partial void LogControlAction(ulong discordUserId, string action, string target, string outcome);
}
