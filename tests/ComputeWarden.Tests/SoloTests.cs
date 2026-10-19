using ComputeWarden.Core;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;

namespace ComputeWarden.Tests;

/// <summary>Solo claims: a quiet machine for timing tests and benchmarks.</summary>
public class SoloTests
{
    private sealed class Rig
    {
        public FakeClock Clock { get; } = new();
        public FakeBlockerProvider Processes { get; } = new();
        public CapturingLog Log { get; } = new();
        public Warden Warden { get; }

        public Rig() => Warden = new Warden(Clock, new LeaseOptions(), new WardenLimits(),
            new ManualBlockerProvider(Clock), new[] { Processes }, Log);

        public AcquireResult Solo(string owner = "T", ResourceSet resources = ResourceSet.None)
            => Warden.Acquire(new ClaimRequest(owner, "timing test", ClaimKind.Solo, resources, EstimateSeconds: 60));

        public AcquireResult Shared(string owner = "A")
            => Warden.Acquire(new ClaimRequest(owner, "dotnet test", ClaimKind.Shared, EstimateSeconds: 120));

        public AcquireResult Exclusive(string owner = "X", WhenBusy whenBusy = WhenBusy.Throttle)
            => Warden.Acquire(new ClaimRequest(owner, "heavy job", ClaimKind.Exclusive, ResourceSet.Cpu, whenBusy));

        public Grant Renew(AcquireResult r) => Warden.Renew(r.ReservationId!).Advice?.Grant ?? Grant.Full;
    }

    [Fact]
    public void Solo_on_an_idle_machine_gets_the_whole_machine()
    {
        var rig = new Rig();

        var r = rig.Solo(resources: ResourceSet.Cpu);

        Assert.True(r.Acquired);
        Assert.Equal(ClaimKind.Solo, r.Kind);
        Assert.Equal(ResourceSet.All, r.Resources); // a quiet machine is all of it, whatever was asked
        Assert.Equal(Grant.Full, r.Advice!.Grant);
        Assert.Equal("solo", Warden.KindName(ClaimKind.Solo));
    }

    [Fact]
    public void Solo_waits_for_running_shared_work_and_never_runs_small()
    {
        var rig = new Rig();
        var a = rig.Shared();

        var t = rig.Solo();

        Assert.Equal(Grant.Queued, t.Advice!.Grant);
        Assert.Contains("waiting for 1 running claim(s) to finish", t.Advice.Reason);
        Assert.NotNull(t.Advice.Queue!.EstWaitSeconds);
        Assert.Equal(Grant.Full, rig.Renew(a)); // running work keeps going

        rig.Warden.Release(a.ReservationId!);
        Assert.Equal(Grant.Full, rig.Renew(t));
    }

    [Fact]
    public void Nothing_new_starts_behind_a_waiting_solo_claim()
    {
        var rig = new Rig();
        var a = rig.Shared();
        var t = rig.Solo();
        Assert.Equal(Grant.Queued, t.Advice!.Grant);

        var b = rig.Shared("B");
        var x = rig.Exclusive();
        var legacy = rig.Warden.Acquire("legacy", "index");

        Assert.Equal(Grant.Queued, b.Advice!.Grant);
        Assert.Contains("T is waiting for a quiet machine", b.Advice.Reason);
        Assert.Contains(b.Advice.Queue!.Ahead, i => i.Id == t.ReservationId);
        Assert.Equal(Grant.Queued, x.Advice!.Grant);
        Assert.False(legacy.Acquired);

        // The last running claim finishes: the solo claim goes, the rest still wait for it.
        rig.Warden.Release(a.ReservationId!);
        Assert.Equal(Grant.Queued, rig.Renew(b));
        Assert.Equal(Grant.Full, rig.Renew(t));
        Assert.Equal(Grant.Queued, rig.Renew(x));
        Assert.Contains("T has the machine to itself", rig.Warden.Renew(b.ReservationId!).Advice!.Reason);

        // Released: the line moves again (the exclusive holder now throttles the shared claim on cpu).
        rig.Warden.Release(t.ReservationId!);
        Assert.Equal(Grant.Full, rig.Renew(x));
        Assert.Equal(Grant.Throttled, rig.Renew(b));
    }

    [Fact]
    public void Solo_waits_for_an_exclusive_holder_and_work_ahead_of_it_in_line()
    {
        var rig = new Rig();
        var x = rig.Exclusive("X");
        var y = rig.Exclusive("Y", WhenBusy.Wait);
        var t = rig.Solo();
        Assert.Equal(Grant.Queued, y.Advice!.Grant);
        Assert.Equal(Grant.Queued, t.Advice!.Grant);

        rig.Warden.Release(x.ReservationId!);
        Assert.Equal(Grant.Full, rig.Renew(y)); // ahead of the solo claim, so it goes first
        Assert.Equal(Grant.Queued, rig.Renew(t));

        rig.Warden.Release(y.ReservationId!);
        Assert.Equal(Grant.Full, rig.Renew(t));
    }

    [Fact]
    public void A_game_keeps_a_solo_claim_waiting()
    {
        var rig = new Rig();
        rig.Processes.Result = ProviderResult.Ok(FakeBlockerProvider.Process("Overwatch.exe", 1)
            with { Resources = ResourceSet.Cpu | ResourceSet.Gpu | ResourceSet.Ram });

        var t = rig.Solo();

        Assert.Equal(Grant.Queued, t.Advice!.Grant);
        Assert.Null(t.Advice.Queue!.EstWaitSeconds); // a game's end can't be predicted

        rig.Processes.Result = ProviderResult.Empty;
        Assert.Equal(Grant.Full, rig.Renew(t));
    }

    [Fact]
    public void Status_shows_the_solo_claim_waiting_in_line()
    {
        var rig = new Rig();
        rig.Shared();
        var t = rig.Solo();

        var waiting = rig.Warden.GetWaiting();

        var info = Assert.Single(waiting);
        Assert.Equal(t.ReservationId, info.Id);
        Assert.Equal(ClaimKind.Solo, info.Kind);
    }
}
