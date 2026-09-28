using System.Text.Json;
using System.Text.Json.Nodes;
using ComputeWarden.Client;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Diagnostics;
using ComputeWarden.Core.Providers;
using ComputeWarden.Daemon;
using ComputeWarden.Mcp;

namespace ComputeWarden.Tests;

/// <summary>
/// Exercises the MCP tool methods directly (not over stdio) against an in-process daemon,
/// verifying the structured JSON they return.
/// </summary>
[Collection("mcp-gateway")] // these mutate the static WardenGateway; run serially
public class McpToolsTests
{
    private sealed class StubLister : IProcessLister
    {
        private volatile RunningProcess[] _processes = Array.Empty<RunningProcess>();
        public void Set(params RunningProcess[] procs) => _processes = procs;
        public IReadOnlyList<RunningProcess> List() => _processes;
    }

    private static WardenHost StartHostWithTools(WardenConfig? config = null, IProcessLister? lister = null)
    {
        var pipeName = "ComputeWarden-test-" + Guid.NewGuid().ToString("N");
        var host = new WardenHost(
            config ?? WardenConfig.CreateDefault(), NullLog.Instance, lister ?? new StubLister(), pipeName: pipeName);
        host.Start();
        // Point the tools at this host's pipe and disable auto-spawn.
        WardenGateway.Configure(new WardenClient(pipeName), autoSpawn: false);
        return host;
    }

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    [Fact]
    public async Task Status_tool_reports_available()
    {
        await using var host = StartHostWithTools();

        var res = Parse(await ComputeWardenTools.Status());

        Assert.Equal("AVAILABLE", (string)res["state"]!);
        Assert.True((bool)res["can_run_intensive"]!);
        Assert.Empty(res["blockers"]!.AsArray());
    }

    [Fact]
    public async Task Acquire_release_tools_full_cycle()
    {
        await using var host = StartHostWithTools();

        var acquire = Parse(await ComputeWardenTools.Acquire("Claude Code", "indexing", 300));
        Assert.True((bool)acquire["acquired"]!);
        var id = (string)acquire["reservation_id"]!;
        Assert.NotNull(acquire["lease_remaining_seconds"]);

        var blocked = Parse(await ComputeWardenTools.Acquire("Other", "work"));
        Assert.False((bool)blocked["acquired"]!);
        var blocker = Assert.Single(blocked["blockers"]!.AsArray());
        Assert.Equal("RESERVATION", (string)blocker!["type"]!);

        var renew = Parse(await ComputeWardenTools.Renew(id, 120));
        Assert.True((bool)renew["renewed"]!);

        var release = Parse(await ComputeWardenTools.Release(id));
        Assert.True((bool)release["released"]!);

        Assert.True((bool)Parse(await ComputeWardenTools.Acquire("Other", "work"))["acquired"]!);
    }

    [Fact]
    public async Task Can_run_tool_carries_informational_note()
    {
        await using var host = StartHostWithTools();

        var res = Parse(await ComputeWardenTools.CanRun());

        Assert.True((bool)res["can_run"]!);
        Assert.Contains("Informational", (string)res["note"]!);
    }

    [Fact]
    public async Task Manual_busy_tools_set_and_clear()
    {
        await using var host = StartHostWithTools();

        var set = Parse(await ComputeWardenTools.SetManualBusy("disk maintenance"));
        Assert.Equal("MANUAL", (string)set["blocker"]!["type"]!);

        var status = Parse(await ComputeWardenTools.Status());
        Assert.Equal("BUSY", (string)status["state"]!);

        var clear = Parse(await ComputeWardenTools.ClearManualBusy());
        Assert.True((bool)clear["cleared"]!);
    }

    [Fact]
    public async Task Stats_tool_reports_usage_counters()
    {
        await using var host = StartHostWithTools();

        await ComputeWardenTools.Acquire("A", "work");   // one success
        await ComputeWardenTools.Acquire("B", "work");   // one failure (blocked)

        var stats = Parse(await ComputeWardenTools.Stats());

        Assert.Equal(1, (int)stats["total_acquisitions"]!);
        Assert.Equal(1, (int)stats["failed_acquisitions"]!);
        Assert.Equal(1, (int)stats["current_reservations"]!);
    }

    [Fact]
    public async Task Detected_process_shows_in_status_tool()
    {
        var lister = new StubLister();
        lister.Set(new RunningProcess(14280, "MSBuild"));
        var config = new WardenConfig
        {
            ProcessRules = { new ProcessRule { Name = "MSBuild", Executable = "MSBuild.exe" } },
            ProcessDetection = new ProcessDetectionOptions { PollIntervalMs = 250, AvailableAfterMs = 0 },
        };
        await using var host = StartHostWithTools(config, lister);

        JsonNode status = Parse(await ComputeWardenTools.Status());
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while ((string)status["state"]! != "BUSY" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            status = Parse(await ComputeWardenTools.Status());
        }

        Assert.Equal("BUSY", (string)status["state"]!);
        var blocker = Assert.Single(status["blockers"]!.AsArray());
        Assert.Equal("PROCESS", (string)blocker!["type"]!);
    }
}
