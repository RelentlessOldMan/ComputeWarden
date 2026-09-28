using ComputeWarden.Core;
using ComputeWarden.Core.Model;

namespace ComputeWarden.Tests;

public class ConcurrencyTests
{
    /// <summary>
    /// The critical correctness test (spec §36): when many agents attempt to acquire the
    /// exclusive machine simultaneously, exactly one must succeed and every loser must see
    /// the winner as a blocker.
    /// </summary>
    [Fact]
    public void Concurrent_acquire_yields_exactly_one_winner()
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
                results[id] = warden.Acquire($"agent-{id}", "intensive work");
            });
            threads[i].Start();
        }

        foreach (var t in threads) t.Join();

        Assert.Equal(1, results.Count(r => r.Acquired));
        Assert.Equal(agents - 1, results.Count(r => !r.Acquired));
        Assert.All(
            results.Where(r => !r.Acquired),
            loser => Assert.Contains(loser.Blockers, b => b.Type == BlockerType.Reservation));
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
}
