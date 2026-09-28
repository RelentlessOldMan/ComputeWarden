namespace ComputeWarden.Core.Config;

/// <summary>Process polling and debounce settings.</summary>
public sealed class ProcessDetectionOptions
{
    /// <summary>How often the process list is polled. Kept cheap (name-only enumeration).</summary>
    public int PollIntervalMs { get; init; } = 2000;

    /// <summary>
    /// Delay before a newly-appeared matched process becomes a blocker. Default 0 (immediate).
    /// </summary>
    public int BusyAfterMs { get; init; } = 0;

    /// <summary>
    /// How long a matched process continues to count as a blocker after it disappears.
    /// Prevents the machine flapping to Available during short gaps between related processes.
    /// </summary>
    public int AvailableAfterMs { get; init; } = 3000;
}
