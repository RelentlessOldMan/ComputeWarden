using ComputeWarden.Core.Model;
using ComputeWarden.Core.Time;

namespace ComputeWarden.Core.Providers;

/// <summary>
/// Holds an optional single manual blocker set by an administrator/operator for work
/// ComputeWarden cannot automatically detect (e.g. disk maintenance). Thread-safe.
/// </summary>
public sealed class ManualBlockerProvider : IBlockerProvider
{
    private readonly IClock _clock;
    private readonly object _gate = new();
    private Blocker? _blocker;

    public ManualBlockerProvider(IClock clock) => _clock = clock;

    public string Name => "ManualBlockerProvider";

    public bool IsSet
    {
        get { lock (_gate) return _blocker is not null; }
    }

    /// <summary>Creates (or replaces) the manual blocker. Returns the created blocker.</summary>
    public Blocker Set(string reason)
    {
        var blocker = new Blocker(
            Id: "manual",
            Type: BlockerType.Manual,
            Source: Name,
            Description: reason,
            CreatedAt: _clock.UtcNow);

        lock (_gate) _blocker = blocker;
        return blocker;
    }

    /// <summary>Clears the manual blocker. Returns true if one existed.</summary>
    public bool Clear()
    {
        lock (_gate)
        {
            var existed = _blocker is not null;
            _blocker = null;
            return existed;
        }
    }

    public ProviderResult GetBlockers()
    {
        lock (_gate)
            return _blocker is null ? ProviderResult.Empty : ProviderResult.Ok(_blocker);
    }
}
