using ComputeWarden.Core;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;
using ComputeWarden.Daemon;
using YamlDotNet.Core;

namespace ComputeWarden.Tests;

/// <summary>Shared work, the line, advice, and the user mode (DESIGN.md §16).</summary>
public class SharingTests
{
    private const ResourceSet Game = ResourceSet.Cpu | ResourceSet.Gpu | ResourceSet.Ram;

    private sealed class Rig
    {
        public FakeClock Clock { get; } = new();
        public FakeBlockerProvider Processes { get; } = new();
        public CapturingLog Log { get; } = new();
        public Warden Warden { get; }

        public Rig(SharingOptions? sharing = null, LeaseOptions? lease = null)
            => Warden = new Warden(Clock, lease ?? new LeaseOptions(), new WardenLimits(),
                new ManualBlockerProvider(Clock), new[] { Processes }, Log, sharing);

        public void StartGame(string exe = "Overwatch.exe", ResourceSet resources = Game)
            => Processes.Result = ProviderResult.Ok(FakeBlockerProvider.Process(exe, 1) with { Resources = resources });

        public void StopGame() => Processes.Result = ProviderResult.Empty;

        public AcquireResult Shared(string owner = "A", ResourceSet resources = ResourceSet.None, int? estimate = null)
            => Warden.Acquire(new ClaimRequest(owner, "dotnet test", ClaimKind.Shared, resources, EstimateSeconds: estimate));

        public AcquireResult Exclusive(string owner, ResourceSet resources = ResourceSet.Cpu | ResourceSet.Ram,
            WhenBusy whenBusy = WhenBusy.Throttle, string? userPriority = null, int? estimate = null)
            => Warden.Acquire(new ClaimRequest(owner, "heavy job", ClaimKind.Exclusive, resources, whenBusy,
                EstimateSeconds: estimate, UserPriority: userPriority));

        public Grant Renew(AcquireResult r) => Warden.Renew(r.ReservationId!).Advice?.Grant ?? Grant.Full;
    }

    // ---- Shared work ----------------------------------------------------

    [Fact]
    public void Shared_claim_on_an_idle_machine_runs_full_and_does_not_make_it_busy()
    {
        var rig = new Rig();

        var r = rig.Shared();

        Assert.True(r.Acquired);
        Assert.Equal(ClaimKind.Shared, r.Kind);
        Assert.Equal(Grant.Full, r.Advice!.Grant);
        Assert.Equal(Warden.SharedDefault, r.Resources);
        var status = rig.Warden.GetStatus();
        Assert.Equal(MachineState.Available, status.State);
        Assert.Single(status.Claims!);
    }

    [Fact]
    public void Shared_claims_never_block_each_other_or_legacy_acquires()
    {
        var rig = new Rig();
        rig.Shared("A");
        Assert.Equal(Grant.Full, rig.Shared("B").Advice!.Grant);

        Assert.True(rig.Warden.Acquire("legacy", "index").Acquired);
    }

    [Fact]
    public void Game_throttles_shared_work_with_concrete_advice()
    {
        var rig = new Rig();
        rig.StartGame();

        var r = rig.Shared();

        Assert.True(r.Acquired);
        var a = r.Advice!;
        Assert.Equal(Grant.Throttled, a.Grant);
        Assert.Equal(ResourceSet.Cpu | ResourceSet.Ram, a.Contended); // disk is free, gpu not asked for
        Assert.Equal(1, a.Throttle!.MaxCores);
        Assert.Equal("below_normal", a.Throttle.Priority);
        Assert.Equal(2, a.Throttle.MaxRamGb);
        Assert.Contains("Overwatch.exe", a.Reason);
        Assert.Contains("-m:1", a.How);
        Assert.Contains("start \"\" /b /wait /belownormal <command>", a.How);
    }

    [Fact]
    public void Three_throttled_jobs_fit_while_gaming_and_the_fourth_waits()
    {
        var rig = new Rig();
        rig.StartGame();

        Assert.Equal(Grant.Throttled, rig.Shared("A").Advice!.Grant);
        Assert.Equal(Grant.Throttled, rig.Shared("B").Advice!.Grant);
        Assert.Equal(Grant.Throttled, rig.Shared("C").Advice!.Grant);
        var d = rig.Shared("D");

        Assert.Equal(Grant.Queued, d.Advice!.Grant);
        Assert.Contains("throttle slots full", d.Advice.Reason);
    }

    [Fact]
    public void Jobs_while_gaming_set_by_the_user_replaces_the_slot_count_beside_a_game()
    {
        var rig = new Rig();
        Assert.Equal(3, rig.Warden.GameJobs); // the config's slots until the user picks
        rig.Warden.SetGameJobs(1);
        rig.StartGame();

        var a = rig.Shared("A");
        var b = rig.Shared("B");

        Assert.Equal(Grant.Throttled, a.Advice!.Grant);
        Assert.Equal(Grant.Queued, b.Advice!.Grant);
        Assert.Contains("(1 while a game runs)", b.Advice.Reason);
        Assert.True(rig.Log.Contains("Jobs while gaming set to 1"));

        rig.Warden.SetGameJobs(2); // raising it lets the next one start
        Assert.Equal(Grant.Throttled, rig.Renew(b));

        rig.Warden.SetGameJobs(0); // lowering it stops new starts, not running work
        Assert.Equal(Grant.Queued, rig.Shared("C").Advice!.Grant);
        Assert.Equal(Grant.Throttled, rig.Renew(a));
    }

