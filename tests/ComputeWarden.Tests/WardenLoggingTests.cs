using ComputeWarden.Core;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Providers;

namespace ComputeWarden.Tests;

public class WardenLoggingTests
{
    private static (Warden warden, FakeClock clock, CapturingLog log) New(int leaseDefault = 100)
    {
        var clock = new FakeClock();
        var log = new CapturingLog();
        var warden = new Warden(
            clock,
            new LeaseOptions { DefaultSeconds = leaseDefault, MinimumSeconds = 1, MaximumSeconds = 3600 },
            new WardenLimits(),
            new ManualBlockerProvider(clock),
            extraProviders: null,
            log: log);
        return (warden, clock, log);
    }

    [Fact]
    public void Acquire_and_release_are_logged()
    {
        var (warden, _, log) = New();

        var a = warden.Acquire("Claude Code", "indexing", 100);
        Assert.True(log.Contains("Reservation acquired"));
        Assert.True(log.Contains("Claude Code"));

        warden.Release(a.ReservationId!);
        Assert.True(log.Contains("Reservation released"));
        Assert.True(log.Contains("held"));
    }

    [Fact]
    public void Expiry_is_logged_distinctly_from_release()
    {
        var (warden, clock, log) = New(leaseDefault: 100);
        warden.Acquire("Agent A", "work", 100);

        clock.AdvanceSeconds(101);
        warden.GetStatus(); // triggers prune of the lapsed lease

        Assert.True(log.Contains("Reservation expired (owner did not release)"));
        Assert.False(log.Contains("Reservation released")); // it expired, it wasn't released
    }
}
