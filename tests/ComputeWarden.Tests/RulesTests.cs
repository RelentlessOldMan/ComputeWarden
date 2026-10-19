using ComputeWarden.Core;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;
using ComputeWarden.Daemon;
using YamlDotNet.Core;

namespace ComputeWarden.Tests;

/// <summary>
/// The warden's three rules: (1) while the user games, work runs small and only a few jobs at a
/// time; (2) one big-memory job at a time; (3) solo claims get a quiet machine for a short timing run.
/// </summary>
public class RulesTests
{
    private sealed class Rig
    {
        public FakeClock Clock { get; } = new();
        public FakeBlockerProvider Processes { get; } = new();
        public CapturingLog Log { get; } = new();
        public Warden Warden { get; }

        public Rig(SharingOptions? sharing = null) => Warden = new Warden(Clock, new LeaseOptions(), new WardenLimits(),
            new ManualBlockerProvider(Clock), new[] { Processes }, Log, sharing);

        public void StartGame() => Processes.Result = ProviderResult.Ok(FakeBlockerProvider.Process("Overwatch.exe", 1));
        public void StopGame() => Processes.Result = ProviderResult.Empty;

        public AcquireResult Shared(string owner = "A", int estimate = 120, bool big = false)
            => Warden.Acquire(new ClaimRequest(owner, "dotnet test", ClaimKind.Shared, big, EstimateSeconds: estimate));

        public AcquireResult Big(string owner = "B", int estimate = 600) => Shared(owner, estimate, big: true);

        public AcquireResult Solo(string owner = "T", int estimate = 60)
            => Warden.Acquire(new ClaimRequest(owner, "timing test", ClaimKind.Solo, EstimateSeconds: estimate));

        public Grant Renew(AcquireResult r) => Warden.Renew(r.ReservationId!).Advice!.Grant;
    }

    // ---- Everyday work ----------------------------------------------------

    [Fact]
    public void Shared_work_on_an_idle_machine_runs_full_and_never_blocks_other_work()
    {
        var rig = new Rig();

        var claims = Enumerable.Range(0, 8).Select(i => rig.Shared($"A{i}")).ToList();

        Assert.All(claims, c => Assert.Equal(Grant.Full, c.Advice!.Grant));
        Assert.All(claims, c => Assert.Equal(ClaimKind.Shared, c.Kind));
        Assert.Equal(MachineState.Available, rig.Warden.GetStatus().State); // agent work doesn't make it busy
        Assert.False(rig.Warden.GetStatus().Claims!.Any(c => c.Phase != ClaimPhase.Running));
    }

    [Fact]
    public void Invalid_claims_are_refused()
    {
        var rig = new Rig();

        Assert.False(rig.Warden.Acquire(new ClaimRequest("", "x")).Acquired);
        Assert.False(rig.Warden.Acquire(new ClaimRequest("A", "x", EstimateSeconds: -1)).Acquired);
        var tooLong = rig.Warden.Acquire(new ClaimRequest("A", "x", ClaimKind.Solo, EstimateSeconds: 900));
        Assert.False(tooLong.Acquired);
        Assert.Contains("solo is for short timing runs", tooLong.Reason);
        Assert.Equal(3, rig.Warden.GetStats().FailedAcquisitions);
    }

    // ---- Rule 1: gaming ---------------------------------------------------

    [Fact]
    public void Game_throttles_work_with_concrete_advice()
    {
        var rig = new Rig();
        rig.StartGame();

        var a = rig.Shared();

        var advice = a.Advice!;
        Assert.Equal(Grant.Throttled, advice.Grant);
        Assert.Contains("Overwatch.exe is running", advice.Reason);
        Assert.Equal(1, advice.Throttle!.MaxCores);
        Assert.Equal("below_normal", advice.Throttle.Priority);
        Assert.Equal(2, advice.Throttle.MaxRamGb);
        Assert.Contains("-m:1", advice.How);
        Assert.Contains("/belownormal", advice.How);
        Assert.Contains("keep memory under 2 GB", advice.How);
        Assert.Contains("keep off the GPU", advice.How);
        Assert.Equal(MachineState.Busy, a.MachineState);
    }

