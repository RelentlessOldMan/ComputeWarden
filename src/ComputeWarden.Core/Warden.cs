using System.Globalization;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Diagnostics;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;
using ComputeWarden.Core.Time;

namespace ComputeWarden.Core;

/// <summary>
/// The authoritative, thread-safe coordinator. Holds agent claims and answers "how big may
/// this work run right now?". Everything runs under one lock. Everything is advice: the warden
/// never caps or kills a process.
/// <para>Three rules (DESIGN.md §16):</para>
/// <list type="number">
/// <item><b>Gaming.</b> While a configured game runs (or the manual hold is on, or the process
/// list can't be read), running work is told to run throttled and at most
/// <see cref="GameJobs"/> claims run at once; the rest wait in line. A hold lets nothing new start.</item>
/// <item><b>Big memory.</b> Only one <see cref="ClaimRequest.BigMemory"/> claim runs at a time;
/// the others wait. Other work is unaffected.</item>
/// <item><b>Solo.</b> A solo claim (a timing run) starts once nothing else runs; nothing new
/// starts while it runs, and while it waits only work estimated to end before the running work
/// does. It lasts at most <see cref="MaxSoloSeconds"/>.</item>
/// </list>
/// <para>Otherwise everything runs full. The line is first come, first served: a claim that
/// can't start yet keeps its place, and a claim whose turn has come keeps the slot until its owner
/// starts or its ticket lapses.</para>
/// </summary>
public sealed class Warden
{
    public const int MaxGameJobs = 12;

    /// <summary>The longest a solo claim may hold the machine.</summary>
    public const int MaxSoloSeconds = 600;

    /// <summary>The longest a solo claim may hold the machine when the user approved a longer run.</summary>
    public const int MaxApprovedSoloSeconds = 3600;

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
    private int? _gameJobs; // the user's limit on jobs running beside a game; null = the config's
    private long _seq;
    private long _totalAcquisitions;
    private long _failedAcquisitions;
    private long _expiredReservations;

    /// <param name="manual">The manual hold (also exposed via set/clear helpers).</param>
    /// <param name="extraProviders">Other providers, e.g. the process monitor. Process blockers are games.</param>
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
        providers.Add(manual);
        _providers = providers;

