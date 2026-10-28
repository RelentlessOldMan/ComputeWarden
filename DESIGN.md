# ComputeWarden — Design

> This document records the design decisions and why they were made. For usage, see the
> README. §1–§15 describe the original design; §16 is how claims work since 2.0, and wins
> where they differ.

## 1. Summary

ComputeWarden is a **cooperative, machine-wide traffic cop** for resource-intensive
work. A tool claims its build, test or run; the warden hands out an expiring **claim** with
**advice** (full, throttled, or queued), from a set of **blockers** (detected processes such
as games, the user's manual hold, a running timing run) and the other claims (§16). Nothing
is enforced — it coordinates well-behaved tools by convention.

## 2. Locked decisions

| Decision | Choice |
|---|---|
| Language / runtime | C# / **.NET 10** (the MCP SDK requires it; one target everywhere). Ship self-contained single-file exes so target machines don't need the runtime installed. |
| Deployment | Plain background **daemon**, no Windows Service, no installer |
| Daemon lifecycle | The first MCP adapter or `cw` call starts it if missing (race-safe via named mutex); **runs until reboot** (idle-timeout deferred) |
| Topology | Daemon = single source of truth; thin per-client MCP adapter forwards over a **named pipe** |
| IPC transport | Named pipe `\\.\pipe\ComputeWarden`, newline-delimited JSON, versioned |
| Claims | Shared by default; one big-memory job at a time; solo for short timing runs (§16). `reservation_id` is a **bearer capability** |
| Persistence | None for reservations/process blockers; config persisted; manual blocker persistence deferred |
| Portability | Windows-first; process enumeration + pipe path behind interfaces for a future Linux port |

## 3. State model

State is **derived**, never stored as a boolean:

```
blockers = ProcessProvider.Get() + ManualProvider.Get() + the running solo claim, if any

if any provider failed in a way that affects confidence:  UNKNOWN
elif blockers.Any():                                       BUSY
else:                                                      AVAILABLE
```

Shared and big-memory claims are not blockers: they never make the machine `BUSY`. `BUSY`
means a game or other configured program runs, the hold is on, or a timing run has the
machine.

- **UNKNOWN** is not a blocker; it means "couldn't compute the blocker set."
  It is fail-conservative: claims are treated as if a game were running (§16 rules).
- Debounce (`available_after_ms`) lives **inside** the ProcessProvider, so acquire
  automatically respects the "recently-seen" grace window without special-casing.

## 4. Blocker

```csharp
record Blocker(
    string   Id,
    BlockerType Type,      // Process | Reservation | Manual
    string   Source,       // e.g. "ProcessBlockerProvider", "ReservationManager"
    string   Description,  // human-readable
    string?  Owner,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ExpiresAt,
    IReadOnlyDictionary<string,string>? Metadata,
    ResourceSet Resources = ResourceSet.All); // kept for the wire; 2.0 ignores it
```

`IBlockerProvider { ProviderResult GetBlockers(); }` (blockers + a confidence flag) is the
extension seam for future measured CPU/disk/GPU providers.

## 5. Acquire algorithm (the critical correctness property)

Single lock, atomic check-and-create:

```
lock (state):
    expire stale claims
    blockers = collect from all providers        // uses last process snapshot
    r = new claim(id, owner, desc, kind, bigMemory, lease)
    claims.add(r)
    plan = one first-come-first-served pass over the claims that haven't started (§16 rules)
    advice = plan[r]                             // full, throttled, or queued
    if advice isn't queued: r starts now
    return { acquired:true, reservationId:r.Id, advice, expiresAt, blockers }
```

Notes:
- Only an invalid request (empty owner, a negative estimate, a solo over 10 minutes without `userApproved`, or over 60 with it,
  `kind: exclusive`, oversized fields) is refused. Every valid one gets a claim.
- Acquire checks the **last process poll snapshot** (up to `poll_interval_ms` stale) — it
  does *not* trigger a fresh enumeration. The cooperative contract only promises
  "known-safe at acquisition time".
- Lease seconds are clamped server-side to `[minimum_seconds, maximum_seconds]`, never below 1s.
- Because the pass runs under the lock, two big-memory claims arriving together can't both
  be told full: one runs, the other is first in line.
- A game starting *after* a claim starts doesn't revoke it; the claim's next renew says
  throttled.

## 6. Reservations & leases

- `renew(reservation_id, lease_seconds?)` → updates `last_renewed_at` + `expires_at`;
  fails cleanly if the id is unknown/expired.
- `release(reservation_id)` → idempotent; releasing an unknown/expired id returns
  `{ released:false, reason:"Reservation no longer exists" }`, not an error.
- `reservation_id` **is** the capability — whoever holds it may renew/release. Documented
  as such; no separate owner token in v1 (fine for a local cooperative tool).
- Expiry is checked lazily on every state read/acquire **and** by a low-frequency sweep
  timer so leases disappear even with no traffic.

## 7. Manual override

- `set_manual_busy(reason)` / `clear_manual_busy()` turn the user's hold on and off: a single
  MANUAL blocker, visually distinct in status. While it's on, running claims are throttled
  and nothing new starts (§16).
- May be disabled via config if only admins should control it.

## 8. Process monitoring

- Poll every `poll_interval_ms` (default 2000). Use a **cheap name-only enumeration**
  (`Process.GetProcesses()` reading `ProcessName`, no per-process handle inspection).
- Match against enabled `process_rules` by executable name (case-insensitive, with/without
  `.exe`; a directory in the rule is ignored). Command-line/regex matching are not implemented.
- Enumeration failure → provider reports low confidence → state UNKNOWN.
- **No log spam:** log only transitions (blocker appeared / cleared), never "still running."

## 9. IPC protocol (named pipe)

- Pipe: `\\.\pipe\ComputeWarden`. Restrict ACL to the current user/session.
- Wire: one JSON object per line (request), one per line (response). `v` field for version.
- Requests: `STATUS | CAN_RUN | ACQUIRE | RENEW | RELEASE | SET_MANUAL_BUSY | CLEAR_MANUAL_BUSY | STATS`.
- Server validates every input: string/metadata size caps, lease bounds, kinds, estimates;
  unknown ops get a structured error (never crash the pipe loop). Request lines are capped
  at 256K characters *while reading*, and idle connections are dropped after 30s.
- Versioning is additive: the daemon serves any `v` up to its own and ignores unknown
  fields, because the daemon is long-lived and adapters update independently.

```jsonc
// request
{ "v": 1, "op": "ACQUIRE", "owner": "Claude Code", "description": "dotnet build",
  "kind": "shared", "estimateSeconds": 90 }
// response
{ "v": 1, "ok": true, "acquired": true, "kind": "shared", "reservationId": "7f93…",
  "advice": { "grant": "full", … }, "expiresAt": "…", "machineState": "AVAILABLE", "blockers": [] }
```

## 10. MCP adapter (`ComputeWarden.Mcp`)

- Stdio MCP server (one per client, spawned by Claude Code et al.). stdout is the JSON-RPC
  transport, so all adapter logging goes to stderr.
- On a tool call: probe the pipe; if nothing is listening, launch the daemon detached and
  wait (up to 8s) for it to serve. Duplicate launches are harmless: the daemon's
  single-instance mutex (`Global\ComputeWardenDaemon`) makes extras exit immediately. An
  ACL-denied pipe (daemon owned by another user / elevated) is reported, not "fixed" by
  spawning.
- Exposes one tool per daemon operation, plus `computewarden_stats`.
- Responses are **structured JSON**, not prose.
- `can_run` is informational only — carries a note that availability isn't guaranteed after
  the call; agents that will do protected work must `acquire`.

## 11. Configuration

- Location: `%ProgramData%\ComputeWarden\config.yaml` (machine-wide, matches daemon scope).
  Overridable via `COMPUTEWARDEN_CONFIG` env var.
- Format (server poll interval, lease bounds, `process_detection`,
  `process_rules`, logging). Missing config → defaults; corrupt or unreadable → defaults,
  logged as an error, daemon still starts.
- **Hot-reload** of `process_rules`, their debounce, and `sharing` via a file watcher
  (500ms debounce). A corrupt or momentarily-missing file keeps the current settings. The
  other settings are read at startup only.

## 12. Project layout

```
ComputeWarden.slnx
├── src/
│   ├── ComputeWarden.Core/        # Blocker, Reservation, providers, state calc, config models
│   ├── ComputeWarden.Daemon/      # ProcessMonitor, LeaseSweeper, named-pipe server, host, logs
│   ├── ComputeWarden.Client/      # IPC client library (used by the adapter and cw)
│   ├── ComputeWarden.Mcp/         # stdio MCP adapter + auto-spawn logic
│   └── ComputeWarden.Cli/         # cw: status, the hold, cw wait and cw run
└── tests/
    └── ComputeWarden.Tests/       # xUnit
```

Core has **no** OS or IPC dependencies (pure, unit-testable). Daemon/Client/Mcp are thin.

## 13. Observability

Counters kept in memory and returned by `computewarden_stats`: `total_acquisitions`,
`failed_acquisitions`, `expired_reservations`, `current_reservations`,
`current_process_blockers`, `uptime_seconds`. Lifecycle events (acquire/release/expiry,
process blockers appearing/clearing, config reloads) go to
`%ProgramData%\ComputeWarden\logs\daemon.log`, capped at 5 MB with one backup.
An expiry is logged as "owner did not release", the main sign of an agent misusing leases.

## 14. Test plan

- **Rules** (`RulesTests`): everyday work side by side, gaming throttle and the game-jobs
  cap, the hold, UNKNOWN, one big-memory job at a time, solo, the line's order, check-in
  windows, leases from estimates, long-poll, save/restore.
- **Concurrency:** 256 big-memory acquires at once → exactly one runs, the rest are 1..255
  in line (critical).
- **Process:** appears / disappears, multiple, unconfigured ignored, monitor failure→UNKNOWN,
  availability debounce.
- **Wire** (`WireTests`): dispatcher, client, MCP tools and `cw` over a real pipe; malformed
  IPC, corrupt config, oversized metadata, a client hanging up mid-wait.
- Core logic is tested with a fake clock + fake process provider (no real processes needed).

## 15. Definition of done

Two agents build side by side at full speed. The user starts a game: both are told to run
small on their next renew, and a fourth job waits for a slot. The game exits: everyone is
told full again. Two big-memory jobs never run together, and a timing run gets a quiet
machine. If an agent crashes, its lease expires and its slot frees up.

## 16. Shared work, advice, and the line

> Status: built (`Warden.Evaluate`, `RulesTests`, `WireTests`). Everything below is **advisory**. The warden tells agents
> how big to run; it never caps, kills, or reprioritizes a process.

### Motivation

The user wants three things from the warden, and nothing more:

1. **Gaming:** while the user games, agent work runs small and only a few jobs at a time, so the
   game's fps holds. Afterwards everyone goes full speed again without being told by hand.
2. **Memory:** two sessions don't each fill the machine's memory at once.
3. **Timing:** a session can get a quiet machine for a short timing run.

Before 2.0 the warden had more: exclusive claims per resource, resource sets on every claim,
user modes (steady/open/hold), `userPriority`, and a line the user could reorder. They didn't
serve these goals better and made the advice hard to predict (sessions saw "running full,
exclusive" beside others throttled for no reason the user wanted), so 2.0 removed them.

### Claims

| Claim | Request | Rule |
|---|---|---|
| **Shared** | `kind: shared` (the default) | Every build, test and run. `full` unless the machine is slowed (below), then `throttled`. Shared claims never block each other. |
| **Big memory** | `bigMemory: true` | A shared claim that needs more than ~8 GB of RAM and can't cap it, or more than half the GPU's memory. At most one runs at a time; the next is `queued` until it ends. It doesn't hold up claims that aren't big-memory. |
| **Solo** | `kind: solo` | A timing run of at most 10 minutes (`MaxSoloSeconds`), or up to 60 (`MaxApprovedSoloSeconds`) with `userApproved: true`, which agents set only when the user explicitly approved that run. Starts only when nothing else runs and nothing slows the machine; while it runs, nothing new starts, and while it waits, only work that ends first (rule 3). Its lease never reaches past 10 minutes after it starts, whatever it asks for. A longer `estimateSeconds` is refused. |

`kind: exclusive` is refused with an error naming what to use instead. Claims are leases:
renew, release, and crash-safe expiry work as in §6.

### Answer

| Grant | Meaning |
|---|---|
| `full` | Run normally. |
| `throttled` | Run small: `{max_cores, priority, max_ram_gb}` plus `how`, a concrete hint (`dotnet build -m:1`, `start /belownormal`, keep memory under 2 GB, keep off the GPU). |
| `queued` | Don't start: `{position, ahead[], est_wait_seconds}` and a reason. The estimate is null when a game or hold is in the way, since nobody knows when that ends. |

### Rules

The machine is **slowed** while a configured process (a game) runs, while the user's manual
hold is on, or while the state is UNKNOWN (the process list can't be read, so a game can't be
ruled out).

1. **Slowed:** every running claim is told `throttled`. At most `game_jobs` claims (default 3,
   0..12, set on the panel and kept across restarts) run; later ones are `queued`. Measured on
   a 6-core desktop, a game's fps falls with the memory traffic of the work beside it, roughly
   per busy core, whatever its priority; with one core per throttled job, this number is the
   knob that controls it. Work already running is never told `queued`, only `throttled`: it
   can't un-start. A **hold** is stricter: nothing new starts.
2. **Big memory:** a big-memory claim is `queued` while another big-memory claim runs.
3. **Solo:** a solo claim is `queued` until nothing else runs and the machine isn't slowed.
   Running work isn't affected: it finishes, and then the solo claim starts. While a solo
   claim runs, every claim behind it is `queued`. While it waits for running work, a claim
   behind it may still start (**backfill**) if its `estimateSeconds` says it ends no later
   than the running work should, so it uses the gap without delaying the timing run. A claim
   with no estimate, or another solo claim, waits. While a solo claim waits for a game or the
   hold, nothing behind it starts: nobody knows when that ends.
4. **The line is first come, first served.** One pass over the claims that haven't started,
   in arrival order, decides each one's grant. A claim whose turn has come counts as running
   from then on, so its slot is kept for it through its check-in window and a newcomer can't
   take it. There is no way to skip or reorder the line.

### Checking back

`renew` returns the current advice, so advice tracks the machine while work is running:

- If a game starts, the next renew says `throttled`. The running process won't shrink, but
  the agent's next step will be smaller.
- If the game exits, a throttled claim's next renew says `full`, and new claims get `full`
  straight away.
- A queued ticket's renew returns its position, or `full`/`throttled` when its turn comes.
  Tickets must check in every `queue_checkin_seconds` (default 120), or they drop out of
  line, so a crashed agent can't stall the queue. The ticket's lease is the check-in window
  until it starts. A renew marked `probe` (what `cw wait` sends) checks the ticket in and
  reports its advice, but never starts the claim, even when its turn has come: it stays a
  ticket with the check-in window until its owner renews, and that renew starts it with its
  real lease. So an owner that is woken and never starts drops out within one window, and
  the slot passes to the next in line.
- **`cw wait <id>`:** long-polls a queued claim's probe renew in a loop and exits when it may
  start (0), when the claim is gone (1), after `--max` (default 1h) still queued (3), or when
  the daemon stays unreachable after a few retries (4). An agent runs it as a background
  command, so it does other work and is woken on exit instead of polling.
- **`cw run`:** acquires, long-polls the owner's renew while queued, then starts the command
  suspended, puts it in a new job object and resumes it, so nothing it spawns escapes the job.
  While it runs, a long-poll renew loop applies each change of advice to the job: `throttled`
  sets the job's affinity limit to `max_cores` cores picked at random from all but core 0, and
  its priority-class limit to the throttle priority; `full` after a throttle sets every core and
  normal priority (a job never throttled is left as the command set itself). The job is
  kill-on-close, so ending `cw run` ends the tree; the claim is released when the command
  exits. A lost claim is acquired again; an unreachable daemon at the start (an error and one
  retry) means the command runs unclaimed. Memory isn't capped: a job memory limit fails
  allocations, which would crash the work instead of slowing it. `--low` throttles the job to
  one core at below-normal priority before it resumes and ignores the advice after; it also
  sets `DOTNET_CLI_USE_MSBUILD_SERVER=0` and `UseSharedCompilation=false` for the command, since
  a compiler server started outside the job would do its work outside the limit.
- **Long-poll:** `RENEW` with `waitSeconds` (max 60) holds the answer until the claim's
  grant differs from the last one it was told, or the wait runs out. The claim is kept alive
  for the wait (plus 15 s). The dispatcher polls `Warden.PeekGrant`, then does the real
  renew. An unknown id answers at once.

### The user's controls

- **Manual hold:** `cw hold [reason]` / `cw hold off`, the panel's Hold switch, or an agent
  calling `computewarden_set_manual_busy` on the user's explicit word. See rule 1.
- **Jobs while gaming:** the panel's picker (0 to 4), or `game_jobs` in the config.

Both are the user's call: the panel only accepts them from a monitor, never from another app
on the hub.

### Estimates

Agents supply `estimateSeconds`. It feeds queue wait estimates (time left on the running
claims in the way) and the default lease: once started, a claim with an estimate and no
`leaseSeconds` gets `ceil(estimate × 1.25) + 30` seconds, clamped to the lease bounds.

### Config (defaults)

```yaml
sharing:                     # hot-reloads with the rest of the config
  throttle_max_cores: 1
  throttle_priority: below_normal   # or idle
  throttle_max_ram_gb: 2
  game_jobs: 3
  queue_checkin_seconds: 120
```

`max_throttled_per_resource` (before 2.0) is read as `game_jobs`; `unshareable` and rule
`resources` are accepted and ignored.

### Compatibility

The IPC version stays 1. An acquire without `kind` is a shared claim. `resources`, `whenBusy`,
`cores`, `ramGb` and `userPriority` in a request are ignored; `SET_MODE` is an unknown op. A
saved state from before 2.0 (version 1) loads: its exclusive claims come back as shared, and
its mode is dropped.

### Agent policy

Every session claims its builds, tests, and runs (two small tool calls per command) in every
project, sets `bigMemory` only for jobs over ~8 GB of RAM that can't cap themselves or over half the GPU's memory, uses `solo` only
for timing runs of 10 minutes or less (longer only with the user's explicit approval), and follows the advice. Projects keep no ComputeWarden
rules of their own, only facts about their jobs' resource needs (duration, peak memory, GPU, disk).

### Example

Overwatch is in `process_rules`; `game_jobs` is 3.

```
10:00  A: shared build                  → full
10:01  B: big-memory index, est 20 min  → full
10:02  C: big-memory index              → queued #1, ~19 min ("B is running a big-memory job")
10:03  D: shared test run               → full (C waiting doesn't hold D up)
10:05  User starts Overwatch            → A, B, D: next renew says throttled
10:06  E: shared build                  → queued #2 (3 jobs may run while gaming, all taken)
10:08  A releases                       → C still waits on B (big memory), so A's slot goes
                                          to E: its renew says throttled
10:21  B releases                       → C's renew says throttled (the third slot)
10:40  Overwatch exits                  → C, D, E: next renew says full
10:45  T: solo timing run               → queued: waits for C, D, E (the last should end at 10:55)
10:46  F: shared test, est 5 min        → full: it ends before 10:55, so T isn't delayed
10:47  G: shared build, est 15 min      → queued behind T
```
