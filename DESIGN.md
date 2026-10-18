# ComputeWarden — Design

> Derived from `OriginalSpec.md`. This document records the design decisions and why they
> were made. The spec is the "what"; this is the "how". For usage, see the README.

## 1. Summary

ComputeWarden is a **cooperative, machine-wide traffic cop** for resource-intensive
work. A tool asks "is it safe to begin intensive work?"; the warden answers from a set
of **blockers** (detected processes, active reservations, a manual override) and, if
clear, atomically hands out an expiring **reservation**. Nothing is enforced — it
coordinates well-behaved tools by convention.

## 2. Locked decisions

| Decision | Choice |
|---|---|
| Language / runtime | C# / **.NET 10** (MCP SDK requires it; single target everywhere — mixing 8/10 caused problems in a sibling project). Ship self-contained single-file exes so target machines don't need the runtime installed. |
| Deployment | Plain background **daemon**, no Windows Service, no installer |
| Daemon lifecycle | First MCP adapter auto-spawns it if missing (race-safe via named mutex); **runs until reboot** (idle-timeout deferred) |
| Topology | Daemon = single source of truth; thin per-client MCP adapter forwards over a **named pipe** |
| IPC transport | Named pipe `\\.\pipe\ComputeWarden`, newline-delimited JSON, versioned |
| Reservations | **Exclusive per resource** (cpu/gpu/ram/network/disk; holds conflict only where they overlap, default all); `reservation_id` is a **bearer capability** |
| Persistence | None for reservations/process blockers; config persisted; manual blocker persistence deferred |
| Portability | Windows-first; process enumeration + pipe path behind interfaces for a future Linux port |

## 3. State model

State is **derived**, never stored as a boolean:

```
blockers = reservations + ProcessProvider.Get() + ManualProvider.Get()

if any provider failed in a way that affects confidence:  UNKNOWN
elif blockers.Any():                                       BUSY
else:                                                      AVAILABLE
```

Every blocker occupies a **resource set** (`cpu | gpu | ram | network | disk`, a flags enum).
Machine-wide `BUSY` means *something* is held; `busy_resources` says what. Availability for a
specific request only considers blockers that overlap it. Anything that doesn't name its
resources (an old client, a process rule without `resources`, a bare manual hold) occupies
**all** of them, which is exactly the pre-resource "exclusive machine" behavior. Old clients
and old daemons therefore interoperate conservatively, with no protocol version bump.

- **UNKNOWN** is not a blocker; it means "couldn't compute the blocker set."
  `acquire` under UNKNOWN **refuses** (fail-conservative, spec §32). Agents treat
  UNKNOWN as "do not begin work."
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
    ResourceSet Resources = ResourceSet.All);
```

`IBlockerProvider { ProviderResult GetBlockers(); }` (blockers + a confidence flag) is the
extension seam for future measured CPU/disk/GPU providers.

## 5. Acquire algorithm (the critical correctness property)

Single lock, atomic check-and-create (spec §27):

```
lock (state):
    expire stale reservations
    blockers = collect from all providers        // uses last process snapshot
    if any provider unconfident:    return { acquired:false, reason:"UNKNOWN", ... }
    conflicts = blockers overlapping requested resources (default: all)
    if conflicts.Any():             return { acquired:false, blockers: conflicts }
    r = new Reservation(id, owner, desc, resources, lease clamped to [min,max])
    reservations.add(r)
    return { acquired:true, reservationId:r.Id, expiresAt:r.ExpiresAt, resources }
