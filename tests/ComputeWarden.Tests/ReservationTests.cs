using ComputeWarden.Core;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Model;

namespace ComputeWarden.Tests;

public class ReservationTests
{
    [Fact]
    public void Acquire_when_available_succeeds()
    {
        var warden = TestFactory.NewWarden(new FakeClock());

        var result = warden.Acquire("Claude Code", "Indexing repository", 300);

        Assert.True(result.Acquired);
        Assert.NotNull(result.ReservationId);
        Assert.Equal(MachineState.Busy, result.MachineState);
    }

    [Fact]
    public void Acquire_when_reservation_exists_fails_and_reports_blocker()
    {
        var warden = TestFactory.NewWarden(new FakeClock());
        var first = warden.Acquire("Agent A", "work");

        var second = warden.Acquire("Agent B", "work");

        Assert.False(second.Acquired);
        Assert.Equal("Machine currently unavailable", second.Reason);
        var blocker = Assert.Single(second.Blockers);
        Assert.Equal(BlockerType.Reservation, blocker.Type);
        Assert.Equal("Agent A", blocker.Owner);
    }

    [Fact]
    public void Release_frees_the_machine()
    {
        var warden = TestFactory.NewWarden(new FakeClock());
        var first = warden.Acquire("Agent A", "work");

        var release = warden.Release(first.ReservationId!);
        Assert.True(release.Released);

        var second = warden.Acquire("Agent B", "work");
        Assert.True(second.Acquired);
    }

    [Fact]
    public void Renew_extends_expiry()
    {
        var clock = new FakeClock();
        var warden = TestFactory.NewWarden(clock, new LeaseOptions { DefaultSeconds = 100 });
        var acquired = warden.Acquire("Agent A", "work", 100);
        var originalExpiry = acquired.ExpiresAt!.Value;

        clock.AdvanceSeconds(50);
        var renew = warden.Renew(acquired.ReservationId!, 100);

        Assert.True(renew.Renewed);
        Assert.True(renew.ExpiresAt > originalExpiry);
    }

    [Fact]
    public void Reservation_expires_after_lease()
    {
        var clock = new FakeClock();
        var warden = TestFactory.NewWarden(clock, new LeaseOptions { DefaultSeconds = 100, MinimumSeconds = 1 });
        warden.Acquire("Agent A", "work", 100);

        clock.AdvanceSeconds(101);

        // Machine is available again once the lease has lapsed.
        Assert.True(warden.CanRun());
        var second = warden.Acquire("Agent B", "work", 100);
        Assert.True(second.Acquired);
    }

    [Fact]
    public void Renew_unknown_id_fails_cleanly()
    {
        var warden = TestFactory.NewWarden(new FakeClock());

        var renew = warden.Renew("does-not-exist");

        Assert.False(renew.Renewed);
        Assert.Equal("Reservation no longer exists", renew.Reason);
    }

    [Fact]
    public void Renew_expired_id_fails_cleanly()
    {
        var clock = new FakeClock();
        var warden = TestFactory.NewWarden(clock, new LeaseOptions { DefaultSeconds = 100, MinimumSeconds = 1 });
        var acquired = warden.Acquire("Agent A", "work", 100);

        clock.AdvanceSeconds(101);
        var renew = warden.Renew(acquired.ReservationId!);

        Assert.False(renew.Renewed);
    }

    [Fact]
    public void Double_release_is_idempotent()
    {
        var warden = TestFactory.NewWarden(new FakeClock());
        var acquired = warden.Acquire("Agent A", "work");

        var first = warden.Release(acquired.ReservationId!);
        var second = warden.Release(acquired.ReservationId!);

        Assert.True(first.Released);
        Assert.False(second.Released);
        Assert.Equal("Reservation no longer exists", second.Reason);
    }

    [Fact]
    public void Release_unknown_id_is_not_an_error()
    {
        var warden = TestFactory.NewWarden(new FakeClock());

        var release = warden.Release("never-existed");

        Assert.False(release.Released);
    }

    [Fact]
    public void Acquire_requires_owner()
    {
        var warden = TestFactory.NewWarden(new FakeClock());

        var result = warden.Acquire("   ", "work");

        Assert.False(result.Acquired);
        Assert.Equal("owner is required", result.Reason);
    }

    [Fact]
    public void Acquire_rejects_oversized_metadata()
    {
        var warden = TestFactory.NewWarden(new FakeClock());
        var metadata = new Dictionary<string, string> { ["big"] = new string('x', 5000) };

        var result = warden.Acquire("Agent A", "work", metadata: metadata);

        Assert.False(result.Acquired);
        Assert.Contains("metadata value exceeds", result.Reason);
    }

    [Fact]
    public void Lease_seconds_are_clamped_to_bounds()
    {
        var clock = new FakeClock();
        var warden = TestFactory.NewWarden(clock, new LeaseOptions { MinimumSeconds = 30, MaximumSeconds = 3600 });

        var acquired = warden.Acquire("Agent A", "work", 999999);

        // Clamped to maximum: expiry is start + 3600s.
        Assert.Equal(clock.UtcNow.AddSeconds(3600), acquired.ExpiresAt);
    }
}
