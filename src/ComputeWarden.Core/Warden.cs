using System.Globalization;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Diagnostics;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;
using ComputeWarden.Core.Time;

namespace ComputeWarden.Core;

/// <summary>
/// The authoritative, thread-safe coordinator. Holds agent claims and composes blocker
/// providers to answer "how big may this work run right now?". All state-mutating operations
/// execute under a single lock so that no race can grant two conflicting holds.
/// <para>Three kinds of claim share the machine (DESIGN.md §16):
/// <b>priority</b> claims are configured processes (the user's games), which limit everyone on
/// their resources and are never negotiated; <b>exclusive</b> claims own their resources, one
/// holder per resource, with a first-come-first-served line; <b>shared</b> claims (everyday
/// builds and tests) are never refused, only told to run full or throttled. Everything is
/// advice: the warden never caps or kills a process.</para>
/// <para>A legacy acquire (no kind) is an exclusive claim that is refused rather than queued,
/// which is exactly the pre-sharing behavior.</para>
/// </summary>
public sealed class Warden
{
    private const int MaxModeSeconds = 7 * 24 * 3600;

    /// <summary>What everyday work uses when it doesn't say: builds and tests are cpu, ram, and disk.</summary>
    public const ResourceSet SharedDefault = ResourceSet.Cpu | ResourceSet.Ram | ResourceSet.Disk;

    private readonly object _gate = new();
    private readonly Dictionary<string, Reservation> _reservations = new(StringComparer.Ordinal);
    private readonly IClock _clock;
    private readonly LeaseOptions _lease;
    private readonly WardenLimits _limits;
    private readonly IReadOnlyList<IBlockerProvider> _providers;
    private readonly ManualBlockerProvider _manual;
    private readonly ILog _log;
    private readonly DateTimeOffset _startedAt;

    // Mutated only under _gate.
    private SharingOptions _sharing;
    private ModeState _mode;
    private long _seq;
    private long _totalAcquisitions;
    private long _failedAcquisitions;
    private long _expiredReservations;

