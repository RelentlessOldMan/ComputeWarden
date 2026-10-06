using ComputeWarden.Core.Config;
using ComputeWarden.Core.Diagnostics;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;
using ComputeWarden.Core.Time;

namespace ComputeWarden.Core;

/// <summary>
/// The authoritative, thread-safe coordinator. Holds reservations and composes blocker
/// providers to answer "is it safe to begin intensive work?" and to atomically acquire
/// permission. All state-mutating operations execute under a single lock so that no race
/// can grant two conflicting reservations simultaneously (spec §27).
/// <para>Exclusivity is per resource: every blocker occupies a <see cref="ResourceSet"/>, and
/// an acquire succeeds when no blocker overlaps the requested set. Requests that omit
/// resources ask for <see cref="ResourceSet.All"/>, i.e. the whole machine.</para>
/// </summary>
public sealed class Warden
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Reservation> _reservations = new(StringComparer.Ordinal);
    private readonly IClock _clock;
    private readonly LeaseOptions _lease;
    private readonly WardenLimits _limits;
    private readonly IReadOnlyList<IBlockerProvider> _providers;
    private readonly ManualBlockerProvider _manual;
    private readonly ILog _log;
    private readonly DateTimeOffset _startedAt;

    // Counters (mutated only under _gate).
    private long _totalAcquisitions;
    private long _failedAcquisitions;
    private long _expiredReservations;

    /// <param name="manual">The manual blocker provider (also exposed via set/clear helpers).</param>
    /// <param name="extraProviders">Other providers, e.g. the process monitor.</param>
    public Warden(
        IClock clock,
        LeaseOptions lease,
        WardenLimits limits,
        ManualBlockerProvider manual,
        IEnumerable<IBlockerProvider>? extraProviders = null,
        ILog? log = null)
    {
        _clock = clock;
        _lease = lease;
        _limits = limits;
        _manual = manual;
        _log = log ?? NullLog.Instance;

        var providers = new List<IBlockerProvider>();
        if (extraProviders is not null) providers.AddRange(extraProviders);
        providers.Add(manual); // manual participates in blocker collection like any provider
        _providers = providers;

        _startedAt = clock.UtcNow;
    }

    // ---- Read operations ------------------------------------------------

    /// <summary>
    /// Full state. <see cref="StatusResult.State"/> is machine-wide (BUSY if anything holds any
    /// resource); <see cref="StatusResult.BusyResources"/> says which resources are taken.
    /// </summary>
    public StatusResult GetStatus()
    {
        lock (_gate)
        {
            var blockers = CollectBlockersLocked(out var confident);
            var state = DetermineState(confident, blockers);
            return new StatusResult(state, state == MachineState.Available, blockers, Occupied(blockers));
        }
    }

    /// <summary>
    /// Informational availability check for the given resources (default: all). MUST NOT be
    /// treated as a guarantee that availability persists — agents doing protected work must Acquire.
    /// </summary>
    public bool CanRun(ResourceSet resources = ResourceSet.All)
    {
        lock (_gate)
        {
            var blockers = CollectBlockersLocked(out var confident);
            return DetermineState(confident, Conflicting(blockers, resources)) == MachineState.Available;
        }
    }

    public WardenStats GetStats()
    {
        lock (_gate)
        {
            var blockers = CollectBlockersLocked(out _);
            var processBlockers = blockers.Count(b => b.Type == BlockerType.Process);
            return new WardenStats(
                _totalAcquisitions,
                _failedAcquisitions,
                _expiredReservations,
                _reservations.Count,
                processBlockers,
                _clock.UtcNow - _startedAt);
        }
    }

    // ---- Reservation lifecycle -----------------------------------------

    /// <summary>
    /// Atomically checks blockers overlapping <paramref name="resources"/> and, if none, creates
    /// a reservation holding those resources. This is the critical correctness operation:
    /// check + acquire under one lock.
    /// </summary>
    public AcquireResult Acquire(
        string owner,
        string description,
        int? leaseSeconds = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        ResourceSet resources = ResourceSet.All)
    {
        if (resources == ResourceSet.None) resources = ResourceSet.All;

        lock (_gate)
        {
            if (!TryValidate(owner, description, metadata, out var validationError))
            {
                _failedAcquisitions++;
                var b = CollectBlockersLocked(out var c);
                return AcquireResult.Failure(validationError!, DetermineState(c, b), b);
            }

            // Only blockers sharing a resource with this request matter. Unknown is still
            // machine-wide: if we can't see, we can't tell which resources are free.
            var blockers = Conflicting(CollectBlockersLocked(out var confident), resources);
            var state = DetermineState(confident, blockers);

            if (state == MachineState.Unknown)
            {
                _failedAcquisitions++;
                return AcquireResult.Failure(
                    "Machine state is UNKNOWN; refusing to acquire.", state, blockers);
            }

            if (blockers.Count > 0)
            {
                _failedAcquisitions++;
                var reason = resources == ResourceSet.All
                    ? "Machine currently unavailable"
                    : $"Requested resources in use: {string.Join(", ", Resources.ToNames(Occupied(blockers) & resources))}";
                return AcquireResult.Failure(reason, state, blockers);
            }

            var now = _clock.UtcNow;
            var seconds = _lease.Resolve(leaseSeconds);
            var reservation = new Reservation(
                id: NewId(),
                owner: owner,
                description: description ?? string.Empty,
                createdAt: now,
                expiresAt: now.AddSeconds(seconds),
                metadata: CloneMetadata(metadata),
                resources: resources);

            _reservations[reservation.Id] = reservation;
            _totalAcquisitions++;
            // Lifecycle events are rare, so logging under the lock is cheap here and keeps the
            // audit trail ordered.
            _log.Info($"Reservation acquired: {owner} \"{Trunc(reservation.Description)}\" [{Short(reservation.Id)}] {Names(resources)} lease {seconds}s");
            return AcquireResult.Success(reservation);
        }
    }

    /// <summary>Renews an existing reservation. The id acts as a bearer capability.</summary>
    public RenewResult Renew(string reservationId, int? leaseSeconds = null)
    {
        lock (_gate)
        {
            PruneExpiredLocked();
            if (string.IsNullOrEmpty(reservationId) ||
                !_reservations.TryGetValue(reservationId, out var r))
            {
                return RenewResult.Failure("Reservation no longer exists");
            }

            var now = _clock.UtcNow;
            r.LastRenewedAt = now;
            r.ExpiresAt = now.AddSeconds(_lease.Resolve(leaseSeconds));
            _log.Debug($"Reservation renewed: [{Short(r.Id)}] expires {r.ExpiresAt:o}");
            return RenewResult.Success(r.ExpiresAt);
        }
    }

    /// <summary>Releases a reservation. Idempotent — releasing an unknown id is not an error.</summary>
    public ReleaseResult Release(string reservationId)
    {
        lock (_gate)
        {
            PruneExpiredLocked();
            if (!string.IsNullOrEmpty(reservationId) &&
                _reservations.TryGetValue(reservationId, out var r))
            {
                _reservations.Remove(reservationId);
                _log.Info($"Reservation released: {r.Owner} [{Short(r.Id)}] held {Held(r, _clock.UtcNow)}");
                return ReleaseResult.Ok;
            }
            return ReleaseResult.NotFound();
        }
    }

    // ---- Manual blocker -------------------------------------------------

    public Blocker SetManualBusy(string reason, ResourceSet resources = ResourceSet.All)
    {
        if (reason is null) throw new ArgumentNullException(nameof(reason));
        if (reason.Length > _limits.MaxReasonLength)
            throw new ArgumentException($"reason exceeds {_limits.MaxReasonLength} characters", nameof(reason));
        return _manual.Set(reason, resources == ResourceSet.None ? ResourceSet.All : resources);
    }

    public bool ClearManualBusy() => _manual.Clear();

    // ---- Maintenance ----------------------------------------------------

    /// <summary>Forces expiry of stale reservations. Called by the daemon's low-frequency sweep.</summary>
    public void Sweep()
    {
        lock (_gate) PruneExpiredLocked();
    }

    // ---- Internals ------------------------------------------------------

    private List<Blocker> CollectBlockersLocked(out bool confident)
    {
        PruneExpiredLocked();

        var blockers = new List<Blocker>();
        confident = true;

        foreach (var r in _reservations.Values)
            blockers.Add(r.ToBlocker());

        foreach (var provider in _providers)
        {
            var result = provider.GetBlockers();
            if (!result.Confident) confident = false;
            blockers.AddRange(result.Blockers);
        }

        return blockers;
    }

    private static List<Blocker> Conflicting(List<Blocker> blockers, ResourceSet resources)
        => resources == ResourceSet.All
            ? blockers
            : blockers.Where(b => Resources.Overlaps(b.Resources, resources)).ToList();

    private static ResourceSet Occupied(IEnumerable<Blocker> blockers)
    {
        var set = ResourceSet.None;
        foreach (var b in blockers) set |= b.Resources;
        return set;
    }

    private static string Names(ResourceSet set) => "[" + string.Join(",", Resources.ToNames(set)) + "]";

    private static MachineState DetermineState(bool confident, IReadOnlyList<Blocker> blockers)
        => !confident ? MachineState.Unknown
         : blockers.Count > 0 ? MachineState.Busy
         : MachineState.Available;

    private void PruneExpiredLocked()
    {
        var now = _clock.UtcNow;
        List<Reservation>? expired = null;
        foreach (var kv in _reservations)
            if (kv.Value.IsExpiredAt(now))
                (expired ??= new()).Add(kv.Value);

        if (expired is null) return;
        foreach (var r in expired)
        {
            _reservations.Remove(r.Id);
            _expiredReservations++;
            // The prime "misuse" signal: a lease that lapsed because the owner never released
            // (crashed, forgot, or under-estimated its lease). Distinct from a clean release.
            _log.Info($"Reservation expired (owner did not release): {r.Owner} \"{Trunc(r.Description)}\" [{Short(r.Id)}] held {Held(r, now)}");
        }
    }

    private static string Short(string id) => id.Length <= 8 ? id : id[..8];

    private static string Trunc(string text) => text.Length <= 80 ? text : text[..77] + "...";

    private static string Held(Reservation r, DateTimeOffset now)
        => $"{Math.Max(0, (int)(now - r.CreatedAt).TotalSeconds)}s";

    private bool TryValidate(
        string owner,
        string? description,
        IReadOnlyDictionary<string, string>? metadata,
        out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(owner))
        {
            error = "owner is required";
            return false;
        }
        if (owner.Length > _limits.MaxOwnerLength)
        {
            error = $"owner exceeds {_limits.MaxOwnerLength} characters";
            return false;
        }
        if (description is { Length: > 0 } && description.Length > _limits.MaxDescriptionLength)
        {
            error = $"description exceeds {_limits.MaxDescriptionLength} characters";
            return false;
        }
        if (metadata is not null)
        {
            if (metadata.Count > _limits.MaxMetadataEntries)
            {
                error = $"metadata exceeds {_limits.MaxMetadataEntries} entries";
                return false;
            }
            foreach (var kv in metadata)
            {
                if (kv.Value is null)
                {
                    error = $"metadata value for '{kv.Key}' must be a string";
                    return false;
                }
                if (kv.Key.Length > _limits.MaxMetadataKeyLength)
                {
                    error = $"metadata key exceeds {_limits.MaxMetadataKeyLength} characters";
                    return false;
                }
                if (kv.Value.Length > _limits.MaxMetadataValueLength)
                {
                    error = $"metadata value exceeds {_limits.MaxMetadataValueLength} characters";
                    return false;
                }
            }
        }

        return true;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static IReadOnlyDictionary<string, string>? CloneMetadata(
        IReadOnlyDictionary<string, string>? metadata)
        => metadata is null || metadata.Count == 0
            ? null
            : new Dictionary<string, string>(metadata);
}
