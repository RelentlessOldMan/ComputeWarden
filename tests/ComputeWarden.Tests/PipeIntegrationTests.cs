using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Diagnostics;
using ComputeWarden.Core.Providers;
using ComputeWarden.Daemon;

namespace ComputeWarden.Tests;

public class PipeIntegrationTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private sealed class StubLister : IProcessLister
    {
        private volatile RunningProcess[] _processes = Array.Empty<RunningProcess>();
        public void Set(params RunningProcess[] procs) => _processes = procs;
        public IReadOnlyList<RunningProcess> List() => _processes;
    }

    private static async Task<JsonNode> RoundTripAsync(string pipeName, object request)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(3000);
        using var reader = new StreamReader(client, Utf8NoBom, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(client, Utf8NoBom) { AutoFlush = true };

        await writer.WriteLineAsync(JsonSerializer.Serialize(request));
        var line = await reader.ReadLineAsync();
        return JsonNode.Parse(line!)!;
    }

    [Fact]
    public async Task Reservation_round_trip_over_named_pipe()
    {
        var pipeName = "ComputeWarden-test-" + Guid.NewGuid().ToString("N");
        await using var host = new WardenHost(
            WardenConfig.CreateDefault(), NullLog.Instance, new StubLister(), pipeName: pipeName);
        host.Start();

        var status = await RoundTripAsync(pipeName, new { v = 1, op = "STATUS" });
        Assert.Equal("AVAILABLE", (string)status["state"]!);

        var acquire = await RoundTripAsync(pipeName, new { v = 1, op = "ACQUIRE", owner = "A", description = "work" });
        Assert.True((bool)acquire["acquired"]!);
        var id = (string)acquire["reservationId"]!;

        var blocked = await RoundTripAsync(pipeName, new { v = 1, op = "ACQUIRE", owner = "B", description = "work" });
        Assert.False((bool)blocked["acquired"]!);
        Assert.Single(blocked["blockers"]!.AsArray());

        var release = await RoundTripAsync(pipeName, new { v = 1, op = "RELEASE", reservationId = id });
        Assert.True((bool)release["released"]!);

        var afterRelease = await RoundTripAsync(pipeName, new { v = 1, op = "ACQUIRE", owner = "B", description = "work" });
        Assert.True((bool)afterRelease["acquired"]!);
    }

    [Fact]
    public async Task Malformed_request_over_pipe_does_not_kill_server()
    {
        var pipeName = "ComputeWarden-test-" + Guid.NewGuid().ToString("N");
        await using var host = new WardenHost(
            WardenConfig.CreateDefault(), NullLog.Instance, new StubLister(), pipeName: pipeName);
        host.Start();

        var bad = await RoundTripAsync(pipeName, "this is not a request object");
        Assert.False((bool)bad["ok"]!);

        // Server still serves subsequent well-formed requests.
        var status = await RoundTripAsync(pipeName, new { v = 1, op = "STATUS" });
        Assert.True((bool)status["ok"]!);
    }

    [Fact]
    public async Task Oversized_request_is_rejected_without_dispatch()
    {
        var pipeName = "ComputeWarden-test-" + Guid.NewGuid().ToString("N");
        await using var host = new WardenHost(
            WardenConfig.CreateDefault(), NullLog.Instance, new StubLister(), pipeName: pipeName);
        host.Start();

        // A request line well beyond any legitimate size.
        var huge = new { v = 1, op = "STATUS", description = new string('x', 300_000) };
        var res = await RoundTripAsync(pipeName, huge);

        Assert.False((bool)res["ok"]!);
        Assert.Contains("too large", (string)res["error"]!);

        // Server remains healthy for normal requests afterward.
        var status = await RoundTripAsync(pipeName, new { v = 1, op = "STATUS" });
        Assert.True((bool)status["ok"]!);
    }

    [Fact]
    public async Task Detected_process_blocks_acquire_over_pipe()
    {
        var pipeName = "ComputeWarden-test-" + Guid.NewGuid().ToString("N");
        var lister = new StubLister();
        lister.Set(new RunningProcess(14280, "MSBuild"));

        var config = new WardenConfig
        {
            ProcessRules = { new ProcessRule { Name = "MSBuild", Executable = "MSBuild.exe" } },
            ProcessDetection = new ProcessDetectionOptions { PollIntervalMs = 250, AvailableAfterMs = 0 },
        };
        await using var host = new WardenHost(config, NullLog.Instance, lister, pipeName: pipeName);
        host.Start();

        // Poll until the monitor has observed the process (primed at Start, so this is quick).
        JsonNode status = await RoundTripAsync(pipeName, new { v = 1, op = "STATUS" });
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while ((string)status["state"]! != "BUSY" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            status = await RoundTripAsync(pipeName, new { v = 1, op = "STATUS" });
        }

        Assert.Equal("BUSY", (string)status["state"]!);
        var acquire = await RoundTripAsync(pipeName, new { v = 1, op = "ACQUIRE", owner = "A", description = "work" });
        Assert.False((bool)acquire["acquired"]!);
        Assert.Contains(acquire["blockers"]!.AsArray(), b => (string)b!["type"]! == "PROCESS");
    }
}
