namespace ComputeWarden.Core.Model;

/// <summary>What an agent claim wants from the machine.</summary>
public enum ClaimKind
{
    /// <summary>Everyday work (build, test, run): never refused, told how big it may run.</summary>
    Shared,

    /// <summary>
    /// A quiet machine (timing tests, benchmarks): starts only once nothing else runs, and
    /// nothing new starts while it runs (while it waits, only work that ends first). Short: at
    /// most <see cref="Warden.MaxSoloSeconds"/>.
    /// </summary>
    Solo,
}

/// <summary>The warden's answer to a claim: how big it may run right now.</summary>
public enum Grant
{
    Full,
    Throttled,
    Queued,
}

/// <summary>How small to run when throttled. Advisory: the agent decides how to apply it.</summary>
public sealed record ThrottleAdvice(int? MaxCores, string? Priority, double? MaxRamGb);

/// <summary>Where a queued claim stands in line.</summary>
public sealed record QueueInfo(int? Position, IReadOnlyList<ClaimInfo> Ahead, int? EstWaitSeconds);

/// <summary>The warden's current advice for one claim.</summary>
public sealed record Advice(
    Grant Grant,
    string? Reason,
    ThrottleAdvice? Throttle,
    string? How,
    QueueInfo? Queue)
{
    public static readonly Advice Full = new(Grant.Full, null, null, null, null);
}

/// <summary>A snapshot of one agent claim, for status and queue listings.</summary>
public sealed record ClaimInfo(
    string Id,
    string Owner,
    string Description,
    ClaimKind Kind,
    bool BigMemory,
    Grant Grant,
    DateTimeOffset ExpiresAt,
    int? EstimateSeconds,
    int? Position,
    ClaimPhase Phase = ClaimPhase.Running,
    DateTimeOffset? LastCheckIn = null);

/// <summary>Where a claim is, as the user sees it.</summary>
public enum ClaimPhase
{
    /// <summary>Started: the owner was told it may run, and took it.</summary>
    Running,

    /// <summary>Its turn has come (its place is kept for it), but the owner hasn't started yet.</summary>
    Ready,

    /// <summary>Waiting in line to start.</summary>
    Waiting,
}

/// <summary>
/// An agent's request to do work. <see cref="BigMemory"/> is for a job that needs more memory
/// than the machine can give two of at once and can't cap itself: only one runs at a time.
/// </summary>
public sealed record ClaimRequest(
    string Owner,
    string Description,
    ClaimKind Kind = ClaimKind.Shared,
    bool BigMemory = false,
    int? LeaseSeconds = null,
    int? EstimateSeconds = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
