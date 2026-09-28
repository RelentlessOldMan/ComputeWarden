using ComputeWarden.Daemon;
using YamlDotNet.Core;

namespace ComputeWarden.Tests;

// Shares the process-global COMPUTEWARDEN_CONFIG env var with HotReloadTests; same collection
// keeps them serialized so they don't clobber each other's env.
[Collection("config-env")]
public class ConfigLoaderTests
{
    [Fact]
    public void Parses_full_config()
    {
        const string yaml = """
            server:
              poll_interval_ms: 1500
            leases:
              default_seconds: 120
              minimum_seconds: 10
              maximum_seconds: 900
            process_detection:
              available_after_ms: 4000
              busy_after_ms: 250
            process_rules:
              - name: MSBuild
                executable: MSBuild.exe
                enabled: true
              - name: Indexer
                executable: CodeCompass.exe
                enabled: false
            allow_manual_blocker: false
            logging:
              level: warn
            """;

        var config = ConfigLoader.Parse(yaml);

        Assert.Equal(120, config.Leases.DefaultSeconds);
        Assert.Equal(10, config.Leases.MinimumSeconds);
        Assert.Equal(900, config.Leases.MaximumSeconds);
        Assert.Equal(4000, config.ProcessDetection.AvailableAfterMs);
        Assert.Equal(250, config.ProcessDetection.BusyAfterMs);
        Assert.Equal(2, config.ProcessRules.Count);
        Assert.False(config.ProcessRules[1].Enabled);
        Assert.False(config.AllowManualBlocker);
        Assert.Equal("warn", config.LogLevel);
    }

    [Fact]
    public void Process_detection_poll_interval_overrides_server()
    {
        const string yaml = """
            server:
              poll_interval_ms: 2000
            process_detection:
              poll_interval_ms: 500
            """;

        Assert.Equal(500, ConfigLoader.Parse(yaml).ProcessDetection.PollIntervalMs);
    }

    [Fact]
    public void Server_poll_interval_is_fallback()
    {
        const string yaml = """
            server:
              poll_interval_ms: 750
            """;

        Assert.Equal(750, ConfigLoader.Parse(yaml).ProcessDetection.PollIntervalMs);
    }

    [Fact]
    public void Empty_yaml_yields_defaults()
    {
        var config = ConfigLoader.Parse("");

        Assert.Equal(300, config.Leases.DefaultSeconds);
        Assert.Equal(2000, config.ProcessDetection.PollIntervalMs);
        Assert.True(config.AllowManualBlocker);
    }

    [Fact]
    public void Rule_without_executable_is_dropped()
    {
        const string yaml = """
            process_rules:
              - name: Bogus
              - name: Good
                executable: Good.exe
            """;

        var rule = Assert.Single(ConfigLoader.Parse(yaml).ProcessRules);
        Assert.Equal("Good.exe", rule.Executable);
    }

    [Fact]
    public void Invalid_yaml_throws()
        => Assert.ThrowsAny<YamlException>(() => ConfigLoader.Parse("process_rules: [ this: : broken"));

    [Fact]
    public void Load_missing_file_falls_back_to_defaults_without_error()
    {
        Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", Path.Combine(Path.GetTempPath(), "cw-nope-" + Guid.NewGuid().ToString("N") + ".yaml"));
        try
        {
            var result = ConfigLoader.Load();
            Assert.False(result.IsError);
            Assert.Contains("using defaults", result.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", null);
        }
    }

    [Fact]
    public void Load_corrupt_file_falls_back_to_defaults_flagged_as_error()
    {
        var path = Path.Combine(Path.GetTempPath(), "cw-bad-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, "process_rules: [ this: : broken");
        Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", path);
        try
        {
            var result = ConfigLoader.Load();
            Assert.True(result.IsError);
            Assert.Contains("Configuration error", result.Message);
            Assert.Equal(300, result.Config.Leases.DefaultSeconds); // defaults still usable
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPUTEWARDEN_CONFIG", null);
            File.Delete(path);
        }
    }
}
