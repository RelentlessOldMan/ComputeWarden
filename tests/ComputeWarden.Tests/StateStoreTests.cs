using ComputeWarden.Core;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;
using ComputeWarden.Daemon;
using Xunit;

namespace ComputeWarden.Tests;

public class StateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cw-state-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_dir, "state.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static Warden NewWarden(FakeClock clock, CapturingLog? log = null)
        => new(clock, new LeaseOptions(), new WardenLimits(), new ManualBlockerProvider(clock), log: log);

    private static string Gpu(Warden w, string owner)
        => w.Acquire(new ClaimRequest(owner, "work", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait)).ReservationId!;

    [Fact]
    public void A_restart_keeps_claims_their_place_in_line_the_mode_the_hold_and_jobs_while_gaming()
    {
        var clock = new FakeClock();
        var before = NewWarden(clock);
        var a = Gpu(before, "A");
        var b = Gpu(before, "B");
        var c = Gpu(before, "C");
        before.MoveInLine(c, LineMove.Front);
        var shared = before.Acquire(new ClaimRequest("S", "build", ClaimKind.Shared, EstimateSeconds: 600)).ReservationId!;
        before.SetMode(UserMode.Steady, ResourceSet.Gpu, reason: "streaming");
        before.SetManualBusy("disk check", ResourceSet.Disk);
        before.SetGameJobs(1);

        var store = new StateStore(StatePath, before, new CapturingLog());
        store.Save();

        var log = new CapturingLog();
        var after = NewWarden(clock, log);
        new StateStore(StatePath, after, log).Load();

        Assert.Equal(new[] { c, b }, after.GetWaiting().Select(w => w.Id)); // the user's order survives
        var claims = after.GetStatus().Claims!.ToDictionary(x => x.Id);
        Assert.Equal(ClaimPhase.Running, claims[a].Phase);
        Assert.Equal(before.GetStatus().Claims!.Single(x => x.Id == shared).ExpiresAt, claims[shared].ExpiresAt);
        Assert.Equal(UserMode.Steady, after.GetMode().Mode);
        Assert.Equal("streaming", after.GetMode().Reason);
        Assert.Contains(after.GetStatus().Blockers, x => x.Type == BlockerType.Manual && x.Resources == ResourceSet.Disk);
        Assert.Equal(1, after.GameJobs);
        Assert.True(log.Contains("Restored 4 claim(s) from the last run (0 expired while down)"));
        Assert.True(log.Contains("1 job(s) while gaming"));

        // Agents carry on with the ids they have. (Steady mode would keep new gpu work waiting.)
        after.SetMode(UserMode.Auto);
        Assert.True(after.Renew(shared, 600).Renewed);
        Assert.True(after.Release(a).Released);
        var next = Gpu(after, "D");
        Assert.Equal(new[] { b, next }, after.GetWaiting().Select(w => w.Id)); // C's turn came; D joins at the back
        Assert.Equal(ClaimPhase.Ready, after.GetStatus().Claims!.Single(x => x.Id == c).Phase);
    }

    [Fact]
    public void Claims_whose_lease_ran_out_while_down_are_dropped()
    {
        var clock = new FakeClock();
        var before = NewWarden(clock);
        before.Acquire(new ClaimRequest("short", "x", ClaimKind.Shared, EstimateSeconds: 60));
        var longer = before.Acquire(new ClaimRequest("long", "y", ClaimKind.Shared, LeaseSeconds: 3600)).ReservationId!;
        new StateStore(StatePath, before, new CapturingLog()).Save();

        clock.Advance(TimeSpan.FromMinutes(30));
        var log = new CapturingLog();
        var after = NewWarden(clock, log);
        new StateStore(StatePath, after, log).Load();

        Assert.Equal(new[] { longer }, after.GetStatus().Claims!.Select(x => x.Id));
        Assert.True(log.Contains("Restored 1 claim(s) from the last run (1 expired while down)"));
    }

    [Fact]
    public void Save_writes_only_on_change_and_a_bad_file_is_moved_aside()
    {
        var clock = new FakeClock();
        var warden = NewWarden(clock);
        var store = new StateStore(StatePath, warden, new CapturingLog());
        store.Save();
        var first = File.GetLastWriteTimeUtc(StatePath);
        File.SetLastWriteTimeUtc(StatePath, first.AddHours(-1));
        store.Save(); // nothing changed
        Assert.Equal(first.AddHours(-1), File.GetLastWriteTimeUtc(StatePath));
        Assert.False(File.Exists(StatePath + ".tmp"));

        File.WriteAllText(StatePath, "{ not json");
        var log = new CapturingLog();
        var fresh = NewWarden(clock, log);
        new StateStore(StatePath, fresh, log).Load();

        Assert.Empty(fresh.GetStatus().Claims!);
        Assert.False(File.Exists(StatePath));
        Assert.Equal("{ not json", File.ReadAllText(StatePath + ".bad"));
        Assert.True(log.Contains("Couldn't restore the last run's claims"));
    }

    [Fact]
    public void No_file_means_a_clean_start()
    {
        var warden = NewWarden(new FakeClock());
        new StateStore(StatePath, warden, new CapturingLog()).Load();
        Assert.Empty(warden.GetStatus().Claims!);
    }
}
