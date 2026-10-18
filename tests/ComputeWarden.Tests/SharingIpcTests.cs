using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ComputeWarden.Cli;
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

/// <summary>The sharing system over the wire: dispatcher JSON, long-poll renew, client, MCP tools, and cw.</summary>
[Collection("mcp-gateway")] // the MCP tool tests mutate the static WardenGateway
public class SharingIpcTests
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
    public void Acquire_with_kind_returns_advice()
    {
        var (d, _) = New();
        Handle(d, new { v = 1, op = "SET_MODE", mode = "steady", reason = "gaming" });

        var res = Handle(d, new { v = 1, op = "ACQUIRE", owner = "A", description = "build", kind = "shared", estimateSeconds = 60 });

        Assert.True((bool)res["acquired"]!);
        Assert.Equal("shared", (string)res["kind"]!);
        var advice = res["advice"]!;
        Assert.Equal("throttled", (string)advice["grant"]!);
        Assert.Equal(1, (int)advice["throttle"]!["maxCores"]!);
        Assert.Contains("steady mode: gaming", (string)advice["reason"]!);
        Assert.Equal(new[] { "cpu", "ram", "disk" }, advice["contended"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.Equal(new[] { "cpu", "ram", "disk" }, res["resources"]!.AsArray().Select(n => (string)n!).ToArray());
    }

    [Fact]
    public void Legacy_acquire_response_has_no_advice()
    {
        var (d, _) = New();

        var res = Handle(d, new { v = 1, op = "ACQUIRE", owner = "A", description = "x" });

        Assert.True((bool)res["acquired"]!);
        Assert.Null(res["advice"]);
        Assert.Null(res["kind"]);
        Assert.Equal(5, res["resources"]!.AsArray().Count);
    }

    [Fact]
    public void Queued_claim_reports_its_place_in_line()
    {
        var (d, _) = New();
        Handle(d, new { v = 1, op = "ACQUIRE", owner = "A", description = "x", kind = "exclusive" });

        var res = Handle(d, new { v = 1, op = "ACQUIRE", owner = "B", description = "y", kind = "exclusive", whenBusy = "wait" });

        var queue = res["advice"]!["queue"]!;
        Assert.Equal("queued", (string)res["advice"]!["grant"]!);
        Assert.Equal(1, (int)queue["position"]!);
    }

    [Fact]
    public void Status_reports_the_daemon_version()
    {
        var (d, _) = New();

        var res = Handle(d, new { v = 1, op = "STATUS" });

        Assert.Equal(IpcProtocol.ProductVersion, (string)res["daemonVersion"]!);
        Assert.Matches(@"^\d+\.\d+\.\d+", IpcProtocol.ProductVersion);
    }

    [Fact]
    public void Blank_resource_names_give_the_kinds_default()
    {
        var (d, _) = New();

        var blank = Handle(d, new { v = 1, op = "ACQUIRE", owner = "A", description = "x", kind = "shared", resources = new[] { " " } });
        var nulls = Handle(d, new { v = 1, op = "ACQUIRE", owner = "B", description = "y", kind = "shared", resources = new string?[] { null } });

        Assert.Equal(new[] { "cpu", "ram", "disk" }, blank["resources"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.Equal(new[] { "cpu", "ram", "disk" }, nulls["resources"]!.AsArray().Select(n => (string)n!).ToArray());
    }

    [Fact]
    public void Status_lists_mode_and_claims()
    {
        var (d, _) = New();
        Handle(d, new { v = 1, op = "SET_MODE", mode = "hold", durationSeconds = 600, resources = new[] { "gpu" } });
        Handle(d, new { v = 1, op = "ACQUIRE", owner = "A", description = "x", kind = "shared" });

        var res = Handle(d, new { v = 1, op = "STATUS" });

        Assert.Equal("hold", (string)res["mode"]!["mode"]!);
        Assert.Equal("gpu", (string)res["mode"]!["resources"]![0]!);
        Assert.NotNull((string?)res["mode"]!["expiresAt"]);
        var claim = Assert.Single(res["claims"]!.AsArray())!;
        Assert.Equal("A", (string)claim["owner"]!);
        Assert.Equal("full", (string)claim["grant"]!);
    }

    [Theory]
    [InlineData("{\"v\":1,\"op\":\"SET_MODE\",\"mode\":\"turbo\"}", "Unknown mode")]
    [InlineData("{\"v\":1,\"op\":\"ACQUIRE\",\"owner\":\"A\",\"kind\":\"greedy\"}", "Unknown kind")]
    [InlineData("{\"v\":1,\"op\":\"ACQUIRE\",\"owner\":\"A\",\"kind\":\"exclusive\",\"whenBusy\":\"cry\"}", "Unknown whenBusy")]
    [InlineData("{\"v\":1,\"op\":\"SET_MODE\",\"mode\":\"hold\",\"durationSeconds\":0}", "durationSeconds")]
    [InlineData("{\"v\":1,\"op\":\"ACQUIRE\",\"owner\":\"A\",\"kind\":\"shared\",\"estimateSeconds\":\"ten\"}", "'estimateSeconds' has the wrong type")]
    [InlineData("{\"v\":1,\"op\":\"ACQUIRE\",\"owner\":\"A\",\"kind\":\"shared\",\"resources\":\"cpu\"}", "'resources' has the wrong type")]
    [InlineData("{\"v\":1,\"op\":\"ACQUIRE\",\"owner\":\"A\",\"kind\":\"shared\",\"resources\":[\"cpuu\"]}", "Unknown resource 'cpuu'")]
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
        w.SetMode(UserMode.Steady);
        var id = w.Acquire(new ClaimRequest("A", "build", ClaimKind.Shared)).ReservationId!;

        var watch = Stopwatch.StartNew();
        var pending = d.HandleAsync(JsonSerializer.Serialize(new { v = 1, op = "RENEW", reservationId = id, waitSeconds = 30 }));
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);
        w.SetMode(UserMode.Auto);
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
        w.SetMode(UserMode.Steady);
        var id = w.Acquire(new ClaimRequest("A", "build", ClaimKind.Shared)).ReservationId!;

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
    public async Task Client_claims_modes_and_long_poll_round_trip()
    {
        await using var host = StartHost(out var pipe);
        var client = new WardenClient(pipe);

        var mode = await client.SetModeAsync(UserMode.Steady, durationSeconds: 3600, reason: "gaming");
        Assert.Equal(UserMode.Steady, mode.Mode);
        Assert.NotNull(mode.ExpiresAt);

        var claim = await client.AcquireAsync(new ClaimRequest("A", "build", ClaimKind.Shared, LeaseSeconds: 30, EstimateSeconds: 120, Cores: 8, RamGb: 4.5)).Within();
        Assert.Equal(ClaimKind.Shared, claim.Kind);
        Assert.Equal(Grant.Throttled, claim.Advice!.Grant);
        Assert.Equal(2, claim.Advice.Throttle!.MaxRamGb);
        Assert.NotNull(claim.Advice.How);

        var status = await client.StatusAsync().Within();
        Assert.Equal("gaming", status.Mode!.Reason);
        var info = Assert.Single(status.Claims!);
        Assert.Equal(8, info.Cores);
        Assert.Equal(4.5, info.RamGb);
        Assert.Equal(Grant.Throttled, info.Grant);

        var leaseEnd = info.ExpiresAt;
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
        await client.SetModeAsync(UserMode.Auto).Within();
        var renew = await renewing.Within();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"answered after {watch.Elapsed}");
        Assert.True(renew.Renewed);
        Assert.Equal(Grant.Full, renew.Advice!.Grant); // answered on the change, not after 30s

        var queued = await client.AcquireAsync(new ClaimRequest("B", "x", ClaimKind.Exclusive, ResourceSet.Gpu));
        var waiting = await client.AcquireAsync(new ClaimRequest("C", "y", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait, UserPriority: "user said so"));
        Assert.Equal(Grant.Full, queued.Advice!.Grant);
        Assert.Equal(Grant.Queued, waiting.Advice!.Grant);
        Assert.Equal(1, waiting.Advice.Queue!.Position);
        Assert.Empty(waiting.Advice.Queue.Ahead); // B holds; it isn't ahead in line
        Assert.Contains("B holds gpu", waiting.Advice.Reason);
    }

    [Fact]
    public async Task Waiting_renew_stops_when_the_client_hangs_up()
    {
        await using var host = StartHost(out var pipe);
        var client = new WardenClient(pipe);
        await client.SetModeAsync(UserMode.Steady);
        var claim = await client.AcquireAsync(new ClaimRequest("A", "build", ClaimKind.Shared, LeaseSeconds: 60));

        using var cts = new CancellationTokenSource();
        var renewing = client.RenewAsync(claim.ReservationId!, leaseSeconds: 3600, waitSeconds: 30, ct: cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renewing);
        await Task.Delay(1000);

        // Ending the wait now would renew for an hour, if the daemon were still waiting.
        await client.SetModeAsync(UserMode.Auto);
        await Task.Delay(2000);

        var info = Assert.Single((await client.StatusAsync()).Claims!);
        Assert.True(info.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(10), $"renewed for nobody: expires {info.ExpiresAt:O}");
    }

    // ---- MCP tools ------------------------------------------------------

    [Fact]
    public async Task Mcp_tools_report_advice_mode_and_claims()
    {
        await using var host = StartHost(out var pipe);
        WardenGateway.Configure(new WardenClient(pipe), autoSpawn: false);

        var set = JsonNode.Parse(await ComputeWardenTools.SetMode("steady", 7200, "gaming"))!;
        Assert.Equal("steady", (string)set["mode"]!["mode"]!);
        Assert.True((int)set["mode"]!["remaining_seconds"]! > 7000);

        var acq = JsonNode.Parse(await ComputeWardenTools.Acquire("Claude", "dotnet test", kind: "shared", estimateSeconds: 90))!;
        Assert.True((bool)acq["acquired"]!);
        Assert.Equal("throttled", (string)acq["advice"]!["grant"]!);
        Assert.Equal(1, (int)acq["advice"]!["throttle"]!["max_cores"]!);
        Assert.Contains("small", (string)acq["advice"]!["next"]!);

        var typo = JsonNode.Parse(await ComputeWardenTools.Acquire("Claude", "x", resources: new[] { "cpuu" }, kind: "shared"))!;
        Assert.StartsWith("Unknown resource 'cpuu'", (string)typo["error"]!);

        var status = JsonNode.Parse(await ComputeWardenTools.Status())!;
        Assert.Equal("steady", (string)status["mode"]!["mode"]!);
        Assert.Equal("Claude", (string)status["claims"]![0]!["owner"]!);

        await ComputeWardenTools.SetMode("auto");
        var renew = JsonNode.Parse(await ComputeWardenTools.Renew((string)acq["reservation_id"]!, waitSeconds: 5))!;
        Assert.True((bool)renew["renewed"]!);
        Assert.Equal("full", (string)renew["advice"]!["grant"]!); // the game-over / mode-over signal

        var bad = JsonNode.Parse(await ComputeWardenTools.Acquire("Claude", "x", kind: "greedy"))!;
        Assert.Contains("Unknown kind", (string)bad["error"]!);
    }

    [Fact]
    public async Task Mcp_acquire_without_kind_is_the_legacy_shape()
    {
        await using var host = StartHost(out var pipe);
        WardenGateway.Configure(new WardenClient(pipe), autoSpawn: false);

        var acq = JsonNode.Parse(await ComputeWardenTools.Acquire("Claude", "index"))!;

        Assert.True((bool)acq["acquired"]!);
        Assert.Null(acq["advice"]);
        Assert.Null(acq["kind"]);
    }

    // ---- cw -------------------------------------------------------------

    [Fact]
    public async Task Cw_sets_and_shows_the_mode_and_status()
    {
        await using var host = StartHost(out var pipe);
        var client = new WardenClient(pipe);
        var output = new StringWriter();
        var errors = new StringWriter();

        Assert.Equal(0, await Cli.Cli.RunAsync(new[] { "mode", "steady", "2h", "gaming", "tonight" }, client, output, errors));
        Assert.Matches(@"Mode set: steady, (2h|1h59m) left: gaming tonight", output.ToString()); // a second may pass

        await client.AcquireAsync(new ClaimRequest("Codex", "full test suite", ClaimKind.Exclusive, ResourceSet.Cpu, WhenBusy.Wait));
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await Cli.Cli.RunAsync(Array.Empty<string>(), client, output, errors));
        var text = output.ToString();
        Assert.Contains("Mode:    steady", text);
        Assert.Contains("queued #1", text);
        Assert.Contains("Codex: full test suite [cpu]", text);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await Cli.Cli.RunAsync(new[] { "mode", "auto" }, client, output, errors));
        Assert.Contains("auto (normal rules)", output.ToString());
        Assert.Equal(UserMode.Auto, (await client.StatusAsync()).Mode!.Mode);
    }

    [Fact]
    public async Task Cw_reports_bad_input_and_a_missing_daemon()
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var nobody = new WardenClient("ComputeWarden-test-none-" + Guid.NewGuid().ToString("N"), connectTimeoutMs: 100);

        Assert.Equal(2, await Cli.Cli.RunAsync(new[] { "mode", "turbo" }, nobody, output, errors));
        Assert.Contains("Unknown mode 'turbo'", errors.ToString());
        Assert.Equal(2, await Cli.Cli.RunAsync(new[] { "frobnicate" }, nobody, output, errors));
        Assert.Equal(1, await Cli.Cli.RunAsync(new[] { "status" }, nobody, output, errors));
        Assert.Contains("Could not reach", errors.ToString());
    }

    [Fact]
    public async Task Cw_wait_returns_when_the_claim_may_start()
    {
        await using var host = StartHost(out var pipe);
        var client = new WardenClient(pipe);
        var holder = await client.AcquireAsync(new ClaimRequest("H", "render", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait));
        var queued = await client.AcquireAsync(new ClaimRequest("Q", "train", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait));
        Assert.Equal(Grant.Queued, queued.Advice!.Grant);
        var output = new StringWriter();
        var errors = new StringWriter();

        var waiting = Cli.Cli.RunAsync(new[] { "wait", queued.ReservationId! }, client, output, errors);
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);
        await client.ReleaseAsync(holder.ReservationId!);

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
        await client.AcquireAsync(new ClaimRequest("H", "render", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait));
        var queued = await client.AcquireAsync(new ClaimRequest("Q", "train", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait));
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
        var timer = System.Diagnostics.Stopwatch.StartNew();

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
        Assert.Equal(1, await Cli.Cli.RunAsync(new[] { "mode", "steady" }, nobody, new StringWriter(), new StringWriter(), Launch));
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

    [Fact]
    public void Cw_mode_arguments_parse()
    {
        Assert.True(Cli.Cli.TryParseMode(new[] { "hold", "for", "30m", "recording", "--resources", "cpu,gpu" }, out var r, out _));
        Assert.Equal(UserMode.Hold, r.Mode);
        Assert.Equal(1800, r.DurationSeconds);
        Assert.Equal("recording", r.Reason);
        Assert.Equal(new[] { "cpu", "gpu" }, r.Resources);

        Assert.True(Cli.Cli.TryParseMode(new[] { "open" }, out var open, out _));
        Assert.Null(open.DurationSeconds);
        Assert.Null(open.Reason);

        Assert.False(Cli.Cli.TryParseMode(new[] { "open", "10d" }, out _, out _));
        Assert.False(Cli.Cli.TryParseMode(new[] { "steady", "1.5h" }, out _, out _));
        Assert.False(Cli.Cli.TryParseMode(new[] { "steady", "for", "8d" }, out _, out _));
        Assert.False(Cli.Cli.TryParseMode(new[] { "hold", "2", "players" }, out _, out _));
        Assert.True(Cli.Cli.TryParseMode(new[] { "steady", "two", "players" }, out var words, out _));
        Assert.Null(words.DurationSeconds);
        Assert.Equal("two players", words.Reason);
        Assert.False(Cli.Cli.TryParseMode(new[] { "auto", "2h" }, out _, out var error));
        Assert.Contains("auto takes no duration", error);
        Assert.False(Cli.Cli.TryParseMode(new[] { "steady", "--resources", "cpu,bogus" }, out _, out _));
    }

    [Theory]
    [InlineData("steady gaming 2h")]
    [InlineData("steady gaming for 2h")]
    [InlineData("steady 2h gaming")]
    [InlineData("steady for 2h gaming")]
    public void Cw_mode_takes_the_duration_anywhere(string line)
    {
        Assert.True(Cli.Cli.TryParseMode(line.Split(' '), out var r, out var error), error);
        Assert.Equal(7200, r.DurationSeconds);
        Assert.Equal("gaming", r.Reason);
    }

    [Fact]
    public void Cw_mode_keeps_for_in_a_reason_and_refuses_two_durations()
    {
        Assert.True(Cli.Cli.TryParseMode(new[] { "hold", "waiting", "for", "friends" }, out var r, out _));
        Assert.Equal("waiting for friends", r.Reason);
        Assert.Null(r.DurationSeconds);
        Assert.False(Cli.Cli.TryParseMode(new[] { "steady", "2h", "gaming", "3h" }, out _, out var error));
        Assert.Contains("Two durations", error);
        Assert.False(Cli.Cli.TryParseMode(new[] { "steady", "gaming", "10d" }, out _, out _));
    }

    [Theory]
    [InlineData("normal", UserMode.Auto)]
    [InlineData("slow", UserMode.Steady)]
    [InlineData("full", UserMode.Open)]
    [InlineData("stop", UserMode.Hold)]
    [InlineData("STEADY", UserMode.Steady)]
    public void Cw_mode_aliases(string word, UserMode expected)
    {
        Assert.True(Cli.Cli.TryParseMode(new[] { word }, out var r, out var error), error);
        Assert.Equal(expected, r.Mode);
    }

    [Fact]
    public void Cw_mode_resources_short_flag_and_empty_list()
    {
        Assert.True(Cli.Cli.TryParseMode(new[] { "hold", "-r", "gpu, disk" }, out var r, out _));
        Assert.Equal(new[] { "gpu", "disk" }, r.Resources);

        Assert.True(Cli.Cli.TryParseMode(new[] { "hold", "--resources", "" }, out var empty, out _));
        Assert.Empty(empty.Resources!); // nothing named: the daemon applies the mode to everything

        Assert.False(Cli.Cli.TryParseMode(new[] { "hold", "-r" }, out _, out var missing));
        Assert.Contains("--resources needs a list", missing);
        Assert.False(Cli.Cli.TryParseMode(new[] { "auto", "--resources", "cpu" }, out _, out var auto));
        Assert.Contains("auto takes no duration or resources", auto);
    }

    [Fact]
    public async Task Cw_shows_mode_resources_and_user_priority()
    {
        await using var host = StartHost(out var pipe);
        var client = new WardenClient(pipe);
        var output = new StringWriter();

        Assert.Equal(0, await Cli.Cli.RunAsync(new[] { "mode", "hold", "-r", "gpu" }, client, output, new StringWriter()).Within());
        Assert.Contains("Mode set: hold [gpu]", output.ToString());

        await client.AcquireAsync(new ClaimRequest("A", "train", ClaimKind.Exclusive, ResourceSet.Cpu, WhenBusy.Wait, UserPriority: "user: now")).Within();
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await Cli.Cli.RunAsync(Array.Empty<string>(), client, output, new StringWriter()).Within());
        Assert.Contains("user priority: user: now", output.ToString());
    }
}
