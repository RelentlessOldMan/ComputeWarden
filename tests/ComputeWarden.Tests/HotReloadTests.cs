using ComputeWarden.Core.Config;
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
                WardenConfig.CreateDefault(), log, new EmptyLister(), configPath: path);

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
                WardenConfig.CreateDefault(), log, new EmptyLister(), configPath: path);

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
                WardenConfig.CreateDefault(), log, new EmptyLister(), configPath: path);

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
                WardenConfig.CreateDefault(), log, new EmptyLister(), configPath: path);

            host.ReloadConfig();

            Assert.True(log.Contains("keeping current rules"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", null);
            File.Delete(path);
        }
    }
}
