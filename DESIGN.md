# ComputeWarden — Implementation Plan (v1)

> Derived from `OriginalSpec.md`. This document records the concrete design decisions
> and the build plan for version 1. The spec is the "what"; this is the "how".

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
| Reservations | **Exclusive** (one at a time); `reservation_id` is a **bearer capability** |
| Persistence | None for reservations/process blockers; config persisted; manual blocker persistence deferred |
| Portability | Windows-first; process enumeration + pipe path behind interfaces for a future Linux port |

## 3. State model

State is **derived**, never stored as a boolean:

```
blockers = ProcessProvider.Get() + ReservationProvider.Get() + ManualProvider.Get()

if any provider failed in a way that affects confidence:  UNKNOWN
elif blockers.Any():                                       BUSY
else:                                                      AVAILABLE
```

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
    string   Source,       // e.g. "ProcessMonitor", "ReservationManager"
    string   Description,  // human-readable
    string?  Owner,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    IReadOnlyDictionary<string,string>? Metadata);
```

`IBlockerProvider { IReadOnlyList<Blocker> GetBlockers(); }` — the extension seam for
future CPU/disk/GPU providers.

## 5. Acquire algorithm (the critical correctness property)

Single lock, atomic check-and-create (spec §27):

```
lock (state):
    expire stale reservations
    blockers = collect from all providers        // uses last process snapshot
    if state == UNKNOWN:            return { acquired:false, reason:"UNKNOWN", blockers }
    if blockers.Any():              return { acquired:false, blockers }
    r = new Reservation(id, owner, desc, lease clamped to [min,max])
    reservations.add(r)
    return { acquired:true, reservation_id:r.Id, expires_at:r.ExpiresAt, machine_state:BUSY }
```

Notes:
- Acquire checks the **last process poll snapshot** (up to `poll_interval_ms` stale) — it
  does *not* trigger a fresh enumeration (spec §7 "avoid excessive enumeration"). The
  cooperative contract only promises "known-safe at acquisition time," not guaranteed
  exclusivity (spec §28).
- Lease seconds are clamped server-side to `[minimum_seconds, maximum_seconds]`.
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

- `set_manual_busy(reason)` / `clear_manual_busy()` create/remove a single MANUAL blocker,
  visually distinct in status.
- May be disabled via config if only admins should control it (spec §17).

## 8. Process monitoring

- Poll every `poll_interval_ms` (default 2000). Use a **cheap name-only enumeration**
  (`Process.GetProcesses()` reading `ProcessName`, no per-process handle inspection).
- Match against enabled `process_rules` by executable name (case-insensitive, with/without
  `.exe`). Path/cmdline/regex are future fields — schema leaves room, v1 ignores them.
- Enumeration failure → provider reports low confidence → state UNKNOWN.
- **No log spam:** log only transitions (blocker appeared / cleared), never "still running."

## 9. IPC protocol (named pipe)

- Pipe: `\\.\pipe\ComputeWarden`. Restrict ACL to the current user/session.
- Wire: one JSON object per line (request), one per line (response). `v` field for version.
- Requests: `STATUS | ACQUIRE | RENEW | RELEASE | SET_MANUAL_BUSY | CLEAR_MANUAL_BUSY`.
- Server validates every input: string/metadata size caps, lease bounds, reject unknown ops
  with a structured error (never crash the pipe loop).

```jsonc
// request
{ "v": 1, "op": "ACQUIRE", "owner": "Claude Code", "description": "indexing", "lease_seconds": 300 }
// response
{ "v": 1, "ok": true, "acquired": true, "reservation_id": "7f93…", "expires_at": "…", "machine_state": "BUSY" }
```

## 10. MCP adapter (`cw-mcp`)

- Stdio MCP server (one per client, spawned by Claude Code et al.).
- On start: try to connect to the pipe; if absent, acquire the named mutex and spawn the
  daemon detached, wait for the pipe, then connect. (Mutex prevents double-spawn.)
- Exposes tools 1:1 with spec §18: `computewarden_status`, `computewarden_can_run`,
  `computewarden_acquire`, `computewarden_renew`, `computewarden_release`,
  `computewarden_set_manual_busy`, `computewarden_clear_manual_busy`.
- Responses are **structured JSON**, not prose (spec §38).
- `can_run` is informational only — carries a note that availability isn't guaranteed after
  the call; agents that will do protected work must `acquire`.

## 11. Configuration

- Location: `%ProgramData%\ComputeWarden\config.yaml` (machine-wide, matches daemon scope).
  Overridable via `COMPUTEWARDEN_CONFIG` env var.
- Format per spec §25 (server poll interval, lease bounds, `process_detection`,
  `process_rules`, logging). Hot-reload deferred; missing config → sane defaults + a warning.

## 12. Project layout

```
ComputeWarden.sln
├── src/
│   ├── ComputeWarden.Core/        # Blocker, Reservation, providers, state calc, config models
│   ├── ComputeWarden.Daemon/      # ProcessMonitor, managers, named-pipe server, host
│   ├── ComputeWarden.Client/      # IPC client library (used by adapter + future CLI)
│   └── ComputeWarden.Mcp/         # cw-mcp stdio adapter + auto-spawn logic
└── tests/
    └── ComputeWarden.Tests/       # xUnit
```

Core has **no** OS or IPC dependencies (pure, unit-testable). Daemon/Client/Mcp are thin.

## 13. Observability (spec §31)

Counters kept in-memory, returned via a `debug`/status field: `total_acquisitions`,
`failed_acquisitions`, `expired_reservations`, `current_reservations`,
`current_process_blockers`, `uptime`. Lifecycle events logged (spec §30).

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

## 15. Build order (milestones)

1. **Core**: models + state calc + ReservationManager with atomic acquire + fake clock.
   → land the concurrency test here first (it's the whole ballgame).
2. **Daemon**: real ProcessMonitor + lease sweep + named-pipe server.
3. **Client**: IPC client library.
4. **Mcp**: adapter tools + auto-spawn.
5. **Config + logging + counters.**
6. **End-to-end**: the spec §42 "definition of done" scenario as an integration test.

## 16. Definition of done (spec §42)

Two agents, one machine: A acquires, B is refused and told A is the blocker, A renews then
releases, B then acquires. If A crashes, its lease expires and the machine frees up. A
configured external process blocks both until it exits. When that scenario passes
reliably, v1 is done.
