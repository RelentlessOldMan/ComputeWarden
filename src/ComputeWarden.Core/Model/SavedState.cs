namespace ComputeWarden.Core.Model;

/// <summary>
/// What the warden keeps across a daemon restart: the claims (with their place in line and
/// leases), the user's mode, the manual hold, and how many jobs may run beside a game. Process blockers aren't saved; they're
/// detected again.
/// </summary>
public sealed record SavedState(
    int V,
    ModeState Mode,
    SavedHold? Hold,
    IReadOnlyList<SavedClaim> Claims,
    int? GameJobs = null)
{
    public const int Version = 1;
}

public sealed record SavedHold(string Reason, ResourceSet Resources, DateTimeOffset CreatedAt);

/// <summary>One claim, field for field, so a restored claim advises exactly as before.</summary>
public sealed record SavedClaim(
    string Id,
    string Owner,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastRenewedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, string>? Metadata,
    ResourceSet Resources,
    ClaimKind Kind,
    WhenBusy WhenBusy,
    int? EstimateSeconds,
    int? Cores,
    double? RamGb,
    string? UserPriority,
    long Seq,
    int LeaseSeconds,
    bool Holding,
    bool Started,
    DateTimeOffset? StartedAt,
    Grant LastGrant,
    ResourceSet LastContended,
    ResourceSet SlotWait);
