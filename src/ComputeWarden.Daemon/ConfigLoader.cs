using ComputeWarden.Core.Config;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ComputeWarden.Daemon;

/// <summary>Outcome of loading configuration, with a message to log and whether it was an error.</summary>
public readonly record struct ConfigLoadResult(WardenConfig Config, string Message, bool IsError);

/// <summary>
/// Loads <see cref="WardenConfig"/> from YAML. Looks at COMPUTEWARDEN_CONFIG, else
/// %ProgramData%\ComputeWarden\config.yaml. Missing config falls back to defaults; a parse
/// error also falls back to defaults but is flagged as an error so the daemon still runs
/// rather than refusing to start.
/// </summary>
public static class ConfigLoader
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ComputeWarden",
        "config.yaml");

    public static string ResolvePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("COMPUTEWARDEN_CONFIG");
        return string.IsNullOrWhiteSpace(overridePath) ? DefaultPath : overridePath;
    }

    public static ConfigLoadResult Load()
    {
        var path = ResolvePath();

        if (!File.Exists(path))
            return new ConfigLoadResult(WardenConfig.CreateDefault(), $"No config file at {path}; using defaults", IsError: false);

        try
        {
            var text = File.ReadAllText(path);
            var config = Parse(text);
            return new ConfigLoadResult(config, $"Loaded configuration from {path}", IsError: false);
        }
        catch (YamlException ex)
        {
            return new ConfigLoadResult(WardenConfig.CreateDefault(),
                $"Configuration error in {path}: {ex.Message}. Using defaults.", IsError: true);
        }
        catch (IOException ex)
        {
            return new ConfigLoadResult(WardenConfig.CreateDefault(),
                $"Could not read {path}: {ex.Message}. Using defaults.", IsError: true);
        }
    }

    /// <summary>Parses YAML text into a config (used by tests). Throws <see cref="YamlException"/> on invalid YAML.</summary>
    public static WardenConfig Parse(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var root = deserializer.Deserialize<YamlRoot>(yaml) ?? new YamlRoot();

        var leaseDefaults = new LeaseOptions();
        var detectDefaults = new ProcessDetectionOptions();

        var rules = (root.ProcessRules ?? new List<YamlProcessRule>())
            .Where(r => !string.IsNullOrWhiteSpace(r.Executable))
            .Select(r => new ProcessRule
            {
                Name = r.Name ?? r.Executable!,
                Executable = r.Executable!,
                Enabled = r.Enabled ?? true,
                Description = r.Description,
            })
            .ToList();

        return new WardenConfig
        {
            Leases = new LeaseOptions
            {
                DefaultSeconds = root.Leases?.DefaultSeconds ?? leaseDefaults.DefaultSeconds,
                MinimumSeconds = root.Leases?.MinimumSeconds ?? leaseDefaults.MinimumSeconds,
                MaximumSeconds = root.Leases?.MaximumSeconds ?? leaseDefaults.MaximumSeconds,
            },
            ProcessDetection = new ProcessDetectionOptions
            {
                // process_detection.poll_interval_ms wins; server.poll_interval_ms is the fallback.
                PollIntervalMs = root.ProcessDetection?.PollIntervalMs
                                 ?? root.Server?.PollIntervalMs
                                 ?? detectDefaults.PollIntervalMs,
                BusyAfterMs = root.ProcessDetection?.BusyAfterMs ?? detectDefaults.BusyAfterMs,
                AvailableAfterMs = root.ProcessDetection?.AvailableAfterMs ?? detectDefaults.AvailableAfterMs,
            },
            ProcessRules = rules,
            AllowManualBlocker = root.AllowManualBlocker ?? true,
            LogLevel = root.Logging?.Level ?? "info",
        };
    }

    // ---- YAML shape (snake_case via UnderscoredNamingConvention) --------

    private sealed class YamlRoot
    {
        public YamlServer? Server { get; set; }
        public YamlLeases? Leases { get; set; }
        public YamlProcessDetection? ProcessDetection { get; set; }
        public List<YamlProcessRule>? ProcessRules { get; set; }
        public YamlLogging? Logging { get; set; }
        public bool? AllowManualBlocker { get; set; }
    }

    private sealed class YamlServer { public int? PollIntervalMs { get; set; } }

    private sealed class YamlLeases
    {
        public int? DefaultSeconds { get; set; }
        public int? MinimumSeconds { get; set; }
        public int? MaximumSeconds { get; set; }
    }

    private sealed class YamlProcessDetection
    {
        public int? PollIntervalMs { get; set; }
        public int? BusyAfterMs { get; set; }
        public int? AvailableAfterMs { get; set; }
    }

    private sealed class YamlProcessRule
    {
        public string? Name { get; set; }
        public string? Executable { get; set; }
        public bool? Enabled { get; set; }
        public string? Description { get; set; }
    }

    private sealed class YamlLogging { public string? Level { get; set; } }
}
