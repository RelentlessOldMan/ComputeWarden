using System.Diagnostics;
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

/// <summary>The rules over the wire: dispatcher JSON, long-poll renew, client, MCP tools, and cw.</summary>
[Collection("mcp-gateway")] // the MCP tool tests mutate the static WardenGateway
public class WireTests
{
    private sealed class NoProcesses : IProcessLister
    {
        public IReadOnlyList<RunningProcess> List() => Array.Empty<RunningProcess>();
    }

    private static (RequestDispatcher d, Warden w) New()
    {
        var warden = TestFactory.NewWarden(new FakeClock());
        return (new RequestDispatcher(warden, pollInterval: TimeSpan.FromMilliseconds(20)), warden);
    }

    private static JsonNode Handle(RequestDispatcher d, object request)
        => JsonNode.Parse(d.Handle(JsonSerializer.Serialize(request)))!;

    private static WardenHost StartHost(out string pipeName)
    {
        pipeName = "ComputeWarden-test-" + Guid.NewGuid().ToString("N");
        var host = new WardenHost(WardenConfig.CreateDefault(), NullLog.Instance, new NoProcesses(), pipeName: pipeName);
        host.Start();
        return host;
    }

    // ---- Dispatcher ------------------------------------------------------

    [Fact]
    public void Acquire_returns_advice_kind_and_big_memory()
    {
        var (d, _) = New();
        Handle(d, new { v = 1, op = "SET_MANUAL_BUSY", reason = "recording" });

        var res = Handle(d, new { v = 1, op = "ACQUIRE", owner = "A", description = "build", estimateSeconds = 60, bigMemory = true });

        Assert.True((bool)res["acquired"]!);
        Assert.Equal("shared", (string)res["kind"]!);
        Assert.True((bool)res["bigMemory"]!);
        var advice = res["advice"]!;
        Assert.Equal("queued", (string)advice["grant"]!); // a hold lets nothing new start
        Assert.Contains("manual hold: recording", (string)advice["reason"]!);
        Assert.Equal(1, (int)advice["queue"]!["position"]!);
        Assert.Null(advice["contended"]);
    }

    [Fact]
    public void Acquire_without_kind_is_shared_and_runs_full()
    {
        var (d, _) = New();

        var res = Handle(d, new { v = 1, op = "ACQUIRE", owner = "A", description = "x" });

        Assert.True((bool)res["acquired"]!);
        Assert.Equal("shared", (string)res["kind"]!);
        Assert.Equal("full", (string)res["advice"]!["grant"]!);
    }

    [Fact]
    public void Status_reports_the_daemon_version_jobs_while_gaming_and_claims()
    {
        var (d, _) = New();
        Handle(d, new { v = 1, op = "ACQUIRE", owner = "A", description = "x" });
        Handle(d, new { v = 1, op = "ACQUIRE", owner = "T", description = "bench", kind = "solo", estimateSeconds = 60 });

        var res = Handle(d, new { v = 1, op = "STATUS" });

        Assert.Equal(IpcProtocol.ProductVersion, (string)res["daemonVersion"]!);
        Assert.Matches(@"^\d+\.\d+\.\d+", IpcProtocol.ProductVersion);
        Assert.Equal(3, (int)res["gameJobs"]!);
        Assert.Null(res["mode"]);
        Assert.Null(res["busyResources"]);
        var claims = res["claims"]!.AsArray();
        Assert.Equal("running", (string)claims[0]!["phase"]!);
        Assert.Equal("full", (string)claims[0]!["grant"]!);
        Assert.Equal("solo", (string)claims[1]!["kind"]!);
        Assert.Equal("waiting", (string)claims[1]!["phase"]!);
        Assert.Equal(1, (int)claims[1]!["position"]!);
    }

