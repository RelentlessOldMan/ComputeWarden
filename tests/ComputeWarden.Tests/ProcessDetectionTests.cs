using ComputeWarden.Core.Config;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;

namespace ComputeWarden.Tests;

public class ProcessDetectionTests
{
    private sealed class FakeLister : IProcessLister
    {
        public List<RunningProcess> Processes { get; } = new();
        public bool Throw { get; set; }

        public IReadOnlyList<RunningProcess> List()
        {
            if (Throw) throw new InvalidOperationException("enumeration failed");
            return Processes.ToList();
        }

        public void Set(params (string name, int pid)[] procs)
        {
            Processes.Clear();
            foreach (var (name, pid) in procs)
                Processes.Add(new RunningProcess(pid, name));
        }
    }

    private static ProcessBlockerProvider Provider(
        FakeLister lister, FakeClock clock, ProcessDetectionOptions? options = null)
        => new(
            lister,
            new[]
            {
                new ProcessRule { Name = "MSBuild", Executable = "MSBuild.exe" },
                new ProcessRule { Name = "CodeCompass", Executable = "CodeCompass.exe" },
                new ProcessRule { Name = "Disabled", Executable = "Nope.exe", Enabled = false },
            },
            options ?? new ProcessDetectionOptions { AvailableAfterMs = 3000 },
            clock);

    [Fact]
    public void Configured_process_becomes_a_blocker()
    {
        var lister = new FakeLister();
        var clock = new FakeClock();
        var provider = Provider(lister, clock);

        lister.Set(("MSBuild", 14280)); // ProcessName has no .exe on Windows
        provider.Refresh();

        var result = provider.GetBlockers();
        Assert.True(result.Confident);
        var blocker = Assert.Single(result.Blockers);
        Assert.Equal(BlockerType.Process, blocker.Type);
        Assert.Equal("14280", blocker.Metadata!["pids"]);
    }

    [Fact]
    public void Unconfigured_process_is_ignored()
    {
        var lister = new FakeLister();
        var clock = new FakeClock();
        var provider = Provider(lister, clock);

        lister.Set(("chrome", 1000), ("notepad", 1001));
        provider.Refresh();

        Assert.Empty(provider.GetBlockers().Blockers);
    }

    [Fact]
    public void Disabled_rule_does_not_match()
    {
        var lister = new FakeLister();
        var clock = new FakeClock();
        var provider = Provider(lister, clock);

        lister.Set(("Nope", 2000));
        provider.Refresh();

        Assert.Empty(provider.GetBlockers().Blockers);
    }

    [Fact]
    public void Multiple_configured_processes_each_block()
    {
        var lister = new FakeLister();
        var clock = new FakeClock();
        var provider = Provider(lister, clock);

        lister.Set(("MSBuild", 1), ("CodeCompass", 2));
        provider.Refresh();

        Assert.Equal(2, provider.GetBlockers().Blockers.Count);
    }

    [Fact]
    public void Same_executable_multiple_pids_is_one_blocker_with_all_pids()
    {
        var lister = new FakeLister();
        var clock = new FakeClock();
        var provider = Provider(lister, clock);

        lister.Set(("MSBuild", 1), ("MSBuild", 2));
        provider.Refresh();

        var blocker = Assert.Single(provider.GetBlockers().Blockers);
        Assert.Equal("1,2", blocker.Metadata!["pids"]);
    }

    [Fact]
    public void Debounce_keeps_machine_busy_briefly_after_process_exits()
    {
        var lister = new FakeLister();
        var clock = new FakeClock();
        var provider = Provider(lister, clock, new ProcessDetectionOptions { AvailableAfterMs = 3000 });

        lister.Set(("MSBuild", 1));
        provider.Refresh();
        Assert.Single(provider.GetBlockers().Blockers);

        // Process exits, but we're still within the available_after window.
        lister.Set();
        clock.AdvanceSeconds(1);
        provider.Refresh();
        var lingering = Assert.Single(provider.GetBlockers().Blockers);
        Assert.Equal("false", lingering.Metadata!["present"]);

        // Past the window, the blocker clears.
        clock.AdvanceSeconds(3);
        provider.Refresh();
        Assert.Empty(provider.GetBlockers().Blockers);
    }

    [Fact]
    public void Busy_after_delays_blocker_appearance()
    {
        var lister = new FakeLister();
        var clock = new FakeClock();
        var provider = Provider(lister, clock, new ProcessDetectionOptions { BusyAfterMs = 5000, AvailableAfterMs = 0 });

        lister.Set(("MSBuild", 1));
        provider.Refresh();
        Assert.Empty(provider.GetBlockers().Blockers); // not yet — within busy_after window

        clock.AdvanceSeconds(6);
        provider.Refresh();
        Assert.Single(provider.GetBlockers().Blockers);
    }

    [Fact]
    public void UpdateRules_swaps_the_active_rule_set_live()
    {
        var lister = new FakeLister();
        var clock = new FakeClock();
        var provider = Provider(lister, clock); // rules: MSBuild, CodeCompass
        lister.Set(("MSBuild", 1), ("Game", 2));
        provider.Refresh();
        var before = Assert.Single(provider.GetBlockers().Blockers);
        Assert.Contains("MSBuild", before.Description); // Game isn't a rule yet

        // Hot-swap: drop the old rules, add Game.
        provider.UpdateRules(
            new[] { new ProcessRule { Name = "Game", Executable = "Game.exe" } },
            new ProcessDetectionOptions { AvailableAfterMs = 0 });
        provider.Refresh();

        var after = Assert.Single(provider.GetBlockers().Blockers);
        Assert.Contains("Game", after.Description);   // Game now blocks
        Assert.DoesNotContain("MSBuild", after.Description); // MSBuild no longer configured
    }

    [Fact]
    public void Enumeration_failure_yields_non_confident_result()
    {
        var lister = new FakeLister { Throw = true };
        var clock = new FakeClock();
        var provider = Provider(lister, clock);

        provider.Refresh();

        var result = provider.GetBlockers();
        Assert.False(result.Confident);
        Assert.Empty(result.Blockers);
    }
}
