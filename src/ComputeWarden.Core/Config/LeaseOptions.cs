namespace ComputeWarden.Core.Config;

/// <summary>Bounds applied to reservation lease durations. Requests are clamped server-side.</summary>
public sealed class LeaseOptions
{
    public int DefaultSeconds { get; init; } = 300;
    public int MinimumSeconds { get; init; } = 30;
    public int MaximumSeconds { get; init; } = 3600;

    /// <summary>
    /// Clamps a requested lease into [minimum, maximum] (or applies the default when unspecified).
    /// Tolerates a misconfigured range where minimum &gt; maximum by treating minimum as the floor,
    /// so the result is never below the minimum a caller is guaranteed.
    /// </summary>
    public int Resolve(int? requestedSeconds)
    {
        var seconds = requestedSeconds ?? DefaultSeconds;
        var max = Math.Max(MinimumSeconds, MaximumSeconds);
        return Math.Clamp(seconds, MinimumSeconds, max);
    }
}
