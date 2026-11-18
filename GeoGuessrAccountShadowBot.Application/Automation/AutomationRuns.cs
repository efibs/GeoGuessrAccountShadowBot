// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Events;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Automation;

/// <summary>
/// Wakes the automation worker when a run was created or may continue.
/// </summary>
public sealed class AutomationSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already awake.
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// Creates runs. Nothing is evaluated here: conditions may need GeoGuessr, so they wait for the
/// worker, and creating a run only has to touch the store.
/// </summary>
public sealed class AutomationRunFactory(IAutomationStore store, TimeProvider timeProvider)
{
    public IReadOnlyList<IAutomationRunHandle> Create(
        AutomationFire fire,
        IEnumerable<AutomationRule> rules,
        int depth,
        bool manual = false,
        bool dryRun = false,
        ulong? requestedBy = null)
    {
        var now = timeProvider.GetUtcNow();
        var created = new List<IAutomationRunHandle>();

        foreach (var rule in rules)
        {
            created.Add(store.AddRun(new AutomationRun
            {
                RuleName = rule.Name,
                RuleJson = rule.Json,
                Trigger = fire.Trigger,
                GroupKey = fire.GroupKey,
                SubjectUserId = fire.SubjectUserId,
                EventJson = fire.Data.ToJsonString(),
                Depth = depth,
                Manual = manual,
                DryRun = dryRun || rule.DryRun,
                RequestedBy = requestedBy,
                State = AutomationRunState.Pending,
                CreatedAt = now,
                OccurredAt = fire.OccurredAt ?? now,
                ResumeAt = now
            }));
        }

        return created;
    }
}

/// <summary>
/// Turns events into runs, inside the event's transaction, so an event starts its rules exactly
/// when it's processed. Silent events, such as a first sync, start nothing.
/// </summary>
public sealed class AutomationCapture(
    AutomationRuleRegistry registry,
    AutomationRunFactory factory,
    AccountContext account,
    IOptions<ShadowOptions> options)
{
    /// <returns>Whether any run was created.</returns>
    public bool Capture(ShadowEvent shadowEvent)
    {
        if (!options.Value.Control.Automation.Enabled || shadowEvent.Silent)
        {
            return false;
        }

        if (AutomationTriggers.Describe(shadowEvent, account.UserId) is not { } fire)
        {
            return false;
        }

        var rules = registry.RulesFor(fire.Trigger);
        return rules.Count > 0 && factory.Create(fire, rules, depth: 0).Count > 0;
    }
}

/// <summary>What the rate gate decided.</summary>
public enum RateDecision
{
    Acquired,

    /// <summary>Every slot is taken. The run waits and tries again.</summary>
    Wait,

    /// <summary>
    /// The player got too many automated DMs recently. The DM is dropped rather than delayed,
    /// because waiting would let two bots that answer each other queue messages forever.
    /// </summary>
    PlayerLimit
}

/// <summary>
/// Spaces out actions as the account across every rule. A run over the global limit waits for a
/// slot rather than failing, so a rule that messages all friends finishes, just slowly.
/// </summary>
public sealed class AutomationRateGate(TimeProvider timeProvider, IOptions<ShadowOptions> options)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly Lock _lock = new();
    private readonly Queue<DateTimeOffset> _lastMinute = new();
    private readonly Queue<DateTimeOffset> _lastHour = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _dmsByPlayer = new(StringComparer.Ordinal);

    /// <summary>Takes a slot, or says why not.</summary>
    /// <param name="dmRecipient">The recipient, for a DM, which has its own limit per player.</param>
    /// <param name="wait">How long until a slot frees up, for <see cref="RateDecision.Wait"/>.</param>
    public RateDecision TryAcquire(string? dmRecipient, out TimeSpan wait)
    {
        var settings = options.Value.Control.Automation;
        var now = timeProvider.GetUtcNow();
        wait = TimeSpan.Zero;

        lock (_lock)
        {
            Trim(_lastMinute, now - Minute);
            Trim(_lastHour, now - Hour);

            Queue<DateTimeOffset>? dms = null;
            if (dmRecipient is not null)
            {
                if (!_dmsByPlayer.TryGetValue(dmRecipient, out dms))
                {
                    dms = new Queue<DateTimeOffset>();
                    _dmsByPlayer[dmRecipient] = dms;
                }

                Trim(dms, now - Hour);
                if (dms.Count >= settings.MaxDmsPerPlayerPerHour)
                {
                    return RateDecision.PlayerLimit;
                }
            }

            if (_lastMinute.Count >= settings.MaxActionsPerMinute)
            {
                wait = _lastMinute.Peek() + Minute - now;
            }

            if (_lastHour.Count >= settings.MaxActionsPerHour)
            {
                wait = TimeSpan.FromTicks(Math.Max(wait.Ticks, (_lastHour.Peek() + Hour - now).Ticks));
            }

            if (wait > TimeSpan.Zero)
            {
                wait += TimeSpan.FromSeconds(1);
                return RateDecision.Wait;
            }

            _lastMinute.Enqueue(now);
            _lastHour.Enqueue(now);
            dms?.Enqueue(now);
            return RateDecision.Acquired;
        }
    }

    private static void Trim(Queue<DateTimeOffset> queue, DateTimeOffset cutoff)
    {
        while (queue.TryPeek(out var oldest) && oldest <= cutoff)
        {
            queue.Dequeue();
        }
    }
}
