using ComputeWarden.Client;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Diagnostics;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;
using ComputeWarden.Daemon;

namespace ComputeWarden.Tests;

public class WardenClientTests
{
    private sealed class StubLister : IProcessLister
    {
        private volatile RunningProcess[] _processes = Array.Empty<RunningProcess>();
        public void Set(params RunningProcess[] procs) => _processes = procs;
        public IReadOnlyList<RunningProcess> List() => _processes;
    }

    private static WardenHost NewHost(out string pipeName, WardenConfig? config = null, IProcessLister? lister = null)
    {
        pipeName = "ComputeWarden-test-" + Guid.NewGuid().ToString("N");
        return new WardenHost(
            config ?? WardenConfig.CreateDefault(),
            NullLog.Instance,
            lister ?? new StubLister(),
            pipeName: pipeName);
    }

    [Fact]
    public async Task Client_full_lifecycle_against_live_daemon()
    {
        await using var host = NewHost(out var pipeName);
        host.Start();
        var client = new WardenClient(pipeName);

        Assert.True(await client.IsDaemonRunningAsync());

        var status = await client.StatusAsync();
        Assert.Equal(MachineState.Available, status.State);
        Assert.True(await client.CanRunAsync());

        var acquire = await client.AcquireAsync("Claude Code", "indexing", leaseSeconds: 300);
        Assert.True(acquire.Acquired);
        Assert.NotNull(acquire.ReservationId);

        var blocked = await client.AcquireAsync("Other Agent", "work");
        Assert.False(blocked.Acquired);
        Assert.Contains(blocked.Blockers, b => b.Type == BlockerType.Reservation);

        var renew = await client.RenewAsync(acquire.ReservationId!, 120);
        Assert.True(renew.Renewed);

        var release = await client.ReleaseAsync(acquire.ReservationId!);
        Assert.True(release.Released);

        Assert.True((await client.AcquireAsync("Other Agent", "work")).Acquired);
    }

    [Fact]
    public async Task Client_manual_blocker_and_stats()
    {
        await using var host = NewHost(out var pipeName);
        host.Start();
        var client = new WardenClient(pipeName);

        var blocker = await client.SetManualBusyAsync("disk maintenance");
        Assert.Equal(BlockerType.Manual, blocker.Type);
        Assert.Equal(MachineState.Busy, (await client.StatusAsync()).State);

        Assert.True(await client.ClearManualBusyAsync());
        Assert.Equal(MachineState.Available, (await client.StatusAsync()).State);

        await client.AcquireAsync("A", "work");
        var stats = await client.StatsAsync();
        Assert.Equal(1, stats.TotalAcquisitions);
        Assert.Equal(1, stats.CurrentReservations);
    }

    [Fact]
    public async Task Manual_blocker_disabled_surfaces_as_exception()
    {
        var config = WardenConfig.CreateDefault();
        var disabled = new WardenConfig
        {
            ProcessRules = config.ProcessRules,
            AllowManualBlocker = false,
        };
        await using var host = NewHost(out var pipeName, disabled);
        host.Start();
        var client = new WardenClient(pipeName);

        var ex = await Assert.ThrowsAsync<WardenClientException>(() => client.SetManualBusyAsync("x"));
        Assert.Contains("disabled", ex.Message);
    }

    [Fact]
    public async Task IsDaemonRunning_false_when_no_daemon()
    {
        var client = new WardenClient("ComputeWarden-nonexistent-" + Guid.NewGuid().ToString("N"), connectTimeoutMs: 300);
        Assert.False(await client.IsDaemonRunningAsync());
    }
}
