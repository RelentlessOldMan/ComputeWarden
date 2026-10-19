namespace ComputeWarden.Core.Config;

/// <summary>
/// How the warden advises work while a game runs, and how long a waiting ticket lasts.
/// Everything here is advice to agents; nothing is enforced.
/// </summary>
public sealed class SharingOptions
{
    /// <summary>Cores a throttled job should use.</summary>
    public int ThrottleMaxCores { get; init; } = 1;

    /// <summary>Process priority a throttled job should run at (e.g. below_normal, idle).</summary>
    public string ThrottlePriority { get; init; } = "below_normal";

    /// <summary>Memory a throttled job should stay under.</summary>
    public double ThrottleMaxRamGb { get; init; } = 2;

    /// <summary>Jobs that may run beside a game until the user picks a number on the panel.</summary>
    public int GameJobs { get; init; } = 3;

    /// <summary>A queued ticket that doesn't check in (renew) this often drops out of line.</summary>
    public int QueueCheckinSeconds { get; init; } = 120;
}
