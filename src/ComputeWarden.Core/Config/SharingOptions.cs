using ComputeWarden.Core.Model;

namespace ComputeWarden.Core.Config;

/// <summary>
/// How the warden advises everyday (shared) work and runs the line. Everything here is
/// advice to agents; nothing is enforced.
/// </summary>
public sealed class SharingOptions
{
    /// <summary>Cores a throttled job should use.</summary>
    public int ThrottleMaxCores { get; init; } = 1;

    /// <summary>Process priority a throttled job should run at (e.g. below_normal, idle).</summary>
    public string ThrottlePriority { get; init; } = "below_normal";

    /// <summary>Memory a throttled job should stay under.</summary>
    public double ThrottleMaxRamGb { get; init; } = 2;

    /// <summary>Throttled claims allowed per contended resource before new ones are queued.</summary>
    public int MaxThrottledPerResource { get; init; } = 3;

    /// <summary>Resources that can't be shared under contention: requests for them are queued instead.</summary>
    public ResourceSet Unshareable { get; init; } = ResourceSet.Gpu;

    /// <summary>A queued ticket that doesn't check in (renew) this often drops out of line.</summary>
    public int QueueCheckinSeconds { get; init; } = 120;
}
