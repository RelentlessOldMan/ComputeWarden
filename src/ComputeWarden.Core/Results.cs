using ComputeWarden.Core.Model;

namespace ComputeWarden.Core;

/// <summary>
/// Complete current state, for the status operation. <see cref="Blockers"/> are what keeps work
/// small or waiting (games, the manual hold, a solo claim); <see cref="Claims"/> lists every
/// agent claim, running and waiting.
/// </summary>
public sealed record StatusResult(
    MachineState State,
    bool CanRunIntensive,
    IReadOnlyList<Blocker> Blockers,
    IReadOnlyList<ClaimInfo>? Claims = null,
    string? DaemonVersion = null,
    int? GameJobs = null);

/// <summary>
/// Outcome of an acquire. A valid claim is always created and carries <see cref="Advice"/>:
/// full, throttled, or queued. Only an invalid request is refused.
/// </summary>
public sealed record AcquireResult(
    bool Acquired,
    string? ReservationId,
    DateTimeOffset? ExpiresAt,
    MachineState MachineState,
    string? Reason,
    IReadOnlyList<Blocker> Blockers,
    Advice? Advice = null,
    ClaimKind? Kind = null,
    bool BigMemory = false)
{
    public static AcquireResult Claimed(Reservation r, Advice advice, MachineState state, IReadOnlyList<Blocker> blockers) => new(
        Acquired: true,
        ReservationId: r.Id,
        ExpiresAt: r.ExpiresAt,
        MachineState: state,
        Reason: advice.Reason,
        Blockers: blockers,
        Advice: advice,
        Kind: r.Kind,
        BigMemory: r.BigMemory);

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
