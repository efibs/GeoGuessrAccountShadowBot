// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GeoGuessrAccountShadowBot.Application.Connection;
using GeoGuessrAccountShadowBot.Application.Control;
using GeoGuessrAccountShadowBot.Application.Handlers;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Application.Reconciliation;
using GeoGuessrAccountShadowBot.Domain;
using GeoGuessrAccountShadowBot.Domain.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Automation;

/// <summary>
/// Runs rules. A run first has to be admitted, which checks its age, condition and limits. Then it
/// steps through the compiled rule, saving after every step. An action as the account is marked
/// before it starts, so a crash during one ends the run instead of repeating it: like replies typed
/// in Discord, an automated message is sent at most once.
/// </summary>
public sealed partial class AutomationEngine(
    IServiceScopeFactory scopes,
    AutomationRuleRegistry registry,
    AutomationRateGate rateGate,
    AutomationSignal signal,
    OutboxSignal outboxSignal,
    ControlCommandRunner runner,
    IGeoGuessrApi api,
    IGeoGuessrChatSender sender,
    AccountContext account,
    ConnectionStatus connection,
    ReconcileRequests reconcile,
    IOptions<ShadowOptions> options,
    TimeProvider timeProvider,
    ILogger<AutomationEngine> logger)
{
    private readonly AutomationRuleRegistry _registry = registry;
    private readonly AutomationRateGate _rateGate = rateGate;
    private readonly AutomationSignal _signal = signal;
    private readonly ControlCommandRunner _runner = runner;
    private readonly IGeoGuessrApi _api = api;
    private readonly IGeoGuessrChatSender _sender = sender;
    private readonly AccountContext _account = account;
    private readonly ConnectionStatus _connection = connection;
    private readonly ReconcileRequests _reconcile = reconcile;
    private readonly IOptions<ShadowOptions> _options = options;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>Every change to a run goes through here, so a click can't race the worker.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private const int MaxLogLines = 40;
    private const int MaxPostponements = 20;
    private const int MaxRenderedLength = 1000;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Continues a run from where it stopped.</summary>
    public Task ExecuteAsync(long runId, CancellationToken cancellationToken) =>
        WithRunAsync(runId, (execution, _) => execution.RunAsync(cancellationToken), cancellationToken);

    /// <summary>
    /// Ends a run that keeps throwing, so that it can't block the worker.
    /// </summary>
    public Task FailAsync(long runId, string reason, CancellationToken cancellationToken) =>
        WithRunAsync(runId, (execution, _) => execution.FinishAsync(AutomationRunState.Failed, reason, cancellationToken), cancellationToken);

    /// <summary>Answers an approval card.</summary>
    /// <returns>Null when it worked, or why it didn't.</returns>
    public async Task<string?> DecideAsync(long runId, bool approve, ulong decidedBy, CancellationToken cancellationToken)
    {
        string? problem = "This run is no longer waiting for approval.";
        await WithRunAsync(runId, async (execution, run) =>
        {
            if (run.State != AutomationRunState.AwaitingApproval || run.ResumeAt <= _timeProvider.GetUtcNow())
            {
                problem = "This run is no longer waiting for approval.";
                return;
            }

            problem = null;
            await execution.DecideAsync(approve, decidedBy, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (problem is null)
        {
            _signal.Wake();
        }

        return problem;
    }

    /// <summary>Ends an unfinished run, closing its approval card if it has one.</summary>
    public Task CancelAsync(long runId, string reason, CancellationToken cancellationToken) =>
        WithRunAsync(runId, (execution, _) => execution.CancelAsync(reason, cancellationToken), cancellationToken);

    private async Task WithRunAsync(long runId, Func<Execution, AutomationRun, Task> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var automations = scope.ServiceProvider.GetRequiredService<IAutomationStore>();
                if (await automations.FindRunAsync(runId, cancellationToken).ConfigureAwait(false) is not { IsFinished: false } run)
                {
                    return;
                }

                await action(new Execution(this, scope.ServiceProvider, run, preview: false), run).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        outboxSignal.Wake();
    }

    /// <summary>
    /// Runs a rule as a dry run that saves nothing, and returns what it would do.
    /// </summary>
    public async Task<IReadOnlyList<string>> PreviewAsync(AutomationRun run, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var execution = new Execution(this, scope.ServiceProvider, run with { DryRun = true }, preview: true);
            await execution.RunAsync(cancellationToken).ConfigureAwait(false);
            return execution.Lines;
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]{24}$")]
    private static partial Regex UserIdPattern();

    [GeneratedRegex(@"geoguessr\.com/(?:[a-z]{2}/)?user/([0-9a-fA-F]{24})")]
    private static partial Regex ProfileUrlPattern();

    /// <summary>Reads a user id, or a profile link, as typed in Discord or rendered by a template.</summary>
    public static string? ParseUserId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (UserIdPattern().IsMatch(text))
        {
            return text.ToLowerInvariant();
        }

        return ProfileUrlPattern().Match(text) is { Success: true } match ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Automation {Rule} run {RunId} ended as {State}")]
    private partial void LogRunFinished(string rule, long runId, AutomationRunState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automation {Rule} run {RunId} failed: {Code}")]
    private partial void LogRunFailed(string rule, long runId, string code);

    /// <summary>The state of one pass through a run.</summary>
    private sealed class Execution
    {
        private readonly AutomationEngine _engine;
        private readonly IShadowStore _store;
        private readonly IAutomationStore _automations;
        private readonly PlayerDirectory _players;
        private readonly AutomationRunFactory _factory;
        private readonly bool _preview;
        private readonly List<string> _log;
        private AutomationRun _run;
        private AutomationRule? _rule;
        private AutomationPlayer? _player;
        private AutomationScope? _scope;

        public Execution(AutomationEngine engine, IServiceProvider services, AutomationRun run, bool preview)
        {
            _engine = engine;
            _store = services.GetRequiredService<IShadowStore>();
            _automations = services.GetRequiredService<IAutomationStore>();
            _players = services.GetRequiredService<PlayerDirectory>();
            _factory = services.GetRequiredService<AutomationRunFactory>();
            _preview = preview;
            _run = run;
            _log = [.. run.Log];
        }

        public IReadOnlyList<string> Lines => _log;

        private ShadowOptions Settings => _engine._options.Value;

        private DateTimeOffset Now => _engine._timeProvider.GetUtcNow();

        private AutomationRule Rule => _rule!;

        private AutomationScope Scope => _scope!;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            _rule = _engine._registry.Compile(_run.RuleJson, AutomationRuleOrigin.Discord);
            if (_rule is null)
            {
                await FinishAsync(AutomationRunState.Failed, "The rule this run belongs to no longer compiles.", cancellationToken).ConfigureAwait(false);
                return;
            }

            switch (_run.State)
            {
                case AutomationRunState.Acting:
                    var step = _run.ProgramCounter < Rule.Program.Count ? Rule.Program[_run.ProgramCounter].Path : "a step";
                    await FinishAsync(AutomationRunState.Failed, $"The bot stopped during {step}. It isn't repeated, because it may already have happened.", cancellationToken).ConfigureAwait(false);
                    return;
                case AutomationRunState.AwaitingApproval:
                    await ExpireAsync(cancellationToken).ConfigureAwait(false);
                    return;
            }

            if (!_preview && !await IsStillWantedAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                if (!await PrepareScopeAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                if (_run.AdmittedAt is null && !await AdmitAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                await StepAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (AutomationScriptException exception)
            {
                var at = _run.ProgramCounter < Rule.Program.Count && _run.AdmittedAt is not null ? Rule.Program[_run.ProgramCounter].Path : "When";
                await FinishAsync(AutomationRunState.Failed, $"{at}: {exception.Message}", cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>A rule that was removed or switched off takes its waiting runs with it.</summary>
        private async Task<bool> IsStillWantedAsync(CancellationToken cancellationToken)
        {
            var live = _engine._registry.Find(_run.RuleName);
            if (live is not null && (live.Enabled || _run.Manual))
            {
                return true;
            }

            await FinishAsync(AutomationRunState.Cancelled, "The rule was removed or switched off.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        private async Task<bool> PrepareScopeAsync(CancellationToken cancellationToken)
        {
            try
            {
                _player = await ResolvePlayerAsync(_run.SubjectUserId, Rule.ReadsPlayer, cancellationToken).ConfigureAwait(false);
            }
            catch (GeoGuessrUnauthorizedException)
            {
                _engine._connection.ReportUnauthorized();
                await PostponeAsync("GeoGuessr rejected the session cookie.", TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
                return false;
            }
            catch (GeoGuessrUnavailableException)
            {
                await PostponeAsync("GeoGuessr couldn't be reached to read the player.", RetryDelay, cancellationToken).ConfigureAwait(false);
                return false;
            }

            _scope = new AutomationScope(AutomationVariables.Build(Rule, _run, _player, _engine._account, Now));
            return true;
        }

        private async Task<AutomationPlayer?> ResolvePlayerAsync(string? userId, bool withProfile, CancellationToken cancellationToken)
        {
            if (userId is null)
            {
                return null;
            }

            var friendships = await _store.ReadStateAsync<FriendshipSet>(SnapshotKeys.Friendships, cancellationToken).ConfigureAwait(false);
            var roster = await _store.ReadStateAsync<ClubRoster>(SnapshotKeys.ClubRoster, cancellationToken).ConfigureAwait(false);
            var applications = await _store.ReadStateAsync<ClubApplicationSet>(SnapshotKeys.ClubApplications, cancellationToken).ConfigureAwait(false);

            PlayerProfile? profile = null;
            string nick;
            if (withProfile)
            {
                profile = await _engine._api.GetPlayerProfileAsync(userId, cancellationToken).ConfigureAwait(false);
                if (profile is not null)
                {
                    await _store.SavePlayerNameAsync(new PlayerName(userId, profile.Nick, Now), cancellationToken).ConfigureAwait(false);
                }

                nick = profile?.Nick ?? (await _players.GetNickAsync(userId, cancellationToken).ConfigureAwait(false)).Value;
            }
            else
            {
                nick = (await _players.GetNickAsync(userId, cancellationToken).ConfigureAwait(false)).Value;
            }

            return new AutomationPlayer(
                userId,
                nick,
                profile,
                friendships?.Friends.Contains(userId) ?? false,
                friendships?.Incoming.Contains(userId) ?? false,
                friendships?.Outgoing.Contains(userId) ?? false,
                roster?.Members.ContainsKey(userId) ?? false,
                applications?.Pending.ContainsKey(userId) ?? false);
        }

        /// <summary>Checks age, condition and limits. A manual run skips the age and the limits.</summary>
        private async Task<bool> AdmitAsync(CancellationToken cancellationToken)
        {
            var now = Now;

            if (!_run.Manual && now - _run.OccurredAt > Rule.MaxAge)
            {
                return await SkipAsync($"The event was {Describe(now - _run.OccurredAt)} old, and the rule allows {Describe(Rule.MaxAge)}.", cancellationToken).ConfigureAwait(false);
            }

            if (Rule.When is { } when && !Scope.Evaluate(when))
            {
                return await SkipAsync("The condition in When was false.", cancellationToken).ConfigureAwait(false);
            }

            if (!_run.Manual && await CheckLimitsAsync(now, cancellationToken).ConfigureAwait(false) is { } limited)
            {
                return await SkipAsync(limited, cancellationToken).ConfigureAwait(false);
            }

            _run = _run with { AdmittedAt = now };
            if (_preview)
            {
                return true;
            }

            if (_run.SubjectUserId is { } subject && !_run.DryRun)
            {
                await _automations.RecordRunForPlayerAsync(_run.RuleName, subject, now, cancellationToken).ConfigureAwait(false);
            }

            if (Rule.StopOtherRules)
            {
                foreach (var other in await _automations.ReadOpenRunsInGroupAsync(_run.GroupKey, _run.Id, cancellationToken).ConfigureAwait(false))
                {
                    if (other.AdmittedAt is null)
                    {
                        await _automations.UpdateRunAsync(other with
                        {
                            State = AutomationRunState.Skipped,
                            Outcome = $"The rule {_run.RuleName} took this event first.",
                            FinishedAt = now,
                            EventJson = "{}"
                        }, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        private async Task<string?> CheckLimitsAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            var limits = Rule.Limits;

            if (_run.SubjectUserId is { } subject && (limits.OncePerPlayer || limits.PlayerCooldown is not null))
            {
                var last = await _automations.FindLastRunForPlayerAsync(_run.RuleName, subject, cancellationToken).ConfigureAwait(false);
                if (last is { } at)
                {
                    if (limits.OncePerPlayer && (limits.OncePerPlayerWithin is not { } window || now - at < window))
                    {
                        return limits.OncePerPlayerWithin is null
                            ? "The rule already ran for this player, and runs once per player."
                            : $"The rule already ran for this player {Describe(now - at)} ago, and runs once per {Describe(limits.OncePerPlayerWithin.Value)}.";
                    }

                    if (limits.PlayerCooldown is { } cooldown && now - at < cooldown)
                    {
                        return $"The rule ran for this player {Describe(now - at)} ago, and waits {Describe(cooldown)} between runs for a player.";
                    }
                }
            }

            if (limits.Cooldown is { } ruleCooldown
                && await _automations.CountAdmittedRunsAsync(_run.RuleName, null, now - ruleCooldown, cancellationToken).ConfigureAwait(false) > 0)
            {
                return $"The rule ran less than {Describe(ruleCooldown)} ago.";
            }

            if (limits.MaxRunsPerHour is { } max
                && await _automations.CountAdmittedRunsAsync(_run.RuleName, null, now - TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false) >= max)
            {
                return $"The rule already ran {max} times in the last hour.";
            }

            return null;
        }

        private async Task StepAsync(CancellationToken cancellationToken)
        {
            var program = Rule.Program;
            while (_run.ProgramCounter < program.Count)
            {
                var step = program[_run.ProgramCounter];
                var next = _run.ProgramCounter + 1;

                switch (step)
                {
                    case JumpUnlessStep branch:
                        next = Scope.Evaluate(branch.Condition) ? next : branch.Target;
                        break;

                    case JumpStep jump:
                        next = jump.Target;
                        break;

                    case StopStep stop:
                        await FinishAsync(AutomationRunState.Completed, stop.Reason ?? "Stopped by the rule.", cancellationToken).ConfigureAwait(false);
                        return;

                    case WaitStep wait when !_run.DryRun:
                        Log($"⏳ Waiting {Describe(wait.Duration)}.");
                        _run = _run with { ProgramCounter = next, State = AutomationRunState.Waiting, ResumeAt = Now + wait.Duration };
                        await SaveAsync(cancellationToken).ConfigureAwait(false);
                        return;

                    case WaitStep wait:
                        Log($"🧪 Wait {Describe(wait.Duration)}.");
                        break;

                    case ApprovalStep approval when !_run.DryRun:
                        await RequestApprovalAsync(approval, next, cancellationToken).ConfigureAwait(false);
                        return;

                    case ApprovalStep:
                        Log("🧪 Ask for approval.");
                        break;

                    case RaiseStep raise:
                        if (!await RaiseAsync(raise, cancellationToken).ConfigureAwait(false))
                        {
                            return;
                        }

                        break;

                    case PostDiscordStep post:
                        var text = Clip(Scope.RenderMarked(post.Text));
                        var title = post.Title is null ? null : Clip(Scope.RenderMarked(post.Title));
                        if (!_run.DryRun)
                        {
                            Enqueue(new AutomationPost(Rule.Name, post.ChannelId ?? Rule.ReportChannelId, title, text, post.PingRoleIds, post.Style));
                        }

                        Log($"{Mark()} Post in Discord: {text}");
                        break;

                    case ForumNoteStep note:
                        if (await TargetAsync(note.Player, note.Path, cancellationToken).ConfigureAwait(false) is not { } partner)
                        {
                            return;
                        }

                        var noteText = Clip(Scope.RenderMarked(note.Text));
                        if (!_run.DryRun)
                        {
                            Enqueue(new AutomationForumNote(Rule.Name, partner, noteText));
                        }

                        Log($"{Mark()} Note in the post of {await LabelAsync(partner, cancellationToken).ConfigureAwait(false)}: {noteText}");
                        break;

                    default:
                        if (!await ActAsync(step, cancellationToken).ConfigureAwait(false))
                        {
                            return;
                        }

                        break;
                }

                _run = _run with { ProgramCounter = next, Attempts = 0, State = AutomationRunState.Pending };
                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }

            await FinishAsync(AutomationRunState.Completed, _run.DryRun ? "Dry run finished. Nothing was done." : "Done.", cancellationToken).ConfigureAwait(false);
        }

        private string Mark() => _run.DryRun ? "🧪" : "✅";

        /// <summary>Acts as the account. Returns false when the run stopped or has to wait.</summary>
        private async Task<bool> ActAsync(AutomationStep step, CancellationToken cancellationToken)
        {
            var plan = await PlanAsync(step, cancellationToken).ConfigureAwait(false);
            if (plan is null)
            {
                return false;
            }

            if (plan.Skip is { } skip)
            {
                Log($"⏭️ {plan.Description}: {skip}");
                return true;
            }

            if (_run.DryRun)
            {
                Log($"🧪 {plan.Description}");
                return true;
            }

            if (plan.NeedsSocket && !await WaitForSocketAsync(cancellationToken).ConfigureAwait(false))
            {
                var waited = _run.Attempts * RetryDelay;
                if (waited >= Settings.Control.Automation.OfflineGrace)
                {
                    await FinishAsync(AutomationRunState.Failed, $"{step.Path}: the bot wasn't connected to GeoGuessr for {Describe(waited)}.", cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await PostponeAsync("The bot isn't connected to GeoGuessr right now.", RetryDelay, cancellationToken, countAttempt: true, limit: false).ConfigureAwait(false);
                }

                return false;
            }

            switch (_engine._rateGate.TryAcquire(plan.DmRecipient, out var wait))
            {
                case RateDecision.PlayerLimit:
                    Log($"⏭️ {plan.Description}: this player already got {Settings.Control.Automation.MaxDmsPerPlayerPerHour} automated DMs in the last hour.");
                    return true;
                case RateDecision.Wait:
                    _run = _run with { State = AutomationRunState.Pending, ResumeAt = Now + wait };
                    await SaveAsync(cancellationToken).ConfigureAwait(false);
                    return false;
            }

            _run = _run with { State = AutomationRunState.Acting };
            await SaveAsync(cancellationToken).ConfigureAwait(false);

            Result result;
            try
            {
                result = await _engine._runner.RunAsAutomationAsync(Rule.Name, plan.Action, plan.Target, plan.Execute, cancellationToken).ConfigureAwait(false);
            }
            catch (GeoGuessrUnauthorizedException)
            {
                _engine._connection.ReportUnauthorized();
                result = new Error(ErrorKind.Unavailable, "automation.unauthorized", "GeoGuessr rejected the session cookie.");
            }
            catch (GeoGuessrUnavailableException)
            {
                result = new Error(ErrorKind.Unavailable, "automation.unavailable", "GeoGuessr couldn't be reached, so it's unknown whether this happened.");
            }

            if (!result.IsSuccess)
            {
                Log($"❌ {plan.Description}: {result.Error!.Message}");
                _engine.LogRunFailed(Rule.Name, _run.Id, result.Error.Code);
                await FinishAsync(AutomationRunState.Failed, $"{step.Path}: {result.Error.Message}", cancellationToken).ConfigureAwait(false);
                return false;
            }

            Log($"✅ {plan.Description}");
            return true;
        }

        /// <summary>An action, checked and ready to run.</summary>
        private sealed record ActionPlan(
            string Description,
            string Action,
            string Target,
            Func<CancellationToken, Task<Result>> Execute,
            bool NeedsSocket = false,
            string? DmRecipient = null,
            string? Skip = null);

        private async Task<ActionPlan?> PlanAsync(AutomationStep step, CancellationToken cancellationToken)
        {
            switch (step)
            {
                case SendDmStep dm:
                    {
                        if (await TargetAsync(dm.To, dm.Path, cancellationToken).ConfigureAwait(false) is not { } peer)
                        {
                            return null;
                        }

                        var text = Scope.RenderPlain(dm.Text);
                        var max = Settings.Control.DmReply.MaxLength;
                        if (await CheckTextAsync(dm.Path, text, max, cancellationToken).ConfigureAwait(false) is false)
                        {
                            return null;
                        }

                        if (peer == _engine._account.UserId)
                        {
                            await FinishAsync(AutomationRunState.Failed, $"{dm.Path}: the account can't message itself.", cancellationToken).ConfigureAwait(false);
                            return null;
                        }

                        var label = await LabelAsync(peer, cancellationToken).ConfigureAwait(false);
                        return new ActionPlan(
                            $"Send a DM to {label}: {AutomationMarkup.Untrusted(text)}",
                            "dm",
                            peer,
                            ct => SendDmAsync(peer, text, ct),
                            NeedsSocket: true,
                            DmRecipient: peer);
                    }

                case SendClubMessageStep club:
                    {
                        var text = Scope.RenderPlain(club.Text);
                        if (await CheckTextAsync(club.Path, text, Settings.Control.ClubChat.MaxLength, cancellationToken).ConfigureAwait(false) is false)
                        {
                            return null;
                        }

                        if (_engine._account.ClubId is not { } clubId)
                        {
                            await FinishAsync(AutomationRunState.Failed, $"{club.Path}: the account isn't in a club.", cancellationToken).ConfigureAwait(false);
                            return null;
                        }

                        return new ActionPlan(
                            $"Post in club chat: {AutomationMarkup.Untrusted(text)}",
                            "club-say",
                            clubId,
                            ct => _engine._sender.SendClubMessageAsync(clubId, text, ct),
                            NeedsSocket: true);
                    }

                case PlayerActionStep action:
                    return await PlanPlayerActionAsync(action, cancellationToken).ConfigureAwait(false);

                default:
                    throw new InvalidOperationException($"Unknown step {step.GetType().Name}.");
            }
        }

        private async Task<ActionPlan?> PlanPlayerActionAsync(PlayerActionStep step, CancellationToken cancellationToken)
        {
            if (await TargetAsync(step.Player, step.Path, cancellationToken).ConfigureAwait(false) is not { } target)
            {
                return null;
            }

            var label = await LabelAsync(target, cancellationToken).ConfigureAwait(false);
            var api = _engine._api;

            if (step.Kind is PlayerActionKind.AcceptClubApplication or PlayerActionKind.DeclineClubApplication)
            {
                if (_engine._account.ClubId is not { } clubId)
                {
                    await FinishAsync(AutomationRunState.Failed, $"{step.Path}: the account isn't in a club.", cancellationToken).ConfigureAwait(false);
                    return null;
                }

                var accept = step.Kind == PlayerActionKind.AcceptClubApplication;
                return new ActionPlan(
                    $"{(accept ? "Accept" : "Decline")} the club application of {label}",
                    accept ? "club-application-accept" : "club-application-decline",
                    target,
                    async ct =>
                    {
                        var result = accept
                            ? await api.AcceptClubApplicationAsync(clubId, target, ct).ConfigureAwait(false)
                            : await api.DeclineClubApplicationAsync(clubId, target, ct).ConfigureAwait(false);
                        _engine._reconcile.Request(ReconcileScope.Club);
                        return result;
                    });
            }

            // The friendship lists are read fresh, because declining someone who already became a
            // friend would end the friendship.
            FriendshipSet friendships;
            try
            {
                friendships = await api.GetFriendshipsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (GeoGuessrUnavailableException)
            {
                await PostponeAsync("GeoGuessr couldn't be reached to read the friend list.", RetryDelay, cancellationToken).ConfigureAwait(false);
                return null;
            }

            string description;
            string name;
            string? skip;
            Func<CancellationToken, Task<Result>> act;
            switch (step.Kind)
            {
                case PlayerActionKind.SendFriendRequest:
                    description = $"Send a friend request to {label}";
                    name = "friend-send";
                    skip = friendships.Friends.Contains(target) ? "already a friend."
                        : friendships.Outgoing.Contains(target) ? "a request is already pending."
                        : friendships.Incoming.Contains(target) ? "they already sent the account a request."
                        : null;
                    act = ct => api.SendFriendRequestAsync(target, ct);
                    break;
                case PlayerActionKind.AcceptFriendRequest:
                    description = $"Accept the friend request of {label}";
                    name = "friend-accept";
                    skip = friendships.Incoming.Contains(target) ? null : "there is no pending request from them.";
                    act = ct => api.AcceptFriendRequestAsync(target, ct);
                    break;
                default:
                    description = $"Decline the friend request of {label}";
                    name = "friend-decline";
                    skip = friendships.Incoming.Contains(target) ? null : "there is no pending request from them.";
                    act = ct => api.DeclineFriendRequestAsync(target, ct);
                    break;
            }

            return new ActionPlan(description, name, target, async ct =>
            {
                var result = await act(ct).ConfigureAwait(false);
                _engine._reconcile.Request(ReconcileScope.Friends);
                return result;
            }, Skip: skip);
        }

        private async Task<bool> CheckTextAsync(string path, string text, int max, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                await FinishAsync(AutomationRunState.Failed, $"{path}: the text came out empty.", cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (text.Length > max)
            {
                await FinishAsync(AutomationRunState.Failed, $"{path}: the text has {text.Length} characters, and GeoGuessr cuts at {max}.", cancellationToken).ConfigureAwait(false);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Sends a DM. The pending row has to exist before the send, because the echo can come back
        /// within milliseconds, and it's what shows the message as sent by the rule.
        /// </summary>
        private async Task<Result> SendDmAsync(string peer, string text, CancellationToken cancellationToken)
        {
            var hash = ChatText.Hash(text);
            _store.AddPendingReply(peer, hash, 0, 0, Now, Rule.Name);
            await _store.CommitAsync(cancellationToken).ConfigureAwait(false);

            var result = await _engine._sender.SendDirectMessageAsync(peer, text, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess && await _store.FindPendingReplyAsync(peer, hash, cancellationToken).ConfigureAwait(false) is { } pending)
            {
                await _store.RemovePendingReplyAsync(pending.Id, cancellationToken).ConfigureAwait(false);
                await _store.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        private async Task<bool> WaitForSocketAsync(CancellationToken cancellationToken)
        {
            var connection = _engine._connection;
            return connection.Current.Phase switch
            {
                ConnectionPhase.Connected => true,
                ConnectionPhase.Reconnecting => await connection
                    .WaitUntilConnectedAsync(Settings.Control.DmReply.ReconnectGrace, cancellationToken)
                    .ConfigureAwait(false),
                _ => false
            };
        }

        private async Task RequestApprovalAsync(ApprovalStep approval, int next, CancellationToken cancellationToken)
        {
            var expiresAt = Now + approval.Timeout;
            var prompt = approval.Prompt is null ? null : Clip(Scope.RenderMarked(approval.Prompt));
            var planned = await DescribeAheadAsync(next, cancellationToken).ConfigureAwait(false);

            Enqueue(new AutomationApprovalRequest(Rule.Name, _run.Id, _run.SubjectUserId, prompt, planned, expiresAt, approval.ChannelId ?? Rule.ReportChannelId));
            Log("✋ Waiting for approval.");
            _run = _run with { ProgramCounter = next, State = AutomationRunState.AwaitingApproval, ResumeAt = expiresAt };
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Describes the steps an approval would let happen, up to the next decision.</summary>
        private async Task<IReadOnlyList<string>> DescribeAheadAsync(int from, CancellationToken cancellationToken)
        {
            var lines = new List<string>();
            var program = Rule.Program;
            var index = from;

            for (; index < program.Count && lines.Count < 10; index++)
            {
                var step = program[index];
                try
                {
                    var line = step switch
                    {
                        SendDmStep or SendClubMessageStep => (await DescribeActionAsync(step, cancellationToken).ConfigureAwait(false)),
                        PlayerActionStep action => await DescribePlayerActionAsync(action, cancellationToken).ConfigureAwait(false),
                        PostDiscordStep post => $"Post in Discord: {Clip(Scope.RenderMarked(post.Text))}",
                        ForumNoteStep note => $"Write a note in a forum post: {Clip(Scope.RenderMarked(note.Text))}",
                        RaiseStep raise => $"Raise Custom:{raise.EventName}",
                        _ => null
                    };

                    if (line is null)
                    {
                        break;
                    }

                    lines.Add(line);
                }
                catch (AutomationScriptException exception)
                {
                    lines.Add($"{step.Path} can't be shown: {exception.Message}");
                }
            }

            if (index < program.Count && program[index] is not StopStep)
            {
                lines.Add("…and then more steps, depending on how things go.");
            }

            return lines;
        }

        private async Task<string?> DescribeActionAsync(AutomationStep step, CancellationToken cancellationToken)
        {
            switch (step)
            {
                case SendDmStep dm:
                    var peer = dm.To is null ? _run.SubjectUserId : ParseUserId(Scope.RenderPlain(dm.To));
                    var label = peer is null ? "nobody" : await LabelAsync(peer, cancellationToken).ConfigureAwait(false);
                    return $"Send a DM to {label}: {AutomationMarkup.Untrusted(Scope.RenderPlain(dm.Text))}";
                case SendClubMessageStep club:
                    return $"Post in club chat: {AutomationMarkup.Untrusted(Scope.RenderPlain(club.Text))}";
                default:
                    return null;
            }
        }

        private async Task<string> DescribePlayerActionAsync(PlayerActionStep step, CancellationToken cancellationToken)
        {
            var target = step.Player is null ? _run.SubjectUserId : ParseUserId(Scope.RenderPlain(step.Player));
            var label = target is null ? "nobody" : await LabelAsync(target, cancellationToken).ConfigureAwait(false);
            return step.Kind switch
            {
                PlayerActionKind.SendFriendRequest => $"Send a friend request to {label}",
                PlayerActionKind.AcceptFriendRequest => $"Accept the friend request of {label}",
                PlayerActionKind.DeclineFriendRequest => $"Decline the friend request of {label}",
                PlayerActionKind.AcceptClubApplication => $"Accept the club application of {label}",
                _ => $"Decline the club application of {label}"
            };
        }

        private async Task<bool> RaiseAsync(RaiseStep raise, CancellationToken cancellationToken)
        {
            var settings = Settings.Control.Automation;
            if (_run.Depth + 1 > settings.MaxChainDepth)
            {
                await FinishAsync(AutomationRunState.Failed, $"{raise.Path}: raising Custom:{raise.EventName} would go deeper than Control:Automation:MaxChainDepth ({settings.MaxChainDepth}).", cancellationToken).ConfigureAwait(false);
                return false;
            }

            IReadOnlyList<string?> targets;
            if (raise.ForEach is { } audience)
            {
                targets = await AudienceAsync(audience, cancellationToken).ConfigureAwait(false);
                if (targets.Count > settings.MaxFanOut)
                {
                    await FinishAsync(AutomationRunState.Failed, $"{raise.Path}: {audience} has {targets.Count} players, and Control:Automation:MaxFanOut allows {settings.MaxFanOut}.", cancellationToken).ConfigureAwait(false);
                    return false;
                }
            }
            else if (raise.Player is { } player)
            {
                if (await TargetAsync(player, raise.Path, cancellationToken).ConfigureAwait(false) is not { } single)
                {
                    return false;
                }

                targets = [single];
            }
            else
            {
                targets = [_run.SubjectUserId];
            }

            var data = new JsonObject { ["source_rule"] = Rule.Name, ["source_run"] = _run.Id };
            foreach (var (field, template) in raise.Data)
            {
                data[field] = Scope.RenderPlain(template);
            }

            var key = AutomationTriggerSpec.CustomPrefix + raise.EventName;
            var listeners = _engine._registry.RulesFor(key);
            var who = targets.Count == 1 ? "" : $" for {targets.Count} players";

            if (_preview)
            {
                Log($"🧪 Raise {key}{who}, which starts: {(listeners.Count == 0 ? "no rule" : string.Join(", ", listeners.Select(rule => rule.Name)))}.");
                return true;
            }

            var created = 0;
            foreach (var target in targets)
            {
                var fire = new AutomationFire(key, target, Now, data.DeepClone().AsObject(), $"raise:{_run.Id}:{_run.ProgramCounter}:{target}");
                created += _factory.Create(fire, listeners, _run.Depth + 1, dryRun: _run.DryRun).Count;
            }

            Log($"{Mark()} Raise {key}{who}, which started {created} runs.");
            _engine._signal.Wake();
            return true;
        }

        private async Task<IReadOnlyList<string?>> AudienceAsync(AutomationAudience audience, CancellationToken cancellationToken)
        {
            IEnumerable<string> ids = audience switch
            {
                AutomationAudience.Friends =>
                    (await _store.ReadStateAsync<FriendshipSet>(SnapshotKeys.Friendships, cancellationToken).ConfigureAwait(false))?.Friends ?? [],
                AutomationAudience.FriendRequests =>
                    (await _store.ReadStateAsync<FriendshipSet>(SnapshotKeys.Friendships, cancellationToken).ConfigureAwait(false))?.Incoming ?? [],
                AutomationAudience.ClubMembers =>
                    (await _store.ReadStateAsync<ClubRoster>(SnapshotKeys.ClubRoster, cancellationToken).ConfigureAwait(false))?.Members.Keys ?? [],
                _ =>
                    (await _store.ReadStateAsync<ClubApplicationSet>(SnapshotKeys.ClubApplications, cancellationToken).ConfigureAwait(false))?.Pending.Keys ?? []
            };

            return ids.Where(id => id != _engine._account.UserId).Cast<string?>().ToArray();
        }

        /// <summary>The player a step is about: the run's player, unless a template names another.</summary>
        private async Task<string?> TargetAsync(AutomationScript? template, string path, CancellationToken cancellationToken)
        {
            if (template is null)
            {
                if (_run.SubjectUserId is { } subject)
                {
                    return subject;
                }

                await FinishAsync(AutomationRunState.Failed, $"{path}: this step needs a player, and the run has none. Name one with Player or To.", cancellationToken).ConfigureAwait(false);
                return null;
            }

            var rendered = Scope.RenderPlain(template);
            if (ParseUserId(rendered) is { } id)
            {
                return id;
            }

            await FinishAsync(AutomationRunState.Failed, $"{path}: '{Clip(rendered)}' isn't a GeoGuessr user id or profile link.", cancellationToken).ConfigureAwait(false);
            return null;
        }

        private async Task<string> LabelAsync(string userId, CancellationToken cancellationToken)
        {
            if (_player is { } player && player.Id == userId)
            {
                return AutomationMarkup.Untrusted(player.Nick);
            }

            return AutomationMarkup.Untrusted((await _players.GetNickAsync(userId, cancellationToken).ConfigureAwait(false)).Value);
        }

        private async Task<bool> SkipAsync(string reason, CancellationToken cancellationToken)
        {
            if (_preview)
            {
                // A preview shows everything, so it notes the skip and carries on.
                Log($"⏭️ A real run would stop here: {reason}");
                return true;
            }

            await FinishAsync(AutomationRunState.Skipped, reason, cancellationToken).ConfigureAwait(false);
            return false;
        }

        private async Task PostponeAsync(string reason, TimeSpan delay, CancellationToken cancellationToken, bool countAttempt = true, bool limit = true)
        {
            if (_preview)
            {
                Log($"⚠️ {reason}");
                return;
            }

            var attempts = _run.Attempts + (countAttempt ? 1 : 0);
            if (limit && attempts > MaxPostponements)
            {
                await FinishAsync(AutomationRunState.Failed, $"{reason} Gave up after {MaxPostponements} tries.", cancellationToken).ConfigureAwait(false);
                return;
            }

            _run = _run with { Attempts = attempts, State = AutomationRunState.Pending, ResumeAt = Now + delay };
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task DecideAsync(bool approve, ulong decidedBy, CancellationToken cancellationToken)
        {
            _rule = _engine._registry.Compile(_run.RuleJson, AutomationRuleOrigin.Discord);
            Enqueue(new AutomationApprovalOutcome(_run.Id, approve ? AutomationRunState.Pending : AutomationRunState.Rejected, decidedBy));

            if (approve)
            {
                Log($"👍 Approved by <@{decidedBy}>.");
                _run = _run with { State = AutomationRunState.Pending, ResumeAt = Now };
                await SaveAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            Log($"👎 Rejected by <@{decidedBy}>.");
            await FinishAsync(AutomationRunState.Rejected, "Someone rejected it.", cancellationToken).ConfigureAwait(false);
        }

        public async Task CancelAsync(string reason, CancellationToken cancellationToken)
        {
            _rule = _engine._registry.Compile(_run.RuleJson, AutomationRuleOrigin.Discord);
            if (_run.State == AutomationRunState.AwaitingApproval)
            {
                Enqueue(new AutomationApprovalOutcome(_run.Id, AutomationRunState.Cancelled, null));
            }

            await FinishAsync(AutomationRunState.Cancelled, reason, cancellationToken).ConfigureAwait(false);
        }

        private async Task ExpireAsync(CancellationToken cancellationToken)
        {
            Enqueue(new AutomationApprovalOutcome(_run.Id, AutomationRunState.Expired, null));
            await FinishAsync(AutomationRunState.Expired, "Nobody approved in time.", cancellationToken).ConfigureAwait(false);
        }

        public async Task FinishAsync(AutomationRunState state, string outcome, CancellationToken cancellationToken)
        {
            if (_preview)
            {
                Log(state == AutomationRunState.Completed ? $"🏁 {outcome}" : $"⚠️ {outcome}");
                return;
            }

            var mode = _rule?.Report ?? AutomationReportMode.Failures;
            var isFailure = state is AutomationRunState.Failed or AutomationRunState.Expired;
            var report = _run.DryRun || _run.Manual
                         || (mode == AutomationReportMode.All && state is not (AutomationRunState.Skipped or AutomationRunState.Cancelled))
                         || (mode != AutomationReportMode.None && isFailure);

            if (report)
            {
                Enqueue(new AutomationReport(_run.RuleName, _run.Id, state, outcome, _run.SubjectUserId, _log, _run.DryRun, _rule?.ReportChannelId));
            }

            // What the run saw and did may hold chat text, so only the outcome is kept.
            _log.Clear();
            _run = _run with { State = state, Outcome = outcome, FinishedAt = Now, EventJson = "{}" };
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            _engine.LogRunFinished(_run.RuleName, _run.Id, state);
        }

        private void Enqueue(OutboxMessage message)
        {
            if (!_preview)
            {
                _store.Enqueue(message);
            }
        }

        private void Log(string line)
        {
            if (_log.Count < MaxLogLines)
            {
                _log.Add(line);
            }
            else if (_log.Count == MaxLogLines)
            {
                _log.Add("…");
            }
        }

        private async Task SaveAsync(CancellationToken cancellationToken)
        {
            if (_preview)
            {
                return;
            }

            _run = _run with { Log = [.. _log] };
            await _automations.UpdateRunAsync(_run, cancellationToken).ConfigureAwait(false);
            await _store.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        private static string Clip(string text) =>
            text.Length <= MaxRenderedLength ? text : string.Concat(text.AsSpan(0, MaxRenderedLength), "…");
    }

    /// <summary>A short duration in words, such as "2 h 5 min".</summary>
    public static string Describe(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
        {
            return $"{Math.Max(0, (int)span.TotalSeconds)} s";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return $"{(int)span.TotalMinutes} min";
        }

        if (span < TimeSpan.FromDays(1))
        {
            return span.Minutes == 0 ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalHours} h {span.Minutes} min";
        }

        return span.Hours == 0 ? $"{(int)span.TotalDays} d" : $"{(int)span.TotalDays} d {span.Hours} h";
    }
}
