// SPDX-License-Identifier: GPL-2.0-or-later

using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Pipeline;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Tests.TestSupport;

namespace GeoGuessrAccountShadowBot.Tests.Application;

/// <summary>
/// What happens after something fails. Issue #19 was a failed commit that stopped the host, and the
/// restart resent a card.
/// </summary>
public sealed class ResilienceTests
{
    private static async Task<ShadowTestHost> HostWithOneCardAsync()
    {
        var host = await ShadowTestHost.CreateAsync();
        await host.WithStoreAsync(async store =>
        {
            store.Enqueue(new StatusMessage("card", "d", false));
            await store.CommitAsync();
            return true;
        });
        return host;
    }

    [Fact]
    public async Task FailedCommitAfterDelivery_IsRecordedOnTheRetry_AndNothingIsResent()
    {
        await using var host = await HostWithOneCardAsync();
        host.Publisher.Failure = _ =>
        {
            // Discord has the card at this point, and the commit that would record it fails.
            host.Commits.FailNext(1);
            return null;
        };

        var retryAfter = await host.DrainOutboxAsync();

        retryAfter.Should().BeNull();
        host.Commits.Failed.Should().Be(1);
        (await host.ReadOutboxAsync()).Should().BeEmpty();

        host.Publisher.Failure = _ => null;
        await host.DrainOutboxAsync();
        host.Publisher.Delivered.Should().ContainSingle();
    }

    [Fact]
    public async Task CommitThatKeepsFailing_BacksOff_InsteadOfThrowing()
    {
        await using var host = await HostWithOneCardAsync();
        host.Publisher.Failure = _ =>
        {
            host.Commits.FailNext(2);
            return null;
        };

        var retryAfter = await host.DrainOutboxAsync();

        retryAfter.Should().NotBeNull("a broken database must not resend in a tight loop");
        host.Publisher.Delivered.Should().ContainSingle();
        (await host.ReadOutboxAsync()).Should().ContainSingle("the entry stays queued and is sent again later");
    }
}

public sealed class WorkerSupervisionTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 16)]
    [InlineData(9, 256)]
    [InlineData(10, 300)]
    [InlineData(1000, 300)]
    public void Backoff_DoublesUpToFiveMinutes(int crashes, int seconds)
    {
        WorkerSupervision.Backoff(crashes).Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public async Task CrashedWorker_Restarts_AndTheFirstCrashIsReported()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        var worker = new FlakyWorker(host.Get<WorkerSupervision>(), new InvalidOperationException("boom"), failures: 3);

        await worker.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(host, () => worker.Finished.Task.IsCompleted);

        worker.Runs.Should().Be(4);
        worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        Alerts(host).Should().ContainSingle().Which.Detail.Should().Contain(nameof(FlakyWorker));
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancellationThatIsntAShutdown_IsACrashToo()
    {
        // An HTTP timeout throws TaskCanceledException without the host stopping.
        await using var host = await ShadowTestHost.CreateAsync();
        var worker = new FlakyWorker(host.Get<WorkerSupervision>(), new TaskCanceledException("timeout"), failures: 1);

        await worker.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(host, () => worker.Finished.Task.IsCompleted);

        worker.Runs.Should().Be(2);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StoppingDuringTheBackoff_EndsTheWorkerCleanly()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        var worker = new FlakyWorker(host.Get<WorkerSupervision>(), new InvalidOperationException("boom"), failures: int.MaxValue);

        await worker.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(host, () => worker.Runs >= 1, advance: TimeSpan.Zero);
        await worker.StopAsync(CancellationToken.None);

        worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task FatalFailure_StillStopsTheBot()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        var worker = new FlakyWorker(host.Get<WorkerSupervision>(), new FatalShadowException("wrong database"), failures: 1);

        await worker.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(host, () => worker.ExecuteTask!.IsCompleted, advance: TimeSpan.Zero);

        worker.Runs.Should().Be(1);
        worker.ExecuteTask!.Exception!.InnerException.Should().BeOfType<FatalShadowException>();
    }

    private static List<OperatorAlertRaised> Alerts(ShadowTestHost host)
    {
        var alerts = new List<OperatorAlertRaised>();
        while (host.Get<EventBus>().Reader.TryRead(out var shadowEvent))
        {
            if (shadowEvent is OperatorAlertRaised alert)
            {
                alerts.Add(alert);
            }
        }

        return alerts;
    }

    /// <summary>Moves the fake clock in steps until the condition holds, since a backoff waits on it.</summary>
    private static async Task AdvanceUntilAsync(ShadowTestHost host, Func<bool> condition, TimeSpan? advance = null)
    {
        for (var step = 0; step < 500 && !condition(); step++)
        {
            await Task.Delay(5);
            host.Time.Advance(advance ?? TimeSpan.FromSeconds(1));
        }

        condition().Should().BeTrue();
    }

    private sealed class FlakyWorker(WorkerSupervision supervision, Exception failure, int failures) : SupervisedBackgroundService(supervision)
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task RunAsync(CancellationToken stoppingToken)
        {
            if (Interlocked.Increment(ref _runs) <= failures)
            {
                throw failure;
            }

            Finished.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
