using System.Text.Json;
using System.Text.Json.Nodes;
using ComputeWarden.Core;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Ipc;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;
using ComputeWarden.Daemon;
using YamlDotNet.Core;

namespace ComputeWarden.Tests;

/// <summary>Per-resource exclusivity: holds conflict only where their resource sets overlap.</summary>
public class ResourceTests
{
    private const ResourceSet CpuNet = ResourceSet.Cpu | ResourceSet.Network;
    private const ResourceSet GpuRam = ResourceSet.Gpu | ResourceSet.Ram;

    private sealed class FixedLister : IProcessLister
    {
        public RunningProcess[] Processes { get; set; } = Array.Empty<RunningProcess>();
        public IReadOnlyList<RunningProcess> List() => Processes;
    }

    // ---- Parsing ---------------------------------------------------------

    [Fact]
    public void Parse_null_or_empty_means_all()
    {
        Assert.Equal(ResourceSet.All, Resources.Parse(null));
        Assert.Equal(ResourceSet.All, Resources.Parse(Array.Empty<string>()));
    }

    [Fact]
    public void Parse_is_case_insensitive_and_accepts_aliases()
    {
        Assert.Equal(CpuNet, Resources.Parse(new[] { "CPU", " network " }));
        Assert.Equal(ResourceSet.Ram, Resources.Parse(new[] { "memory" }));
        Assert.Equal(ResourceSet.All, Resources.Parse(new[] { "all" }));
    }

    [Fact]
    public void Parse_rejects_unknown_names()
    {
        var ex = Assert.Throws<ArgumentException>(() => Resources.Parse(new[] { "cpu", "vram" }));
        Assert.Contains("vram", ex.Message);
    }

    [Fact]
    public void ToNames_round_trips()
        => Assert.Equal(new[] { "cpu", "network" }, Resources.ToNames(CpuNet));

    // ---- Warden semantics ------------------------------------------------

    [Fact]
    public void Disjoint_reservations_run_in_parallel()
    {
        var warden = TestFactory.NewWarden(new FakeClock());

        var a = warden.Acquire("A", "download + compile", resources: CpuNet);
        var b = warden.Acquire("B", "train model", resources: GpuRam);

        Assert.True(a.Acquired);
        Assert.True(b.Acquired);
        Assert.Equal(CpuNet, a.Resources);
    }

    [Fact]
    public void Overlapping_reservation_is_refused_with_only_the_conflicting_blocker()
    {
        var warden = TestFactory.NewWarden(new FakeClock());
        warden.Acquire("A", "compile", resources: CpuNet);
        warden.Acquire("B", "train", resources: ResourceSet.Gpu);

        var c = warden.Acquire("C", "encode", resources: ResourceSet.Cpu | ResourceSet.Disk);

        Assert.False(c.Acquired);
        Assert.Equal("Requested resources in use: cpu", c.Reason);
        var blocker = Assert.Single(c.Blockers);
        Assert.Equal("A", blocker.Owner);
    }

    [Fact]
    public void Whole_machine_request_conflicts_with_any_partial_hold_and_vice_versa()
    {
        var warden = TestFactory.NewWarden(new FakeClock());
        var gpu = warden.Acquire("A", "train", resources: ResourceSet.Gpu);

        Assert.False(warden.Acquire("B", "everything").Acquired);

        warden.Release(gpu.ReservationId!);
        Assert.True(warden.Acquire("B", "everything").Acquired);
        Assert.False(warden.Acquire("C", "net only", resources: ResourceSet.Network).Acquired);
    }

    [Fact]
    public void A_later_acquire_is_a_separate_reservation_and_can_conflict_with_the_callers_own_hold()
    {
        // Documents why agents must request every resource in one call: there is no
        // "upgrade", and ownership is not identity.
        var warden = TestFactory.NewWarden(new FakeClock());
        var ram = warden.Acquire("A", "analysis", resources: ResourceSet.Ram);

        var disk = warden.Acquire("A", "analysis", resources: ResourceSet.Disk);
        var both = warden.Acquire("A", "analysis", resources: ResourceSet.Ram | ResourceSet.Disk);

        Assert.True(disk.Acquired);
        Assert.NotEqual(ram.ReservationId, disk.ReservationId);
        Assert.False(both.Acquired);
        Assert.Equal(2, both.Blockers.Count);
    }

    [Fact]
    public void Status_reports_busy_resources_and_can_run_is_per_resource()
    {
        var warden = TestFactory.NewWarden(new FakeClock());
        warden.Acquire("A", "train", resources: GpuRam);

        var status = warden.GetStatus();

        Assert.Equal(MachineState.Busy, status.State);
        Assert.Equal(GpuRam, status.BusyResources);
        Assert.True(warden.CanRun(CpuNet));
        Assert.False(warden.CanRun(ResourceSet.Ram));
        Assert.False(warden.CanRun());
    }

    [Fact]
    public void Unknown_state_refuses_even_disjoint_requests()
    {
        var fake = new FakeBlockerProvider { Result = ProviderResult.Unhealthy };
        var warden = TestFactory.NewWarden(new FakeClock(), null, fake);

        var r = warden.Acquire("A", "work", resources: ResourceSet.Network);

        Assert.False(r.Acquired);
        Assert.Equal(MachineState.Unknown, r.MachineState);
    }