    [Theory]
    [InlineData("{\"v\":1,\"op\":\"ACQUIRE\",\"owner\":\"A\",\"kind\":\"greedy\"}", "Unknown kind")]
    [InlineData("{\"v\":1,\"op\":\"ACQUIRE\",\"owner\":\"A\",\"kind\":\"exclusive\"}", "kind exclusive was removed")]
    [InlineData("{\"v\":1,\"op\":\"ACQUIRE\",\"owner\":\"A\",\"estimateSeconds\":\"ten\"}", "'estimateSeconds' has the wrong type")]
    [InlineData("{\"v\":1,\"op\":\"ACQUIRE\",\"owner\":\"A\",\"bigMemory\":\"yes\"}", "'bigMemory' has the wrong type")]
    [InlineData("{\"v\":1,\"op\":\"SET_MODE\",\"mode\":\"steady\"}", "SET_MODE")]
    public void Bad_values_are_reported_as_errors(string line, string expected)
    {
        var (d, _) = New();

        var res = JsonNode.Parse(d.Handle(line))!;

        Assert.False((bool)res["ok"]!);
        Assert.Contains(expected, (string)res["error"]!);
    }

    [Fact]
    public async Task Renew_with_wait_returns_as_soon_as_the_grant_changes()
    {
        var (d, w) = New();
        w.SetManualBusy("recording");
        var id = w.Acquire(new ClaimRequest("A", "build")).ReservationId!;

        var watch = Stopwatch.StartNew();
        var pending = d.HandleAsync(JsonSerializer.Serialize(new { v = 1, op = "RENEW", reservationId = id, waitSeconds = 30 }));
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);
        w.ClearManualBusy();
        var res = JsonNode.Parse(await pending.Within())!;

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"answered after {watch.Elapsed}");
        Assert.True((bool)res["renewed"]!);
        Assert.Equal("full", (string)res["advice"]!["grant"]!);
        Assert.Equal(Grant.Full, w.LastGrant(id));
    }

    [Fact]
    public async Task Renew_with_wait_gives_up_at_the_deadline()
    {
        var (d, w) = New();
        var id = w.Acquire(new ClaimRequest("A", "build")).ReservationId!;
        w.SetManualBusy("recording");
        w.Renew(id); // hears the throttle, so the wait has nothing new to report

        var watch = Stopwatch.StartNew();
        var res = JsonNode.Parse(await d.HandleAsync(JsonSerializer.Serialize(new { v = 1, op = "RENEW", reservationId = id, waitSeconds = 1 })).Within())!;

        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(0.8), $"answered after {watch.Elapsed}");
        Assert.True((bool)res["renewed"]!);
        Assert.Equal("throttled", (string)res["advice"]!["grant"]!);
    }

    [Fact]
    public async Task Renew_with_wait_on_an_unknown_id_answers_at_once()
    {
        var (d, _) = New();

        var watch = Stopwatch.StartNew();
        var res = JsonNode.Parse(await d.HandleAsync("{\"v\":1,\"op\":\"RENEW\",\"reservationId\":\"nope\",\"waitSeconds\":30}").Within())!;

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.False((bool)res["renewed"]!);
    }

    // ---- Client over the pipe -------------------------------------------

    [Fact]
    public async Task Client_claims_hold_and_long_poll_round_trip()
    {
        await using var host = StartHost(out var pipe);
        var client = new WardenClient(pipe);

        var claim = await client.AcquireAsync(new ClaimRequest("A", "build", ClaimKind.Shared, LeaseSeconds: 30, EstimateSeconds: 120)).Within();
        Assert.Equal(ClaimKind.Shared, claim.Kind);
        Assert.Equal(Grant.Full, claim.Advice!.Grant);
        await client.SetManualBusyAsync("recording").Within();

        var status = await client.StatusAsync().Within();
        Assert.Equal(3, status.GameJobs);
        Assert.Contains(status.Blockers, b => b.Type == BlockerType.Manual && b.Description == "recording");
        var info = Assert.Single(status.Claims!);
        Assert.Equal(ClaimPhase.Running, info.Phase);

        var renew = await client.RenewAsync(claim.ReservationId!).Within();
        Assert.Equal(Grant.Throttled, renew.Advice!.Grant);
        Assert.Equal(2, renew.Advice.Throttle!.MaxRamGb);
        Assert.NotNull(renew.Advice.How);

        var leaseEnd = Assert.Single((await client.StatusAsync().Within()).Claims!).ExpiresAt;
        var watch = Stopwatch.StartNew();
        var renewing = client.RenewAsync(claim.ReservationId!, waitSeconds: 30);
        // The waiting renew pushes the lease out; once it has, the daemon is holding the renew.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Assert.Single((await client.StatusAsync().Within()).Claims!).ExpiresAt <= leaseEnd.AddSeconds(5))
        {
            Assert.True(DateTime.UtcNow < deadline, "the renew never reached the daemon");
            await Task.Delay(25);
        }
        Assert.False(renewing.IsCompleted);
        Assert.True(await client.ClearManualBusyAsync().Within());
        var done = await renewing.Within();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"answered after {watch.Elapsed}");
        Assert.Equal(Grant.Full, done.Advice!.Grant); // answered on the change, not after 30s

        var big = await client.AcquireAsync(new ClaimRequest("B", "index", BigMemory: true));
        var waiting = await client.AcquireAsync(new ClaimRequest("C", "index", BigMemory: true));
        Assert.True(big.BigMemory);
        Assert.Equal(Grant.Full, big.Advice!.Grant);
        Assert.Equal(Grant.Queued, waiting.Advice!.Grant);
        Assert.Empty(waiting.Advice.Queue!.Ahead); // B runs; it isn't ahead in line
        Assert.Contains("B is running a big-memory job", waiting.Advice.Reason);
        Assert.True((await client.StatusAsync()).Claims!.Single(c => c.Owner == "C").BigMemory);
    }

    [Fact]
    public async Task Waiting_renew_stops_when_the_client_hangs_up()
    {
        await using var host = StartHost(out var pipe);
        var client = new WardenClient(pipe);
        var claim = await client.AcquireAsync(new ClaimRequest("A", "build", LeaseSeconds: 60));
        await client.SetManualBusyAsync("recording");
        await client.RenewAsync(claim.ReservationId!); // hears the throttle

        using var cts = new CancellationTokenSource();
        var renewing = client.RenewAsync(claim.ReservationId!, leaseSeconds: 3600, waitSeconds: 30, ct: cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renewing);
        await Task.Delay(1000);

        // Ending the wait now would renew for an hour, if the daemon were still waiting.
        await client.ClearManualBusyAsync();
        await Task.Delay(2000);

        var info = Assert.Single((await client.StatusAsync()).Claims!);
        Assert.True(info.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(10), $"renewed for nobody: expires {info.ExpiresAt:O}");
    }

    // ---- MCP tools ------------------------------------------------------

    [Fact]
    public async Task Mcp_tools_report_advice_and_claims()
    {
        await using var host = StartHost(out var pipe);
        WardenGateway.Configure(new WardenClient(pipe), autoSpawn: false);

        var acq = JsonNode.Parse(await ComputeWardenTools.Acquire("Claude", "dotnet test", estimateSeconds: 90))!;
        Assert.True((bool)acq["acquired"]!);
        Assert.Equal("shared", (string)acq["kind"]!);
        Assert.Equal("full", (string)acq["advice"]!["grant"]!);

        await ComputeWardenTools.SetManualBusy("recording");
        var held = JsonNode.Parse(await ComputeWardenTools.Renew((string)acq["reservation_id"]!))!;
        Assert.Equal("throttled", (string)held["advice"]!["grant"]!);
        Assert.Equal(1, (int)held["advice"]!["throttle"]!["max_cores"]!);
        Assert.Contains("small", (string)held["advice"]!["next"]!);

        var big = JsonNode.Parse(await ComputeWardenTools.Acquire("Claude", "index", bigMemory: true))!;
        Assert.True((bool)big["big_memory"]!);
        Assert.Equal("queued", (string)big["advice"]!["grant"]!);

        var status = JsonNode.Parse(await ComputeWardenTools.Status())!;
        Assert.Equal(3, (int)status["game_jobs"]!);
        Assert.Equal("Claude", (string)status["claims"]![0]!["owner"]!);
        Assert.Equal("running", (string)status["claims"]![0]!["phase"]!);
        Assert.Equal("waiting", (string)status["claims"]![1]!["phase"]!);

        await ComputeWardenTools.ClearManualBusy();
        var renew = JsonNode.Parse(await ComputeWardenTools.Renew((string)acq["reservation_id"]!, waitSeconds: 5))!;
        Assert.True((bool)renew["renewed"]!);
        Assert.Equal("full", (string)renew["advice"]!["grant"]!); // the game-over signal

        var bad = JsonNode.Parse(await ComputeWardenTools.Acquire("Claude", "x", kind: "greedy"))!;
        Assert.Contains("Unknown kind", (string)bad["error"]!);
        var exclusive = JsonNode.Parse(await ComputeWardenTools.Acquire("Claude", "x", kind: "exclusive"))!;
        Assert.Contains("kind exclusive was removed", (string)exclusive["error"]!);
        Assert.Contains("solo", (string)exclusive["error"]!);
    }

    // ---- cw -------------------------------------------------------------

    [Fact]
    public async Task Cw_turns_the_hold_on_and_off_and_shows_status()
    {
        await using var host = StartHost(out var pipe);
        var client = new WardenClient(pipe);
        var output = new StringWriter();
        var errors = new StringWriter();

        Assert.Equal(0, await Cli.Cli.RunAsync(new[] { "hold", "recording", "a", "video" }, client, output, errors));
        Assert.Contains("Hold on: recording a video.", output.ToString());

        await client.AcquireAsync(new ClaimRequest("Codex", "full test suite"));
        await client.AcquireAsync(new ClaimRequest("Timer", "bench", ClaimKind.Solo, EstimateSeconds: 60));
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await Cli.Cli.RunAsync(Array.Empty<string>(), client, output, errors));
        var text = output.ToString();
        Assert.Contains("Hold: recording a video", text);
        Assert.Contains("queued #1", text);
        Assert.Contains("Codex: full test suite", text);
        Assert.Contains("solo", text);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await Cli.Cli.RunAsync(new[] { "hold", "off" }, client, output, errors));
        Assert.Contains("Hold off.", output.ToString());
        Assert.Equal(0, await Cli.Cli.RunAsync(new[] { "hold", "off" }, client, output, errors));
        Assert.Contains("No hold was on.", output.ToString());
        Assert.DoesNotContain((await client.StatusAsync()).Blockers, b => b.Type == BlockerType.Manual);
    }

    [Fact]
    public async Task Cw_reports_bad_input_and_a_missing_daemon()
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var nobody = new WardenClient("ComputeWarden-test-none-" + Guid.NewGuid().ToString("N"), connectTimeoutMs: 100);

        Assert.Equal(2, await Cli.Cli.RunAsync(new[] { "mode", "steady" }, nobody, output, errors));
        Assert.Equal(2, await Cli.Cli.RunAsync(new[] { "frobnicate" }, nobody, output, errors));
        Assert.Equal(1, await Cli.Cli.RunAsync(new[] { "status" }, nobody, output, errors));
        Assert.Contains("Could not reach", errors.ToString());
    }

    [Fact]
    public async Task Cw_wait_returns_when_the_claim_may_start()
    {
        await using var host = StartHost(out var pipe);
        var client = new WardenClient(pipe);
        var timer = await client.AcquireAsync(new ClaimRequest("T", "bench", ClaimKind.Solo, EstimateSeconds: 60));
        var queued = await client.AcquireAsync(new ClaimRequest("Q", "train", EstimateSeconds: 300));
        Assert.Equal(Grant.Queued, queued.Advice!.Grant);
        var output = new StringWriter();
        var errors = new StringWriter();

        var waiting = Cli.Cli.RunAsync(new[] { "wait", queued.ReservationId! }, client, output, errors);
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);
        await client.ReleaseAsync(timer.ReservationId!);

        Assert.Equal(0, await waiting.Within());
        Assert.Contains("Your turn: full.", output.ToString());
        Assert.Contains("or the slot passes to the next in line", output.ToString());

        // cw wait only checked the ticket in; the owner's own renew starts the claim with its real lease.
        Assert.Equal(Grant.Queued, host.Warden.LastGrant(queued.ReservationId!));
        var started = await client.RenewAsync(queued.ReservationId!);
        Assert.Equal(Grant.Full, host.Warden.LastGrant(queued.ReservationId!));
        Assert.True(started.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(200), $"expires {started.ExpiresAt:o}");
    }

    [Fact]
    public async Task Cw_wait_gives_up_after_max_and_reports_a_lost_claim()
    {
        await using var host = StartHost(out var pipe);
        var client = new WardenClient(pipe);
        await client.AcquireAsync(new ClaimRequest("T", "bench", ClaimKind.Solo, EstimateSeconds: 60));
        var queued = await client.AcquireAsync(new ClaimRequest("Q", "train"));
        var output = new StringWriter();
        var errors = new StringWriter();

        Assert.Equal(Cli.Cli.StillQueued, await Cli.Cli.RunAsync(new[] { "wait", queued.ReservationId!, "--max", "1s" }, client, output, errors).Within());
        Assert.Contains("Still queued after 1s", output.ToString());
        Assert.Equal(Grant.Queued, (await client.RenewAsync(queued.ReservationId!)).Advice!.Grant); // still in line

        Assert.Equal(1, await Cli.Cli.RunAsync(new[] { "wait", "no-such-claim" }, client, output, errors).Within());
        Assert.Contains("Claim gone", errors.ToString());
        Assert.Equal(2, await Cli.Cli.RunAsync(new[] { "wait" }, client, output, errors));
        Assert.Equal(2, await Cli.Cli.RunAsync(new[] { "wait", "x", "--max", "soon" }, client, output, errors));
    }

    [Fact]
    public async Task Cw_wait_retries_an_unreachable_daemon_then_says_so()
    {
        var client = new WardenClient("ComputeWarden-Test-" + Guid.NewGuid().ToString("N"), connectTimeoutMs: 50);
        var output = new StringWriter();
        var errors = new StringWriter();
        var timer = Stopwatch.StartNew();

        Assert.Equal(Cli.Cli.Unreachable, await Cli.Cli.RunAsync(new[] { "wait", "abc" }, client, output, errors).Within());

        Assert.True(timer.Elapsed >= TimeSpan.FromSeconds(6), $"gave up after {timer.Elapsed}"); // backed off 1+2+4 s
        Assert.Contains("run cw wait again", errors.ToString());
        Assert.DoesNotContain("Claim gone", errors.ToString());
    }

    [Fact]
    public async Task Cw_starts_the_daemon_only_when_it_needs_one()
    {
        var launches = 0;
        bool Launch() { launches++; return false; }
        var nobody = new WardenClient("ComputeWarden-test-none-" + Guid.NewGuid().ToString("N"), connectTimeoutMs: 100);

        Assert.Equal(0, await Cli.Cli.RunAsync(new[] { "help" }, nobody, new StringWriter(), new StringWriter(), Launch));
        Assert.Equal(0, launches);
        Assert.Equal(1, await Cli.Cli.RunAsync(new[] { "hold" }, nobody, new StringWriter(), new StringWriter(), Launch));
        Assert.Equal(1, launches);

        await using var host = StartHost(out var pipe);
        Assert.Equal(0, await Cli.Cli.RunAsync(Array.Empty<string>(), new WardenClient(pipe), new StringWriter(), new StringWriter(), Launch));
        Assert.Equal(1, launches);
    }

    [Theory]
    [InlineData("90m", 5400)]
    [InlineData("2h", 7200)]
    [InlineData("1h30m", 5400)]
    [InlineData("1d", 86400)]
    [InlineData("45s", 45)]
    [InlineData("7d", 604800)]
    public void Cw_parses_durations(string text, int seconds)
    {
        Assert.True(Cli.Cli.TryParseDuration(text, out var parsed));
        Assert.Equal(seconds, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("8d")]
    [InlineData("7d1s")]
    [InlineData("gaming")]
    [InlineData("2x")]
    [InlineData("30")]
    [InlineData("1.5h")]
    [InlineData("2h30")]
    [InlineData("99999999999999999999h")]
    public void Cw_rejects_bad_durations(string text)
        => Assert.False(Cli.Cli.TryParseDuration(text, out _));
}
