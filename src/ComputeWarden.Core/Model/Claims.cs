namespace ComputeWarden.Core.Model;

/// <summary>What an agent claim wants from its resources.</summary>
public enum ClaimKind
{
    /// <summary>Will saturate the resources: one holder per resource, the rest wait or run throttled.</summary>
    Exclusive,

    /// <summary>Everyday work (build, test, run): never refused, told how big it may run.</summary>
    Shared,
}

/// <summary>What an exclusive claim does when it can't have its resources right away.</summary>
public enum WhenBusy
{
    /// <summary>Run small now (and stay in line for full use).</summary>
    Throttle,

    /// <summary>Don't start; wait in line.</summary>
    Wait,

    /// <summary>Legacy acquire: refuse outright and create nothing (pre-sharing behavior).</summary>
    Refuse,
}

/// <summary>The warden's answer to a claim: how big it may run right now.</summary>
public enum Grant
{
    Full,
    Throttled,
    Queued,
}

/// <summary>Where the user moves a waiting claim in the line.</summary>
public enum LineMove
{
    Front,
    Up,
    Down,
}

/// <summary>
/// The machine-wide setting only the user sets. It adjusts how agents treat each other; it
/// never loosens a priority claim (a running game always limits everyone on its resources).
/// </summary>
public enum UserMode
{
    /// <summary>The normal rules.</summary>
    Auto,

    /// <summary>"Y'all go slow and steady": everything throttled, nobody full.</summary>
    Steady,

    /// <summary>"Full bore allowed with a lease": no throttling or throttle cap between agents. A game still throttles, with its cap.</summary>
    Open,

    /// <summary>"Nobody start anything": every new claim is queued.</summary>
    Hold,
}

/// <summary>The current user mode and the resources it covers.</summary>
public sealed record ModeState(
    UserMode Mode,
    ResourceSet Resources,
    string? Reason,
    DateTimeOffset SetAt,
    DateTimeOffset? ExpiresAt)
{
    public static ModeState Auto(DateTimeOffset now) => new(UserMode.Auto, ResourceSet.All, null, now, null);
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
    QueueInfo? Queue,
    ResourceSet Contended)
{
    public static readonly Advice Full = new(Grant.Full, null, null, null, null, ResourceSet.None);

    /// <summary>Queued only for a throttle slot: the resources it's waiting for one on.</summary>
    internal ResourceSet SlotWait { get; init; }
}

/// <summary>A snapshot of one agent claim, for status and queue listings.</summary>
public sealed record ClaimInfo(
    string Id,
    string Owner,
    string Description,
    ClaimKind Kind,
    ResourceSet Resources,
    Grant Grant,
    bool Holding,
    DateTimeOffset ExpiresAt,
    int? EstimateSeconds,
    int? Cores,
    double? RamGb,
    string? UserPriority,
    int? Position,
    ClaimPhase Phase = ClaimPhase.Running,
    DateTimeOffset? LastCheckIn = null);

/// <summary>Where a claim is, as the user sees it. Filled in by status; other uses leave the default.</summary>
public enum ClaimPhase
{
    /// <summary>Started: the owner was told it may run, and took it.</summary>
    Running,

    /// <summary>Its turn has come (its resources are kept for it), but the owner hasn't started yet.</summary>
    Ready,

    /// <summary>Waiting in line to start.</summary>
    Waiting,
}

/// <summary>
/// An agent's request to do work, with its own estimate of what that work needs.
/// <see cref="Resources"/> None means the kind's default (shared: cpu+ram+disk; exclusive: all).
/// </summary>
public sealed record ClaimRequest(
    string Owner,
    string Description,
    ClaimKind Kind,
    ResourceSet Resources = ResourceSet.None,
    WhenBusy WhenBusy = WhenBusy.Throttle,
    int? LeaseSeconds = null,
    int? EstimateSeconds = null,
    int? Cores = null,
    double? RamGb = null,
    string? UserPriority = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
