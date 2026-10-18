using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ComputeWarden.Client;
using ComputeWarden.Core;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Diagnostics;
using ComputeWarden.Core.Ipc;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;
using ComputeWarden.Daemon;
using ComputeWarden.Mcp;

namespace ComputeWarden.Tests;

/// <summary>Agent arguments and the fields read back, end to end: MCP tool, client, pipe, dispatcher.</summary>
[Collection("mcp-gateway")] // the MCP tool tests mutate the static WardenGateway
public class AgentArgumentTests
{
    private sealed class NoProcesses : IProcessLister
    {
        public IReadOnlyList<RunningProcess> List() => Array.Empty<RunningProcess>();
    }

    private static WardenHost StartHost(out string pipeName)
    {
        pipeName = TestIsolation.NewPipeName();
        var host = new WardenHost(WardenConfig.CreateDefault(), NullLog.Instance, new NoProcesses(), pipeName: pipeName);
        host.Start();
        return host;
    }

    private static async Task<JsonNode> Tool(Task<string> call) => JsonNode.Parse(await call.Within())!;

    private static JsonNode ClaimOf(JsonNode status, string owner)
        => status["claims"]!.AsArray().Single(c => (string)c!["owner"]! == owner)!;

    private static string[] Names(JsonNode? array) => array!.AsArray().Select(n => (string)n!).ToArray();

    // ---- MCP tools ------------------------------------------------------

    [Fact]
    public async Task Mcp_round_trip_carries_every_claim_and_advice_field()
    {
        await using var host = StartHost(out var pipe);
        WardenGateway.Configure(new WardenClient(pipe), autoSpawn: false);
        try
        {
            var a = await Tool(ComputeWardenTools.Acquire("A", "holder", resources: new[] { "cpu" }, kind: "exclusive", estimateSeconds: 400));
            Assert.Equal("full", (string)a["advice"]!["grant"]!);
            Assert.InRange((int)a["lease_remaining_seconds"]!, 520, 530); // ceil(400 * 1.25) + 30

            var b = await Tool(ComputeWardenTools.Acquire("B", "waiter", resources: new[] { "cpu" }, kind: "exclusive", whenBusy: "wait", estimateSeconds: 100));
            Assert.Equal("queued", (string)b["advice"]!["grant"]!);
            Assert.Equal(1, (int)b["advice"]!["queue"]!["position"]!);
            Assert.InRange((int)b["advice"]!["queue"]!["est_wait_seconds"]!, 380, 400); // A's estimate

            var c = await Tool(ComputeWardenTools.Acquire("C", "vip", resources: new[] { "cpu" }, kind: "exclusive", whenBusy: "wait", userPriority: "user: me first"));
            Assert.Equal("queued", (string)c["advice"]!["grant"]!);
            Assert.Equal(1, (int)c["advice"]!["queue"]!["position"]!); // ahead of B

            var s = await Tool(ComputeWardenTools.Acquire("S", "build", resources: new[] { "cpu", "disk" }, kind: "shared"));
            Assert.Equal("throttled", (string)s["advice"]!["grant"]!);
            Assert.Equal(new[] { "cpu" }, Names(s["advice"]!["contended"]));

            var g = await Tool(ComputeWardenTools.Acquire("G", "render", resources: new[] { "gpu" }, kind: "exclusive"));
            Assert.Equal("full", (string)g["advice"]!["grant"]!); // disjoint from A

            var renewB = await Tool(ComputeWardenTools.Renew((string)b["reservation_id"]!));
            var ahead = Assert.Single(renewB["advice"]!["queue"]!["ahead"]!.AsArray())!;
            Assert.Equal("C", (string)ahead["owner"]!);
            Assert.Equal("user: me first", (string)ahead["user_priority"]!);

            var status = await Tool(ComputeWardenTools.Status());
            var ca = ClaimOf(status, "A");
            Assert.Equal("exclusive", (string)ca["kind"]!);
            Assert.True((bool)ca["holding"]!);
            Assert.Equal(400, (int)ca["estimate_seconds"]!);
            var cb = ClaimOf(status, "B");
            Assert.False((bool)cb["holding"]!);
            Assert.Equal(2, (int)cb["position"]!);
            Assert.Equal(100, (int)cb["estimate_seconds"]!);
            var cc = ClaimOf(status, "C");
            Assert.Equal("user: me first", (string)cc["user_priority"]!);
            Assert.Equal(1, (int)cc["position"]!);
            var cs = ClaimOf(status, "S");
            Assert.Equal("shared", (string)cs["kind"]!);
            Assert.Equal("throttled", (string)cs["grant"]!);
            Assert.Null(cs["holding"]); // shown only for exclusive claims
            Assert.Equal(new[] { "cpu", "disk" }, Names(cs["resources"]));
        }
        finally
        {
            TestIsolation.ConfigureDeadGateway();
        }
    }

