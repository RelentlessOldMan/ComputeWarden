using System.Diagnostics;
using System.Numerics;
using ComputeWarden.Cli;
using ComputeWarden.Client;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Diagnostics;
using ComputeWarden.Core.Providers;
using ComputeWarden.Daemon;

namespace ComputeWarden.Tests;

/// <summary>cw run: claim, run, throttle the tree on a game (the hold stands in for one), release.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class RunCommandTests
{
    private sealed class NoProcesses : IProcessLister
    {
        public IReadOnlyList<RunningProcess> List() => Array.Empty<RunningProcess>();
    }

    /// <summary>cw writes from the thread pool while the test reads.</summary>
    private sealed class Log
    {
        private readonly StringWriter _inner = new();
        public TextWriter Writer { get; }
        public Log() => Writer = TextWriter.Synchronized(_inner);
        public override string ToString() { lock (Writer) return _inner.ToString(); }

        public async Task WaitFor(string text)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!ToString().Contains(text))
            {
                Assert.True(DateTime.UtcNow < deadline, $"never saw '{text}' in:\n{this}");
                await Task.Delay(25);
            }
        }
    }

    private static WardenHost StartHost(out WardenClient client)
    {
        var pipe = "ComputeWarden-test-" + Guid.NewGuid().ToString("N");
        var host = new WardenHost(WardenConfig.CreateDefault(), NullLog.Instance, new NoProcesses(), pipeName: pipe);
        host.Start();
        client = new WardenClient(pipe);
        return host;
    }

    private static Task<int> Run(WardenClient client, Log log, params string[] args)
        => Cli.Cli.RunAsync(new[] { "run" }.Concat(args).ToArray(), client, TextWriter.Null, log.Writer);

    [Fact]
    public async Task Passes_the_exit_code_through_and_releases_the_claim()
    {
        await using var host = StartHost(out var client);
        var log = new Log();

        Assert.Equal(7, await Run(client, log, "--estimate", "1m", "--owner", "T", "--", "cmd", "/c", "exit 7").Within());

        Assert.Contains("(full)", log.ToString());
        Assert.Empty((await client.StatusAsync()).Claims!);
    }

    [Fact]
    public async Task Throttles_the_running_tree_on_a_game_and_restores_it_after()
    {
        await using var host = StartHost(out var client);
        var log = new Log();
        var running = Run(client, log, "--estimate", "5m", "--owner", "T", "--", "ping", "-n", "120", "127.0.0.1");
        await log.WaitFor("Started pid");
        var pid = int.Parse(System.Text.RegularExpressions.Regex.Match(log.ToString(), @"pid (\d+)").Groups[1].Value);
        using var ping = Process.GetProcessById(pid);
        var claim = Assert.Single((await client.StatusAsync()).Claims!);
        Assert.Equal("T", claim.Owner);
        Assert.Equal(300, claim.EstimateSeconds);

        try
        {
            await client.SetManualBusyAsync("a game");
            await log.WaitFor("Throttled to 1 core at below-normal priority");
            ping.Refresh();
            Assert.Equal(ProcessPriorityClass.BelowNormal, ping.PriorityClass);
            var mask = (ulong)ping.ProcessorAffinity;
            Assert.Equal(1, BitOperations.PopCount(mask));
            if (Environment.ProcessorCount > 1) Assert.Equal(0UL, mask & 1); // never core 0

            await client.ClearManualBusyAsync();
            await log.WaitFor("Full speed again.");
            ping.Refresh();
            Assert.Equal(ProcessPriorityClass.Normal, ping.PriorityClass);
            Assert.Equal(ThrottledJob.AllCores(Environment.ProcessorCount), (ulong)ping.ProcessorAffinity);
        }
        finally
        {
            ping.Kill();
        }

        Assert.NotEqual(0, await running.Within());
        Assert.Empty((await client.StatusAsync()).Claims!);
    }

    [Fact]
    public async Task Low_holds_the_job_small_from_the_start_and_through_a_game()
    {
        await using var host = StartHost(out var client);
        var log = new Log();
        var running = Run(client, log, "--estimate", "5m", "--owner", "T", "--low", "--", "ping", "-n", "120", "127.0.0.1");
        await log.WaitFor("Started pid");
        Assert.Contains("low: one core at below-normal priority", log.ToString());
        var pid = int.Parse(System.Text.RegularExpressions.Regex.Match(log.ToString(), @"pid (\d+)").Groups[1].Value);
        using var ping = Process.GetProcessById(pid);

        void AssertSmall()
        {
            ping.Refresh();
            Assert.Equal(ProcessPriorityClass.BelowNormal, ping.PriorityClass);
            Assert.Equal(1, BitOperations.PopCount((ulong)ping.ProcessorAffinity));
        }

        try
        {
            AssertSmall();
            // A game coming and going doesn't let it go: the claim's long poll answers, and nothing changes.
            await client.SetManualBusyAsync("a game");
            await WaitForGrant(client, Core.Model.Grant.Throttled);
            await client.ClearManualBusyAsync();
            await WaitForGrant(client, Core.Model.Grant.Full);
            await Task.Delay(500);
            AssertSmall();
            Assert.DoesNotContain("Full speed again", log.ToString());
        }
        finally
        {
            ping.Kill();
        }
        await running.Within();
    }

    private static async Task WaitForGrant(WardenClient client, Core.Model.Grant grant)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Assert.Single((await client.StatusAsync()).Claims!).Grant != grant)
        {
            Assert.True(DateTime.UtcNow < deadline, $"the claim never became {grant}");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task Low_turns_off_shared_compiler_servers_for_the_command()
    {
        var client = new WardenClient("ComputeWarden-Test-" + Guid.NewGuid().ToString("N"), connectTimeoutMs: 50);
        var log = new Log();

        Assert.Equal(9, await Run(client, log, "--estimate", "1m", "--low", "--", "cmd", "/c",
            "if %UseSharedCompilation%%DOTNET_CLI_USE_MSBUILD_SERVER%==false0 exit 9").Within());
        Assert.Equal(0, await Run(client, log, "--estimate", "1m", "--", "cmd", "/c",
            "if %UseSharedCompilation%%DOTNET_CLI_USE_MSBUILD_SERVER%==false0 exit 9").Within());
    }

    [Fact]
    public void Environment_block_keeps_this_process_s_variables_under_the_overrides()
    {
        var block = ThrottledJob.EnvironmentBlock(new Dictionary<string, string> { ["UseSharedCompilation"] = "false" });
        var vars = block.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("UseSharedCompilation=false", vars);
        Assert.Contains(vars, v => v.StartsWith("PATH=", StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith("\0", block);
    }

    [Fact]
    public async Task Waits_its_turn_before_starting()
    {
        await using var host = StartHost(out var client);
        var log = new Log();
        await client.SetManualBusyAsync("a game");

        var running = Run(client, log, "--estimate", "1m", "--owner", "T", "--", "cmd", "/c", "exit 3");
        await log.WaitFor("Queued #1");
        await Task.Delay(300);
        Assert.False(running.IsCompleted);
        Assert.DoesNotContain("Started", log.ToString());

        await client.ClearManualBusyAsync();
        Assert.Equal(3, await running.Within());
        Assert.Contains("(full)", log.ToString());
    }

    [Fact]
    public async Task Runs_unclaimed_when_the_daemon_is_unreachable()
    {
        var client = new WardenClient("ComputeWarden-Test-" + Guid.NewGuid().ToString("N"), connectTimeoutMs: 50);
        var log = new Log();

        Assert.Equal(5, await Run(client, log, "--estimate", "1m", "--", "cmd", "/c", "exit 5").Within());
        Assert.Contains("running without a claim", log.ToString());
        Assert.Contains("(unclaimed)", log.ToString());
    }

    [Fact]
    public async Task Rejects_bad_arguments_and_missing_commands()
    {
        var client = new WardenClient("ComputeWarden-Test-" + Guid.NewGuid().ToString("N"), connectTimeoutMs: 50);
        var log = new Log();

        Assert.Equal(2, await Run(client, log, "--", "cmd", "/c", "exit 0")); // no estimate
        Assert.Contains("--estimate is required", log.ToString());
        Assert.Equal(2, await Run(client, log, "--estimate", "1m"));
        Assert.Equal(2, await Run(client, log, "--estimate", "soon", "--", "cmd"));
        Assert.Equal(2, await Run(client, log, "--estimate", "1m", "--fast", "--", "cmd"));
        Assert.Equal(RunCommand.CouldNotStart, await Run(client, log, "--estimate", "1m", "--", "no-such-program-" + Guid.NewGuid().ToString("N")));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData(@"a\\b", @"a\\b")]
    public void Quotes_arguments_so_they_read_back_unchanged(string arg, string quoted)
        => Assert.Equal(quoted, RunCommand.Quote(arg));

    [Fact]
    public void Finds_programs_on_the_path_and_runs_batch_files_through_cmd()
    {
        Assert.True(RunCommand.TryBuildCommandLine(new[] { "cmd", "/c", "exit 1" }, out var line));
        Assert.EndsWith("cmd.exe /c \"exit 1\"", line, StringComparison.OrdinalIgnoreCase);

        var dir = Directory.CreateTempSubdirectory("cw-run-");
        try
        {
            var script = Path.Combine(dir.FullName, "tool.cmd");
            File.WriteAllText(script, "@exit /b 0");
            Assert.True(RunCommand.TryBuildCommandLine(new[] { Path.Combine(dir.FullName, "tool"), "x y" }, out line));
            Assert.Contains("cmd.exe /d /s /c \"", line, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith($"{RunCommand.Quote(script)} \"x y\"\"", line, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Picks_cores_off_core_zero()
    {
        var random = new Random(1);
        for (var i = 0; i < 50; i++)
        {
            var mask = ThrottledJob.PickCores(2, 8, random);
            Assert.Equal(2, BitOperations.PopCount(mask));
            Assert.Equal(0UL, mask & 1);
            Assert.True(mask < 1UL << 8);
        }
        Assert.Equal(1UL, ThrottledJob.PickCores(1, 1, random));
        Assert.Equal(7, BitOperations.PopCount(ThrottledJob.PickCores(99, 8, random)));
        Assert.Equal(ulong.MaxValue, ThrottledJob.AllCores(64));
    }
}