    [Fact]
    public void Jobs_while_gaming_doesnt_limit_work_when_no_game_runs()
    {
        var rig = new Rig();
        rig.Warden.SetGameJobs(0);

        Assert.Equal(Grant.Full, rig.Shared("A").Advice!.Grant);
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Warden.SetGameJobs(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Warden.SetGameJobs(Warden.MaxGameJobs + 1));
        Assert.Equal(0, rig.Warden.GameJobs);
    }

    [Fact]
    public void Game_exit_turns_throttled_work_full_on_renew()
    {
        var rig = new Rig();
        rig.StartGame();
        var r = rig.Shared();
        Assert.Equal(Grant.Throttled, r.Advice!.Grant);

        rig.StopGame();

        Assert.Equal(Grant.Full, rig.Renew(r));
    }

    [Fact]
    public void Game_starting_mid_run_throttles_running_work_on_renew()
    {
        var rig = new Rig();
        var r = rig.Shared();
        rig.StartGame();

        var renew = rig.Warden.Renew(r.ReservationId!);

        Assert.True(renew.Renewed);
        Assert.Equal(Grant.Throttled, renew.Advice!.Grant);
        Assert.Contains("Overwatch.exe", renew.Reason);
    }

    [Fact]
    public void Running_work_is_never_queued_even_when_slots_are_full()
    {
        var rig = new Rig(new SharingOptions { MaxThrottledPerResource = 1 });
        var a = rig.Shared("A");
        var b = rig.Shared("B");
        rig.StartGame();

        Assert.Equal(Grant.Throttled, rig.Renew(a));
        Assert.Equal(Grant.Throttled, rig.Renew(b)); // over the cap, but already running
        Assert.Equal(Grant.Queued, rig.Shared("C").Advice!.Grant);
    }

    [Fact]
    public void Gpu_work_is_queued_behind_a_game_rather_than_throttled()
    {
        var rig = new Rig();
        rig.StartGame();

        var r = rig.Shared(resources: ResourceSet.Gpu);

        Assert.Equal(Grant.Queued, r.Advice!.Grant);
        Assert.Contains("gpu can't be shared", r.Advice.Reason);
        Assert.Null(r.Advice.Queue!.EstWaitSeconds); // nobody knows when the game ends
    }

    [Fact]
    public void Game_on_unrelated_resources_does_not_throttle()
    {
        var rig = new Rig();
        rig.StartGame(resources: ResourceSet.Gpu);

        Assert.Equal(Grant.Full, rig.Shared().Advice!.Grant);
    }

    [Fact]
    public void Shared_work_runs_throttled_beside_an_exclusive_holder()
    {
        var rig = new Rig();
        var heavy = rig.Exclusive("Heavy");
        Assert.Equal(Grant.Full, heavy.Advice!.Grant);

        var r = rig.Shared();

        Assert.Equal(Grant.Throttled, r.Advice!.Grant);
        Assert.Contains("Heavy holds", r.Advice.Reason);
        Assert.Equal(ResourceSet.Cpu | ResourceSet.Ram, r.Advice.Contended);
    }

    // ---- Exclusive claims and the line ---------------------------------

    [Fact]
    public void Exclusive_claims_take_turns_first_come_first_served()
    {
        var rig = new Rig();
        var a = rig.Exclusive("A");
        var b = rig.Exclusive("B", whenBusy: WhenBusy.Wait, estimate: 60);
        var c = rig.Exclusive("C", whenBusy: WhenBusy.Wait);

        Assert.Equal(Grant.Full, a.Advice!.Grant);
        Assert.Equal(Grant.Queued, b.Advice!.Grant);
        Assert.Equal(1, b.Advice.Queue!.Position);
        Assert.Equal(2, c.Advice!.Queue!.Position);
        Assert.Equal("B", Assert.Single(c.Advice.Queue.Ahead).Owner);

        rig.Warden.Release(a.ReservationId!);

        Assert.Equal(Grant.Full, rig.Renew(b));
        Assert.Equal(Grant.Queued, rig.Renew(c));
    }

    [Fact]
    public void Exclusive_throttle_claim_runs_small_then_goes_full_when_its_turn_comes()
    {
        var rig = new Rig();
        var a = rig.Exclusive("A");
        var b = rig.Exclusive("B");

        Assert.Equal(Grant.Throttled, b.Advice!.Grant);
        Assert.Contains("A holds", b.Advice.Reason);

        rig.Warden.Release(a.ReservationId!);

        Assert.Equal(Grant.Full, rig.Renew(b));
        Assert.True(rig.Warden.GetStatus().Claims!.Single().Holding);
    }

