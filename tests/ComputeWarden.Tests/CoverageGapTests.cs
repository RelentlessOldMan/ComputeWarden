using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using ComputeWarden.Client;
using ComputeWarden.Core;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Diagnostics;
using ComputeWarden.Core.Ipc;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;
using ComputeWarden.Core.Time;
using ComputeWarden.Daemon;
using ComputeWarden.Mcp;

namespace ComputeWarden.Tests;

/// <summary>Input limits are the daemon's guard against oversized or malformed client data (spec §26).</summary>
public class InputValidationTests
{
    private static readonly WardenLimits Limits = new();

    public static TheoryData<string, string, Dictionary<string, string>?, string> Invalid() => new()
    {
        { "  ", "w", null, "owner is required" },
        { new string('o', Limits.MaxOwnerLength + 1), "w", null, "owner exceeds" },
        { "A", new string('d', Limits.MaxDescriptionLength + 1), null, "description exceeds" },
        { "A", "w", Enumerable.Range(0, Limits.MaxMetadataEntries + 1).ToDictionary(i => $"k{i}", i => "v"), "metadata exceeds" },
        { "A", "w", new() { [new string('k', Limits.MaxMetadataKeyLength + 1)] = "v" }, "metadata key exceeds" },
        { "A", "w", new() { ["k"] = new string('v', Limits.MaxMetadataValueLength + 1) }, "metadata value exceeds" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_acquire_is_refused_counted_and_reserves_nothing(
        string owner, string description, Dictionary<string, string>? metadata, string expectedReason)
    {
        var warden = TestFactory.NewWarden(new FakeClock());

        var result = warden.Acquire(owner, description, metadata: metadata);

        Assert.False(result.Acquired);
        Assert.Contains(expectedReason, result.Reason);
        Assert.Equal(1, warden.GetStats().FailedAcquisitions);
        Assert.True(warden.CanRun());
    }

    [Fact]
    public void Values_exactly_at_the_limits_are_accepted()
    {
        var warden = TestFactory.NewWarden(new FakeClock());
        var metadata = Enumerable.Range(0, Limits.MaxMetadataEntries)
            .ToDictionary(i => i.ToString().PadLeft(Limits.MaxMetadataKeyLength, 'k'), _ => new string('v', Limits.MaxMetadataValueLength));

        var result = warden.Acquire(
            new string('o', Limits.MaxOwnerLength), new string('d', Limits.MaxDescriptionLength), metadata: metadata);

        Assert.True(result.Acquired);
    }

    [Fact]
    public void Over_long_manual_reason_is_a_clean_ipc_error()
    {
        var d = new RequestDispatcher(TestFactory.NewWarden(new FakeClock()));

        var res = JsonNode.Parse(d.Handle($$"""{"v":1,"op":"SET_MANUAL_BUSY","reason":"{{new string('r', Limits.MaxReasonLength + 1)}}"}"""))!;

        Assert.False((bool)res["ok"]!);
        Assert.Contains("reason exceeds", (string)res["error"]!);
    }
}

public class DispatcherErrorPathTests
{
    private sealed class ThrowingProvider : IBlockerProvider
    {
        public string Name => "Throwing";
        public ProviderResult GetBlockers() => throw new InvalidOperationException("provider exploded");
    }

    private static JsonNode Handle(RequestDispatcher d, string line) => JsonNode.Parse(d.Handle(line))!;

    [Theory]
    [InlineData("""{"v":1}""", "Missing 'op'")]
    [InlineData("""{"v":1,"op":"  "}""", "Missing 'op'")]
    [InlineData("null", "Missing 'op'")]
    [InlineData("""{"v":1,"op":"SET_MANUAL_BUSY"}""", "'reason' is required")]
    public void Malformed_requests_get_specific_errors(string line, string expected)
    {
        var res = Handle(new RequestDispatcher(TestFactory.NewWarden(new FakeClock())), line);

        Assert.False((bool)res["ok"]!);
        Assert.Equal(expected, (string)res["error"]!);
    }

    [Fact]
    public void Clearing_manual_blocker_is_refused_when_disabled()
    {
        var warden = TestFactory.NewWarden(new FakeClock());
        warden.SetManualBusy("maintenance");
        var res = Handle(new RequestDispatcher(warden, allowManualBlocker: false), """{"v":1,"op":"CLEAR_MANUAL_BUSY"}""");

        Assert.False((bool)res["ok"]!);
        Assert.False(warden.CanRun()); // still held
    }

    [Fact]
    public void Faulting_provider_yields_a_structured_internal_error_and_the_dispatcher_keeps_serving()
    {
        var clock = new FakeClock();
        var faulty = new Warden(clock, new LeaseOptions(), new WardenLimits(), new ManualBlockerProvider(clock), new[] { new ThrowingProvider() });
        var d = new RequestDispatcher(faulty);

        var res = Handle(d, """{"v":1,"op":"STATUS"}""");
        var again = Handle(d, """{"v":1,"op":"CAN_RUN"}""");

        Assert.False((bool)res["ok"]!);
        Assert.Equal("Internal error: provider exploded", (string)res["error"]!);
        Assert.False((bool)again["ok"]!); // same fault, still a response — not a crash
    }

    [Fact]
    public void Sweep_expires_reservations_without_any_client_traffic()
    {
        var clock = new FakeClock();
        var log = new CapturingLog();
        var warden = new Warden(clock, new LeaseOptions(), new WardenLimits(), new ManualBlockerProvider(clock), log: log);
        warden.Acquire("A", "work", 60);

        clock.AdvanceSeconds(61);
        warden.Sweep();

        Assert.True(log.Contains("Reservation expired (owner did not release): A"));
        Assert.Equal(1, warden.GetStats().ExpiredReservations);
    }
}

/// <summary>
/// The client must interoperate with daemons older and newer than itself (they roll out
/// independently), so these feed it hand-written responses from a fake daemon.
/// </summary>
public class ClientCompatibilityTests
{
    private static async Task<T> AgainstFakeDaemon<T>(string response, Func<WardenClient, Task<T>> call)
    {
        var pipeName = "ComputeWarden-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var enc = new UTF8Encoding(false);
            using var reader = new StreamReader(server, enc, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(server, enc, 1024, leaveOpen: true) { AutoFlush = true };
            await reader.ReadLineAsync();
            await writer.WriteLineAsync(response);
        });

        var result = await call(new WardenClient(pipeName));
        await serve;
        return result;
    }

    [Fact]
    public async Task Blockers_from_a_daemon_without_resources_hold_the_whole_machine()
    {
        var status = await AgainstFakeDaemon(
            """{"v":1,"ok":true,"state":"BUSY","canRunIntensive":false,"blockers":[{"id":"r1","type":"RESERVATION","source":"x","description":"old"}]}""",
            c => c.StatusAsync());

        Assert.Equal(ResourceSet.All, Assert.Single(status.Blockers).Resources);
        Assert.Equal(ResourceSet.None, status.BusyResources); // field absent, not guessed
    }

    [Fact]
    public async Task Unknown_resource_names_from_a_newer_daemon_are_ignored_not_fatal()
    {
        var status = await AgainstFakeDaemon(
            """{"v":1,"ok":true,"state":"BUSY","busyResources":["gpu","npu"],"blockers":[{"id":"r1","type":"PROCESS","resources":["gpu","npu"]}]}""",
            c => c.StatusAsync());

        Assert.Equal(ResourceSet.Gpu, status.BusyResources);
        Assert.Equal(ResourceSet.Gpu, Assert.Single(status.Blockers).Resources);
        Assert.Equal(BlockerType.Process, status.Blockers[0].Type);
    }

    [Fact]
    public async Task Daemon_error_envelope_becomes_a_client_exception_with_its_message()
    {
        var ex = await Assert.ThrowsAsync<WardenClientException>(() => AgainstFakeDaemon(
            """{"v":1,"ok":false,"error":"Unsupported protocol version 9"}""", c => c.StatusAsync()));

        Assert.Equal("Daemon error: Unsupported protocol version 9", ex.Message);
    }

    [Fact]
    public async Task Unparseable_response_is_reported_with_the_parse_failure_as_cause()
    {
        var ex = await Assert.ThrowsAsync<WardenClientException>(() => AgainstFakeDaemon(
            "this is not json", c => c.StatusAsync()));

        Assert.Contains("unparseable", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task Acquire_parses_granted_resources_and_expiry()
    {
        var result = await AgainstFakeDaemon(
            """{"v":1,"ok":true,"acquired":true,"reservationId":"abc","expiresAt":"2026-10-06T12:00:00.0000000+00:00","machineState":"BUSY","resources":["cpu","disk"],"blockers":[]}""",
            c => c.AcquireAsync("A", "w", resources: new[] { "cpu", "disk" }));

        Assert.True(result.Acquired);
        Assert.Equal(ResourceSet.Cpu | ResourceSet.Disk, result.Resources);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero), result.ExpiresAt);
    }
}

public class DaemonLauncherTests
{
    // COMPUTEWARDEN_DAEMON is only read by DaemonLauncher, so no collection is needed.
    private static (string?, string[]) ResolveWith(string? overridePath)
    {
        Environment.SetEnvironmentVariable("COMPUTEWARDEN_DAEMON", overridePath);
        try { return DaemonLauncher.Resolve(); }
        finally { Environment.SetEnvironmentVariable("COMPUTEWARDEN_DAEMON", null); }
    }

