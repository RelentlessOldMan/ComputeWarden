using ComputeWarden.Core.Config;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;
using ComputeWarden.Daemon;

namespace ComputeWarden.Tests;

// Shares the process-global COMPUTEWARDEN_CONFIG env var with ConfigLoaderTests; same collection
// keeps them from running concurrently and clobbering each other's env.
[Collection("config-env")]
public class HotReloadTests
{
    private sealed class EmptyLister : IProcessLister
    {
        public IReadOnlyList<RunningProcess> List() => Array.Empty<RunningProcess>();
    }

    private sealed class OneProcess : IProcessLister
    {
        public IReadOnlyList<RunningProcess> List() => new[] { new RunningProcess(4242, "X") };
    }

    private static string TestPipe() => "ComputeWarden-test-" + Guid.NewGuid().ToString("N");

    private static string WriteTempConfig(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), "cw-cfg-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    [Fact]
    public async Task ReloadConfig_applies_new_rules_and_logs()
    {
        var path = WriteTempConfig("process_rules:\n  - name: Game\n    executable: Game.exe\n");
        Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", path);
        try
        {
            var log = new CapturingLog();
            await using var host = new WardenHost(
                WardenConfig.CreateDefault(), log, new EmptyLister(), pipeName: TestPipe(), configPath: path);

            host.ReloadConfig();

            Assert.True(log.Contains("Configuration reloaded"));
            Assert.True(log.Contains("1 process rule"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", null);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReloadConfig_reads_the_hosts_own_path_not_the_env_var()
    {
        var path = WriteTempConfig("process_rules:\n  - name: Game\n    executable: Game.exe\n");
        var other = WriteTempConfig("process_rules: []\n");
        Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", other);
        try
        {
            var log = new CapturingLog();
            await using var host = new WardenHost(
                WardenConfig.CreateDefault(), log, new EmptyLister(), pipeName: TestPipe(), configPath: path);

            host.ReloadConfig();

            Assert.True(log.Contains("1 process rule"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", null);
            File.Delete(path);
            File.Delete(other);
        }
    }

    [Fact]
    public async Task ReloadConfig_keeps_current_rules_when_file_is_briefly_missing()
    {
        // Editors that save via rename leave a moment with no file; reloading then must not
        // swap the live rules for the built-in defaults.
        var path = Path.Combine(Path.GetTempPath(), "cw-cfg-missing-" + Guid.NewGuid().ToString("N") + ".yaml");
        Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", path);
        try
        {
            var log = new CapturingLog();
            await using var host = new WardenHost(
                WardenConfig.CreateDefault(), log, new EmptyLister(), pipeName: TestPipe(), configPath: path);

            host.ReloadConfig();

            Assert.True(log.Contains("config file not found"));
            Assert.False(log.Contains("Configuration reloaded"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", null);
        }
    }

    [Fact]
    public async Task ReloadConfig_counts_only_enabled_rules()
    {
        var path = WriteTempConfig(
            "process_rules:\n  - name: A\n    executable: A.exe\n  - name: B\n    executable: B.exe\n    enabled: false\n");
        Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", path);
        try
        {
            var log = new CapturingLog();
            await using var host = new WardenHost(
                WardenConfig.CreateDefault(), log, new EmptyLister(), pipeName: TestPipe(), configPath: path);

            host.ReloadConfig();

            Assert.True(log.Contains("1 process rule(s) active"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", null);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReloadConfig_keeps_current_rules_on_corrupt_file()
    {
        var path = WriteTempConfig("process_rules: [ broken: : yaml");
        Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", path);
        try
        {
            var log = new CapturingLog();
            await using var host = new WardenHost(
                WardenConfig.CreateDefault(), log, new EmptyLister(), pipeName: TestPipe(), configPath: path);

            host.ReloadConfig();

            Assert.True(log.Contains("keeping current rules"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", null);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReloadConfig_applies_sharing_and_drops_removed_rules()
    {
        const string before = "process_detection:\n  poll_interval_ms: 50\n  available_after_ms: 0\n" +
            "process_rules:\n  - name: X\n    executable: X.exe\n    resources: [disk]\n" +
            "sharing:\n  throttle_max_cores: 2\n";
        const string after = "process_detection:\n  poll_interval_ms: 50\n  available_after_ms: 0\n" +
            "sharing:\n  throttle_max_cores: 3\n";
        var path = WriteTempConfig(before);
        Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", path);
        try
        {
            await using var host = new WardenHost(
                ConfigLoader.Parse(before), new CapturingLog(), new OneProcess(), pipeName: TestPipe(), configPath: path);
            host.Start();
            host.Warden.SetMode(UserMode.Steady);

            // Startup uses the file's sharing section.
            Assert.Equal(2, host.Warden.Acquire(new ClaimRequest("A", "x", ClaimKind.Shared)).Advice!.Throttle!.MaxCores);
            await Until(() => host.Warden.GetStatus().Blockers.Any(b => b.Type == BlockerType.Process));

            File.WriteAllText(path, after);
            host.ReloadConfig();

            Assert.Equal(3, host.Warden.Acquire(new ClaimRequest("B", "y", ClaimKind.Shared)).Advice!.Throttle!.MaxCores);
            await Until(() => !host.Warden.GetStatus().Blockers.Any(b => b.Type == BlockerType.Process));
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", null);
            File.Delete(path);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met within 10s");
            await Task.Delay(25);
        }
    }
}