    [Fact]
    public void Exclusive_claims_on_disjoint_resources_both_run_full()
    {
        var rig = new Rig();
        Assert.Equal(Grant.Full, rig.Exclusive("A", ResourceSet.Cpu).Advice!.Grant);
        Assert.Equal(Grant.Full, rig.Exclusive("B", ResourceSet.Gpu).Advice!.Grant);
    }

    [Fact]
    public void Exclusive_refuse_creates_nothing_when_busy()
    {
        var rig = new Rig();
        rig.Exclusive("A");

        var b = rig.Exclusive("B", whenBusy: WhenBusy.Refuse);

        Assert.False(b.Acquired);
        Assert.Single(rig.Warden.GetStatus().Claims!);
    }

    [Fact]
    public void User_priority_skips_the_line_but_never_preempts_a_holder()
    {
        var rig = new Rig();
        var a = rig.Exclusive("A");
        var b = rig.Exclusive("B", whenBusy: WhenBusy.Wait);
        var urgent = rig.Exclusive("Urgent", whenBusy: WhenBusy.Wait, userPriority: "user: release build first");

        Assert.Equal(1, urgent.Advice!.Queue!.Position);
        Assert.Equal(Grant.Full, rig.Renew(a)); // still holds

        rig.Warden.Release(a.ReservationId!);

        Assert.Equal(Grant.Full, rig.Renew(urgent));
        Assert.Equal(Grant.Queued, rig.Renew(b));
        Assert.True(rig.Log.Contains("user priority: user: release build first"));
    }

    [Fact]
    public void Legacy_acquire_cannot_jump_the_line()
    {
        var rig = new Rig();
        rig.StartGame(resources: ResourceSet.Gpu);
        var a = rig.Exclusive("A", ResourceSet.Cpu);
        rig.Exclusive("B", ResourceSet.Cpu | ResourceSet.Gpu, WhenBusy.Wait);
        rig.Warden.Release(a.ReservationId!);
        // The cpu is free, but B (still waiting on the gpu) is in line for it.

        var legacy = rig.Warden.Acquire("legacy", "index", resources: ResourceSet.Cpu);

        Assert.False(legacy.Acquired);
        Assert.Contains("waiting in line", legacy.Reason);
    }

    [Fact]
    public void Exclusive_waits_for_a_game_and_takes_over_when_it_exits()
    {
        var rig = new Rig();
        rig.StartGame();
        var r = rig.Exclusive("A", whenBusy: WhenBusy.Wait);
        Assert.Equal(Grant.Queued, r.Advice!.Grant);
        Assert.Contains("Overwatch.exe", r.Advice.Reason);

        rig.StopGame();

        Assert.Equal(Grant.Full, rig.Renew(r));
        Assert.True(rig.Log.Contains("Claim promoted to hold: A"));
    }

    [Fact]
    public void Game_throttles_even_an_exclusive_holder()
    {
        var rig = new Rig();
        var a = rig.Exclusive("A");
        rig.StartGame();

        var renew = rig.Warden.Renew(a.ReservationId!);

        Assert.Equal(Grant.Throttled, renew.Advice!.Grant);
    }

    [Fact]
    public void Queue_estimate_adds_the_holders_remaining_time_and_those_ahead()
    {
        var rig = new Rig();
        rig.Exclusive("A", estimate: 100);
        rig.Exclusive("B", whenBusy: WhenBusy.Wait, estimate: 50);
        rig.Clock.AdvanceSeconds(40);

        var c = rig.Exclusive("C", whenBusy: WhenBusy.Wait);

        Assert.Equal(60 + 50, c.Advice!.Queue!.EstWaitSeconds);
    }

    [Fact]
    public void Queued_ticket_that_stops_checking_in_drops_out_of_line()
    {
        var rig = new Rig(new SharingOptions { QueueCheckinSeconds = 30 });
        var a = rig.Exclusive("A");
        var b = rig.Exclusive("B", whenBusy: WhenBusy.Wait);
        var c = rig.Exclusive("C", whenBusy: WhenBusy.Wait);

        rig.Clock.AdvanceSeconds(20);
        rig.Renew(c);
        rig.Clock.AdvanceSeconds(20); // B missed its check-in

        Assert.False(rig.Warden.Renew(b.ReservationId!).Renewed);
        Assert.Equal(1, rig.Warden.Renew(c.ReservationId!).Advice!.Queue!.Position);
        Assert.True(rig.Log.Contains("Queue ticket dropped (no check-in): B"));
        Assert.Equal(0, rig.Warden.GetStats().ExpiredReservations);
        Assert.True(rig.Warden.Renew(a.ReservationId!).Renewed); // the holder is unaffected
    }

    [Fact]
    public void Started_claim_uses_its_own_lease_derived_from_the_estimate()
    {
        var rig = new Rig();

        var r = rig.Shared(estimate: 400);

        // 400 * 1.25 + 30
        Assert.Equal(rig.Clock.UtcNow.AddSeconds(530), r.ExpiresAt);
        rig.Clock.AdvanceSeconds(100);
        Assert.Equal(rig.Clock.UtcNow.AddSeconds(530), rig.Warden.Renew(r.ReservationId!).ExpiresAt);
    }

