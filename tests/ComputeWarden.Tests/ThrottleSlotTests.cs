using ComputeWarden.Core;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;

namespace ComputeWarden.Tests;

/// <summary>How throttle slots, the slot queue, and wait estimates are counted.</summary>
public class ThrottleSlotTests
{
    private sealed class Rig
    {
        public FakeClock Clock { get; } = new();
        public FakeBlockerProvider Processes { get; } = new();
        public Warden Warden { get; }
        public Rig(SharingOptions? sharing = null)
            => Warden = new Warden(Clock, new LeaseOptions(), new WardenLimits(),
                new ManualBlockerProvider(Clock), new[] { Processes }, null, sharing);
        public void Game(ResourceSet r) => Processes.Result = ProviderResult.Ok(FakeBlockerProvider.Process("g.exe", 1) with { Resources = r });
        public AcquireResult Shared(ResourceSet r, string? up = null) => Warden.Acquire(new ClaimRequest("s", "build", ClaimKind.Shared, r, UserPriority: up));
        public AcquireResult Excl(ResourceSet r, WhenBusy wb = WhenBusy.Throttle, int? est = null, string? up = null)
            => Warden.Acquire(new ClaimRequest("e", "heavy", ClaimKind.Exclusive, r, wb, EstimateSeconds: est, UserPriority: up));
    }

    // Tickets queued because the gpu is unshareable aren't waiting for a cpu slot.
    [Fact]
    public void Gpu_queued_tickets_do_not_consume_cpu_throttle_slots()
    {
        var rig = new Rig();
        rig.Game(ResourceSet.Cpu | ResourceSet.Gpu | ResourceSet.Ram);
        for (var i = 0; i < 3; i++)
            Assert.Equal(Grant.Queued, rig.Shared(ResourceSet.Cpu | ResourceSet.Gpu).Advice!.Grant); // gpu unshareable
        // Nobody is throttled on cpu, so a cpu-only build should get a slot.
        Assert.Equal(Grant.Throttled, rig.Shared(ResourceSet.Cpu).Advice!.Grant);
    }

    // Nor are tickets queued by a hold.
    [Fact]
    public void Hold_queued_tickets_do_not_consume_cpu_throttle_slots()
    {
        var rig = new Rig();
        rig.Game(ResourceSet.Cpu);
        rig.Warden.SetManualBusy("disk maintenance", ResourceSet.Disk);
        for (var i = 0; i < 3; i++)
            Assert.Equal(Grant.Queued, rig.Shared(Warden.SharedDefault | ResourceSet.Disk).Advice!.Grant); // held on disk
        Assert.Equal(Grant.Throttled, rig.Shared(ResourceSet.Cpu).Advice!.Grant);
    }

    // A claim takes a slot only on what it was throttled on.
    [Fact]
    public void Claims_throttled_only_on_disk_do_not_fill_cpu_slots()
    {
        var rig = new Rig();
        rig.Excl(ResourceSet.Disk);                 // holder on disk
        for (var i = 0; i < 3; i++)
        {
            var a = rig.Shared(Warden.SharedDefault | ResourceSet.Disk).Advice!;
            Assert.Equal(Grant.Throttled, a.Grant);
            Assert.Equal(ResourceSet.Disk, a.Contended); // throttled on disk only; cpu is full speed
        }
        rig.Excl(ResourceSet.Cpu);                  // holder on cpu
        // An exclusive cpu job behind the cpu holder: nobody is throttled on cpu.
        Assert.Equal(Grant.Throttled, rig.Excl(ResourceSet.Cpu).Advice!.Grant);
    }

    // An exclusive claim waiting for a slot keeps its place ahead of later shared claims.
    [Fact]
    public void Cap_queued_exclusive_keeps_its_place_ahead_of_later_shared()
    {
        var rig = new Rig();
        rig.Game(ResourceSet.Cpu);
        var s = Enumerable.Range(0, 3).Select(_ => rig.Shared(ResourceSet.Cpu)).ToList();
        Assert.All(s, x => Assert.Equal(Grant.Throttled, x.Advice!.Grant));
        var e = rig.Excl(ResourceSet.Cpu);
        Assert.Equal(Grant.Queued, e.Advice!.Grant);
        rig.Warden.Release(s[0].ReservationId!);   // a slot frees
        var late = rig.Shared(ResourceSet.Cpu);     // newcomer arrives before E checks in
        Assert.Equal(Grant.Queued, late.Advice!.Grant);
    }

    // User priority goes to the front of the slot queue too.
    [Fact]
    public void UserPriority_shared_claim_skips_the_cap_queue()
    {
        var rig = new Rig();
        rig.Game(ResourceSet.Cpu);
        for (var i = 0; i < 3; i++) rig.Shared(ResourceSet.Cpu);
        var waiter = rig.Shared(ResourceSet.Cpu);
        Assert.Equal(Grant.Queued, waiter.Advice!.Grant);
        var vip = rig.Shared(ResourceSet.Cpu, up: "user: me first");
        Assert.Equal(1, vip.Advice!.Queue!.Position); // should be at the front
    }

    // Huge estimates don't wrap the wait estimate negative.
    [Fact]
    public void Wait_estimate_does_not_overflow()
    {
        var rig = new Rig();
        rig.Excl(ResourceSet.Cpu, est: int.MaxValue / 2);
        rig.Excl(ResourceSet.Cpu, WhenBusy.Wait, est: int.MaxValue / 2);
        rig.Excl(ResourceSet.Cpu, WhenBusy.Wait, est: int.MaxValue / 2);
        var last = rig.Excl(ResourceSet.Cpu, WhenBusy.Wait);
        Assert.True(last.Advice!.Queue!.EstWaitSeconds is null or >= 0, $"est = {last.Advice.Queue.EstWaitSeconds}");
    }

    // can_run says no where a legacy acquire would be refused for the line.
    [Fact]
    public void CanRun_agrees_with_legacy_acquire_when_line_waits()
    {
        var rig = new Rig();
        rig.Game(ResourceSet.Gpu);
        Assert.Equal(Grant.Queued, rig.Excl(ResourceSet.Cpu | ResourceSet.Gpu, WhenBusy.Wait).Advice!.Grant);
        var can = rig.Warden.CanRun(ResourceSet.Cpu);
        var got = rig.Warden.Acquire("legacy", "x", resources: ResourceSet.Cpu).Acquired;
        Assert.Equal(can, got);
    }

    // Running work a new game now contends counts against the cap before it checks in.
    [Fact]
    public void Cap_counts_running_work_that_a_new_game_now_contends()
    {
        var rig = new Rig();
        var running = Enumerable.Range(0, 3).Select(_ => rig.Shared(ResourceSet.Cpu)).ToList();
        Assert.All(running, x => Assert.Equal(Grant.Full, x.Advice!.Grant));
        rig.Game(ResourceSet.Cpu);
        Assert.Equal(Grant.Queued, rig.Shared(ResourceSet.Cpu).Advice!.Grant); // 3 running already contend
    }
}