```

Notes:
- Acquire checks the **last process poll snapshot** (up to `poll_interval_ms` stale) — it
  does *not* trigger a fresh enumeration (spec §7 "avoid excessive enumeration"). The
  cooperative contract only promises "known-safe at acquisition time," not guaranteed
  exclusivity (spec §28).
- Lease seconds are clamped server-side to `[minimum_seconds, maximum_seconds]`, never below 1s.
- UNKNOWN refuses every request, even one for resources nothing appears to hold: if the
  process list can't be read, nothing is known to be free.
- There is no "add resources to my reservation" operation. A second acquire is an unrelated
  reservation, and growing holds piecemeal is the hold-and-wait pattern that lets two agents
  stall on each other. Agents request everything in one call; if needs change, they release
  and re-acquire.
- A process starting *after* a reservation is granted adds a second blocker; the existing
  reservation is **not** revoked (spec §28).

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

- `set_manual_busy(reason, resources?)` / `clear_manual_busy()` create/remove a single MANUAL
  blocker, visually distinct in status.
- May be disabled via config if only admins should control it (spec §17).

## 8. Process monitoring

- Poll every `poll_interval_ms` (default 2000). Use a **cheap name-only enumeration**
  (`Process.GetProcesses()` reading `ProcessName`, no per-process handle inspection).
- Match against enabled `process_rules` by executable name (case-insensitive, with/without
  `.exe`; a directory in the rule is ignored). Each rule may name the `resources` it
  occupies (default all). Command-line/regex matching are not implemented.
- Enumeration failure → provider reports low confidence → state UNKNOWN.
- **No log spam:** log only transitions (blocker appeared / cleared), never "still running."

## 9. IPC protocol (named pipe)

- Pipe: `\\.\pipe\ComputeWarden`. Restrict ACL to the current user/session.
- Wire: one JSON object per line (request), one per line (response). `v` field for version.
- Requests: `STATUS | CAN_RUN | ACQUIRE | RENEW | RELEASE | SET_MANUAL_BUSY | CLEAR_MANUAL_BUSY | STATS`.
- Server validates every input: string/metadata size caps, lease bounds, resource names;
  unknown ops get a structured error (never crash the pipe loop). Request lines are capped
  at 256K characters *while reading*, and idle connections are dropped after 30s.
- Versioning is additive: the daemon serves any `v` up to its own and ignores unknown
  fields, because the daemon is long-lived and adapters update independently.

```jsonc
// request
{ "v": 1, "op": "ACQUIRE", "owner": "Claude Code", "description": "indexing",
  "leaseSeconds": 300, "resources": ["cpu", "disk"] }
// response
{ "v": 1, "ok": true, "acquired": true, "resources": ["cpu", "disk"], "reservationId": "7f93…",
  "expiresAt": "…", "machineState": "BUSY", "reason": null, "blockers": [] }
```

## 10. MCP adapter (`ComputeWarden.Mcp`)

- Stdio MCP server (one per client, spawned by Claude Code et al.). stdout is the JSON-RPC
  transport, so all adapter logging goes to stderr.
- On a tool call: probe the pipe; if nothing is listening, launch the daemon detached and
  wait (up to 8s) for it to serve. Duplicate launches are harmless: the daemon's
  single-instance mutex (`Global\ComputeWardenDaemon`) makes extras exit immediately. An
  ACL-denied pipe (daemon owned by another user / elevated) is reported, not "fixed" by
  spawning.
- Exposes tools 1:1 with spec §18, plus `computewarden_stats`.
- Responses are **structured JSON**, not prose (spec §38).
- `can_run` is informational only — carries a note that availability isn't guaranteed after
  the call; agents that will do protected work must `acquire`.

## 11. Configuration

- Location: `%ProgramData%\ComputeWarden\config.yaml` (machine-wide, matches daemon scope).
  Overridable via `COMPUTEWARDEN_CONFIG` env var.
- Format per spec §25 (server poll interval, lease bounds, `process_detection`,
  `process_rules`, logging). Missing config → defaults; corrupt or unreadable → defaults,
  logged as an error, daemon still starts.
- **Hot-reload** of `process_rules` + debounce via a file watcher (500ms debounce). A corrupt
  or momentarily-missing file keeps the current rules. The other settings are read at
  startup only.

## 12. Project layout

```
ComputeWarden.slnx
├── src/
│   ├── ComputeWarden.Core/        # Blocker, Reservation, providers, state calc, config models
│   ├── ComputeWarden.Daemon/      # ProcessMonitor, LeaseSweeper, named-pipe server, host, logs
│   ├── ComputeWarden.Client/      # IPC client library (used by adapter + future CLI)
│   └── ComputeWarden.Mcp/         # stdio MCP adapter + auto-spawn logic
└── tests/
    └── ComputeWarden.Tests/       # xUnit