    [Fact]
    public void Probe_hears_its_turn_without_starting_the_claim()
    {
        var rig = new Rig(new SharingOptions { QueueCheckinSeconds = 30 });
        var a = rig.Exclusive("A");
        var b = rig.Exclusive("B", whenBusy: WhenBusy.Wait, estimate: 400);
        rig.Warden.Release(a.ReservationId!);

        // The background waiter's renew: told "go", but the claim is still a ticket with its window.
        var woken = rig.Warden.Renew(b.ReservationId!, probe: true);
        Assert.Equal(Grant.Full, woken.Advice!.Grant);
        Assert.Equal(rig.Clock.UtcNow.AddSeconds(30), woken.ExpiresAt);
        Assert.Equal(Grant.Queued, rig.Warden.LastGrant(b.ReservationId!));

        // The owner's renew starts it, with the full lease from its estimate (400 * 1.25 + 30).
        rig.Clock.AdvanceSeconds(10);
        Assert.Equal(rig.Clock.UtcNow.AddSeconds(530), rig.Warden.Renew(b.ReservationId!).ExpiresAt);
        Assert.Equal(Grant.Full, rig.Warden.LastGrant(b.ReservationId!));
    }

    [Fact]
    public void Owner_that_hears_its_turn_itself_gets_the_full_lease_at_once()
    {
        var rig = new Rig(new SharingOptions { QueueCheckinSeconds = 30 });
        var a = rig.Exclusive("A");
        var b = rig.Exclusive("B", whenBusy: WhenBusy.Wait, estimate: 400);
        rig.Warden.Release(a.ReservationId!);

        Assert.Equal(rig.Clock.UtcNow.AddSeconds(530), rig.Warden.Renew(b.ReservationId!).ExpiresAt);
    }

    [Fact]
    public void Probe_on_a_running_claim_is_an_ordinary_renew()
    {
        var rig = new Rig();
        var r = rig.Shared(estimate: 400);
        rig.Clock.AdvanceSeconds(100);

        Assert.Equal(rig.Clock.UtcNow.AddSeconds(530), rig.Warden.Renew(r.ReservationId!, probe: true).ExpiresAt);
    }

    [Fact]
    public void Woken_claim_that_never_starts_drops_out_as_a_ticket_and_passes_the_slot_on()
    {
        var rig = new Rig(new SharingOptions { QueueCheckinSeconds = 30 });
        var a = rig.Exclusive("A");
        var b = rig.Exclusive("B", whenBusy: WhenBusy.Wait, estimate: 3000);
        var c = rig.Exclusive("C", whenBusy: WhenBusy.Wait);
        rig.Warden.Release(a.ReservationId!);
        Assert.Equal(Grant.Full, rig.Warden.Renew(b.ReservationId!, probe: true).Advice!.Grant); // B's waiter hears "go"...

        // ...and while B hasn't started, C is told B's whole estimate, not just B's short window.
        var waiting = rig.Warden.Renew(c.ReservationId!).Advice!;
        Assert.Equal(Grant.Queued, waiting.Grant);
        Assert.True(waiting.Queue!.EstWaitSeconds >= 3000, $"est {waiting.Queue.EstWaitSeconds}");

        rig.Clock.AdvanceSeconds(20);
        Assert.Equal(Grant.Queued, rig.Renew(c));
        rig.Clock.AdvanceSeconds(20); // B's owner never renewed to start

        Assert.False(rig.Warden.Renew(b.ReservationId!).Renewed);
        Assert.True(rig.Log.Contains("Queue ticket dropped (no check-in): B"));
        Assert.Equal(0, rig.Warden.GetStats().ExpiredReservations);
        Assert.Equal(Grant.Full, rig.Renew(c));
    }

    [Fact]
    public void Waiters_on_separate_resources_wake_together_but_not_on_the_same_one()
    {
        var rig = new Rig();
        var holder = rig.Exclusive("H", ResourceSet.Gpu | ResourceSet.Disk);
        var gpu1 = rig.Exclusive("G1", ResourceSet.Gpu, WhenBusy.Wait);
        var gpu2 = rig.Exclusive("G2", ResourceSet.Gpu, WhenBusy.Wait);
        var disk = rig.Exclusive("D", ResourceSet.Disk, WhenBusy.Wait);
        Assert.Equal(Grant.Queued, rig.Renew(gpu1));
        Assert.Equal(Grant.Queued, rig.Renew(disk));

        rig.Warden.Release(holder.ReservationId!);

        Assert.Equal(Grant.Full, rig.Warden.PeekGrant(gpu1.ReservationId!));
        Assert.Equal(Grant.Full, rig.Warden.PeekGrant(disk.ReservationId!));
        Assert.Equal(Grant.Queued, rig.Warden.PeekGrant(gpu2.ReservationId!));
    }

    // ---- User mode ------------------------------------------------------