    [Fact]
    public void Three_jobs_run_while_gaming_and_the_fourth_waits_its_turn()
    {
        var rig = new Rig();
        rig.StartGame();

        var running = new[] { rig.Shared("A"), rig.Shared("B"), rig.Shared("C") };
        var d = rig.Shared("D");

        Assert.All(running, r => Assert.Equal(Grant.Throttled, r.Advice!.Grant));
        Assert.Equal(Grant.Queued, d.Advice!.Grant);
        Assert.Contains("3 job(s) may run while gaming, and all are taken", d.Advice.Reason);
        Assert.Null(d.Advice.Queue!.EstWaitSeconds); // a game has no end we can predict
        Assert.Equal(1, d.Advice.Queue.Position);

        rig.Warden.Release(running[0].ReservationId!);
        Assert.Equal(Grant.Throttled, rig.Renew(d));
    }

    [Fact]
    public void Jobs_while_gaming_is_the_users_number_and_only_limits_while_gaming()
    {
        var rig = new Rig(new SharingOptions { GameJobs = 1 });
        Assert.Equal(1, rig.Warden.GameJobs);
        rig.Warden.SetGameJobs(0);

        var idle = new[] { rig.Shared("A"), rig.Shared("B") };
        Assert.All(idle, r => Assert.Equal(Grant.Full, r.Advice!.Grant)); // no game: no limit

        rig.StartGame();
        var c = rig.Shared("C");
        Assert.Equal(Grant.Queued, c.Advice!.Grant);
        Assert.Contains("no jobs may run while gaming", c.Advice.Reason);
        Assert.All(idle, r => Assert.Equal(Grant.Throttled, rig.Renew(r))); // running work keeps going, small

        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Warden.SetGameJobs(Warden.MaxGameJobs + 1));
    }

    [Fact]
    public void Game_starting_and_stopping_is_heard_on_renew()
    {
        var rig = new Rig();
        var a = rig.Shared();
        Assert.Equal(Grant.Full, a.Advice!.Grant);

        rig.StartGame();
        Assert.Equal(Grant.Throttled, rig.Renew(a));
        Assert.Equal(Grant.Throttled, rig.Warden.PeekGrant(a.ReservationId!));

        rig.StopGame();
        Assert.Equal(Grant.Full, rig.Warden.PeekGrant(a.ReservationId!));
        Assert.Equal(Grant.Throttled, rig.Warden.LastGrant(a.ReservationId!)); // peeking changes nothing
        Assert.Equal(Grant.Full, rig.Renew(a));
    }

    [Fact]
    public void Line_is_first_come_first_served_when_a_slot_frees()
    {
        var rig = new Rig(new SharingOptions { GameJobs = 1 });
        rig.StartGame();
        var a = rig.Shared("A");
        var b = rig.Shared("B");
        var c = rig.Shared("C");
        Assert.Equal(2, c.Advice!.Queue!.Position);
        Assert.Equal("B", Assert.Single(c.Advice.Queue.Ahead).Owner);

        rig.Warden.Release(a.ReservationId!);

        Assert.Equal(Grant.Queued, rig.Renew(c)); // B's turn came first: its slot is kept for it
        Assert.Equal(Grant.Throttled, rig.Renew(b));
    }

    [Fact]
    public void Manual_hold_lets_nothing_new_start_and_throttles_running_work()
    {
        var rig = new Rig();
        var a = rig.Shared("A");
        rig.Warden.SetManualBusy("recording a video");

        var b = rig.Shared("B");

        Assert.Equal(Grant.Queued, b.Advice!.Grant);
        Assert.Contains("manual hold: recording a video", b.Advice.Reason);
        Assert.Equal(Grant.Throttled, rig.Renew(a));
        Assert.Equal(MachineState.Busy, rig.Warden.GetStatus().State);

        Assert.True(rig.Warden.ClearManualBusy());
        Assert.Equal(Grant.Full, rig.Renew(b));
        Assert.Equal(Grant.Full, rig.Renew(a));
    }

    [Fact]
    public void Unknown_state_counts_as_gaming()
    {
        var rig = new Rig(new SharingOptions { GameJobs = 1 });
        rig.Processes.Result = ProviderResult.Unhealthy;

        var a = rig.Shared("A");
        var b = rig.Shared("B");

        Assert.Equal(Grant.Throttled, a.Advice!.Grant);
        Assert.Contains("UNKNOWN", a.Advice.Reason);
        Assert.Equal(Grant.Queued, b.Advice!.Grant);
        Assert.Equal(MachineState.Unknown, rig.Warden.GetStatus().State);
        Assert.False(rig.Warden.CanRun());
    }

    // ---- Rule 2: one big-memory job at a time -----------------------------

    [Fact]
    public void Only_one_big_memory_job_runs_and_everyday_work_is_unaffected()
    {
        var rig = new Rig();
        var big = rig.Big("CodeCarver");

        var second = rig.Big("CodeCompass");
        var normal = rig.Shared("Other");

        Assert.Equal(Grant.Full, big.Advice!.Grant);
        Assert.True(big.BigMemory);
        Assert.Equal(Grant.Queued, second.Advice!.Grant);
        Assert.Contains("CodeCarver is running a big-memory job", second.Advice.Reason);
        Assert.Contains("one at a time", second.Advice.Reason);
        Assert.Equal(Grant.Full, normal.Advice!.Grant); // a waiting big job doesn't hold up others
        Assert.Equal(MachineState.Available, rig.Warden.GetStatus().State);

        rig.Warden.Release(big.ReservationId!);
        Assert.Equal(Grant.Full, rig.Renew(second));
    }

    [Fact]
    public void Big_memory_wait_estimate_is_the_running_jobs_remaining_time()
    {
        var rig = new Rig();
        rig.Big("A", estimate: 600);
        rig.Clock.AdvanceSeconds(100);

        var b = rig.Big("B");

        Assert.Equal(500, b.Advice!.Queue!.EstWaitSeconds);
        Assert.Empty(b.Advice.Queue.Ahead); // A is running, not ahead in line
    }

    [Fact]
    public void While_gaming_a_waiting_big_job_is_ahead_for_the_next_slot()
    {
        var rig = new Rig(new SharingOptions { GameJobs = 1 });
        rig.Big("A");
        rig.Big("B"); // waits on A
        rig.StartGame();

        var c = rig.Shared("C");

        // When A ends, its slot and the big-memory turn both go to B: first come, first served.
        Assert.Equal(Grant.Queued, c.Advice!.Grant);
        var ahead = Assert.Single(c.Advice.Queue!.Ahead);
        Assert.Equal("B", ahead.Owner);
        Assert.Equal(ClaimPhase.Waiting, ahead.Phase);
    }

    // ---- Rule 3: solo -----------------------------------------------------

    [Fact]
    public void Solo_on_an_idle_machine_runs_full_and_makes_it_busy()
    {
        var rig = new Rig();

        var t = rig.Solo();

        Assert.Equal(ClaimKind.Solo, t.Kind);
        Assert.Equal(Grant.Full, t.Advice!.Grant);
        Assert.Contains("at most 10 minutes", t.Advice.Reason);
        var status = rig.Warden.GetStatus();
        Assert.Equal(MachineState.Busy, status.State);
        Assert.Equal(BlockerType.Reservation, Assert.Single(status.Blockers).Type);
    }

    [Fact]
    public void Solo_waits_for_running_work_and_nothing_new_starts_behind_it()
    {
        var rig = new Rig();
        var a = rig.Shared("A", estimate: 120);

        var t = rig.Solo();
        var b = rig.Shared("B", estimate: 300); // ends after A: it would delay the solo claim
        var big = rig.Big("C");

        Assert.Equal(Grant.Queued, t.Advice!.Grant);
        Assert.Contains("waiting for 1 running claim(s) to finish", t.Advice.Reason);
        Assert.Equal(120, t.Advice.Queue!.EstWaitSeconds);
        Assert.Equal(Grant.Queued, b.Advice!.Grant);
        Assert.Contains("T is waiting for a quiet machine", b.Advice.Reason);
        Assert.Equal(Grant.Queued, big.Advice!.Grant);
        Assert.Equal(Grant.Full, rig.Renew(a)); // running work keeps going, full

        rig.Warden.Release(a.ReservationId!);
        Assert.Equal(Grant.Queued, rig.Renew(b)); // the solo claim's turn has come; B still waits
        Assert.Equal(Grant.Full, rig.Renew(t));
        Assert.Contains("has the machine to itself", rig.Warden.Renew(b.ReservationId!).Advice!.Reason);

        rig.Warden.Release(t.ReservationId!);
        Assert.Equal(Grant.Full, rig.Renew(b));
        Assert.Equal(Grant.Full, rig.Renew(big));
    }

    [Fact]
    public void Short_work_starts_behind_a_waiting_solo_if_it_ends_before_the_running_work()
    {
        var rig = new Rig();
        var a = rig.Shared("A", estimate: 600);
        rig.Clock.AdvanceSeconds(100); // A should end in 500 s
        var t = rig.Solo();

        var fits = rig.Shared("B", estimate: 300);
        var exact = rig.Shared("C", estimate: 500);
        var tooLong = rig.Shared("D", estimate: 501);
        var unknown = rig.Warden.Acquire(new ClaimRequest("E", "build"));
        var solo2 = rig.Solo("U", estimate: 60);

        Assert.Equal(Grant.Queued, t.Advice!.Grant);
        Assert.Equal(Grant.Full, fits.Advice!.Grant);
        Assert.Equal(Grant.Full, exact.Advice!.Grant);
        Assert.Equal(Grant.Queued, tooLong.Advice!.Grant);
        Assert.Contains("only work whose estimateSeconds ends before the running work does", tooLong.Advice.Reason);
        Assert.Equal(Grant.Queued, unknown.Advice!.Grant); // no estimate: can't know it fits
        Assert.Equal(Grant.Queued, solo2.Advice!.Grant);
        Assert.Equal(500, t.Advice.Queue!.EstWaitSeconds); // the gap work doesn't push the solo claim back

        rig.Warden.Release(a.ReservationId!);
        rig.Warden.Release(fits.ReservationId!);
        rig.Warden.Release(exact.ReservationId!);
        Assert.Equal(Grant.Full, rig.Renew(t));
    }

    [Fact]
    public void Nothing_starts_behind_a_solo_claim_waiting_for_a_game()
    {
        var rig = new Rig();
        rig.StartGame();
        rig.Solo();

        var b = rig.Shared("B", estimate: 10);

        Assert.Equal(Grant.Queued, b.Advice!.Grant);
        Assert.Contains("nothing new starts until it's done", b.Advice.Reason);
    }

    [Fact]
    public void Game_keeps_a_solo_claim_waiting()
    {
        var rig = new Rig();
        rig.StartGame();

        var t = rig.Solo();

        Assert.Equal(Grant.Queued, t.Advice!.Grant);
        Assert.Contains("a timing run waits for a quiet machine", t.Advice.Reason);

        rig.StopGame();
        Assert.Equal(Grant.Full, rig.Renew(t));
    }

    [Fact]
    public void Solo_claim_lasts_at_most_ten_minutes_however_it_renews()
    {
        var rig = new Rig();
        var t = rig.Warden.Acquire(new ClaimRequest("T", "bench", ClaimKind.Solo, LeaseSeconds: 3600));
        Assert.True(t.ExpiresAt <= rig.Clock.UtcNow.AddSeconds(Warden.MaxSoloSeconds));

        rig.Clock.AdvanceSeconds(500);
        var renewed = rig.Warden.Renew(t.ReservationId!, leaseSeconds: 3600);
        Assert.Equal(rig.Clock.UtcNow.AddSeconds(100), renewed.ExpiresAt);
        rig.Warden.BeginWait(t.ReservationId!, 60);
        Assert.Equal(rig.Clock.UtcNow.AddSeconds(100), rig.Warden.GetStatus().Claims![0].ExpiresAt);

        rig.Clock.AdvanceSeconds(100);
        Assert.Empty(rig.Warden.GetStatus().Claims!);
        Assert.True(rig.Log.Contains("solo time (10m) is up"));
    }

    [Fact]
    public void User_approved_solo_claim_may_run_up_to_an_hour_and_keeps_that_across_a_restart()
    {
        var rig = new Rig();
        var t = rig.Warden.Acquire(new ClaimRequest("T", "long bench", ClaimKind.Solo, EstimateSeconds: 1800, UserApproved: true));
        Assert.True(t.Acquired);
        Assert.Equal(Grant.Full, t.Advice!.Grant);
        Assert.Contains("at most 60 minutes", t.Advice.Reason);
        Assert.True(rig.Log.Contains("user-approved 60m"));

        var tooLong = rig.Warden.Acquire(new ClaimRequest("U", "x", ClaimKind.Solo, EstimateSeconds: 3700, UserApproved: true));
        Assert.False(tooLong.Acquired);
        Assert.Contains("even with the user's approval", tooLong.Reason);
        var unapproved = rig.Warden.Acquire(new ClaimRequest("U", "x", ClaimKind.Solo, EstimateSeconds: 1800));
        Assert.Contains("needs the user's explicit approval", unapproved.Reason);

        var restored = new Rig();
        restored.Warden.Restore(rig.Warden.Save());
        restored.Clock.AdvanceSeconds(1500); // past 10 minutes, inside the approved hour
        var renewed = restored.Warden.Renew(t.ReservationId!, leaseSeconds: 3600);
        Assert.True(renewed.Renewed);
        Assert.Equal(restored.Clock.UtcNow.AddSeconds(2100), renewed.ExpiresAt);
    }

    [Fact]
    public void Status_shows_phases_and_places_in_line()
    {
        var rig = new Rig();
        rig.Shared("A");
        rig.Solo("T");
        rig.Shared("B", estimate: 300); // ends after A, so it waits behind T

        var claims = rig.Warden.GetStatus().Claims!;

        Assert.Equal(new[] { ClaimPhase.Running, ClaimPhase.Waiting, ClaimPhase.Waiting }, claims.Select(c => c.Phase));
        Assert.Equal(new int?[] { null, 1, 2 }, claims.Select(c => c.Position));
        Assert.Equal(new[] { "T", "B" }, rig.Warden.GetWaiting().Select(c => c.Owner));
    }

    // ---- Tickets and leases ----------------------------------------------

    [Fact]
    public void Queued_ticket_that_stops_checking_in_drops_out_of_line()
    {
        var rig = new Rig(new SharingOptions { GameJobs = 0, QueueCheckinSeconds = 60 });
        rig.StartGame();
        var a = rig.Shared("A");
        Assert.Equal(Grant.Queued, a.Advice!.Grant);

        rig.Clock.AdvanceSeconds(61);

        Assert.Empty(rig.Warden.GetStatus().Claims!);
        Assert.True(rig.Log.Contains("Queue ticket dropped (no check-in): A"));
        Assert.Equal(0, rig.Warden.GetStats().ExpiredReservations); // never ran: not a lapsed lease
    }

    [Fact]
    public void Started_claim_gets_a_lease_from_its_estimate()
    {
        var rig = new Rig();

        var a = rig.Shared("A", estimate: 200);

        Assert.Equal(rig.Clock.UtcNow.AddSeconds(280), a.ExpiresAt); // 200 * 1.25 + 30
    }

    [Fact]
    public void Probe_hears_its_turn_without_starting_the_claim_and_the_slot_passes_on_if_unused()
    {
        var rig = new Rig(new SharingOptions { GameJobs = 1, QueueCheckinSeconds = 60 });
        rig.StartGame();
        var a = rig.Shared("A");
        var b = rig.Shared("B");
        var c = rig.Shared("C");
        rig.Warden.Release(a.ReservationId!);

        var probe = rig.Warden.Renew(b.ReservationId!, probe: true);
        Assert.Equal(Grant.Throttled, probe.Advice!.Grant);
        Assert.Equal(Grant.Queued, rig.Warden.LastGrant(b.ReservationId!)); // not started
        Assert.Equal(ClaimPhase.Ready, rig.Warden.GetStatus().Claims!.Single(x => x.Owner == "B").Phase);

        rig.Clock.AdvanceSeconds(30);
        rig.Renew(c); // C keeps checking in; B never starts
        rig.Clock.AdvanceSeconds(31);
        Assert.Equal(Grant.Throttled, rig.Renew(c));
    }

    [Fact]
    public void Probe_on_a_running_claim_is_an_ordinary_renew()
    {
        var rig = new Rig();
        var a = rig.Shared("A", estimate: 200);
        rig.Clock.AdvanceSeconds(100);

        var probe = rig.Warden.Renew(a.ReservationId!, probe: true);

        Assert.Equal(rig.Clock.UtcNow.AddSeconds(280), probe.ExpiresAt);
    }

    [Fact]
    public void Long_poll_keeps_the_claim_alive_while_it_waits()
    {
        var rig = new Rig();
        var a = rig.Shared("A", estimate: 10); // a 42s lease

        Assert.Equal(Grant.Full, rig.Warden.BeginWait(a.ReservationId!, 60));
        rig.Clock.AdvanceSeconds(70);

        Assert.Single(rig.Warden.GetStatus().Claims!);
        Assert.Null(rig.Warden.BeginWait("nope", 60));
    }

    // ---- Saving across restarts ------------------------------------------

    [Fact]
    public void Save_and_restore_keep_claims_hold_and_jobs_while_gaming()
    {
        var rig = new Rig();
        rig.Big("Big");
        var t = rig.Solo("T");
        rig.Warden.SetManualBusy("video");
        rig.Warden.SetGameJobs(2);

        var saved = rig.Warden.Save();
        Assert.Equal(SavedState.Version, saved.V);
        var after = new Rig();
        after.Clock.AdvanceSeconds(1);
        Assert.Equal(2, after.Warden.Restore(saved));

        var claims = after.Warden.GetStatus().Claims!;
        Assert.True(claims[0].BigMemory);
        Assert.Equal(ClaimKind.Solo, claims[1].Kind);
        Assert.Equal(ClaimPhase.Waiting, claims[1].Phase);
        Assert.Equal(2, after.Warden.GameJobs);
        Assert.Contains(after.Warden.GetStatus().Blockers, b => b.Description == "video");
        Assert.Equal(Grant.Queued, after.Warden.Renew(t.ReservationId!).Advice!.Grant);
    }

    [Fact]
    public void Version_one_claims_restore_with_exclusive_read_as_shared()
    {
        var now = DateTimeOffset.UnixEpoch;
        var old = new SavedState(1, null, new[]
        {
            new SavedClaim("x", "Old", "heavy", now, now, now.AddHours(1), null, "exclusive", false, 60, 1, 300, true, now, Grant.Full),
            new SavedClaim("s", "Timer", "bench", now, now, now.AddHours(1), null, "solo", false, 60, 2, 300, false, null, Grant.Queued),
        });
        var rig = new Rig();

        rig.Warden.Restore(old);

        var claims = rig.Warden.GetStatus().Claims!;
        Assert.Equal(ClaimKind.Shared, claims[0].Kind);
        Assert.Equal(ClaimKind.Solo, claims[1].Kind);
        Assert.Equal(Grant.Queued, rig.Warden.Renew("s").Advice!.Grant); // waits for Old to finish
    }

    // ---- Config -----------------------------------------------------------

    [Fact]
    public void Sharing_settings_hot_reload()
    {
        var rig = new Rig();
        rig.StartGame();
        rig.Warden.UpdateSharing(new SharingOptions { ThrottleMaxCores = 2, GameJobs = 1 });

        var a = rig.Shared("A");
        var b = rig.Shared("B");

        Assert.Equal(2, a.Advice!.Throttle!.MaxCores);
        Assert.Equal(Grant.Queued, b.Advice!.Grant);
    }

    [Fact]
    public void Sharing_section_parses_with_old_names_accepted()
    {
        var config = ConfigLoader.Parse("""
            sharing:
              throttle_max_cores: 2
              throttle_priority: Idle
              max_throttled_per_resource: 5
              unshareable: [gpu]
            """);

        var s = config.Sharing;
        Assert.Equal(2, s.ThrottleMaxCores);
        Assert.Equal("idle", s.ThrottlePriority);
        Assert.Equal(5, s.GameJobs);
        Assert.Equal(new SharingOptions().ThrottleMaxRamGb, s.ThrottleMaxRamGb);
        Assert.Equal(new SharingOptions().QueueCheckinSeconds, s.QueueCheckinSeconds);
        Assert.Equal(1, ConfigLoader.Parse("sharing:\n  game_jobs: 1\n  max_throttled_per_resource: 5\n").Sharing.GameJobs);
    }

    [Fact]
    public void Example_config_parses_and_documents_the_sharing_defaults()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config.example.yaml"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var config = ConfigLoader.Parse(File.ReadAllText(Path.Combine(dir.FullName, "config.example.yaml")));

        Assert.Equivalent(new SharingOptions(), config.Sharing);
    }

    [Theory]
    [InlineData("throttle_max_cores: 0")]
    [InlineData("throttle_max_ram_gb: 0")]
    [InlineData("game_jobs: -1")]
    [InlineData("game_jobs: 13")]
    [InlineData("queue_checkin_seconds: 5")]
    [InlineData("throttle_priority: realtime")]
    public void Bad_sharing_values_are_rejected(string line)
        => Assert.ThrowsAny<YamlException>(() => ConfigLoader.Parse("sharing:\n  " + line + "\n"));
}
