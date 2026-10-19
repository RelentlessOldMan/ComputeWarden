namespace ComputeWarden.Core.Model;

/// <summary>
/// An agent claim: running work, or a ticket in line. All are ephemeral leases: they expire
/// automatically so a crashed owner can never block the machine permanently. The
/// <see cref="Id"/> is an opaque bearer capability — whoever holds it may renew or release it.
/// </summary>
public sealed class Reservation
{
    public string Id { get; }
    public string Owner { get; }
    public string Description { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastRenewedAt { get; internal set; }
    public DateTimeOffset ExpiresAt { get; internal set; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    public ClaimKind Kind { get; init; } = ClaimKind.Shared;
    public bool BigMemory { get; init; }
    public int? EstimateSeconds { get; init; }

    /// <summary>A solo claim's time limit once it starts: 10 minutes, or longer if the user approved it.</summary>
    public int SoloLimitSeconds { get; init; } = Warden.MaxSoloSeconds;

    /// <summary>Arrival order: first come, first served.</summary>
    public long Seq { get; init; }

    /// <summary>Lease length to apply once the claim is running (tickets use the check-in window until then).</summary>
    public int LeaseSeconds { get; init; }

    /// <summary>Has been told it may run (full or throttled), and took it. Until then it's a ticket in line.</summary>
    public bool Started { get; internal set; }

    public DateTimeOffset? StartedAt { get; internal set; }

    /// <summary>The grant last given to the owner, which a long-polling renew waits to change.</summary>
    public Grant LastGrant { get; internal set; } = Grant.Full;

    public Reservation(
        string id,
        string owner,
        string description,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        Id = id;
        Owner = owner;
        Description = description;
        CreatedAt = createdAt;
        LastRenewedAt = createdAt;
        ExpiresAt = expiresAt;
        Metadata = metadata ?? new Dictionary<string, string>();
    }

    public bool IsExpiredAt(DateTimeOffset now) => ExpiresAt <= now;

    /// <summary>Represents this claim as a blocker (a solo claim holding the machine).</summary>
    public Blocker ToBlocker() => new(
        Id: Id,
        Type: BlockerType.Reservation,
        Source: "ReservationManager",
        Description: $"{Owner}: {Description}",
        Owner: Owner,
        CreatedAt: CreatedAt,
        ExpiresAt: ExpiresAt,
        Metadata: Metadata);
}
