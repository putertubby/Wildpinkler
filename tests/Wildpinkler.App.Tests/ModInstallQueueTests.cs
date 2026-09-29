using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Exercises <see cref="ModInstallQueue"/> with no dispatcher attached, so every job and state
/// change runs inline on the queue's worker thread. A scripted fake installer drives outcomes
/// (Done / Failed / Cancelled) and gates are used to freeze a job mid-install so intermediate
/// state (Installing, StatusText, IsBusy) can be observed deterministically.
/// </summary>
public class ModInstallQueueTests
{
    [Fact]
    public async Task Jobs_RunOneAtATime_InOrder()
    {
        var queue = new ModInstallQueue();
        var gate = new object();
        var inFlight = 0;
        var maxInFlight = 0;
        var order = new List<string>();

        queue.SetInstaller(async (profile, mod, cts) =>
        {
            lock (gate)
            {
                inFlight++;
                maxInFlight = Math.Max(maxInFlight, inFlight);
                order.Add(mod.Name);
            }
            // Small overlap window: if two jobs ever ran concurrently, inFlight would hit 2.
            await Task.Delay(25);
            lock (gate)
            {
                inFlight--;
            }
            return InstallOutcome.Done;
        });

        queue.Enqueue(MakeProfile(), [MakeMod("A"), MakeMod("B"), MakeMod("C")]);
        await queue.DisposeAsync();

        Assert.Equal(1, maxInFlight);
        Assert.Equal(new[] { "A", "B", "C" }, order);
    }

    [Fact]
    public async Task StatusText_ReflectsQueueThenActiveThenIdle()
    {
        var queue = new ModInstallQueue();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.SetInstaller(async (profile, mod, cts) =>
        {
            await gate.Task;
            return InstallOutcome.Done;
        });

        queue.Enqueue(MakeProfile(), [MakeMod("A"), MakeMod("B")]);

        // The gate keeps every job frozen once the worker picks it up, so before the gate is
        // released StatusText can only be the pending-only message or the active message for A.
        // (The worker may already have promoted A to Installing by the time we read it.)
        Assert.True(
            queue.StatusText == "2 install(s) queued" || queue.StatusText == "Installing 1 of 2: A",
            $"Unexpected early StatusText: {queue.StatusText}");

        var jobA = queue.Jobs[0];
        await WaitForAsync(() => jobA.State == ModInstallJobState.Installing);
        Assert.True(queue.IsBusy);
        Assert.Equal("Installing 1 of 2: A", queue.StatusText);

        gate.SetResult(true);
        await queue.DisposeAsync();

        Assert.Equal(ModInstallJobState.Done, jobA.State);
        Assert.Equal(ModInstallJobState.Done, queue.Jobs[1].State);
        Assert.False(queue.IsBusy);
        Assert.Equal(string.Empty, queue.StatusText);
    }

    [Fact]
    public async Task FailedOutcome_DoesNotStopTheWorker()
    {
        var queue = new ModInstallQueue();

        queue.SetInstaller((profile, mod, cts) =>
            Task.FromResult(mod.Name == "A" ? InstallOutcome.Failed : InstallOutcome.Done));

        queue.Enqueue(MakeProfile(), [MakeMod("A"), MakeMod("B")]);
        await queue.DisposeAsync();

        Assert.Equal(ModInstallJobState.Failed, queue.Jobs[0].State);
        Assert.Equal(ModInstallJobState.Done, queue.Jobs[1].State);
    }

    [Fact]
    public async Task CancelledJob_TransitionsToCancelledAndQueueContinues()
    {
        var queue = new ModInstallQueue();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.SetInstaller(async (profile, mod, cts) =>
        {
            if (mod.Name == "A")
            {
                started.TrySetResult(true);
                try
                {
                    // The installer observes the job's own cancellation token, exactly like the
                    // real page wires its Cancel button.
                    await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                return cts.IsCancellationRequested ? InstallOutcome.Cancelled : InstallOutcome.Done;
            }
            return InstallOutcome.Done;
        });

        queue.Enqueue(MakeProfile(), [MakeMod("A"), MakeMod("B")]);
        await started.Task;

        // Cancel the running first job.
        queue.Jobs[0].Cancellation.Cancel();

        await queue.DisposeAsync();

        Assert.Equal(ModInstallJobState.Cancelled, queue.Jobs[0].State);
        Assert.Equal(ModInstallJobState.Done, queue.Jobs[1].State);
    }

    [Fact]
    public async Task NullInstaller_YieldsFailedState()
    {
        var queue = new ModInstallQueue();
        // No installer registered: the worker reports Failed for whatever is enqueued.
        queue.Enqueue(MakeProfile(), [MakeMod("A")]);
        await queue.DisposeAsync();

        Assert.Equal(ModInstallJobState.Failed, queue.Jobs[0].State);
    }

    [Fact]
    public async Task JobChanged_FiresForEnqueueAndCompletion()
    {
        var queue = new ModInstallQueue();
        var gate = new object();
        var count = 0;
        queue.JobChanged += (_, _) =>
        {
            lock (gate)
            {
                count++;
            }
        };

        queue.SetInstaller((profile, mod, cts) => Task.FromResult(InstallOutcome.Done));
        queue.Enqueue(MakeProfile(), [MakeMod("A"), MakeMod("B")]);
        await queue.DisposeAsync();

        // Each job notifies once when enqueued (Queued) and once on completion (Done).
        lock (gate)
        {
            Assert.True(count >= 4, $"expected at least 4 JobChanged events, got {count}");
        }
    }

    [Fact]
    public async Task JobsAreNotClearedAfterBatchCompletes()
    {
        var queue = new ModInstallQueue();
        queue.SetInstaller((profile, mod, cts) => Task.FromResult(InstallOutcome.Done));
        queue.Enqueue(MakeProfile(), [MakeMod("A"), MakeMod("B")]);
        await queue.DisposeAsync();

        // The batch view is only reset when the next batch is enqueued while idle, not on completion.
        Assert.Equal(2, queue.Jobs.Count);
    }

    private static Profile MakeProfile() => new()
    {
        Id = "profile-1",
        GameId = "game-1",
        FolderPath = "/fake/profile"
    };

    private static ModEntry MakeMod(string name) => new()
    {
        Id = name,
        Name = name,
        ArchivePath = $"/fake/{name}.zip"
    };

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10);
        }
    }
}
