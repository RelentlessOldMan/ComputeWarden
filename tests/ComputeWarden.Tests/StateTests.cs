using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;

namespace ComputeWarden.Tests;

public class StateTests
{
    [Fact]
    public void No_blockers_is_available()
    {
        var warden = TestFactory.NewWarden(new FakeClock());

        var status = warden.GetStatus();

        Assert.Equal(MachineState.Available, status.State);
        Assert.True(status.CanRunIntensive);
        Assert.Empty(status.Blockers);
    }

    [Fact]
    public void Process_blocker_makes_machine_busy()
    {
        var process = new FakeBlockerProvider
        {
            Result = ProviderResult.Ok(FakeBlockerProvider.Process("MSBuild.exe", 14280)),
        };
        var warden = TestFactory.NewWarden(new FakeClock(), extraProviders: process);

        var status = warden.GetStatus();

        Assert.Equal(MachineState.Busy, status.State);
        Assert.False(status.CanRunIntensive);
        Assert.Contains(status.Blockers, b => b.Type == BlockerType.Process);
    }

    [Fact]
    public void Process_blocker_disappears_when_process_exits()
    {
        var process = new FakeBlockerProvider
        {
            Result = ProviderResult.Ok(FakeBlockerProvider.Process("MSBuild.exe", 14280)),
        };
        var warden = TestFactory.NewWarden(new FakeClock(), extraProviders: process);
        Assert.Equal(MachineState.Busy, warden.GetStatus().State);

        process.Result = ProviderResult.Empty; // process exited

        Assert.Equal(MachineState.Available, warden.GetStatus().State);
    }

    [Fact]
    public void Acquire_refused_while_process_blocker_present()
    {
        var process = new FakeBlockerProvider
        {
            Result = ProviderResult.Ok(FakeBlockerProvider.Process("MSBuild.exe", 14280)),
        };
        var warden = TestFactory.NewWarden(new FakeClock(), extraProviders: process);

        var result = warden.Acquire("Agent A", "work");

        Assert.False(result.Acquired);
        Assert.Contains(result.Blockers, b => b.Type == BlockerType.Process);
    }

    [Fact]
    public void Monitor_failure_yields_unknown_and_refuses_acquire()
    {
        var process = new FakeBlockerProvider { Result = ProviderResult.Unhealthy };
        var warden = TestFactory.NewWarden(new FakeClock(), extraProviders: process);

        var status = warden.GetStatus();
        Assert.Equal(MachineState.Unknown, status.State);
        Assert.False(status.CanRunIntensive);

        var acquire = warden.Acquire("Agent A", "work");
        Assert.False(acquire.Acquired);
        Assert.Contains("UNKNOWN", acquire.Reason);
    }

    [Fact]
    public void Manual_blocker_makes_machine_busy_and_clears()
    {
        var warden = TestFactory.NewWarden(new FakeClock());

        warden.SetManualBusy("Running disk maintenance");
        var busy = warden.GetStatus();
        Assert.Equal(MachineState.Busy, busy.State);
        Assert.Contains(busy.Blockers, b => b.Type == BlockerType.Manual);

        warden.ClearManualBusy();
        Assert.Equal(MachineState.Available, warden.GetStatus().State);
    }

    [Fact]
    public void Process_starting_during_reservation_reports_both_blockers()
    {
        var process = new FakeBlockerProvider();
        var warden = TestFactory.NewWarden(new FakeClock(), extraProviders: process);
        warden.Acquire("Agent A", "work"); // reservation exists

        process.Result = ProviderResult.Ok(FakeBlockerProvider.Process("MSBuild.exe", 14280));

        var status = warden.GetStatus();
        Assert.Equal(MachineState.Busy, status.State);
        Assert.Contains(status.Blockers, b => b.Type == BlockerType.Reservation);
        Assert.Contains(status.Blockers, b => b.Type == BlockerType.Process);
    }
}