    [Fact]
    public void Manual_blocker_can_hold_a_subset()
    {
        var warden = TestFactory.NewWarden(new FakeClock());
        warden.SetManualBusy("disk maintenance", ResourceSet.Disk);

        Assert.False(warden.Acquire("A", "index", resources: ResourceSet.Disk | ResourceSet.Cpu).Acquired);
        Assert.True(warden.Acquire("B", "train", resources: ResourceSet.Gpu).Acquired);
    }

    [Fact]
    public void Process_rule_blocks_only_its_resources()
    {
        var clock = new FakeClock();
        var lister = new FixedLister { Processes = new[] { new RunningProcess(1, "CodeCarver") } };
        var provider = new ProcessBlockerProvider(
            lister,
            new[] { new ProcessRule { Name = "CodeCarver", Executable = "CodeCarver.exe", Resources = ResourceSet.Ram } },
            new ProcessDetectionOptions(),
            clock);
        provider.Refresh();
        var warden = TestFactory.NewWarden(clock, null, provider);

        Assert.False(warden.Acquire("A", "big analysis", resources: ResourceSet.Ram).Acquired);
        Assert.True(warden.Acquire("B", "compile", resources: ResourceSet.Cpu).Acquired);
    }

    [Fact]
    public void Hot_reloaded_rule_resources_apply_to_an_already_running_process()
    {
        var clock = new FakeClock();
        var lister = new FixedLister { Processes = new[] { new RunningProcess(1, "Game") } };
        var provider = new ProcessBlockerProvider(
            lister,
            new[] { new ProcessRule { Name = "Game", Executable = "Game.exe" } },
            new ProcessDetectionOptions(),
            clock);
        provider.Refresh();
        Assert.Equal(ResourceSet.All, Assert.Single(provider.GetBlockers().Blockers).Resources);

        provider.UpdateRules(
            new[] { new ProcessRule { Name = "Game", Executable = "Game.exe", Resources = ResourceSet.Gpu } },
            new ProcessDetectionOptions());
        provider.Refresh();

        Assert.Equal(ResourceSet.Gpu, Assert.Single(provider.GetBlockers().Blockers).Resources);
    }

    // ---- Config ----------------------------------------------------------

    [Fact]
    public void Config_parses_rule_resources_and_defaults_to_all()
    {
        var config = ConfigLoader.Parse("""
            process_rules:
              - name: CodeCarver
                executable: CodeCarver.exe
                resources: [ram, disk]
              - name: Game
                executable: Game.exe
            """);

        Assert.Equal(ResourceSet.Ram | ResourceSet.Disk, config.ProcessRules[0].Resources);
        Assert.Equal(ResourceSet.All, config.ProcessRules[1].Resources);
    }

    [Fact]
    public void Config_rejects_unknown_resource_name()
    {
        var ex = Assert.Throws<YamlException>(() => ConfigLoader.Parse("""
            process_rules:
              - name: Game
                executable: Game.exe
                resources: [gpu, vram]
            """));
        Assert.Contains("vram", ex.Message);
    }

    // ---- IPC -------------------------------------------------------------

    private static JsonNode Handle(RequestDispatcher d, object request)
        => JsonNode.Parse(d.Handle(JsonSerializer.Serialize(request)))!;

    [Fact]
    public void Ipc_acquire_with_resources_and_blockers_carry_them()
    {
        var d = new RequestDispatcher(TestFactory.NewWarden(new FakeClock()));

        var a = Handle(d, new { v = 1, op = "ACQUIRE", owner = "A", description = "w", resources = new[] { "gpu", "ram" } });
        var b = Handle(d, new { v = 1, op = "ACQUIRE", owner = "B", description = "w", resources = new[] { "cpu" } });
        var c = Handle(d, new { v = 1, op = "ACQUIRE", owner = "C", description = "w", resources = new[] { "ram" } });
        var status = Handle(d, new { v = 1, op = "STATUS" });

        Assert.True((bool)a["acquired"]!);
        Assert.Equal("[\"gpu\",\"ram\"]", a["resources"]!.ToJsonString());
        Assert.True((bool)b["acquired"]!);
        Assert.False((bool)c["acquired"]!);
        Assert.Equal("[\"gpu\",\"ram\"]", c["blockers"]![0]!["resources"]!.ToJsonString());
        Assert.Equal("[\"cpu\",\"gpu\",\"ram\"]", status["busyResources"]!.ToJsonString());
    }

    [Fact]
    public void Ipc_unknown_resource_is_a_clean_error()
    {
        var d = new RequestDispatcher(TestFactory.NewWarden(new FakeClock()));

        var res = Handle(d, new { v = 1, op = "ACQUIRE", owner = "A", description = "w", resources = new[] { "vram" } });

        Assert.False((bool)res["ok"]!);
        Assert.Contains("Unknown resource 'vram'", (string)res["error"]!);
    }

    [Fact]
    public void Ipc_request_without_resources_keeps_whole_machine_semantics()
    {
        // An adapter predating resources sends none: it must still hold everything.
        var d = new RequestDispatcher(TestFactory.NewWarden(new FakeClock()));

        Handle(d, new { v = 1, op = "ACQUIRE", owner = "Old", description = "w" });
        var res = Handle(d, new { v = 1, op = "ACQUIRE", owner = "New", description = "w", resources = new[] { "network" } });

        Assert.False((bool)res["acquired"]!);
    }
}