    /// <param name="manual">The manual blocker provider (also exposed via set/clear helpers).</param>
    /// <param name="extraProviders">Other providers, e.g. the process monitor. Process blockers are priority claims.</param>
    public Warden(
        IClock clock,
        LeaseOptions lease,
        WardenLimits limits,
        ManualBlockerProvider manual,
        IEnumerable<IBlockerProvider>? extraProviders = null,
        ILog? log = null,
        SharingOptions? sharing = null)
    {
        _clock = clock;
        _lease = lease;
        _limits = limits;
        _manual = manual;
        _log = log ?? NullLog.Instance;
        _sharing = sharing ?? new SharingOptions();

        var providers = new List<IBlockerProvider>();
        if (extraProviders is not null) providers.AddRange(extraProviders);
        providers.Add(manual); // manual participates in blocker collection like any provider
        _providers = providers;

        _startedAt = clock.UtcNow;
        _mode = ModeState.Auto(_startedAt);
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
            var x = BuildLocked();
            var now = _clock.UtcNow;
            var state = DetermineState(x.Confident, x.Blockers);
            var claims = _reservations.Values
                .OrderBy(r => r.Seq)
                .Select(r => Info(r, x) with { Phase = PhaseLocked(r, x, now), LastCheckIn = r.LastRenewedAt })
                .ToList();
            return new StatusResult(
                state, state == MachineState.Available, x.Blockers, Occupied(x.Blockers), _mode, claims);
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
            var x = BuildLocked();
            // Resources promised to a claim waiting in line aren't free either: a legacy acquire is refused them.
            return DetermineState(x.Confident, Conflicting(x.Blockers, resources)) == MachineState.Available &&
                   !x.Line.Any(w => Resources.Overlaps(w.Resources, resources));
        }
    }

    public WardenStats GetStats()
    {
        lock (_gate)
        {
            var x = BuildLocked();
            var processBlockers = x.Blockers.Count(b => b.Type == BlockerType.Process);
            return new WardenStats(
                _totalAcquisitions,
                _failedAcquisitions,
                _expiredReservations,
                _reservations.Count,
                processBlockers,
                _clock.UtcNow - _startedAt);
        }
    }

    // ---- Claim lifecycle ------------------------------------------------

    /// <summary>
    /// Legacy acquire: atomically checks blockers overlapping <paramref name="resources"/> and,
    /// if none (and nobody is waiting in line for them), creates an exclusive hold. Otherwise
    /// refuses and creates nothing.
    /// </summary>
    public AcquireResult Acquire(
        string owner,
        string description,
        int? leaseSeconds = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        ResourceSet resources = ResourceSet.All)
        => Acquire(new ClaimRequest(owner, description, ClaimKind.Exclusive, resources,
            WhenBusy.Refuse, leaseSeconds, Metadata: metadata));

    /// <summary>
    /// Creates a claim and returns the warden's advice for it. Shared claims and exclusive
    /// claims that throttle or wait are always created (unless invalid); a
    /// <see cref="WhenBusy.Refuse"/> exclusive claim is the legacy acquire.
    /// Resources <see cref="ResourceSet.None"/> means the kind's default: everyday work is
    /// cpu+ram+disk (so a build isn't held up by a game's GPU), heavy work the whole machine.
    /// </summary>
    public AcquireResult Acquire(ClaimRequest request)
    {
        var kind = request.Kind;
        var resources = request.Resources != ResourceSet.None ? request.Resources
            : kind == ClaimKind.Shared ? SharedDefault
            : ResourceSet.All;
        var whenBusy = kind == ClaimKind.Shared ? WhenBusy.Throttle : request.WhenBusy;

        lock (_gate)
        {
            if (!TryValidate(request, out var validationError))
            {
                _failedAcquisitions++;
                var bx = BuildLocked();
                return AcquireResult.Failure(validationError!, DetermineState(bx.Confident, bx.Blockers), bx.Blockers);
            }

            var now = _clock.UtcNow;

            if (whenBusy == WhenBusy.Refuse)
                return AcquireLegacyLocked(request, resources, now);

            var leaseSeconds = _lease.Resolve(request.LeaseSeconds ?? LeaseFromEstimate(request.EstimateSeconds));
            var r = new Reservation(
                id: NewId(),
                owner: request.Owner,
                description: request.Description ?? string.Empty,
                createdAt: now,
                expiresAt: now.AddSeconds(leaseSeconds),
                metadata: CloneMetadata(request.Metadata),
                resources: resources)
            {
                Kind = kind,
                WhenBusy = whenBusy,
                EstimateSeconds = request.EstimateSeconds,
                Cores = request.Cores,
                RamGb = request.RamGb,
                UserPriority = string.IsNullOrWhiteSpace(request.UserPriority) ? null : request.UserPriority,
                Seq = ++_seq,
                LeaseSeconds = leaseSeconds,
            };
            _reservations[r.Id] = r;
            _totalAcquisitions++;

            // Building after the insert puts the new claim in line, so the same promotion pass
            // that serves earlier waiters decides whether it can hold right away.
            var x = BuildLocked();
            var advice = Evaluate(r, x, now);
            Apply(r, advice, now, renewSeconds: null);

            var priorityNote = r.UserPriority is null ? "" : $" (user priority: {Trunc(r.UserPriority)})";
            _log.Info($"Claim {KindName(kind)}: {r.Owner} \"{Trunc(r.Description)}\" [{Short(r.Id)}] {Names(resources)} -> {GrantName(advice.Grant)}{Because(advice)}{priorityNote}");

            var after = BuildLocked();
            var blockers = Conflicting(after.Blockers, resources).Where(b => b.Id != r.Id).ToList();
            return AcquireResult.Claimed(r, advice, DetermineState(after.Confident, after.Blockers), blockers);
        }
    }

    private AcquireResult AcquireLegacyLocked(ClaimRequest request, ResourceSet resources, DateTimeOffset now)
    {
        var x = BuildLocked();

        // Only blockers sharing a resource with this request matter. Unknown is still
        // machine-wide: if we can't see, we can't tell which resources are free.
        var blockers = Conflicting(x.Blockers, resources);
        var state = DetermineState(x.Confident, blockers);

        if (state == MachineState.Unknown)
        {
            _failedAcquisitions++;
            return AcquireResult.Failure("Machine state is UNKNOWN; refusing to acquire.", state, blockers);
        }

        if (blockers.Count > 0)
        {
            _failedAcquisitions++;
            var reason = resources == ResourceSet.All
                ? "Machine currently unavailable"
                : $"Requested resources in use: {string.Join(", ", Resources.ToNames(Occupied(blockers) & resources))}";
            return AcquireResult.Failure(reason, state, blockers);
        }

        // First come, first served: a legacy caller can't take resources someone is waiting for.
        var waiting = x.Line.Where(w => Resources.Overlaps(w.Resources, resources)).ToList();
        if (waiting.Count > 0)
        {
            _failedAcquisitions++;
            return AcquireResult.Failure(
                $"Requested resources are promised to {waiting.Count} claim(s) waiting in line", MachineState.Busy, blockers);
        }

        var seconds = _lease.Resolve(request.LeaseSeconds);
        var reservation = new Reservation(
            id: NewId(),
            owner: request.Owner,
            description: request.Description ?? string.Empty,
            createdAt: now,
            expiresAt: now.AddSeconds(seconds),
            metadata: CloneMetadata(request.Metadata),
            resources: resources)
        {
            Kind = ClaimKind.Exclusive,
            WhenBusy = WhenBusy.Refuse,
            Seq = ++_seq,
            LeaseSeconds = seconds,
            Holding = true,
            Started = true,
            StartedAt = now,
        };

        _reservations[reservation.Id] = reservation;
        _totalAcquisitions++;
        // Lifecycle events are rare, so logging under the lock is cheap here and keeps the
        // audit trail ordered.
        _log.Info($"Reservation acquired: {request.Owner} \"{Trunc(reservation.Description)}\" [{Short(reservation.Id)}] {Names(resources)} lease {seconds}s");
        return AcquireResult.Success(reservation);
    }

    /// <summary>
    /// Renews a claim's lease and returns its current advice: this is how running work notices
    /// a game starting or stopping, and how a queued ticket checks in. The id acts as a bearer
    /// capability.
    /// </summary>
    /// <param name="probe">
    /// Check in and report the advice without starting the claim: what a background waiter
    /// (<c>cw wait</c>) sends. A ticket whose turn has come stays unstarted, with only the
    /// check-in window, until its owner renews; so an owner that never starts passes the slot on.
    /// </param>
    public RenewResult Renew(string reservationId, int? leaseSeconds = null, bool probe = false)
    {
        lock (_gate)
        {
            var x = BuildLocked();
            if (string.IsNullOrEmpty(reservationId) ||
                !_reservations.TryGetValue(reservationId, out var r))
            {
                return RenewResult.Failure("Reservation no longer exists");
            }

            var now = _clock.UtcNow;
            var advice = Evaluate(r, x, now);
            if (probe && !r.Started && advice.Grant != Grant.Queued)
            {
                r.LastRenewedAt = now;
                r.ExpiresAt = now.AddSeconds(Math.Max(1, _sharing.QueueCheckinSeconds));
                _log.Debug($"Waiter told {GrantName(advice.Grant)}: [{Short(r.Id)}] has until {r.ExpiresAt:o} to start");
                return RenewResult.Success(r.ExpiresAt, advice);
            }
            var previous = r.LastGrant;
            var wasStarted = r.Started;
            Apply(r, advice, now, leaseSeconds);

            if (advice.Grant != previous || r.Started != wasStarted)
                _log.Info($"Claim now {GrantName(advice.Grant)}: {r.Owner} [{Short(r.Id)}]{Because(advice)}");
            else
                _log.Debug($"Reservation renewed: [{Short(r.Id)}] expires {r.ExpiresAt:o}");

            return RenewResult.Success(r.ExpiresAt, r.WhenBusy == WhenBusy.Refuse && advice.Grant == Grant.Full ? null : advice);
        }
    }

    /// <summary>
    /// The grant a claim would get right now, without renewing it; null if it no longer exists.
    /// Lets a long-polling renew wait for a change (a game exiting, a holder releasing).
    /// </summary>
    public Grant? PeekGrant(string reservationId)
    {
        lock (_gate)
        {
            var x = BuildLocked();
            if (string.IsNullOrEmpty(reservationId) || !_reservations.TryGetValue(reservationId, out var r))
                return null;
            return Evaluate(r, x, _clock.UtcNow).Grant;
        }
    }

    /// <summary>The grant last given to a claim; null if it no longer exists.</summary>
    public Grant? LastGrant(string reservationId)
    {
        lock (_gate)
            return !string.IsNullOrEmpty(reservationId) && _reservations.TryGetValue(reservationId, out var r)
                ? r.LastGrant
                : null;
    }

    /// <summary>
    /// Starts a long-polling renew: keeps the claim alive for the wait (the renew itself only
    /// lands at the end), and returns the grant it was last given; null if it no longer exists.
    /// </summary>
    public Grant? BeginWait(string reservationId, int waitSeconds)
    {
        lock (_gate)
        {
            PruneExpiredLocked();
            if (string.IsNullOrEmpty(reservationId) || !_reservations.TryGetValue(reservationId, out var r))
                return null;
            var until = _clock.UtcNow.AddSeconds(waitSeconds + WaitMarginSeconds);
            if (r.ExpiresAt < until) r.ExpiresAt = until;
            return r.LastGrant;
        }
    }

    /// <summary>Slack on top of a long-poll's wait, covering the reply's trip back.</summary>
    private const int WaitMarginSeconds = 15;

    /// <summary>Releases a claim. Idempotent — releasing an unknown id is not an error.</summary>
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

    // ---- User control ---------------------------------------------------

    /// <summary>
    /// Sets the machine-wide user mode. Only the user should do this (agents only on the
    /// user's explicit word). <paramref name="durationSeconds"/> reverts to auto afterwards.
    /// </summary>
    public ModeState SetMode(UserMode mode, ResourceSet resources = ResourceSet.All, int? durationSeconds = null, string? reason = null)
    {
        if (reason is { Length: > 0 } && reason.Length > _limits.MaxReasonLength)
            throw new ArgumentException($"reason exceeds {_limits.MaxReasonLength} characters", nameof(reason));
        if (durationSeconds is < 1 or > MaxModeSeconds)
            throw new ArgumentException($"durationSeconds must be between 1 and {MaxModeSeconds}", nameof(durationSeconds));

        lock (_gate)
        {
            var now = _clock.UtcNow;
            _mode = mode == UserMode.Auto
                ? ModeState.Auto(now)
                : new ModeState(
                    mode,
                    resources == ResourceSet.None ? ResourceSet.All : resources,
                    string.IsNullOrWhiteSpace(reason) ? null : reason,
                    now,
                    durationSeconds is int d ? now.AddSeconds(d) : null);

            var until = _mode.ExpiresAt is { } e ? $" until {e:o}" : "";
            var why = _mode.Reason is null ? "" : $": {Trunc(_mode.Reason)}";
            _log.Info($"User mode set to {ModeName(_mode.Mode)} {Names(_mode.Resources)}{until}{why}");
            return _mode;
        }
    }

    /// <summary>
    /// Claims that haven't started yet and are waiting their turn, first in line first. Running
    /// claims and exclusive claims already holding their resources aren't in it.
    /// </summary>
    public IReadOnlyList<ClaimInfo> GetWaiting()
    {
        lock (_gate)
        {
            var x = BuildLocked();
            var now = _clock.UtcNow;
            return WaitingLocked(x, now)
                .Select(r => Info(r, x) with { Phase = ClaimPhase.Waiting, LastCheckIn = r.LastRenewedAt })
                .ToList();
        }
    }

    /// <summary>
    /// Moves a waiting claim in the line. Only the user should do this. The waiting claims keep
    /// their arrival numbers between them, handed out in the new order, so every queue rule (the
    /// exclusive line, throttle-slot tickets) sees the new order. User-priority claims stay first:
    /// moving a claim in among them gives it priority, and moving one out of them takes it away.
    /// Returns the claim's new place (1 = next), or null if it isn't waiting.
    /// </summary>
    public int? MoveInLine(string reservationId, LineMove move)
    {
        lock (_gate)
        {
            PruneExpiredLocked();
            var waiting = WaitingLocked(BuildLocked(), _clock.UtcNow);
            var from = waiting.FindIndex(r => r.Id == reservationId);
            if (from < 0) return null;
            var to = move switch
            {
                LineMove.Front => 0,
                LineMove.Up => Math.Max(0, from - 1),
                LineMove.Down => Math.Min(waiting.Count - 1, from + 1),
                _ => throw new ArgumentOutOfRangeException(nameof(move)),
            };
            if (to == from) return to + 1;

            var seqs = waiting.Select(r => r.Seq).OrderBy(s => s).ToList();
            var c = waiting[from];
            waiting.RemoveAt(from);
            waiting.Insert(to, c);
            if (c.UserPriority is null && to + 1 < waiting.Count && waiting[to + 1].UserPriority is not null)
                c.UserPriority = "moved up the line by the user";
            else if (c.UserPriority is not null && to > 0 && waiting[to - 1].UserPriority is null)
                c.UserPriority = null;
            for (int i = 0; i < waiting.Count; i++) waiting[i].Seq = seqs[i];

            _log.Info($"User moved {c.Owner} \"{Trunc(c.Description)}\" [{Short(c.Id)}] {MoveName(move)} the line: now #{to + 1} of {waiting.Count}");
            return to + 1;
        }
    }

    private static string MoveName(LineMove move) => move switch
    {
        LineMove.Front => "to the front of",
        LineMove.Up => "up",
        _ => "down",
    };

    // The line as the user sees it: claims still waiting for their turn, first in line first.
    private List<Reservation> WaitingLocked(Context x, DateTimeOffset now)
        => _reservations.Values
            .Where(r => PhaseLocked(r, x, now) == ClaimPhase.Waiting)
            .OrderBy(r => r.UserPriority is null ? 1 : 0)
            .ThenBy(r => r.Seq)
            .ToList();

    // A claim whose turn has come but that hasn't started (an exclusive one promoted to hold, or a
    // ticket that heard its turn through `cw wait`) keeps its resources until it starts or lapses.
    private ClaimPhase PhaseLocked(Reservation r, Context x, DateTimeOffset now)
    {
        if (r.Started) return ClaimPhase.Running;
        if (r.Holding || Evaluate(r, x, now).Grant != Grant.Queued) return ClaimPhase.Ready;
        return ClaimPhase.Waiting;
    }

    public ModeState GetMode()
    {
        lock (_gate)
        {
            ExpireModeLocked(_clock.UtcNow);
            return _mode;
        }
    }

    public Blocker SetManualBusy(string reason, ResourceSet resources = ResourceSet.All)
    {
        if (reason is null) throw new ArgumentNullException(nameof(reason));
        if (reason.Length > _limits.MaxReasonLength)
            throw new ArgumentException($"reason exceeds {_limits.MaxReasonLength} characters", nameof(reason));
        return _manual.Set(reason, resources == ResourceSet.None ? ResourceSet.All : resources);
    }

    public bool ClearManualBusy() => _manual.Clear();

    // ---- Maintenance ----------------------------------------------------

    /// <summary>Applies hot-reloaded sharing settings.</summary>
    public void UpdateSharing(SharingOptions sharing)
    {
        lock (_gate) _sharing = sharing;
    }

    /// <summary>
    /// Expires stale claims, ends a timed user mode, and promotes waiters whose resources have
    /// freed up. Called by the daemon's low-frequency sweep.
    /// </summary>
    public void Sweep()
    {
        lock (_gate) BuildLocked();
    }

    // ---- The rules ------------------------------------------------------

    /// <summary>Everything one decision needs, computed once per operation under the lock.</summary>
    private sealed class Context
    {
        public bool Confident = true;
        public List<Blocker> Blockers = new();
        public List<Blocker> ProcessBlockers = new();
        public List<Blocker> ManualBlockers = new();
        public ResourceSet Priority;
        public ResourceSet Hold;
        public ResourceSet Steady;
        public ResourceSet Open;
        public List<Reservation> Holders = new();
        public List<Reservation> Line = new();
    }

    /// <summary>
    /// Prunes, collects blockers, and runs the promotion pass: walking the line in order, a
    /// waiter takes its resources if nothing holds them and nobody earlier is waiting for them.
    /// </summary>
    private Context BuildLocked()
    {
        var now = _clock.UtcNow;
        PruneExpiredLocked();
        ExpireModeLocked(now);

        var x = new Context();
        foreach (var provider in _providers)
        {
            var result = provider.GetBlockers();
            if (!result.Confident) x.Confident = false;
            foreach (var b in result.Blockers)
            {
                if (b.Type == BlockerType.Process) { x.ProcessBlockers.Add(b); x.Priority |= b.Resources; }
                else { x.ManualBlockers.Add(b); x.Hold |= b.Resources; }
            }
        }

        switch (_mode.Mode)
        {
            case UserMode.Hold: x.Hold |= _mode.Resources; break;
            case UserMode.Steady: x.Steady = _mode.Resources; break;
            case UserMode.Open: x.Open = _mode.Resources; break;
        }

        x.Holders = _reservations.Values.Where(r => r.Holding).ToList();
        x.Line = _reservations.Values
            .Where(r => r.InLine)
            .OrderBy(r => r.UserPriority is null ? 1 : 0)
            .ThenBy(r => r.Seq)
            .ToList();

        if (x.Confident)
        {
            var held = Occupied(x.Holders);
            var waitedFor = ResourceSet.None;
            var unavailable = x.Hold | x.Priority | x.Steady;
            foreach (var w in x.Line.ToList())
            {
                if (Resources.Overlaps(w.Resources, unavailable | held | waitedFor))
                {
                    waitedFor |= w.Resources;
                    continue;
                }
                w.Holding = true;
                held |= w.Resources;
                x.Holders.Add(w);
                x.Line.Remove(w);
                // A brand-new claim taking free resources is logged by Acquire itself.
                if (w.Started || w.LastGrant == Grant.Queued)
                    _log.Info($"Claim promoted to hold: {w.Owner} \"{Trunc(w.Description)}\" [{Short(w.Id)}] {Names(w.Resources)}");
            }
        }

        foreach (var h in x.Holders) x.Blockers.Add(h.ToBlocker());
        x.Blockers.AddRange(x.ProcessBlockers);
        x.Blockers.AddRange(x.ManualBlockers);
        if (_mode.Mode is UserMode.Hold or UserMode.Steady)
        {
            var why = _mode.Reason is null ? "" : $": {_mode.Reason}";
            x.Blockers.Add(new Blocker(
                Id: "mode",
                Type: BlockerType.Manual,
                Source: "UserMode",
                Description: $"User set {ModeName(_mode.Mode)} mode{why}",
                Owner: "user",
                CreatedAt: _mode.SetAt,
                ExpiresAt: _mode.ExpiresAt,
                Resources: _mode.Resources));
        }
        return x;
    }

    /// <summary>The advice for one claim given the current state. Pure: changes nothing.</summary>
    private Advice Evaluate(Reservation c, Context x, DateTimeOffset now)
    {
        var res = c.Resources;
        var ahead = c.InLine ? x.Line.TakeWhile(w => w != c).Where(w => Resources.Overlaps(w.Resources, res)).ToList() : new();

        // Work that hasn't started is told to wait. Work already running can't un-start, so
        // the most it's asked is to run small.
        Advice Defer(ResourceSet contended, string? reason)
            => c.Started ? Throttled(contended, reason) : Queued(c, x, ahead, contended, now, reason);

        // Holds are known even when the process list isn't, so they come first.
        var hold = x.Hold & res;
        if (hold != ResourceSet.None)
            return Defer(hold, HoldReason(x, hold));

        if (!x.Confident)
        {
            // A game may be running unseen: treat everything as contended by one.
            var unknown = new List<string> { "machine state is UNKNOWN (the process list can't be read)" };
            if (c.Kind == ClaimKind.Exclusive) return Defer(res, Join(unknown));
            return Contended(c, x, ahead, res, priorityLike: res, steadyOnly: ResourceSet.None, unknown, now);
        }

        var priority = x.Priority & res;
        var steady = x.Steady & res;
        var others = ResourceSet.None;
        foreach (var h in x.Holders)
            if (h != c) others |= h.Resources;
        others &= res;

        ResourceSet contended;
        var steadyOnly = ResourceSet.None; // slowed only by steady mode: nobody else is on it
        var reasons = new List<string>();
        if (c.Kind == ClaimKind.Exclusive && !c.Holding)
        {
            var waitedFor = Occupied(ahead) & res;
            contended = priority | steady | others | waitedFor;
            if (contended == ResourceSet.None) contended = res; // not promoted, so something is in the way
            steadyOnly = steady & ~(priority | others | waitedFor);
            Explain(reasons, x, c, priority, steady, others);
            if (ahead.Count > 0) reasons.Add($"{ahead.Count} ahead in line");
            if (c.WhenBusy == WhenBusy.Wait)
                return Defer(contended, Join(reasons));
        }
        else if (c.Kind == ClaimKind.Exclusive)
        {
            contended = priority | steady;
            steadyOnly = steady & ~priority;
            Explain(reasons, x, c, priority, steady, ResourceSet.None);
            // Promoted before it checked in, then a game or steady mode arrived: it asked never to run small.
            if (!c.Started && c.WhenBusy == WhenBusy.Wait && contended != ResourceSet.None)
                return Queued(c, x, ahead, contended, now, Join(reasons));
        }
        else
        {
            var blocking = others & ~x.Open;
            contended = priority | steady | blocking;
            steadyOnly = steady & ~(priority | blocking);
            Explain(reasons, x, c, priority, steady, blocking);
        }

        if (contended == ResourceSet.None) return Advice.Full;
        return Contended(c, x, ahead, contended, priority | steady, steadyOnly, reasons, now);
    }

    /// <summary>
    /// Advice for a claim that can't run full on <paramref name="contended"/>: throttled, or
    /// queued when the resource can't be shared or its throttle slots are full.
    /// <paramref name="priorityLike"/> is the part a game, steady mode, or UNKNOWN causes, which open mode never lifts.
    /// <paramref name="steadyOnly"/> is slowed only by steady mode: nothing else uses it, so even the gpu runs (small).
    /// </summary>
    private Advice Contended(Reservation c, Context x, List<Reservation> ahead, ResourceSet contended,
        ResourceSet priorityLike, ResourceSet steadyOnly, List<string> reasons, DateTimeOffset now)
    {
        var sharing = _sharing;
        var unshareable = contended & sharing.Unshareable & ~steadyOnly;
        if (unshareable != ResourceSet.None)
        {
            reasons.Add($"{string.Join("/", Resources.ToNames(unshareable))} can't be shared right now");
            return c.Started ? Throttled(contended, Join(reasons)) : Queued(c, x, ahead, contended, now, Join(reasons));
        }

        // The cap only limits new starts: running work keeps going (throttled at worst).
        // Open mode lifts it between agents, but never where a game (or steady/UNKNOWN) contends.
        var capped = priorityLike | (contended & ~x.Open);
        if (!c.Started && capped != ResourceSet.None)
        {
            var waiting = EarlierWaiting(c, capped);
            var full = FullSlots(c, x, capped, waiting, sharing.MaxThrottledPerResource);
            if (full != ResourceSet.None)
            {
                reasons.Add($"throttle slots full on {string.Join("/", Resources.ToNames(full))} ({sharing.MaxThrottledPerResource} per resource)");
                ahead = ahead.Union(waiting).OrderBy(r => r.UserPriority is null ? 1 : 0).ThenBy(r => r.Seq).ToList();
                return Queued(c, x, ahead, contended, now, Join(reasons), slotWait: capped);
            }
        }

        return Throttled(contended, Join(reasons));
    }

    /// <summary>Records the advice given and sets the lease: tickets get the check-in window, running work its lease.</summary>
    private void Apply(Reservation r, Advice advice, DateTimeOffset now, int? renewSeconds)
    {
        r.LastGrant = advice.Grant;
        r.LastContended = advice.Grant == Grant.Throttled ? advice.Contended : ResourceSet.None;
        r.SlotWait = advice.Grant == Grant.Queued ? advice.SlotWait : ResourceSet.None;
        r.LastRenewedAt = now;
        if (!r.Started && advice.Grant != Grant.Queued)
        {
            r.Started = true;
            r.StartedAt = now;
        }

        int seconds;
        if (!r.Started) seconds = Math.Max(1, _sharing.QueueCheckinSeconds);
        else if (r.WhenBusy == WhenBusy.Refuse) seconds = _lease.Resolve(renewSeconds); // legacy: renew defaults as before
        else seconds = renewSeconds is null ? r.LeaseSeconds : _lease.Resolve(renewSeconds);
        r.ExpiresAt = now.AddSeconds(seconds);
    }

    private void Explain(List<string> reasons, Context x, Reservation c, ResourceSet priority, ResourceSet steady, ResourceSet others)
    {
        foreach (var p in x.ProcessBlockers)
            if (Resources.Overlaps(p.Resources, priority))
                reasons.Add(p.Description);
        if (steady != ResourceSet.None)
            reasons.Add(_mode.Reason is null ? "user set steady mode" : $"user set steady mode: {_mode.Reason}");
        if (others != ResourceSet.None)
            foreach (var h in x.Holders)
                if (h != c && Resources.Overlaps(h.Resources, others))
                    reasons.Add($"{h.Owner} holds {string.Join("/", Resources.ToNames(h.Resources & others))} ({Trunc(h.Description)})");
    }

    private string HoldReason(Context x, ResourceSet hold)
    {
        var reasons = new List<string>();
        foreach (var m in x.ManualBlockers)
            if (Resources.Overlaps(m.Resources, hold))
                reasons.Add($"manual hold: {m.Description}");
        if (_mode.Mode == UserMode.Hold && Resources.Overlaps(_mode.Resources, hold))
            reasons.Add(_mode.Reason is null ? "user set hold mode" : $"user set hold mode: {_mode.Reason}");
        return Join(reasons) + "; don't start new work on these resources";
    }

    /// <summary>
    /// Tickets of either kind still waiting for a throttle slot on <paramref name="capped"/> that
    /// are ahead of <paramref name="c"/>: earlier arrivals, and user-priority claims before everyone
    /// else. A newcomer doesn't take a freed slot ahead of them. Tickets queued for some other
    /// reason (an unshareable gpu, a hold) aren't waiting for a slot, so they don't count.
    /// </summary>
    private List<Reservation> EarlierWaiting(Reservation c, ResourceSet capped)
        => _reservations.Values
            .Where(r => r != c && !r.Started && r.LastGrant == Grant.Queued &&
                        Resources.Overlaps(r.SlotWait, capped) && Ahead(r, c))
            .OrderBy(r => r.UserPriority is null ? 1 : 0)
            .ThenBy(r => r.Seq)
            .ToList();

    /// <summary>Line order: user-priority claims first, then first come, first served.</summary>
    private static bool Ahead(Reservation r, Reservation c)
    {
        var rFirst = r.UserPriority is not null;
        var cFirst = c.UserPriority is not null;
        return rFirst != cFirst ? rFirst : r.Seq < c.Seq;
    }

    /// <summary>
    /// The resources in <paramref name="capped"/> whose throttle slots are all taken, counted per
    /// resource: tickets ahead waiting for one, running work throttled on it, and running work a
    /// game (or steady mode, or UNKNOWN) now contends there, which turns throttled at its next check-in.
    /// </summary>
    private ResourceSet FullSlots(Reservation c, Context x, ResourceSet capped, List<Reservation> waiting, int max)
    {
        var slowing = x.Priority | x.Steady | (x.Confident ? ResourceSet.None : ResourceSet.All);
        var full = ResourceSet.None;
        foreach (var bit in Bits(capped))
        {
            var used = waiting.Count(r => Resources.Overlaps(r.SlotWait, bit)) + _reservations.Values.Count(r =>
                r != c && r.Started && (r.LastGrant == Grant.Throttled
                    ? Resources.Overlaps(r.LastContended, bit)
                    : Resources.Overlaps(r.Resources & slowing, bit)));
            if (used >= max) full |= bit;
        }
        return full;
    }

    private static IEnumerable<ResourceSet> Bits(ResourceSet set)
    {
        for (var bit = 1; bit != 0 && bit <= (int)set; bit <<= 1)
            if (((int)set & bit) != 0) yield return (ResourceSet)bit;
    }

    private Advice Throttled(ResourceSet contended, string? reason)
    {
        var s = _sharing;
        var cpu = Resources.Overlaps(contended, ResourceSet.Cpu);
        var ram = Resources.Overlaps(contended, ResourceSet.Ram);
        var throttle = new ThrottleAdvice(
            MaxCores: cpu ? s.ThrottleMaxCores : null,
            Priority: cpu ? s.ThrottlePriority : null,
            MaxRamGb: ram ? s.ThrottleMaxRamGb : null);
        return new Advice(Grant.Throttled, reason, throttle, HowText(contended, s), null, contended);
    }

    private Advice Queued(Reservation c, Context x, List<Reservation> ahead, ResourceSet contended, DateTimeOffset now, string? reason,
        ResourceSet slotWait = ResourceSet.None)
    {
        int? position = c.InLine || ahead.Count > 0 || slotWait != ResourceSet.None ? ahead.Count + 1 : null;

        // Waits behind a game, a hold, or steady mode (directly or through a claim ahead) have no end we can predict.
        int? estimate = null;
        if (!Resources.Overlaps(c.Resources | Occupied(ahead), x.Priority | x.Hold | x.Steady) && x.Confident)
        {
            long remaining = 0; // long: estimates are only checked for >= 0, so their sum can pass int.MaxValue
            foreach (var h in x.Holders)
                if (h != c && Resources.Overlaps(h.Resources, c.Resources))
                    remaining = Math.Max(remaining, Remaining(h, now));
            foreach (var a in ahead)
                remaining += a.EstimateSeconds ?? a.LeaseSeconds;
            estimate = (int)Math.Min(remaining, int.MaxValue);
        }

        var queue = new QueueInfo(position, ahead.Select(a => Info(a, x)).ToList(), estimate);
        return new Advice(Grant.Queued, reason, null, "Don't start yet. Check back by renewing this reservation.", queue, contended)
        {
            SlotWait = slotWait,
        };
    }

    private static long Remaining(Reservation h, DateTimeOffset now)
    {
        var leaseLeft = Math.Max(0, (long)(h.ExpiresAt - now).TotalSeconds);
        if (!h.Started) return leaseLeft + (h.EstimateSeconds ?? h.LeaseSeconds); // promoted, not checked in yet
        if (h.EstimateSeconds is int e && h.StartedAt is { } started)
            return Math.Clamp((long)(started.AddSeconds(e) - now).TotalSeconds, 0, leaseLeft);
        return leaseLeft;
    }

    private static string HowText(ResourceSet contended, SharingOptions s)
    {
        var parts = new List<string>();
        if (Resources.Overlaps(contended, ResourceSet.Cpu))
        {
            var n = s.ThrottleMaxCores;
            parts.Add($"use at most {n} core{(n == 1 ? "" : "s")} at {s.ThrottlePriority.Replace('_', '-')} priority " +
                      $"(e.g. dotnet build -m:{n}, dotnet test -- RunConfiguration.MaxCpuCount={n}, make -j{n})");
            // Windows start's priority switches; an unrecognized setting just gets no example.
            var startSwitch = s.ThrottlePriority.Replace("-", "_").ToLowerInvariant() switch
            {
                "below_normal" => "/belownormal",
                "idle" or "low" => "/low",
                _ => null,
            };
            if (startSwitch is not null)
                parts.Add($"to lower the priority, run it as: cmd /c start \"\" /b /wait {startSwitch} <command>");
        }
        if (Resources.Overlaps(contended, ResourceSet.Ram))
            parts.Add($"keep memory under {s.ThrottleMaxRamGb.ToString(CultureInfo.InvariantCulture)} GB (no large parallel test hosts or in-memory datasets)");
        if (Resources.Overlaps(contended, ResourceSet.Disk))
            parts.Add("avoid heavy disk I/O (no clean rebuilds, full scans, or large copies)");
        if (Resources.Overlaps(contended, ResourceSet.Network))
            parts.Add("avoid large downloads or uploads");
        if (Resources.Overlaps(contended, ResourceSet.Gpu))
            parts.Add("keep off the GPU");
        var text = string.Join("; ", parts);
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..] + ".";
    }

    private ClaimInfo Info(Reservation r, Context x)
    {
        int? position = r.InLine ? x.Line.IndexOf(r) + 1 : null;
        if (position == 0) position = null;
        return new ClaimInfo(r.Id, r.Owner, r.Description, r.Kind, r.Resources, r.LastGrant, r.Holding,
            r.ExpiresAt, r.EstimateSeconds, r.Cores, r.RamGb, r.UserPriority, position);
    }

    // ---- Internals ------------------------------------------------------

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

    private static ResourceSet Occupied(IEnumerable<Reservation> claims)
    {
        var set = ResourceSet.None;
        foreach (var c in claims) set |= c.Resources;
        return set;
    }

    private static string Names(ResourceSet set) => "[" + string.Join(",", Resources.ToNames(set)) + "]";

    private static string? Join(List<string> reasons) => reasons.Count == 0 ? null : string.Join("; ", reasons);

    private static string Because(Advice a) => a.Reason is null ? "" : $" ({Trunc(a.Reason)})";

    private static int? LeaseFromEstimate(int? estimateSeconds)
        => estimateSeconds is int e and > 0 ? (int)Math.Min(int.MaxValue, Math.Ceiling(e * 1.25) + 30) : null;

    public static string KindName(ClaimKind kind) => kind == ClaimKind.Shared ? "shared" : "exclusive";

    public static string GrantName(Grant grant) => grant switch
    {
        Grant.Full => "full",
        Grant.Throttled => "throttled",
        _ => "queued",
    };

    public static string ModeName(UserMode mode) => mode switch
    {
        UserMode.Steady => "steady",
        UserMode.Open => "open",
        UserMode.Hold => "hold",
        _ => "auto",
    };

    private static MachineState DetermineState(bool confident, IReadOnlyList<Blocker> blockers)
        => !confident ? MachineState.Unknown
         : blockers.Count > 0 ? MachineState.Busy
         : MachineState.Available;

    private void ExpireModeLocked(DateTimeOffset now)
    {
        if (_mode.ExpiresAt is { } e && e <= now)
        {
            _log.Info($"User mode {ModeName(_mode.Mode)} ended (time up); back to auto");
            _mode = ModeState.Auto(now);
        }
    }

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
            if (!r.Started)
            {
                // A ticket that stopped checking in: its agent gave up or crashed. Not a misuse
                // of a lease, since it never ran.
                _log.Info($"Queue ticket dropped (no check-in): {r.Owner} \"{Trunc(r.Description)}\" [{Short(r.Id)}]");
                continue;
            }
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

    private bool TryValidate(ClaimRequest request, out string? error)
    {
        error = null;
        var owner = request.Owner;
        var description = request.Description;
        var metadata = request.Metadata;

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
        if (request.UserPriority is { Length: > 0 } up && up.Length > _limits.MaxReasonLength)
        {
            error = $"userPriority exceeds {_limits.MaxReasonLength} characters";
            return false;
        }
        if (request.EstimateSeconds is < 0 || request.Cores is < 0 || request.RamGb is < 0 || request.RamGb is double.NaN)
        {
            error = "estimateSeconds, cores, and ramGb must not be negative";
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