```

Core has **no** OS or IPC dependencies (pure, unit-testable). Daemon/Client/Mcp are thin.

## 13. Observability (spec §31)

Counters kept in memory and returned by `computewarden_stats`: `total_acquisitions`,
`failed_acquisitions`, `expired_reservations`, `current_reservations`,
`current_process_blockers`, `uptime_seconds`. Lifecycle events (acquire/release/expiry,
process blockers appearing/clearing, config reloads) go to
`%ProgramData%\ComputeWarden\logs\daemon.log`, capped at 5 MB with one backup (spec §30).
An expiry is logged as "owner did not release", the main sign of an agent misusing leases.

## 14. Test plan (spec §36)

- **Reservation:** acquire-when-available, acquire-when-reserved, release, renew, expiry,
  invalid id, double release, **concurrent acquire → exactly one winner** (critical).
- **Process:** appears / disappears, multiple, unconfigured ignored, monitor failure→UNKNOWN,
  availability debounce.
- **Combined:** process+reservation, manual+process, process starts during reservation,
  reservation expires while process blocker persists.
- **Failure:** client disappears, daemon restart clears stale reservations, malformed IPC,
  corrupt config, oversized metadata rejected, rapid acquire/release.
- Core state/acquire logic tested with a fake clock + fake process provider (no real
  processes needed for the critical concurrency test).

## 15. Definition of done (spec §42)

Two agents, one machine: A acquires, B is refused and told A is the blocker, A renews then
releases, B then acquires. If A crashes, its lease expires and the machine frees up. A
configured external process blocks both until it exits. When that scenario passes
reliably, v1 is done.

## 16. Shared work, advice, and the line

> Status: built (`Warden.Evaluate`, `SharingTests`, `SharingIpcTests`). Everything below is **advisory**. The warden tells agents
> how big to run; it never caps, kills, or reprioritizes a process.

### Motivation

Today the warden only answers yes or no, and only for jobs heavy enough to bother asking.
Everyday builds, test runs, and app launches never ask, so they pile onto the machine while
a game is running, or crawl along at one core after it has quit. The target behavior is
what a human would tell several sessions by hand: *"I'm gaming, keep it small"*, and later
*"I'm done with my game, go full ham as long as you have the lease."* The aim is for
sessions to get that answer from the warden, live, without being told.

### Claim kinds

| Kind | Created by | Meaning |
|---|---|---|
| **Priority** | Config process rules (games, etc.) | **The user's own lease.** Gaming means the user holds those resources. Non-negotiable: never queued, asked, or throttled, and every agent on those resources is limited for as long as it runs. |
| **Exclusive** | Agent heavy job (today's `acquire`) | Will saturate these resources. One holder per resource; the rest wait in line or run throttled. |
| **Shared** | Agent everyday build/test/run | Normal work. Never refused; told how big it may run right now. With no `resources`, it means cpu+ram+disk (not all), so a GPU-using game throttles a build instead of queuing it. |

Agent claims (exclusive, shared, and queue tickets) are all **leases**: renew, release,
and crash-safe expiry are unchanged. A lease just gains a `kind` and the warden's current
**advice**.

### Request and answer

```
acquire(owner, description, resources, kind: "shared" | "exclusive",
        estimateSeconds, cores?, ramGb?, whenBusy: "throttle" | "wait",
        userPriority?: "<why the user said so>")
