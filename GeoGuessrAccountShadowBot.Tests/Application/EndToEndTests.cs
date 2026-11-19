// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;
using System.Text;
using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application.Automation;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Pipeline;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;
using GeoGuessrAccountShadowBot.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace GeoGuessrAccountShadowBot.Tests.Application;

/// <summary>
/// The real workers running at the same time, each with its own database connection, as in
/// production. Tests of one part at a time can't see races between parts, which is how issue #19
/// passed every test: the outbox dispatcher and the automation worker cached the same new player's
/// name at once, the dispatcher's commit failed after its card was sent, and the restart resent it.
/// </summary>
public sealed class EndToEndTests
{
    private const string Peer = ShadowTestHost.PeerId;

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private const string Rule = """
        { "Control": { "Automation": { "Rules": [
          { "Name": "welcome", "On": "FriendRequestAccepted", "DryRun": true, "Do": [{ "SendDm": "Hi {{ player.nick }}" }] }
        ] } } }
        """;

    [Fact]
    public async Task AcceptedFriendRequest_WithARule_IsAnnouncedOnce_AndNoWorkerCrashes()
    {
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(Rule))).Build();
        await using var host = await ShadowTestHost.CreateAsync(
            options => options.Control.Automation.Enabled = true, configuration, fileDatabase: true, lookUpNames: true);

        // Replays production's order. Every lookup waits until both workers are inside one, so
        // both miss the cache. The dispatcher gets its answer first and goes on to send its card;
        // the automation worker gets its answer during that send and saves the name, which is the
        // moment that collided with the dispatcher's pending insert. Timeouts keep a bot that
        // behaves differently from hanging; the assertions then say what's wrong.
        var lookups = 0;
        var bothLookingUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcherAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Api.GetPlayerNickAsync(Peer, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            var isDispatcher = Environment.StackTrace.Contains(nameof(OutboxDispatcher), StringComparison.Ordinal);
            if (Interlocked.Increment(ref lookups) >= 2)
            {
                bothLookingUp.TrySetResult();
            }

            await Task.WhenAny(bothLookingUp.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            if (isDispatcher)
            {
                dispatcherAnswered.TrySetResult();
            }
            else
            {
                await Task.WhenAny(dispatcherAnswered.Task, Task.Delay(TimeSpan.FromSeconds(3)));
                await Task.Delay(50);
            }

            return (string?)"geobobo";
        });
        await ReloadRulesAsync(host);

        // The account sent a request earlier. The first snapshot is the silent baseline.
        await host.ProcessAsync(new FriendshipsFetched(Friendships(outgoing: [Peer])));

        IHostedService[] workers =
        [
            host.Services.GetServices<IHostedService>().OfType<EventPipelineWorker>().Single(),
            host.Services.GetServices<IHostedService>().OfType<OutboxDispatcher>().Single(),
            host.Get<AutomationWorker>()
        ];
        foreach (var worker in workers)
        {
            await worker.StartAsync(CancellationToken.None);
        }

        try
        {
            // Production was idle when the accept arrived, so both workers start on it at once.
            await WaitUntilAsync(host, () => host.Publisher.Snapshot().OfType<StatusMessage>().Any());

            await host.Get<IEventPublisher>().PublishAsync(new FriendshipsFetched(Friendships(friends: [Peer])));

            // A worker that died never delivers the report, so stop waiting as soon as one does.
            await WaitUntilAsync(host, () => host.Publisher.Snapshot().OfType<AutomationReport>().Any()
                                       || workers.Cast<BackgroundService>().Any(worker => worker.ExecuteTask!.IsCompleted));

            // Long enough for a restarted worker to resend the card, if anything were going to.
            await WaitUntilAsync(host, () => false, mustHold: false);
            var delivered = host.Publisher.Snapshot();

            foreach (var worker in workers.Cast<BackgroundService>())
            {
                worker.ExecuteTask!.IsCompleted.Should().BeFalse($"{worker.GetType().Name} should still be running, but {worker.ExecuteTask.Exception?.GetBaseException().Message}");
            }

            lookups.Should().Be(2, "the test must make both workers miss the cache, or it proves nothing");
            delivered.OfType<EventCard>().Where(card => card.EventKey == EventKeys.FriendRequestAccepted)
                .Should().ContainSingle().Which.ActorUserId.Should().Be(Peer);
            delivered.OfType<AutomationReport>().Should().ContainSingle();
            delivered.OfType<StatusMessage>().Should().NotContain(status => status.Title == "A background worker crashed");

            (await host.WithStoreAsync(store => store.FindPlayerNameAsync(Peer)))!.Nick.Should().Be("geobobo");
        }
        finally
        {
            foreach (var worker in workers)
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }
    }

    private static FriendshipSet Friendships(string[]? friends = null, string[]? outgoing = null) =>
        new((friends ?? []).ToImmutableSortedSet(), ImmutableSortedSet<string>.Empty, (outgoing ?? []).ToImmutableSortedSet());

    private static async Task ReloadRulesAsync(ShadowTestHost host)
    {
        var scope = host.Services.CreateAsyncScope();
        await using (scope)
        {
            await host.Get<AutomationRuleRegistry>().ReloadAsync(
                scope.ServiceProvider.GetRequiredService<IShadowStore>(),
                scope.ServiceProvider.GetRequiredService<IAutomationStore>(),
                CancellationToken.None);
        }
    }

    /// <summary>
    /// Waits in real time, and moves the fake clock along, so that a supervised worker's restart
    /// backoff passes too. Without that a crashed worker would just stay down, and a resend that
    /// the restart causes would never show.
    /// </summary>
    private static async Task WaitUntilAsync(ShadowTestHost host, Func<bool> condition, bool mustHold = true)
    {
        var deadline = DateTime.UtcNow + (mustHold ? Deadline : TimeSpan.FromSeconds(2));
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            host.Time.Advance(TimeSpan.FromSeconds(1));
        }

        if (mustHold)
        {
            condition().Should().BeTrue("the workers should have finished within {0}", Deadline);
        }
    }
}