    [Fact]
    public void Steady_mode_throttles_everyone_and_blocks_new_full_holds()
    {
        var rig = new Rig();
        var running = rig.Shared("A");
        rig.Warden.SetMode(UserMode.Steady, reason: "gaming soon");

        Assert.Equal(Grant.Throttled, rig.Renew(running));
        var heavy = rig.Exclusive("B");
        Assert.Equal(Grant.Throttled, heavy.Advice!.Grant);
        Assert.Contains("steady mode: gaming soon", heavy.Advice.Reason);
        Assert.False(rig.Warden.Acquire("legacy", "x").Acquired);
    }

    [Fact]
    public void Hold_mode_queues_new_work_and_throttles_running_work()
    {
        var rig = new Rig();
        var running = rig.Shared("A");
        rig.Warden.SetMode(UserMode.Hold);

        Assert.Equal(Grant.Throttled, rig.Renew(running));
        Assert.Equal(Grant.Queued, rig.Shared("B").Advice!.Grant);
        Assert.Equal(Grant.Queued, rig.Exclusive("C").Advice!.Grant);
    }

    [Fact]
    public void Open_mode_lifts_throttling_between_agents_but_keeps_exclusives_apart()
    {
        var rig = new Rig();
        rig.Warden.SetMode(UserMode.Open);
        rig.Exclusive("A");

        Assert.Equal(Grant.Full, rig.Shared("B").Advice!.Grant);
        Assert.Equal(Grant.Throttled, rig.Exclusive("C").Advice!.Grant);
    }

    [Fact]
    public void No_mode_overrides_a_game()
    {
        var rig = new Rig();
        rig.StartGame();
        rig.Warden.SetMode(UserMode.Open);

        Assert.Equal(Grant.Throttled, rig.Shared().Advice!.Grant);
        Assert.Equal(Grant.Queued, rig.Exclusive("B", whenBusy: WhenBusy.Wait).Advice!.Grant);
    }

    [Fact]
    public void Timed_mode_reverts_to_auto()
    {
        var rig = new Rig();
        rig.Warden.SetMode(UserMode.Steady, durationSeconds: 60);
        Assert.Equal(Grant.Throttled, rig.Shared("A").Advice!.Grant);

        rig.Clock.AdvanceSeconds(61);

        Assert.Equal(UserMode.Auto, rig.Warden.GetMode().Mode);
        Assert.Equal(Grant.Full, rig.Shared("B").Advice!.Grant);
        Assert.True(rig.Log.Contains("ended (time up)"));
    }

    [Fact]
    public void Mode_can_cover_only_some_resources()
    {
        var rig = new Rig();
        rig.Warden.SetMode(UserMode.Hold, ResourceSet.Gpu);

        Assert.Equal(Grant.Full, rig.Shared().Advice!.Grant);
        Assert.Equal(Grant.Queued, rig.Shared("B", ResourceSet.Gpu).Advice!.Grant);
    }

    [Fact]
    public void Mode_appears_in_status_as_a_blocker_and_state()
    {
        var rig = new Rig();
        rig.Warden.SetMode(UserMode.Hold, reason: "recording");

        var s = rig.Warden.GetStatus();

        Assert.Equal(UserMode.Hold, s.Mode!.Mode);
        Assert.Equal(MachineState.Busy, s.State);
        Assert.Contains(s.Blockers, b => b.Id == "mode" && b.Description.Contains("recording"));
    }

    [Fact]
    public void Open_mode_is_not_a_blocker()
    {
        var rig = new Rig();
        rig.Warden.SetMode(UserMode.Open);

        Assert.Equal(MachineState.Available, rig.Warden.GetStatus().State);
    }

    [Fact]
    public void Mode_duration_is_bounded()
    {
        var rig = new Rig();
        Assert.Throws<ArgumentException>(() => rig.Warden.SetMode(UserMode.Hold, durationSeconds: 0));
        Assert.Throws<ArgumentException>(() => rig.Warden.SetMode(UserMode.Hold, durationSeconds: 8 * 24 * 3600));
    }

    [Fact]
    public void Manual_hold_queues_new_claims()
    {
        var rig = new Rig();
        rig.Warden.SetManualBusy("disk maintenance", ResourceSet.Disk);

        var r = rig.Shared();

        Assert.Equal(Grant.Queued, r.Advice!.Grant);
        Assert.Contains("manual hold: disk maintenance", r.Advice.Reason);
    }

    // ---- Edge cases -----------------------------------------------------

    [Fact]
    public void Unknown_state_throttles_shared_work_and_queues_new_exclusives()
    {
        var rig = new Rig();
        rig.Processes.Result = ProviderResult.Unhealthy;

        Assert.Equal(Grant.Throttled, rig.Shared().Advice!.Grant);
        Assert.Equal(Grant.Queued, rig.Exclusive("B").Advice!.Grant);
        Assert.False(rig.Warden.Acquire("legacy", "x").Acquired);
    }

    [Fact]
    public void Invalid_claim_is_rejected()
    {
        var rig = new Rig();

        var r = rig.Warden.Acquire(new ClaimRequest("A", "x", ClaimKind.Shared, EstimateSeconds: -1));

        Assert.False(r.Acquired);
        Assert.Empty(rig.Warden.GetStatus().Claims!);
    }