        _startedAt = clock.UtcNow;
    }

    // ---- Read operations ------------------------------------------------

    /// <summary>Full state: BUSY while a game, the hold, or a solo claim has the machine.</summary>
    public StatusResult GetStatus()
    {
        lock (_gate)
        {
            var x = BuildLocked();
            var now = _clock.UtcNow;
            var state = DetermineState(x);
            var claims = _reservations.Values
                .OrderBy(r => r.Seq)
                .Select(r => Info(r, x) with { Phase = PhaseLocked(r, x), LastCheckIn = r.LastRenewedAt })
                .ToList();
            return new StatusResult(state, CanRunLocked(x), x.Blockers, claims, GameJobs: GameJobsLocked);
        }
    }

    /// <summary>
    /// Informational: would new work run full right now? MUST NOT be treated as a guarantee;
    /// agents doing work must Acquire.
    /// </summary>
    public bool CanRun()
    {
        lock (_gate) return CanRunLocked(BuildLocked());
    }

    private static bool CanRunLocked(Context x)
        => DetermineState(x) == MachineState.Available && !x.Plan.Values.Any(w => w is not null);

    public WardenStats GetStats()
    {
        lock (_gate)
        {
            var x = BuildLocked();
            return new WardenStats(
                _totalAcquisitions,
                _failedAcquisitions,
                _expiredReservations,
                _reservations.Count,
                x.Games.Count,
                _clock.UtcNow - _startedAt);
        }
    }

    // ---- Claim lifecycle ------------------------------------------------

    /// <summary>
    /// Creates a claim and returns the warden's advice for it: full, throttled, or queued.
    /// Only an invalid request is refused.
    /// </summary>
    public AcquireResult Acquire(ClaimRequest request)
    {
        lock (_gate)
        {
            if (!TryValidate(request, out var validationError))
            {
                _failedAcquisitions++;
                var bx = BuildLocked();
                return AcquireResult.Failure(validationError!, DetermineState(bx), bx.Blockers);
            }

            var now = _clock.UtcNow;
            var leaseSeconds = _lease.Resolve(request.LeaseSeconds ?? LeaseFromEstimate(request.EstimateSeconds));
            var soloLimit = SoloLimit(request);
            if (request.Kind == ClaimKind.Solo) leaseSeconds = Math.Min(leaseSeconds, soloLimit);
            var r = new Reservation(
                id: NewId(),
                owner: request.Owner,
                description: request.Description ?? string.Empty,
                createdAt: now,
                expiresAt: now.AddSeconds(leaseSeconds),
                metadata: CloneMetadata(request.Metadata))
            {
                Kind = request.Kind,
                BigMemory = request.BigMemory,
                EstimateSeconds = request.EstimateSeconds,
                SoloLimitSeconds = soloLimit,
                Seq = ++_seq,
                LeaseSeconds = leaseSeconds,
            };
            _reservations[r.Id] = r;
            _totalAcquisitions++;

            var x = BuildLocked();
            var advice = Evaluate(r, x);
            Apply(r, advice, now, renewSeconds: null);

            _log.Info($"Claim {Label(r)}: {r.Owner} \"{Trunc(r.Description)}\" [{Short(r.Id)}] -> {GrantName(advice.Grant)}{Because(advice)}");

            var after = BuildLocked();
            return AcquireResult.Claimed(r, advice, DetermineState(after), after.Blockers.Where(b => b.Id != r.Id).ToList());
        }
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
            if (string.IsNullOrEmpty(reservationId) || !_reservations.TryGetValue(reservationId, out var r))
                return RenewResult.Failure("Reservation no longer exists");

            var now = _clock.UtcNow;
            var advice = Evaluate(r, x);
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

            return RenewResult.Success(r.ExpiresAt, advice);
        }
    }

    /// <summary>
    /// The grant a claim would get right now, without renewing it; null if it no longer exists.
    /// Lets a long-polling renew wait for a change (a game exiting, a claim releasing).
    /// </summary>
    public Grant? PeekGrant(string reservationId)
    {
        lock (_gate)
        {
            var x = BuildLocked();
            if (string.IsNullOrEmpty(reservationId) || !_reservations.TryGetValue(reservationId, out var r))
                return null;
            return Evaluate(r, x).Grant;
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
    /// A solo claim isn't kept past its time.
    /// </summary>
    public Grant? BeginWait(string reservationId, int waitSeconds)
    {
        lock (_gate)
        {
            PruneExpiredLocked();
            if (string.IsNullOrEmpty(reservationId) || !_reservations.TryGetValue(reservationId, out var r))
                return null;
            var until = _clock.UtcNow.AddSeconds(waitSeconds + WaitMarginSeconds);
            if (SoloEnd(r) is { } end && until > end) until = end;
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
            if (!string.IsNullOrEmpty(reservationId) && _reservations.TryGetValue(reservationId, out var r))
            {
                _reservations.Remove(reservationId);
                _log.Info($"Reservation released: {r.Owner} [{Short(r.Id)}] held {Held(r, _clock.UtcNow)}");
                return ReleaseResult.Ok;
            }
            return ReleaseResult.NotFound();
        }
    }

    // ---- User control ---------------------------------------------------

    /// <summary>Claims waiting for their turn, first in line first. Running claims and claims whose turn has come aren't in it.</summary>
    public IReadOnlyList<ClaimInfo> GetWaiting()
    {
        lock (_gate)
        {
            var x = BuildLocked();
            return x.Order
                .Where(r => x.Plan[r] is not null)
                .Select(r => Info(r, x) with { Phase = ClaimPhase.Waiting, LastCheckIn = r.LastRenewedAt })
                .ToList();
        }
    }

    /// <summary>How many jobs may run beside a game: the user's setting, or the config's until they set one.</summary>
    public int GameJobs
    {
        get { lock (_gate) return GameJobsLocked; }
    }

    private int GameJobsLocked => _gameJobs ?? Math.Clamp(_sharing.GameJobs, 0, MaxGameJobs);

    /// <summary>
    /// Sets how many jobs may run beside a game. Only the user should do this. It limits new
    /// starts: lowering it makes the next jobs wait, and work already running keeps going.
    /// </summary>
    public int SetGameJobs(int jobs)
    {
        if (jobs is < 0 or > MaxGameJobs)
            throw new ArgumentOutOfRangeException(nameof(jobs), $"jobs while gaming must be between 0 and {MaxGameJobs}");
        lock (_gate)
        {
            _gameJobs = jobs;
            _log.Info($"Jobs while gaming set to {jobs}");
            return jobs;
        }
    }

    /// <summary>The manual hold: nothing new starts and running work is throttled, as if a game ran with no jobs allowed. The user's call.</summary>
    public Blocker SetManualBusy(string reason)
    {
        if (reason is null) throw new ArgumentNullException(nameof(reason));
        if (reason.Length > _limits.MaxReasonLength)
            throw new ArgumentException($"reason exceeds {_limits.MaxReasonLength} characters", nameof(reason));
        var b = _manual.Set(reason);
        _log.Info($"Manual hold on: {Trunc(reason)}");
        return b;
    }

    public bool ClearManualBusy()
    {
        var cleared = _manual.Clear();
        if (cleared) _log.Info("Manual hold off");
        return cleared;
    }

    // ---- Maintenance ----------------------------------------------------

    /// <summary>Applies hot-reloaded sharing settings.</summary>
    public void UpdateSharing(SharingOptions sharing)
    {
        lock (_gate) _sharing = sharing;
    }

    /// <summary>The state worth keeping across a restart: claims, the manual hold, and jobs while gaming.</summary>
    public SavedState Save()
    {
        lock (_gate)
        {
            var hold = _manual.GetBlockers().Blockers.FirstOrDefault();
            return new SavedState(
                SavedState.Version,
                hold is null ? null : new SavedHold(hold.Description, hold.CreatedAt ?? _clock.UtcNow),
                _reservations.Values.OrderBy(r => r.Seq).Select(r => new SavedClaim(
                    r.Id, r.Owner, r.Description, r.CreatedAt, r.LastRenewedAt, r.ExpiresAt,
                    r.Metadata.Count == 0 ? null : new Dictionary<string, string>(r.Metadata),
                    KindName(r.Kind), r.BigMemory, r.EstimateSeconds,
                    r.Seq, r.LeaseSeconds, r.Started, r.StartedAt, r.LastGrant,
                    r.SoloLimitSeconds == MaxSoloSeconds ? null : r.SoloLimitSeconds)).ToList(),
                _gameJobs);
        }
    }

    /// <summary>
    /// Puts back what <see cref="Save"/> kept, before the daemon starts serving: claims keep their
    /// ids, places in line and leases, so agents carry on as if nothing happened. Anything whose
    /// lease ran out while the daemon was down is dropped. Returns how many claims came back.
    /// </summary>
    public int Restore(SavedState state)
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;
            _gameJobs = state.GameJobs is >= 0 and <= MaxGameJobs ? state.GameJobs : null;
            if (state.Hold is { } hold) _manual.Set(hold.Reason, createdAt: hold.CreatedAt);
            foreach (var c in state.Claims ?? Array.Empty<SavedClaim>())
            {
                _reservations[c.Id] = new Reservation(c.Id, c.Owner, c.Description, c.CreatedAt, c.ExpiresAt, c.Metadata)
                {
                    LastRenewedAt = c.LastRenewedAt,
                    Kind = c.Kind == "solo" ? ClaimKind.Solo : ClaimKind.Shared,
                    BigMemory = c.BigMemory,
                    EstimateSeconds = c.EstimateSeconds,
                    SoloLimitSeconds = c.SoloLimitSeconds is int lim ? Math.Clamp(lim, 1, MaxApprovedSoloSeconds) : MaxSoloSeconds,
                    Seq = c.Seq,
                    LeaseSeconds = c.LeaseSeconds,
                    Started = c.Started,
                    StartedAt = c.StartedAt,
                    LastGrant = c.LastGrant,
                };
                _seq = Math.Max(_seq, c.Seq);
            }

            var kept = _reservations.Values.Count(r => r.ExpiresAt > now);
            var held = state.Hold is null ? "" : "; manual hold";
            var jobs = _gameJobs is int j ? $"; {j} job(s) while gaming" : "";
            _log.Info($"Restored {kept} claim(s) from the last run ({_reservations.Count - kept} expired while down){held}{jobs}");
            BuildLocked(); // drops what expired while down, logging it
            return _reservations.Count;
        }
    }

    /// <summary>Expires stale claims. Called by the daemon's low-frequency sweep.</summary>
    public void Sweep()
    {
        lock (_gate) BuildLocked();
    }

    // ---- The rules ------------------------------------------------------

    /// <summary>Why a claim can't start yet, and what it waits on (for the wait estimate).</summary>
    private sealed record Wait(string Reason, Reservation? On, bool OnRunning);

    /// <summary>Everything one decision needs, computed once per operation under the lock.</summary>
    private sealed class Context
    {
        public bool Confident = true;
        public List<Blocker> Games = new();
        public Blocker? Hold;
        public List<Blocker> Blockers = new();

        /// <summary>Claims not started yet, first come first.</summary>
        public List<Reservation> Order = new();

        /// <summary>For each claim not started: null when its turn has come, else why it waits.</summary>
        public Dictionary<Reservation, Wait?> Plan = new();

        /// <summary>A game, the hold, or UNKNOWN: running work is told to run small.</summary>
        public bool Slowed => Games.Count > 0 || Hold is not null || !Confident;
    }

    /// <summary>
    /// Prunes, collects games and the hold, and walks the line in arrival order deciding whose
    /// turn has come. A claim whose turn has come counts as running, so it keeps its slot until
    /// it starts or its ticket lapses.
    /// </summary>
    private Context BuildLocked()
    {
        PruneExpiredLocked();

        var x = new Context();
        foreach (var provider in _providers)
        {
            var result = provider.GetBlockers();
            if (!result.Confident) x.Confident = false;
            foreach (var b in result.Blockers)
            {
                if (b.Type == BlockerType.Process) x.Games.Add(b);
                else x.Hold ??= b;
            }
        }

        var running = _reservations.Values.Where(r => r.Started).ToList();
        var solo = running.FirstOrDefault(r => r.Kind == ClaimKind.Solo);
        var big = running.FirstOrDefault(r => r.BigMemory);
        Reservation? soloWaiting = null;
        DateTimeOffset? soloDeadline = null; // when the work a waiting solo claim waits for should end
        var now = _clock.UtcNow;
        var jobs = GameJobsLocked;

        x.Order = _reservations.Values.Where(r => !r.Started).OrderBy(r => r.Seq).ToList();
        foreach (var w in x.Order)
        {
            Wait? why;
            if (solo is not null)
                why = new($"{solo.Owner} has the machine to itself for a timing run ({Trunc(solo.Description)})", solo, false);
            else if (soloWaiting is not null && !FitsBefore(w, soloDeadline, now))
                why = new(soloDeadline is null
                    ? $"{soloWaiting.Owner} is waiting for a quiet machine ({Trunc(soloWaiting.Description)}); nothing new starts until it's done"
                    : $"{soloWaiting.Owner} is waiting for a quiet machine ({Trunc(soloWaiting.Description)}); only work whose estimateSeconds ends before the running work does may start first",
                    soloWaiting, true);
            else if (x.Hold is not null)
                why = new($"manual hold: {x.Hold.Description}; nothing new starts", null, false);
            else if (w.Kind == ClaimKind.Solo)
            {
                why = x.Slowed ? new($"{SlowReason(x)}; a timing run waits for a quiet machine", null, false)
                    : running.Count > 0 ? new($"waiting for {running.Count} running claim(s) to finish", null, true)
                    : null;
                if (why is not null)
                {
                    soloWaiting = w;
                    // Waiting on running work (not a game or the hold): short work may start in the gap.
                    if (!x.Slowed) soloDeadline = now.AddSeconds(running.Max(r => EndIn(r, now)));
                }
            }
            else if (x.Slowed && running.Count >= jobs)
                why = new(jobs == 0
                    ? $"{SlowReason(x)}; no jobs may run while gaming"
                    : $"{SlowReason(x)}; {jobs} job(s) may run while gaming, and all are taken", null, false);
            else if (w.BigMemory && big is not null)
                why = new($"{big.Owner} is running a big-memory job ({Trunc(big.Description)}); one at a time", big, false);
            else
                why = null;

            x.Plan[w] = why;
            if (why is not null) continue;
            running.Add(w);
            if (w.Kind == ClaimKind.Solo) solo = w;
            if (w.BigMemory) big ??= w;
        }

        x.Blockers.AddRange(x.Games);
        if (x.Hold is not null) x.Blockers.Add(x.Hold);
        if (solo is not null) x.Blockers.Add(solo.ToBlocker());
        return x;
    }

    /// <summary>The advice for one claim given the current state. Pure: changes nothing.</summary>
    private Advice Evaluate(Reservation c, Context x)
    {
        if (!c.Started && x.Plan.TryGetValue(c, out var why) && why is not null)
            return Queued(c, x, why);
        if (x.Slowed) return Throttled(SlowReason(x));
        return c.Kind == ClaimKind.Solo
            ? Advice.Full with { Reason = $"the machine is yours for at most {c.SoloLimitSeconds / 60} minutes; release as soon as the measured run ends" }
            : Advice.Full;
    }

    private static string SlowReason(Context x)
    {
        var reasons = new List<string>();
        foreach (var g in x.Games) reasons.Add(g.Description);
        if (x.Hold is not null) reasons.Add($"manual hold: {x.Hold.Description}");
        if (!x.Confident) reasons.Add("machine state is UNKNOWN (the process list can't be read)");
        return string.Join("; ", reasons);
    }

    /// <summary>Records the advice given and sets the lease: tickets get the check-in window, running work its lease.</summary>
    private void Apply(Reservation r, Advice advice, DateTimeOffset now, int? renewSeconds)
    {
        r.LastGrant = advice.Grant;
        r.LastRenewedAt = now;
        if (!r.Started && advice.Grant != Grant.Queued)
        {
            r.Started = true;
            r.StartedAt = now;
        }

        var expires = now.AddSeconds(!r.Started
            ? Math.Max(1, _sharing.QueueCheckinSeconds)
            : renewSeconds is null ? r.LeaseSeconds : _lease.Resolve(renewSeconds));
        if (SoloEnd(r) is { } end && expires > end) expires = end;
        r.ExpiresAt = expires;
    }

    /// <summary>When a started solo claim's time is up; null for anything else.</summary>
    private static DateTimeOffset? SoloEnd(Reservation r)
        => r.Kind == ClaimKind.Solo && r.StartedAt is { } s ? s.AddSeconds(r.SoloLimitSeconds) : null;

    private static int SoloLimit(ClaimRequest request)
        => request.UserApproved ? MaxApprovedSoloSeconds : MaxSoloSeconds;

    private Advice Throttled(string reason)
    {
        var s = _sharing;
        return new Advice(Grant.Throttled, reason,
            new ThrottleAdvice(s.ThrottleMaxCores, s.ThrottlePriority, s.ThrottleMaxRamGb), HowText(s), null);
    }

    private Advice Queued(Reservation c, Context x, Wait why)
    {
        // Ahead: earlier claims still waiting, except big-memory ones when this one isn't (they don't hold it up).
        var ahead = x.Order
            .TakeWhile(r => r != c)
            .Where(r => x.Plan[r] is not null && (c.BigMemory || !r.BigMemory || x.Plan[r]!.On is null || !x.Plan[r]!.On!.BigMemory))
            .ToList();
        if (why.On is { } on && !on.Started && !ahead.Contains(on) && on != c) ahead.Insert(0, on);

        // Waits behind a game or the hold have no end we can predict.
        int? estimate = null;
        if (!x.Slowed)
        {
            var now = _clock.UtcNow;
            long remaining = 0; // long: estimates are only checked for >= 0, so their sum can pass int.MaxValue
            IEnumerable<Reservation> blocking = why.OnRunning
                ? _reservations.Values.Where(r => r != c && (r.Started || x.Plan.GetValueOrDefault(r) is null) && r != why.On)
                : why.On is { Started: true } held ? new[] { held } : Array.Empty<Reservation>();
            foreach (var h in blocking)
                remaining = Math.Max(remaining, EndIn(h, now));
            foreach (var a in ahead)
                remaining += a.EstimateSeconds ?? a.LeaseSeconds;
            estimate = (int)Math.Min(remaining, int.MaxValue);
        }

        var queue = new QueueInfo(ahead.Count + 1, ahead.Select(a => Info(a, x)).ToList(), estimate);
        return new Advice(Grant.Queued, why.Reason, null, "Don't start yet. Check back by renewing this reservation.", queue);
    }

    /// <summary>
    /// Backfill: while a solo claim waits for running work, a claim may start if its estimate
    /// says it ends before that work does, so it doesn't delay the solo claim.
    /// </summary>
    private static bool FitsBefore(Reservation w, DateTimeOffset? deadline, DateTimeOffset now)
        => deadline is { } d && w.Kind != ClaimKind.Solo && w.EstimateSeconds is int e && now.AddSeconds(e) <= d;

    /// <summary>Seconds until a running claim should end; one whose turn has come counts its estimate from now.</summary>
    private static long EndIn(Reservation r, DateTimeOffset now)
        => r.Started ? Remaining(r, now) : r.EstimateSeconds ?? r.LeaseSeconds;

    private static long Remaining(Reservation h, DateTimeOffset now)
    {
        var leaseLeft = Math.Max(0, (long)(h.ExpiresAt - now).TotalSeconds);
        if (h.EstimateSeconds is int e && h.StartedAt is { } started)
            return Math.Clamp((long)(started.AddSeconds(e) - now).TotalSeconds, 0, leaseLeft);
        return leaseLeft;
    }

    private static string HowText(SharingOptions s)
    {
        var n = s.ThrottleMaxCores;
        var parts = new List<string>
        {
            $"use at most {n} core{(n == 1 ? "" : "s")} at {s.ThrottlePriority.Replace('_', '-')} priority " +
            $"(e.g. dotnet build -m:{n}, dotnet test -- RunConfiguration.MaxCpuCount={n}, make -j{n})",
        };
        // Windows start's priority switches; an unrecognized setting just gets no example.
        var startSwitch = s.ThrottlePriority.Replace("-", "_").ToLowerInvariant() switch
        {
            "below_normal" => "/belownormal",
            "idle" or "low" => "/low",
            _ => null,
        };
        if (startSwitch is not null)
            parts.Add($"to lower the priority, run it as: cmd /c start \"\" /b /wait {startSwitch} <command>");
        parts.Add($"keep memory under {s.ThrottleMaxRamGb.ToString(CultureInfo.InvariantCulture)} GB (no large parallel test hosts or in-memory datasets)");
        parts.Add("keep off the GPU");
        var text = string.Join("; ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..] + ".";
    }

    // A claim whose turn has come but that hasn't started keeps its slot until it starts or lapses.
    private static ClaimPhase PhaseLocked(Reservation r, Context x)
        => r.Started ? ClaimPhase.Running
         : x.Plan.GetValueOrDefault(r) is null ? ClaimPhase.Ready
         : ClaimPhase.Waiting;

    private static ClaimInfo Info(Reservation r, Context x)
    {
        int? position = null;
        if (!r.Started && x.Plan.GetValueOrDefault(r) is not null)
            position = x.Order.Where(o => x.Plan[o] is not null).TakeWhile(o => o != r).Count() + 1;
        return new ClaimInfo(r.Id, r.Owner, r.Description, r.Kind, r.BigMemory, r.LastGrant,
            r.ExpiresAt, r.EstimateSeconds, position, PhaseLocked(r, x));
    }

    // ---- Internals ------------------------------------------------------

    private static string Because(Advice a) => a.Reason is null ? "" : $" ({Trunc(a.Reason)})";

    private static string Label(Reservation r) => KindName(r.Kind) + (r.BigMemory ? ", big memory" : "")
        + (r.Kind == ClaimKind.Solo && r.SoloLimitSeconds != MaxSoloSeconds ? $", user-approved {r.SoloLimitSeconds / 60}m" : "");

    private static int? LeaseFromEstimate(int? estimateSeconds)
        => estimateSeconds is int e and > 0 ? (int)Math.Min(int.MaxValue, Math.Ceiling(e * 1.25) + 30) : null;

    public static string KindName(ClaimKind kind) => kind == ClaimKind.Solo ? "solo" : "shared";

    public static string GrantName(Grant grant) => grant switch
    {
        Grant.Full => "full",
        Grant.Throttled => "throttled",
        _ => "queued",
    };

    private static MachineState DetermineState(Context x)
        => !x.Confident ? MachineState.Unknown
         : x.Blockers.Count > 0 ? MachineState.Busy
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
            var why = SoloEnd(r) is { } end && end <= now ? $"solo time ({r.SoloLimitSeconds / 60}m) is up" : "owner did not release";
            _log.Info($"Reservation expired ({why}): {r.Owner} \"{Trunc(r.Description)}\" [{Short(r.Id)}] held {Held(r, now)}");
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
        if (request.EstimateSeconds is < 0)
        {
            error = "estimateSeconds must not be negative";
            return false;
        }
        if (request.Kind == ClaimKind.Solo && request.EstimateSeconds > SoloLimit(request))
        {
            error = request.UserApproved
                ? $"solo lasts at most {MaxApprovedSoloSeconds / 60} minutes, even with the user's approval; split the run or run it as shared"
                : $"solo is for short timing runs (at most {MaxSoloSeconds / 60} minutes); a longer one needs the user's explicit approval for that run, otherwise split it or run it as shared";
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
