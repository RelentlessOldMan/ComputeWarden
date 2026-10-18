using ComputeWarden.Core;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Providers;
using ComputeWarden.Core.Model;
using Xunit;

namespace ComputeWarden.Tests;

public class LineTests
{
    private sealed class Rig
    {
        public readonly FakeClock Clock = new();
        public readonly CapturingLog Log = new();
        public readonly Warden Warden;

        public Rig() => Warden = new Warden(Clock, new LeaseOptions(), new WardenLimits(), new ManualBlockerProvider(Clock), log: Log);

        public string Gpu(string owner, string? priority = null)
            => Warden.Acquire(new ClaimRequest(owner, "work", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait,
                UserPriority: priority)).ReservationId!;

        public string[] Line() => Warden.GetWaiting().Select(c => c.Owner).ToArray();
    }

    [Fact]
    public void Waiting_lists_only_claims_still_waiting_first_in_line_first()
    {
        var rig = new Rig();
        rig.Gpu("A");
        rig.Gpu("B");
        rig.Gpu("C");

        Assert.Equal(new[] { "B", "C" }, rig.Line());
        var phases = rig.Warden.GetStatus().Claims!.ToDictionary(c => c.Owner, c => c.Phase);
        Assert.Equal(ClaimPhase.Running, phases["A"]);
        Assert.Equal(ClaimPhase.Waiting, phases["C"]);
    }

    [Fact]
    public void User_moves_a_waiting_claim_and_the_line_follows()
    {
        var rig = new Rig();
        rig.Gpu("A");
        rig.Gpu("B");
        rig.Gpu("C");
        var d = rig.Gpu("D");

        Assert.Equal(1, rig.Warden.MoveInLine(d, LineMove.Front));
        Assert.Equal(new[] { "D", "B", "C" }, rig.Line());
        Assert.Equal(2, rig.Warden.MoveInLine(d, LineMove.Down));
        Assert.Equal(new[] { "B", "D", "C" }, rig.Line());
        Assert.Equal(1, rig.Warden.MoveInLine(d, LineMove.Up));
        Assert.Equal(1, rig.Warden.MoveInLine(d, LineMove.Up)); // already first
        Assert.True(rig.Log.Contains("User moved D \"work\""));

        // The new order is the real one: D gets the gpu when A lets go.
        rig.Warden.Release(rig.Warden.GetStatus().Claims!.Single(c => c.Owner == "A").Id);
        var status = rig.Warden.GetStatus().Claims!.ToDictionary(c => c.Owner);
        Assert.Equal(ClaimPhase.Ready, status["D"].Phase);
        Assert.Equal(ClaimPhase.Waiting, status["B"].Phase);
    }

    [Fact]
    public void Running_or_unknown_claims_cant_be_moved()
    {
        var rig = new Rig();
        var a = rig.Gpu("A");
        Assert.Null(rig.Warden.MoveInLine(a, LineMove.Front));
        Assert.Null(rig.Warden.MoveInLine("nope", LineMove.Front));
    }

    [Fact]
    public void User_priority_claims_stay_first_and_moving_across_them_changes_priority()
    {
        var rig = new Rig();
        rig.Gpu("A");
        var p = rig.Gpu("P", priority: "the user asked");
        rig.Gpu("B");
        var c = rig.Gpu("C");
        Assert.Equal(new[] { "P", "B", "C" }, rig.Line());

        rig.Warden.MoveInLine(c, LineMove.Front);
        Assert.Equal(new[] { "C", "P", "B" }, rig.Line());
        Assert.NotNull(rig.Warden.GetWaiting()[0].UserPriority);

        rig.Warden.MoveInLine(p, LineMove.Down); // past B: P is no longer ahead of the plain claims
        Assert.Equal(new[] { "C", "B", "P" }, rig.Line());
        Assert.Null(rig.Warden.GetWaiting()[2].UserPriority);
    }
}