    [Fact]
    public async Task Mcp_renew_waits_for_the_grant_and_user_priority_takes_the_hold()
    {
        await using var host = StartHost(out var pipe);
        WardenGateway.Configure(new WardenClient(pipe), autoSpawn: false);
        try
        {
            var a = await Tool(ComputeWardenTools.Acquire("A", "holder", resources: new[] { "cpu" }, kind: "exclusive"));
            var b = await Tool(ComputeWardenTools.Acquire("B", "first", resources: new[] { "cpu" }, kind: "exclusive", whenBusy: "wait"));
            var c = await Tool(ComputeWardenTools.Acquire("C", "vip", resources: new[] { "cpu" }, kind: "exclusive", whenBusy: "wait", userPriority: "user: me first"));

            // Nothing changes: the renew waits out its waitSeconds.
            var quiet = Stopwatch.StartNew();
            var unchanged = await Tool(ComputeWardenTools.Renew((string)b["reservation_id"]!, waitSeconds: 2));
            Assert.True(quiet.Elapsed >= TimeSpan.FromSeconds(1.5), $"answered after {quiet.Elapsed}");
            Assert.Equal("queued", (string)unchanged["advice"]!["grant"]!);

            // The holder leaves: the waiting renew answers on the change.
            var watch = Stopwatch.StartNew();
            var renewing = Tool(ComputeWardenTools.Renew((string)c["reservation_id"]!, waitSeconds: 30));
            await Task.Delay(300);
            await Tool(ComputeWardenTools.Release((string)a["reservation_id"]!));
            var renewed = await renewing;
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"answered after {watch.Elapsed}");
            Assert.Equal("full", (string)renewed["advice"]!["grant"]!);

            var status = await Tool(ComputeWardenTools.Status());
            Assert.True((bool)ClaimOf(status, "C")["holding"]!);
            Assert.False((bool)ClaimOf(status, "B")["holding"]!);
            Assert.Equal("queued", (string)(await Tool(ComputeWardenTools.Renew((string)b["reservation_id"]!)))["advice"]!["grant"]!);
        }
        finally
        {
            TestIsolation.ConfigureDeadGateway();
        }
    }

    [Fact]
    public async Task Mcp_set_mode_covers_only_the_named_resources()
    {
        await using var host = StartHost(out var pipe);
        WardenGateway.Configure(new WardenClient(pipe), autoSpawn: false);
        try
        {
            var set = await Tool(ComputeWardenTools.SetMode("hold", resources: new[] { "gpu" }));
            Assert.Equal(new[] { "gpu" }, Names(set["mode"]!["resources"]));

            var cpu = await Tool(ComputeWardenTools.Acquire("A", "build", resources: new[] { "cpu" }, kind: "shared"));
            var gpu = await Tool(ComputeWardenTools.Acquire("B", "render", resources: new[] { "gpu" }, kind: "shared"));

            Assert.Equal("full", (string)cpu["advice"]!["grant"]!);
            Assert.Equal("queued", (string)gpu["advice"]!["grant"]!);
        }
        finally
        {
            TestIsolation.ConfigureDeadGateway();
        }
    }

    // ---- Client ---------------------------------------------------------

    [Fact]
    public async Task Client_long_poll_outlasts_the_response_timeout()
    {
        await using var host = StartHost(out var pipe);
        var client = new WardenClient(pipe, responseTimeout: TimeSpan.FromSeconds(1));
        await client.SetModeAsync(UserMode.Steady).Within();
        var claim = await client.AcquireAsync(new ClaimRequest("A", "build", ClaimKind.Shared)).Within();

        var watch = Stopwatch.StartNew();
        var renew = await client.RenewAsync(claim.ReservationId!, waitSeconds: 3).Within();

        Assert.True(renew.Renewed);
        Assert.Equal(Grant.Throttled, renew.Advice!.Grant);
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(2.5), $"answered after {watch.Elapsed}");
    }

    [Fact]
    public async Task Client_asks_for_at_most_the_longest_wait()
    {
        var pipeName = TestIsolation.NewPipeName();
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var enc = new UTF8Encoding(false);
            using var reader = new StreamReader(server, enc, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(server, enc, 1024, leaveOpen: true) { AutoFlush = true };
            var line = await reader.ReadLineAsync();
            await writer.WriteLineAsync("""{"v":1,"ok":true,"renewed":true}""");
            return line!;
        });

        var renew = await new WardenClient(pipeName).RenewAsync("abc", waitSeconds: 600).Within();
        var sent = JsonSerializer.Deserialize<IpcRequest>(await serve.Within(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.True(renew.Renewed);
        Assert.Equal(RequestDispatcher.MaxWaitSeconds, sent.WaitSeconds);
    }

    // ---- Dispatcher -----------------------------------------------------

    [Fact]
    public async Task Dispatcher_caps_the_wait_at_60_seconds()
    {
        var clock = new FakeClock();
        var warden = TestFactory.NewWarden(clock);
        var d = new RequestDispatcher(warden, pollInterval: TimeSpan.FromMilliseconds(20));
        warden.SetMode(UserMode.Steady);
        var id = warden.Acquire(new ClaimRequest("A", "build", ClaimKind.Shared, LeaseSeconds: 30)).ReservationId!;

        var pending = d.HandleAsync(JsonSerializer.Serialize(new { v = 1, op = "RENEW", reservationId = id, waitSeconds = 600 }));

        // The wait keeps the claim alive for the capped wait plus the reply's margin, not for 600s.
        Assert.Equal(clock.UtcNow.AddSeconds(60 + 15), Assert.Single(warden.GetStatus().Claims!).ExpiresAt);
        warden.SetMode(UserMode.Auto);
        Assert.Equal("full", (string)JsonNode.Parse(await pending.Within())!["advice"]!["grant"]!);
    }

    [Theory]
    [InlineData(IpcProtocol.Version, true)]
    [InlineData(0, true)]
    [InlineData(IpcProtocol.Version + 1, false)]
    public void Dispatcher_serves_its_own_version_and_older_but_not_newer(int v, bool served)
    {
        var d = new RequestDispatcher(TestFactory.NewWarden(new FakeClock()));

        var res = JsonNode.Parse(d.Handle(JsonSerializer.Serialize(new { v, op = "STATUS" })))!;

        Assert.Equal(served, (bool)res["ok"]!);
        if (!served) Assert.Contains("Unsupported protocol version", (string)res["error"]!);
    }
}
