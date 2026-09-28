using ComputeWarden.Core.Model;

namespace ComputeWarden.Core.Providers;

/// <summary>
/// Result of querying a blocker provider. <see cref="Confident"/> is false when the
/// provider could not reliably determine its blockers (e.g. process enumeration threw),
/// which drives the machine into <see cref="MachineState.Unknown"/>.
/// </summary>
public sealed record ProviderResult(IReadOnlyList<Blocker> Blockers, bool Confident)
{
    public static ProviderResult Ok(IReadOnlyList<Blocker> blockers) => new(blockers, true);
    public static ProviderResult Ok(params Blocker[] blockers) => new(blockers, true);

    /// <summary>The provider failed to determine state; forces Unknown.</summary>
    public static readonly ProviderResult Unhealthy = new(Array.Empty<Blocker>(), false);

    public static readonly ProviderResult Empty = new(Array.Empty<Blocker>(), true);
}
