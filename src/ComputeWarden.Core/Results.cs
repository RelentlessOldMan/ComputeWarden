using ComputeWarden.Core.Model;

namespace ComputeWarden.Core;

/// <summary>
/// Complete current state, for the status operation. <see cref="Blockers"/> are what holds
/// resources (processes, manual holds, a blocking user mode, exclusive holds);
/// <see cref="Claims"/> lists every agent claim, including shared work and the line.
/// </summary>
public sealed record StatusResult(
    MachineState State,
    bool CanRunIntensive,
    IReadOnlyList<Blocker> Blockers,
    ResourceSet BusyResources = ResourceSet.None,
    ModeState? Mode = null,
    IReadOnlyList<ClaimInfo>? Claims = null);

/// <summary>
/// Outcome of an acquire. A legacy acquire (no kind) either holds its resources or is refused.
/// A claim with a kind is always created (unless invalid) and carries <see cref="Advice"/>:
/// full, throttled, or queued.
/// </summary>
public sealed record AcquireResult(
    bool Acquired,
    string? ReservationId,
    DateTimeOffset? ExpiresAt,
    MachineState MachineState,
    string? Reason,
    IReadOnlyList<Blocker> Blockers,
    ResourceSet Resources = ResourceSet.None,
    Advice? Advice = null,
    ClaimKind? Kind = null)
{
    public static AcquireResult Success(Reservation r) => new(
        Acquired: true,
        ReservationId: r.Id,
        ExpiresAt: r.ExpiresAt,
        MachineState: MachineState.Busy,
        Reason: null,
        Blockers: Array.Empty<Blocker>(),
        Resources: r.Resources);

    public static AcquireResult Claimed(Reservation r, Advice advice, MachineState state, IReadOnlyList<Blocker> blockers) => new(
        Acquired: true,
        ReservationId: r.Id,
        ExpiresAt: r.ExpiresAt,
        MachineState: state,
        Reason: advice.Reason,
        Blockers: blockers,
        Resources: r.Resources,
        Advice: advice,
        Kind: r.Kind);

    public static AcquireResult Failure(string reason, MachineState state, IReadOnlyList<Blocker> blockers) =>
        new(false, null, null, state, reason, blockers);
}

/// <summary>Outcome of a lease renewal, which is also how a claim checks back for current advice.</summary>
public sealed record RenewResult(
    bool Renewed,
    DateTimeOffset? ExpiresAt,
    string? Reason,
    Advice? Advice = null)
{
    public static RenewResult Success(DateTimeOffset expiresAt, Advice? advice = null)
        => new(true, expiresAt, advice?.Reason, advice);

    public static RenewResult Failure(string reason) => new(false, null, reason);
}

/// <summary>Outcome of a release. Idempotent — releasing an unknown/expired id is not an error.</summary>
public sealed record ReleaseResult(bool Released, string? Reason)
{
    public static readonly ReleaseResult Ok = new(true, null);
    public static ReleaseResult NotFound() => new(false, "Reservation no longer exists");
}
