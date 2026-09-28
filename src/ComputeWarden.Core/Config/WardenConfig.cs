namespace ComputeWarden.Core.Config;

/// <summary>Top-level configuration. Loaded from YAML by the daemon; sane defaults otherwise.</summary>
public sealed class WardenConfig
{
    public LeaseOptions Leases { get; init; } = new();
    public ProcessDetectionOptions ProcessDetection { get; init; } = new();
    public List<ProcessRule> ProcessRules { get; init; } = new();
    public WardenLimits Limits { get; init; } = new();

    /// <summary>Whether clients may set/clear the manual blocker over IPC.</summary>
    public bool AllowManualBlocker { get; init; } = true;

    public string LogLevel { get; init; } = "info";

    public static WardenConfig CreateDefault() => new()
    {
        ProcessRules =
        {
            new ProcessRule { Name = "MSBuild", Executable = "MSBuild.exe" },
            new ProcessRule { Name = "CodeCompass", Executable = "CodeCompass.exe" },
        },
    };
}