    [Fact]
    public void Sharing_settings_hot_reload()
    {
        var rig = new Rig();
        rig.StartGame();
        rig.Warden.UpdateSharing(new SharingOptions { ThrottleMaxCores = 2, Unshareable = ResourceSet.None });

        var r = rig.Shared(resources: ResourceSet.Gpu | ResourceSet.Cpu);

        Assert.Equal(Grant.Throttled, r.Advice!.Grant);
        Assert.Equal(2, r.Advice.Throttle!.MaxCores);
    }

    [Fact]
    public void Legacy_hold_renew_without_contention_carries_no_advice()
    {
        var rig = new Rig();
        var r = rig.Warden.Acquire("legacy", "x");

        var renew = rig.Warden.Renew(r.ReservationId!);

        Assert.Null(renew.Advice);
        Assert.Null(renew.Reason);
    }

    [Fact]
    public void PeekGrant_reports_without_renewing()
    {
        var rig = new Rig();
        rig.StartGame();
        var r = rig.Shared();
        var expires = r.ExpiresAt;
        rig.StopGame();

        Assert.Equal(Grant.Full, rig.Warden.PeekGrant(r.ReservationId!));
        Assert.Equal(Grant.Throttled, rig.Warden.LastGrant(r.ReservationId!));
        Assert.Equal(expires, rig.Warden.GetStatus().Claims!.Single().ExpiresAt);
        Assert.Null(rig.Warden.PeekGrant("nope"));
    }

    // ---- Config ---------------------------------------------------------

