using System.Diagnostics;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class BoundedShutdownTests
{
    [Fact]
    public async Task Run_IsOnceUnderConcurrentCallers()
    {
        int steps = 0;
        int finals = 0;
        using var release = new ManualResetEventSlim();
        var shutdown = new BoundedShutdown(
            new[] { BoundedShutdownStep.Run("step", () => Interlocked.Increment(ref steps)) },
            () => Interlocked.Increment(ref finals));

        var callers = Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() =>
            {
                release.Wait();
                return shutdown.Run();
            }))
            .ToArray();

        release.Set();
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, results.Count(static r => r));
        Assert.Equal(1, steps);
        Assert.Equal(1, finals);
    }

    [Fact]
    public void Run_TimedOutStepDoesNotBlockLaterStepsOrFinalAction()
    {
        var never = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var shutdown = new BoundedShutdown(
            new[]
            {
                BoundedShutdownStep.WaitFor("never", () =>
                {
                    order.Add("never");
                    return never.Task;
                }, TimeSpan.FromMilliseconds(10)),
                BoundedShutdownStep.Run("later", () => order.Add("later")),
            },
            () => order.Add("final"));

        var sw = Stopwatch.StartNew();
        var first = shutdown.Run();
        sw.Stop();

        Assert.True(first);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500));
        Assert.Equal(new[] { "never", "later", "final" }, order);
    }

    [Fact]
    public async Task Run_WaitsForCompletingTimedStepBeforeNextStep()
    {
        // Deterministic: the timed step finishes only when the test releases
        // it, so "Run is still waiting" is checked against a gate, not a clock.
        // (The first version asserted Stopwatch >= Task.Delay(50); the two run
        // on different clocks and a CI runner measured 49.9984 ms.)
        using var started = new ManualResetEventSlim();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var shutdown = new BoundedShutdown(
            new[]
            {
                BoundedShutdownStep.WaitFor("delayed", () => Task.Run(async () =>
                {
                    order.Enqueue("delayed-start");
                    started.Set();
                    await release.Task.ConfigureAwait(false);
                    order.Enqueue("delayed-complete");
                }), TimeSpan.FromSeconds(30)),
                BoundedShutdownStep.Run("later", () => order.Enqueue("later")),
            },
            () => order.Enqueue("final"));

        var run = Task.Run(() => shutdown.Run());

        Assert.True(started.Wait(TimeSpan.FromSeconds(30)), "the timed step never started");
        // The step is parked on the gate, so Run must still be inside it:
        // nothing after it may have run yet.
        await Task.Delay(50);
        Assert.False(run.IsCompleted, "Run returned before its timed step completed");
        Assert.DoesNotContain("later", order);
        Assert.DoesNotContain("final", order);

        release.SetResult();
        var first = await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(first);
        Assert.Equal(new[] { "delayed-start", "delayed-complete", "later", "final" }, order.ToArray());
    }

    [Fact]
    public void Run_ThrowingStepDoesNotStopChain()
    {
        var order = new List<string>();
        var shutdown = new BoundedShutdown(
            new[]
            {
                BoundedShutdownStep.Run("throw", () =>
                {
                    order.Add("throw");
                    throw new InvalidOperationException("boom");
                }),
                BoundedShutdownStep.Run("later", () => order.Add("later")),
            },
            () => order.Add("final"));

        var first = shutdown.Run();

        Assert.True(first);
        Assert.Equal(new[] { "throw", "later", "final" }, order);
    }

    [Fact]
    public void Run_PreservesStepOrderingBeforeFinalAction()
    {
        var order = new List<string>();
        var shutdown = new BoundedShutdown(
            new[]
            {
                BoundedShutdownStep.Run("one", () => order.Add("one")),
                BoundedShutdownStep.WaitFor("two", () =>
                {
                    order.Add("two");
                    return Task.CompletedTask;
                }, TimeSpan.FromSeconds(1)),
                BoundedShutdownStep.Run("three", () => order.Add("three")),
            },
            () => order.Add("final"));

        var first = shutdown.Run();
        var second = shutdown.Run();

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(new[] { "one", "two", "three", "final" }, order);
    }
}
