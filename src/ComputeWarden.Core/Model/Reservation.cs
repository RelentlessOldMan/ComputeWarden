namespace ComputeWarden.Core.Model;

/// <summary>
/// An agent claim on resources: an exclusive hold, a shared (everyday) claim, or a ticket in
/// line. All are ephemeral leases: they expire automatically so a crashed owner can never
/// block the machine permanently. The <see cref="Id"/> is an opaque bearer capability —
/// whoever holds it may renew or release the reservation.
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
    public ResourceSet Resources { get; }

    public ClaimKind Kind { get; init; } = ClaimKind.Exclusive;
    public WhenBusy WhenBusy { get; init; } = WhenBusy.Refuse;
    public int? EstimateSeconds { get; init; }
    public int? Cores { get; init; }
    public double? RamGb { get; init; }
    public string? UserPriority { get; internal set; }

    /// <summary>Arrival order, for first-come-first-served in line. The user can reorder waiting claims.</summary>
    public long Seq { get; internal set; }

    /// <summary>Lease length to apply once the claim is running (tickets use the check-in window until then).</summary>
    public int LeaseSeconds { get; init; }

    /// <summary>An exclusive claim that owns its resources. Only holding claims are blockers.</summary>
    public bool Holding { get; internal set; }

    /// <summary>Has been told it may run (full or throttled). Until then it's a ticket in line.</summary>
    public bool Started { get; internal set; }

    public DateTimeOffset? StartedAt { get; internal set; }

    /// <summary>The grant last given to the owner, which keeps a throttled slot stable across check-ins.</summary>
    public Grant LastGrant { get; internal set; } = Grant.Full;

    /// <summary>What the last throttled advice was throttled on: only those resources' slots are taken.</summary>
    internal ResourceSet LastContended { get; set; }

    /// <summary>A ticket queued only for a throttle slot: the resources it's waiting for one on.</summary>
    internal ResourceSet SlotWait { get; set; }

    public Reservation(
        string id,
        string owner,
        string description,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        IReadOnlyDictionary<string, string>? metadata = null,
        ResourceSet resources = ResourceSet.All)
    {
        Id = id;
        Owner = owner;
        Description = description;
        CreatedAt = createdAt;
        LastRenewedAt = createdAt;
        ExpiresAt = expiresAt;
        Metadata = metadata ?? new Dictionary<string, string>();
        Resources = resources;
    }

    public bool IsExpiredAt(DateTimeOffset now) => ExpiresAt <= now;

    /// <summary>An exclusive claim waiting for its resources (running throttled, or a ticket).</summary>
    public bool InLine => Kind == ClaimKind.Exclusive && !Holding;

    /// <summary>Represents this reservation as a blocker for state calculation.</summary>
    public Blocker ToBlocker() => new(
        Id: Id,
        Type: BlockerType.Reservation,
        Source: "ReservationManager",
        Description: $"{Owner}: {Description}",
        Owner: Owner,
        CreatedAt: CreatedAt,
        ExpiresAt: ExpiresAt,
        Metadata: Metadata,
        Resources: Resources);
}
