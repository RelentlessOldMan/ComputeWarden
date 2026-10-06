namespace ComputeWarden.Core.Model;

/// <summary>
/// Temporary ownership of permission to perform intensive work. Reservations are
/// ephemeral leases: they expire automatically so a crashed owner can never block
/// the machine permanently. The <see cref="Id"/> is an opaque bearer capability —
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
