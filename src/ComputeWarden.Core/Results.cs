using ComputeWarden.Core.Model;

namespace ComputeWarden.Core;

/// <summary>Complete current state, for the status operation.</summary>
public sealed record StatusResult(
    MachineState State,
    bool CanRunIntensive,
    IReadOnlyList<Blocker> Blockers);

/// <summary>Outcome of an atomic acquire attempt.</summary>
public sealed record AcquireResult(
    bool Acquired,
    string? ReservationId,
    DateTimeOffset? ExpiresAt,
    MachineState MachineState,
    string? Reason,
    IReadOnlyList<Blocker> Blockers)
{
    public static AcquireResult Success(Reservation r) => new(
        Acquired: true,
        ReservationId: r.Id,
        ExpiresAt: r.ExpiresAt,
        MachineState: MachineState.Busy,
        Reason: null,
        Blockers: Array.Empty<Blocker>());

    public static AcquireResult Failure(string reason, MachineState state, IReadOnlyList<Blocker> blockers) =>
        new(false, null, null, state, reason, blockers);
}

/// <summary>Outcome of a lease renewal.</summary>
public sealed record RenewResult(
    bool Renewed,
    DateTimeOffset? ExpiresAt,
    string? Reason)
{
    public static RenewResult Success(DateTimeOffset expiresAt) => new(true, expiresAt, null);
    public static RenewResult Failure(string reason) => new(false, null, reason);
}

/// <summary>Outcome of a release. Idempotent — releasing an unknown/expired id is not an error.</summary>
public sealed record ReleaseResult(bool Released, string? Reason)
{
    public static readonly ReleaseResult Ok = new(true, null);
    public static ReleaseResult NotFound() => new(false, "Reservation no longer exists");
}
