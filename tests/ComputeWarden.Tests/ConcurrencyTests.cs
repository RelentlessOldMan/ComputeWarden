using ComputeWarden.Core;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Providers;

namespace ComputeWarden.Tests;

public class ConcurrencyTests
{
    /// <summary>
    /// The critical correctness test: when many agents ask for a big-memory job at once,
    /// exactly one may run, and the rest wait in one line with no gaps or duplicates.
    /// </summary>
    [Fact]
    public void Concurrent_big_memory_acquires_yield_exactly_one_runner()
    {
        const int agents = 256;
        var warden = TestFactory.NewWarden(new FakeClock());
        var results = new AcquireResult[agents];

        // Use dedicated threads (not the thread pool): all threads block on the barrier at
        // once, and the pool injects new threads far too slowly to release a 256-way barrier
        // in reasonable time. Real threads let every agent hit Acquire truly simultaneously.
        using var gate = new Barrier(agents);
        var threads = new Thread[agents];
        for (var i = 0; i < agents; i++)
        {
            var id = i;
            threads[i] = new Thread(() =>
            {
                gate.SignalAndWait(); // maximize contention: all fire at once
                results[id] = warden.Acquire(new ClaimRequest($"agent-{id}", "intensive work", BigMemory: true));
            });
            threads[i].Start();
        }

        foreach (var t in threads) t.Join();

        Assert.All(results, r => Assert.True(r.Acquired));
        Assert.Equal(1, results.Count(r => r.Advice!.Grant == Grant.Full));
        var waiting = results.Where(r => r.Advice!.Grant == Grant.Queued).ToList();
        Assert.Equal(agents - 1, waiting.Count);
        // Each saw the line as it stood when it joined: the n-th to join was n-th in line.
        Assert.Equal(Enumerable.Range(1, agents - 1), waiting.Select(r => r.Advice!.Queue!.Position!.Value).Order());
    }

    [Fact]
    public void Rapid_acquire_release_cycles_stay_consistent()
    {
        var warden = TestFactory.NewWarden(new FakeClock());
        const int iterations = 1000;

        for (var i = 0; i < iterations; i++)
        {
            var acquired = warden.Acquire("agent", "work");
            Assert.True(acquired.Acquired);
            var released = warden.Release(acquired.ReservationId!);
            Assert.True(released.Released);
        }

        Assert.True(warden.CanRun());
    }

    private sealed class StallOnce : IBlockerProvider
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        private int _calls;
        public string Name => "stall";
        public ProviderResult GetBlockers()
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(30));
            }
            return ProviderResult.Empty;
        }
    }

    [Fact]
    public void Acquire_waits_while_another_acquire_holds_the_lock()
    {
        // The first acquire stalls inside a provider, which runs under the warden's lock. A
        // second acquire must wait for it: if it gets through, the lock isn't shared.
        var clock = new FakeClock();
        var stall = new StallOnce();
        var warden = new Warden(clock, new LeaseOptions(), new WardenLimits(), new ManualBlockerProvider(clock), new[] { stall });

        var first = new Thread(() => warden.Acquire(new ClaimRequest("A", "x")));
        first.Start();
        Assert.True(stall.Entered.Wait(TimeSpan.FromSeconds(30)));
        var secondDone = new ManualResetEventSlim();
        var second = new Thread(() => { warden.Acquire(new ClaimRequest("B", "y")); secondDone.Set(); });
        second.Start();

        var gotThrough = secondDone.Wait(TimeSpan.FromSeconds(2));
        stall.Release.Set();
        first.Join();
        second.Join();

        Assert.False(gotThrough);
        Assert.True(secondDone.IsSet);
    }
}