    [Fact]
    public void Sharing_section_parses_with_defaults_for_missing_keys()
    {
        var config = ConfigLoader.Parse("""
            sharing:
              throttle_max_cores: 2
              throttle_priority: Idle
              max_throttled_per_resource: 5
              unshareable: []
            """);

        var s = config.Sharing;
        Assert.Equal(2, s.ThrottleMaxCores);
        Assert.Equal("idle", s.ThrottlePriority);
        Assert.Equal(5, s.MaxThrottledPerResource);
        Assert.Equal(ResourceSet.None, s.Unshareable);
        Assert.Equal(new SharingOptions().ThrottleMaxRamGb, s.ThrottleMaxRamGb);
        Assert.Equal(new SharingOptions().QueueCheckinSeconds, s.QueueCheckinSeconds);
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

    [Fact]
    public void Missing_sharing_section_uses_defaults()
    {
        var s = ConfigLoader.Parse("leases:\n  default_seconds: 300\n").Sharing;

        Assert.Equal(1, s.ThrottleMaxCores);
        Assert.Equal(ResourceSet.Gpu, s.Unshareable);
        Assert.Equal(120, s.QueueCheckinSeconds);
    }

    [Theory]
    [InlineData("throttle_max_cores: 0")]
    [InlineData("throttle_max_ram_gb: 0")]
    [InlineData("max_throttled_per_resource: -1")]
    [InlineData("queue_checkin_seconds: 5")]
    [InlineData("throttle_priority: realtime")]
    [InlineData("unshareable: [vram]")]
    public void Bad_sharing_values_are_rejected(string line)
        => Assert.ThrowsAny<YamlException>(() => ConfigLoader.Parse("sharing:\n  " + line + "\n"));

    // ---- Review regressions ---------------------------------------------

    [Fact]
    public void Open_mode_keeps_the_game_throttle_cap()
    {
        var rig = new Rig();
        rig.StartGame();
        rig.Warden.SetMode(UserMode.Open);
        rig.Shared("A");
        rig.Shared("B");
        rig.Shared("C");

        Assert.Equal(Grant.Queued, rig.Shared("D").Advice!.Grant);
    }

    [Fact]
    public void Hold_mode_still_holds_when_the_state_is_unknown()
    {
        var rig = new Rig();
        rig.Warden.SetMode(UserMode.Hold);
        rig.Processes.Result = ProviderResult.Unhealthy;

        Assert.Equal(Grant.Queued, rig.Shared().Advice!.Grant);
    }

    [Fact]
    public void Unknown_state_still_caps_throttled_starts()
    {
        var rig = new Rig();
        rig.Processes.Result = ProviderResult.Unhealthy;
        rig.Shared("A");
        rig.Shared("B");
        rig.Shared("C");

        Assert.Equal(Grant.Queued, rig.Shared("D").Advice!.Grant);
    }

    [Fact]
    public void Wait_claim_promoted_before_checking_in_is_never_told_to_run_small()
    {
        var rig = new Rig();
        var h = rig.Exclusive("H", ResourceSet.Cpu);
        var w = rig.Exclusive("W", ResourceSet.Cpu, WhenBusy.Wait);
        Assert.Equal(Grant.Queued, w.Advice!.Grant);
        rig.Warden.Release(h.ReservationId!);
        rig.Warden.GetStatus(); // promotes W to the hold

        rig.StartGame();

        Assert.Equal(Grant.Queued, rig.Renew(w));
        rig.StopGame();
        Assert.Equal(Grant.Full, rig.Renew(w));
    }

    [Fact]
    public void Queue_estimate_is_unknown_when_a_claim_ahead_waits_on_a_game()
    {
        var rig = new Rig();
        rig.StartGame(resources: ResourceSet.Gpu);
        rig.Exclusive("A", ResourceSet.Cpu | ResourceSet.Gpu, WhenBusy.Wait, estimate: 60);

        var b = rig.Exclusive("B", ResourceSet.Cpu, WhenBusy.Wait);

        Assert.Equal(Grant.Queued, b.Advice!.Grant);
        Assert.Null(b.Advice.Queue!.EstWaitSeconds);
    }

    [Fact]
    public void Throttle_cap_is_counted_per_resource()
    {
        var rig = new Rig();
        rig.StartGame(resources: ResourceSet.Cpu | ResourceSet.Ram | ResourceSet.Disk);
        rig.Shared("A", ResourceSet.Cpu);
        rig.Shared("B", ResourceSet.Ram);
        rig.Shared("C", ResourceSet.Disk);

        Assert.Equal(Grant.Throttled, rig.Shared("D").Advice!.Grant); // 2 of 3 slots on each
    }

    [Fact]
    public void Freed_throttle_slot_goes_to_the_earlier_waiter_not_a_newcomer()
    {
        var rig = new Rig();
        rig.StartGame();
        var a = rig.Shared("A");
        rig.Shared("B");
        rig.Shared("C");
        var d = rig.Shared("D");
        Assert.Equal(Grant.Queued, d.Advice!.Grant);
        rig.Warden.Release(a.ReservationId!);

        var e = rig.Shared("E");

        Assert.Equal(Grant.Queued, e.Advice!.Grant);
        Assert.Equal("D", Assert.Single(e.Advice.Queue!.Ahead).Owner);
        Assert.Equal(2, e.Advice.Queue.Position);
        Assert.Equal(Grant.Throttled, rig.Renew(d));
    }

    [Fact]
    public void Long_poll_keeps_the_claim_alive_while_it_waits()
    {
        var rig = new Rig();
        rig.StartGame();
        rig.Shared("A");
        rig.Shared("B");
        rig.Shared("C");
        var d = rig.Shared("D"); // queued: 120s check-in window
        rig.Clock.AdvanceSeconds(100);

        Assert.Equal(Grant.Queued, rig.Warden.BeginWait(d.ReservationId!, 60));
        rig.Clock.AdvanceSeconds(60);

        Assert.True(rig.Warden.Renew(d.ReservationId!).Renewed);
    }

    [Fact]
    public void Unstarted_holder_counts_its_whole_job_in_queue_estimates()
    {
        var rig = new Rig();
        var h = rig.Exclusive("H", ResourceSet.Cpu, estimate: 10);
        var w = rig.Exclusive("W", ResourceSet.Cpu, WhenBusy.Wait, estimate: 300);
        rig.Warden.Release(h.ReservationId!);
        rig.Warden.GetStatus(); // W holds but hasn't checked in

        var x = rig.Exclusive("X", ResourceSet.Cpu, WhenBusy.Wait);

        Assert.True(x.Advice!.Queue!.EstWaitSeconds >= 300);
    }

    [Fact]
    public void Legacy_acquire_is_refused_while_a_blocked_waiter_is_in_line()
    {
        var rig = new Rig();
        rig.Exclusive("A", ResourceSet.Cpu);
        rig.Exclusive("B", ResourceSet.Cpu | ResourceSet.Disk, WhenBusy.Wait); // waits on A's cpu; disk is free

        var legacy = rig.Warden.Acquire("legacy", "copy", resources: ResourceSet.Disk);

        Assert.False(legacy.Acquired);
    }

    [Fact]
    public void Free_waiter_is_not_promoted_past_an_earlier_overlapping_waiter()
    {
        var rig = new Rig();
        rig.Exclusive("A", ResourceSet.Cpu);
        rig.Exclusive("B", ResourceSet.Cpu | ResourceSet.Disk, WhenBusy.Wait);

        var c = rig.Exclusive("C", ResourceSet.Disk, WhenBusy.Wait);

        Assert.Equal(Grant.Queued, c.Advice!.Grant);
        Assert.Equal("B", Assert.Single(c.Advice.Queue!.Ahead).Owner);
    }

    [Fact]
    public void Steady_throttles_an_exclusive_holder_on_renew()
    {
        var rig = new Rig();
        var h = rig.Exclusive("H");
        Assert.Equal(Grant.Full, h.Advice!.Grant);

        rig.Warden.SetMode(UserMode.Steady);

        Assert.Equal(Grant.Throttled, rig.Renew(h));
    }

    [Fact]
    public void Nobody_is_promoted_while_the_state_is_unknown()
    {
        var rig = new Rig();
        var a = rig.Exclusive("A", ResourceSet.Cpu);
        var b = rig.Exclusive("B", ResourceSet.Cpu, WhenBusy.Wait);
        rig.Processes.Result = ProviderResult.Unhealthy;
        rig.Warden.Release(a.ReservationId!);

        Assert.Equal(Grant.Queued, rig.Renew(b));
        rig.Processes.Result = ProviderResult.Empty;
        Assert.Equal(Grant.Full, rig.Renew(b));
    }

    [Fact]
    public void Mode_scoped_to_a_resource_leaves_the_others_alone()
    {
        var rig = new Rig();
        rig.Warden.SetMode(UserMode.Steady, resources: ResourceSet.Cpu);

        Assert.Equal(Grant.Full, rig.Shared("A", ResourceSet.Disk).Advice!.Grant);
        Assert.Equal(Grant.Throttled, rig.Shared("B", ResourceSet.Cpu).Advice!.Grant);
    }

    [Fact]
    public void Steady_runs_gpu_work_small_instead_of_queuing_it()
    {
        var rig = new Rig();
        rig.Warden.SetMode(UserMode.Steady);

        Assert.Equal(Grant.Throttled, rig.Shared("A", ResourceSet.Gpu).Advice!.Grant);
        Assert.Equal(Grant.Throttled, rig.Exclusive("B", ResourceSet.All).Advice!.Grant);
    }

    [Fact]
    public void Steady_still_queues_gpu_work_behind_a_game()
    {
        var rig = new Rig();
        rig.StartGame();
        rig.Warden.SetMode(UserMode.Steady);

        Assert.Equal(Grant.Queued, rig.Shared("A", ResourceSet.Gpu).Advice!.Grant);
    }

    // ---- Boundaries and gaps --------------------------------------------

    [Fact]
    public void Waiter_is_not_marked_holding_while_the_state_is_unknown()
    {
        var rig = new Rig();
        var a = rig.Exclusive("A", ResourceSet.Cpu);
        rig.Exclusive("B", ResourceSet.Cpu, WhenBusy.Wait);
        rig.Processes.Result = ProviderResult.Unhealthy;
        rig.Warden.Release(a.ReservationId!);

        var b = Assert.Single(rig.Warden.GetStatus().Claims!);
        Assert.False(b.Holding);
        Assert.False(rig.Log.Contains("Claim promoted to hold: B"));
    }

    [Fact]
    public void Sweep_promotes_a_waiter_once_its_resources_free()
    {
        var rig = new Rig();
        rig.StartGame(resources: ResourceSet.Cpu);
        Assert.Equal(Grant.Queued, rig.Exclusive("B", ResourceSet.Cpu, WhenBusy.Wait).Advice!.Grant);
        rig.StopGame();
        Assert.False(rig.Log.Contains("Claim promoted to hold: B"));

        rig.Warden.Sweep(); // no client traffic in between

        Assert.True(rig.Log.Contains("Claim promoted to hold: B"));
    }

    [Fact]
    public void Mode_ends_exactly_at_its_expiry()
    {
        var rig = new Rig();
        rig.Warden.SetMode(UserMode.Steady, durationSeconds: 60);

        rig.Clock.AdvanceSeconds(60);

        Assert.Equal(UserMode.Auto, rig.Warden.GetMode().Mode);
    }

    [Fact]
    public void Open_mode_scoped_to_gpu_still_throttles_cpu_beside_a_holder()
    {
        var rig = new Rig();
        rig.Warden.SetMode(UserMode.Open, resources: ResourceSet.Gpu);
        rig.Exclusive("A", ResourceSet.Cpu);

        Assert.Equal(Grant.Throttled, rig.Shared("B", ResourceSet.Cpu).Advice!.Grant);
    }

    // DESIGN §16: open mode has no throttle cap between agents (a game's cap still applies).
    [Fact]
    public void Open_mode_lifts_the_throttle_cap_between_agents()
    {
        var rig = new Rig();
        rig.Warden.SetMode(UserMode.Open);
        rig.Exclusive("A", ResourceSet.Cpu);

        for (var i = 0; i < 5; i++)
            Assert.Equal(Grant.Throttled, rig.Exclusive("E" + i, ResourceSet.Cpu).Advice!.Grant);
    }

    [Fact]
    public void Throttle_advice_has_no_ram_limit_when_ram_is_not_contended()
    {
        var rig = new Rig();
        rig.StartGame(resources: ResourceSet.Cpu);

        var a = rig.Shared("A", ResourceSet.Cpu | ResourceSet.Ram).Advice!;

        Assert.Equal(Grant.Throttled, a.Grant);
        Assert.Equal(ResourceSet.Cpu, a.Contended);
        Assert.Null(a.Throttle!.MaxRamGb);
    }

    [Fact]
    public void Legacy_refusal_for_the_line_counts_as_a_failed_acquisition()
    {
        var rig = new Rig();
        rig.Exclusive("A", ResourceSet.Cpu);
        rig.Exclusive("B", ResourceSet.Cpu | ResourceSet.Disk, WhenBusy.Wait);
        var before = rig.Warden.GetStats().FailedAcquisitions;

        Assert.False(rig.Warden.Acquire("legacy", "copy", resources: ResourceSet.Disk).Acquired);

        Assert.Equal(before + 1, rig.Warden.GetStats().FailedAcquisitions);
    }
}