    [Fact]
    public void Override_dll_runs_through_the_dotnet_host()
    {
        var (file, args) = ResolveWith(@"C:\x\ComputeWarden.Daemon.dll");

        Assert.Equal("dotnet", file);
        Assert.Equal(new[] { @"C:\x\ComputeWarden.Daemon.dll" }, args);
    }

    [Fact]
    public void Override_pointing_at_a_missing_exe_resolves_to_nothing()
        => Assert.Null(ResolveWith(@"C:\definitely\not\here\ComputeWarden.Daemon.exe").Item1);

    [Fact]
    public void Override_pointing_at_an_existing_exe_is_used_directly()
    {
        var path = Path.Combine(Path.GetTempPath(), "cw-daemon-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(path, "");
        try
        {
            var (file, args) = ResolveWith(path);
            Assert.Equal(path, file);
            Assert.Empty(args);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Without_override_falls_back_to_the_daemon_built_alongside()
    {
        // The test output folder contains ComputeWarden.Daemon.dll/.exe via project references.
        var (file, _) = ResolveWith(null);

        Assert.NotNull(file);
    }
}

public class BackgroundLoopTests
{
    private sealed class SwitchableLister : IProcessLister
    {
        public volatile bool Fail;
        public volatile RunningProcess[] Processes = Array.Empty<RunningProcess>();
        public IReadOnlyList<RunningProcess> List()
            => Fail ? throw new InvalidOperationException("access denied") : Processes;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(condition(), "condition not met within 5s");
    }

    [Fact]
    public async Task Monitor_logs_confidence_loss_recovery_and_blocker_transitions()
    {
        var lister = new SwitchableLister { Fail = true };
        var log = new CapturingLog();
        var provider = new ProcessBlockerProvider(
            lister, new[] { new ProcessRule { Name = "Game", Executable = "Game.exe" } },
            new ProcessDetectionOptions { AvailableAfterMs = 0 }, SystemClock.Instance);
        await using var monitor = new ProcessMonitor(provider, pollIntervalMs: 250, log);

        monitor.Start();
        Assert.True(log.Contains("machine state is UNKNOWN"));

        lister.Processes = new[] { new RunningProcess(1, "Game") };
        lister.Fail = false;
        await WaitFor(() => log.Contains("Process blocker detected: Game"));
        Assert.True(log.Contains("state confidence restored"));

        lister.Processes = Array.Empty<RunningProcess>();
        await WaitFor(() => log.Contains("Process blocker cleared: Game"));
    }

    [Fact]
    public async Task Sweeper_expires_leases_on_its_own_schedule()
    {
        var clock = new FakeClock();
        var log = new CapturingLog();
        var warden = new Warden(clock, new LeaseOptions(), new WardenLimits(), new ManualBlockerProvider(clock), log: log);
        warden.Acquire("A", "work", 60);
        clock.AdvanceSeconds(61);

        await using var sweeper = new LeaseSweeper(warden, TimeSpan.FromMilliseconds(50), NullLog.Instance);
        sweeper.Start();

        await WaitFor(() => log.Contains("Reservation expired"));
    }
}

[Collection("config-env")]
public class ConfigWatcherTests
{
    [Fact]
    public async Task Saving_the_config_file_triggers_a_debounced_reload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cw-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.yaml");
        File.WriteAllText(path, "process_rules:\n  - name: A\n    executable: A.exe\n");
        Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", path);
        try
        {
            var log = new CapturingLog();
            await using (var host = new WardenHost(
                WardenConfig.CreateDefault(), log, new EmptyLister(), pipeName: "ComputeWarden-test-" + Guid.NewGuid().ToString("N"), configPath: path))
            {
                host.Start();
                Assert.True(log.Contains("Watching"));

                File.WriteAllText(path, "process_rules:\n  - name: A\n    executable: A.exe\n  - name: B\n    executable: B.exe\n");

                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!log.Contains("2 process rule(s) active") && DateTime.UtcNow < deadline)
                    await Task.Delay(50);
                Assert.True(log.Contains("2 process rule(s) active"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", null);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_config_directory_disables_hot_reload_without_failing_startup()
    {
        var log = new CapturingLog();
        var path = Path.Combine(Path.GetTempPath(), "cw-nodir-" + Guid.NewGuid().ToString("N"), "config.yaml");
        await using var host = new WardenHost(
            WardenConfig.CreateDefault(), log, new EmptyLister(), pipeName: "ComputeWarden-test-" + Guid.NewGuid().ToString("N"), configPath: path);

        host.Start();

        Assert.True(log.Contains("hot-reload disabled"));
    }

    [Fact]
    public void Locked_config_file_falls_back_to_defaults_and_is_flagged_as_an_error()
    {
        var path = Path.Combine(Path.GetTempPath(), "cw-locked-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, "process_rules: []\n");
        Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", path);
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = ConfigLoader.Load();

                Assert.True(result.IsError);
                Assert.Contains("Could not read", result.Message);
                Assert.NotEmpty(result.Config.ProcessRules); // defaults
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", null);
            File.Delete(path);
        }
    }

    private sealed class EmptyLister : IProcessLister
    {
        public IReadOnlyList<RunningProcess> List() => Array.Empty<RunningProcess>();
    }
}