```

| Mode | Meaning |
|---|---|
| `full` | Nothing in the way; run normally. For an exclusive claim: go full ham while the lease is held. |
| `throttled` | Run, but small: `{max_cores, priority, max_ram_gb, reason, how}`, where `how` is a concrete hint (`dotnet build -m:1`, `dotnet test -- RunConfiguration.MaxCpuCount=1`). |
| `queued` | In line: `{position, ahead[], est_wait_seconds}`. Check back via renew. The estimate is null when a game, hold, or steady mode is in the way, since nobody knows when that ends. |

### Rules

1. A **priority** claim on a resource throttles everything else on that resource, always.
   No mode, lease, or `userPriority` overrides it. If the
   resource can't be shared (config; by default every resource except `gpu` can be), the
   request is queued instead.
2. An **exclusive** claim gets `full` when its resources are free *and* nobody ahead of it
   in line wants them. Otherwise it gets `throttled` or `queued`, per `whenBusy`
   (default `throttle`).
3. A **shared** claim gets `full` unless a priority or exclusive claim overlaps it, in
   which case it gets `throttled`. Shared claims block neither each other nor exclusive
   claims; they're short and polite, and they're told to shrink on their next check.
4. **Throttle cap:** at most `max_throttled_per_resource` (default 3) throttled claims per
   contended resource. Beyond that, a request is queued. The cap applies only to *new*
   starts: **work that has started is never told `queued`**, only `throttled`. It can't
   un-start, and telling it to stop would just be ignored.
5. **The line is first come, first served.** A newcomer can't get `full` ahead of an
   earlier waiter for an overlapping resource. A queued ticket is promoted to `full` when it
   checks in at the head of the line and its resources are free.
6. **Skipping the line:** `userPriority` puts the request at the front of the line. It
   never takes resources from current holders (advisory), and it is logged and shown in
   status. Agents set it only when the user has told them they have priority.
7. **UNKNOWN:** exclusive claims are queued or refused as today; shared claims are
   throttled.
8. **Legacy acquires** (no `kind`) are also refused when anyone is in line for an
   overlapping resource. They can't jump the queue.

### Checking back

`renew` returns the current advice, so advice tracks the machine while work is running:

- If a game starts during an exclusive job, the next renew says `throttled: Overwatch
  started`. The running process won't shrink, but the agent's next step will be smaller.
- If the game exits, a throttled claim's next renew says `full`, and new claims get `full`
  straight away. This is the automatic "go full ham".
- A queued ticket's renew returns its position, or its promotion to `full`. Tickets must
  check in every `queue_checkin_seconds` (default 120), or they drop out of line, so a
  crashed agent can't stall the queue. The ticket's lease is the check-in window until it
  starts; then it gets its real lease.
- **Long-poll:** `RENEW` with `waitSeconds` (max 60) holds the answer until the claim's
  grant differs from the last one it was told, or the wait runs out. The dispatcher polls
  `Warden.PeekGrant` once a second, which renews nothing, and then does the real renew. So
  waiting agents don't burn tokens polling, and a game exit reaches them within about a
  second. An unknown id answers at once.

### User override: the mode

One machine-wide setting that only the user sets. It adjusts how agents treat *each other*.
It never loosens a priority claim: while a game is running, everyone on its resources is
limited, in every mode.

| Mode | Says | Effect |
|---|---|---|
| `hold` | "Nobody start anything." | Every new claim is queued; work already running is throttled (rule 4). |
| `steady` | "Y'all go slow and steady." | Everything gets `throttled`; nobody gets `full`. Exclusive claims are throttled or queued, per `whenBusy`. |
| `auto` | (default) | The rules above. |
| `open` | "Full bore allowed with a lease." | No throttling between agents: shared work runs `full` beside exclusive leases, with no throttle cap between agents. A game still throttles, and its cap still applies. Exclusive leases still keep two heavy jobs off the same resource. |

- A mode can take `resources` (e.g. `steady` for cpu only), an optional duration (`steady`
  for 2h, then back to `auto`), and a reason. The reason is shown to agents in their advice:
  `throttled: user set steady mode ("gaming tonight")`.
- Running agents pick up a mode change on their next renew, the same way they notice a game
  starting or stopping.
- **Ways to set it:** a small CLI (`cw mode steady 2h "gaming"`, also runnable as
  `! cw mode open` inside a Claude Code session), or by telling any agent, which calls
  `computewarden_set_mode`. The tool is documented as user-only: an agent calls it only on
  the user's explicit word, never on its own judgment (the same trust model as
  `userPriority`).
- Non-auto modes other than `open` also show as a synthetic `mode` blocker in status, so
  legacy clients and `can_run` see the machine as busy.
- `set_manual_busy` and `clear_manual_busy` stay as their own separate provider, not as aliases
  for the mode. A manual hold acts like `hold` on its resources, and clearing it doesn't
  touch the mode.

### Estimates

Agents supply `estimateSeconds`, `cores`, and `ramGb`. These aren't used for core
arithmetic. They feed queue wait estimates (time left on the jobs ahead), status ("3 shared
jobs, ~12 cores claimed, throttled by Overwatch"), and the default lease length: once
started, a claim with an estimate and no `leaseSeconds` gets `ceil(estimate × 1.25) + 30`
seconds, clamped to the lease bounds.

### Config (defaults)

```yaml
sharing:                     # hot-reloads with the rest of the config
  throttle_max_cores: 1
  throttle_priority: below_normal   # or idle
  throttle_max_ram_gb: 2
  max_throttled_per_resource: 3
  unshareable: [gpu]
  queue_checkin_seconds: 120
```

### Compatibility

An acquire without `kind` behaves exactly as today: an exclusive claim that is refused
rather than queued. Old adapters and old CLAUDE.md wording keep working, with no IPC version
bump.

### Agent policy change

For this to work, every session reports its builds, tests, and runs as shared claims
(about two small tool calls per command) in every project, not just opted-in ones. The
global CLAUDE.md rule becomes:

- Report builds, tests, and runs as shared claims, and follow the advice.
- Use exclusive claims for genuinely heavy jobs.
- Set `userPriority` only on the user's word.

### Example

```
10:00  A: shared build (cpu)            → full
10:01  B: exclusive test fan-out (cpu)  → full     (A's next check: throttled)
10:02  C: exclusive indexing (cpu,disk), whenBusy: wait
                                        → queued #1, ~5 min
10:03  User starts Overwatch            → B's renew: throttled; new shared work: throttled
10:04  D, E, F: shared builds           → throttled ×3 (cap reached)
10:05  G: shared build                  → queued #2
10:07  B releases; C checks in          → still queued (Overwatch is priority)
10:40  Overwatch exits                  → C promoted to full on next check; G gets full
```
