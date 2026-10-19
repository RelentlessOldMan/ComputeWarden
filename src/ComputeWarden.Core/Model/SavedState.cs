namespace ComputeWarden.Core.Model;

/// <summary>
/// What the warden keeps across a daemon restart: the claims (with their place in line and
/// leases), the manual hold, and how many jobs may run beside a game. Games aren't saved;
/// they're detected again.
/// <para>Version 1 (before 2.0) still loads: its fields that no longer exist are ignored, and its
/// exclusive claims come back as shared ones.</para>
/// </summary>
public sealed record SavedState(
    int V,
    SavedHold? Hold,
    IReadOnlyList<SavedClaim> Claims,
    int? GameJobs = null)
{
    public const int Version = 2;
}

public sealed record SavedHold(string Reason, DateTimeOffset CreatedAt);

/// <summary>One claim, field for field, so a restored claim advises exactly as before.</summary>
/// <param name="Kind">"shared" or "solo"; anything else (a version-1 "exclusive") reads as shared.</param>
public sealed record SavedClaim(
    string Id,
    string Owner,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastRenewedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, string>? Metadata,
    string? Kind,
    bool BigMemory,
    int? EstimateSeconds,
    long Seq,
    int LeaseSeconds,
    bool Started,
    DateTimeOffset? StartedAt,
    Grant LastGrant);
