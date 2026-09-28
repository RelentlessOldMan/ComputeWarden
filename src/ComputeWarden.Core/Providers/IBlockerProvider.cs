namespace ComputeWarden.Core.Providers;

/// <summary>
/// A source of blockers. The extension seam for future CPU/disk/GPU/thermal providers —
/// resource-detection logic is kept separate from reservation logic so new providers can
/// be added without touching the core.
/// </summary>
public interface IBlockerProvider
{
    /// <summary>Short identifier used in diagnostics/logging.</summary>
    string Name { get; }

    /// <summary>
    /// Returns the current blockers from this source. Must not throw; report failure via
    /// <see cref="ProviderResult.Unhealthy"/> so the machine reports Unknown rather than
    /// falsely reporting Available.
    /// </summary>
    ProviderResult GetBlockers();
}
