namespace ComputeWarden.Core.Config;

/// <summary>Top-level configuration. Loaded from YAML by the daemon; sane defaults otherwise.</summary>
public sealed class WardenConfig
{
    public LeaseOptions Leases { get; init; } = new();
    public ProcessDetectionOptions ProcessDetection { get; init; } = new();
    public List<ProcessRule> ProcessRules { get; init; } = new();
    public WardenLimits Limits { get; init; } = new();
    public SharingOptions Sharing { get; init; } = new();

    /// <summary>Whether clients may set/clear the manual blocker over IPC.</summary>
    public bool AllowManualBlocker { get; init; } = true;

    public string LogLevel { get; init; } = "info";

    /// <summary>No process rules: which games to yield to is the user's to list.</summary>
    public static WardenConfig CreateDefault() => new();
}
