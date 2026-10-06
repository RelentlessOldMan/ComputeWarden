namespace ComputeWarden.Core.Model;

/// <summary>
/// A single reason the machine is unavailable. The machine-wide state is derived
/// from the set of current blockers rather than a fragile boolean. <see cref="Resources"/>
/// is what the blocker occupies; an acquire only conflicts with blockers it overlaps.
/// </summary>
public sealed record Blocker(
    string Id,
    BlockerType Type,
    string Source,
    string Description,
    string? Owner = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? ExpiresAt = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    ResourceSet Resources = ResourceSet.All);
