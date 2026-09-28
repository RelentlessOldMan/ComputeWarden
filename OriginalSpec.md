# ComputeWarden

## 1. Purpose

ComputeWarden is a lightweight machine-wide coordination service exposed through MCP.

Its purpose is to prevent multiple coding agents, developer tools, build systems, indexing tools, and other resource-intensive processes from simultaneously performing expensive work on the same machine.

ComputeWarden answers a simple question:

> **Is it currently safe to begin intensive processing on this machine?**

It determines this from two sources:

1. Automatically detected processes that are known to perform intensive work.
2. Explicit reservations made by cooperating agents and applications.

ComputeWarden must allow multiple independent agents and processes to coordinate without requiring those agents to know about each other.

The initial implementation should prioritize Windows, while keeping the core architecture portable enough to support Linux later.

---

# 2. Goals

ComputeWarden shall:

- Monitor the local machine for configured resource-intensive processes.
- Maintain machine-wide coordination state.
- Allow MCP clients to determine whether intensive processing may begin.
- Allow cooperating clients to reserve the machine for intensive work.
- Allow clients to release their reservations.
- Prevent multiple cooperating agents from unintentionally performing intensive work simultaneously.
- Automatically recover from crashed or abandoned clients.
- Explain why intensive processing is currently blocked.
- Support multiple simultaneous non-intensive clients.
- Require minimal CPU, memory, and disk overhead itself.
- Operate entirely locally.
- Require no cloud service or external database.

ComputeWarden is a coordination mechanism, not a resource scheduler.

It does not need to suspend, terminate, throttle, or reprioritize processes.

---

# 3. Non-Goals

The initial version does not need to:

- Enforce CPU quotas.
- Enforce disk bandwidth limits.
- Enforce memory limits.
- Kill offending processes.
- Suspend offending processes.
- Change Windows process priorities.
- Perform OS-level job scheduling.
- Coordinate between multiple physical machines.
- Provide distributed locking.
- Replace operating-system resource management.
- Guarantee that non-cooperating applications respect ComputeWarden.

ComputeWarden reports and coordinates state. Cooperating applications decide how to respond.

---

# 4. Core Concepts

## 4.1 Machine Availability

ComputeWarden exposes a logical machine state:

- `AVAILABLE`
- `BUSY`

`AVAILABLE` means ComputeWarden currently knows of no condition preventing a new intensive operation.

`BUSY` means one or more blockers currently exist.

A blocker may be:

- A detected process.
- An active ComputeWarden reservation.
- A manual administrative override.

The machine-wide state MUST be derived from the set of current blockers rather than implemented as a single fragile boolean.

---

# 5. Blockers

Each reason the machine is unavailable shall be represented internally as a blocker.

Example:

```text
Blocker
{
    id
    type
    source
    description
    owner
    created_at
    expires_at
    metadata
}
```

Supported blocker types should initially include:

```text
PROCESS
RESERVATION
MANUAL
```

This allows clients to understand not only that the machine is busy, but why.

Example:

```text
BUSY

Reasons:
- devenv.exe PID 14280 matched configured intensive-process rule
- ClaudeCode agent "CodeCarver Review" owns reservation 7f93...
```

---

# 6. Process Monitoring

ComputeWarden shall periodically inspect running processes.

A configurable list of process rules determines which processes should block intensive work.

Example configuration:

```yaml
process_rules:
  - name: Visual Studio Build
    executable: MSBuild.exe

  - name: CodeCompass Indexer
    executable: CodeCompass.exe

  - name: CodeCarver
    executable: CodeCarver.exe
```

Rules should support at minimum:

- Executable name.
- Enabled/disabled state.
- Human-readable description.

Future versions may additionally support:

- Executable path.
- Command-line matching.
- Regular expressions.
- Parent process.
- Child process trees.
- User/session.
- CPU threshold.
- Disk-I/O threshold.

Process detection should initially remain deliberately simple.

---

# 7. Process Polling

The default process polling interval should be configurable.

Suggested default:

```text
2 seconds
```

Process polling must have negligible system impact.

The system should avoid expensive full process inspection when only executable names are required.

---

# 8. Process Debouncing

Short-lived processes can create noisy state transitions.

ComputeWarden should therefore support optional debounce periods.

Example:

```yaml
process_detection:
    poll_interval_ms: 2000
    busy_after_ms: 0
    available_after_ms: 3000
```

`available_after_ms` prevents the machine from immediately becoming available during short gaps between related processes.

---

# 9. Reservations

Cooperating agents must be able to reserve the machine before starting intensive processing.

A reservation represents temporary ownership of permission to perform intensive work.

Example:

```text
Reservation
{
    reservation_id
    owner
    description
    created_at
    last_renewed_at
    expires_at
    metadata
}
```

Reservations MUST use unique opaque identifiers.

UUIDs are sufficient.

---

# 10. Reservation Acquisition

A client requests a reservation before beginning intensive work.

Conceptually:

```text
acquire(
    owner = "Claude Code",
    description = "Indexing repository",
    lease_seconds = 300
)
```

ComputeWarden performs an atomic:

```text
CHECK + ACQUIRE
```

operation.

This is critical.

Clients MUST NOT be required to perform:

```text
if can_run():
    acquire()
```

as two independent operations, because two agents could simultaneously observe the machine as available and both acquire it.

Instead:

```text
acquire()
```

must atomically determine whether acquisition is permitted and create the reservation.

---

# 11. Acquisition Result

Successful acquisition:

```json
{
    "acquired": true,
    "reservation_id": "7f93...",
    "expires_at": "...",
    "machine_state": "BUSY"
}
```

Failed acquisition:

```json
{
    "acquired": false,
    "reason": "Machine currently unavailable",
    "blockers": [...]
}
```

---

# 12. Exclusive Intensive Work

The initial implementation should treat intensive reservations as exclusive.

Only one intensive reservation may exist at a time.

This provides predictable behavior and solves the primary problem:

> Two coding agents should not simultaneously hammer the disk.

Future versions may introduce resource classes or shared reservations.

For example:

```text
DISK_INTENSIVE
CPU_INTENSIVE
GPU_INTENSIVE
NETWORK_INTENSIVE
```

But this complexity should not be required for version 1.

---

# 13. Leases

Reservations MUST expire automatically.

A reservation must never be capable of permanently blocking the machine merely because the owning agent crashed.

Example default:

```text
lease_seconds = 300
```

The owner may renew the lease while work continues.

---

# 14. Lease Renewal

Expose:

```text
renew(reservation_id)
```

or:

```text
renew(
    reservation_id,
    lease_seconds
)
```

Successful renewal updates:

```text
last_renewed_at
expires_at
```

Agents performing long operations should periodically renew their reservation.

---

# 15. Release

Expose:

```text
release(reservation_id)
```

Release should immediately remove the reservation.

Release should be idempotent where practical.

Attempting to release an already-expired reservation should not cause a serious error.

Example response:

```json
{
    "released": false,
    "reason": "Reservation no longer exists"
}
```

---

# 16. Crash Recovery

ComputeWarden MUST recover automatically from:

- Agent crashes.
- MCP client crashes.
- Lost MCP connections.
- Machine sleep.
- ComputeWarden restart.
- Unexpected process termination.

Expired leases shall automatically disappear.

Detected process blockers shall automatically disappear when their processes are no longer running.

No normal failure should require manually editing a flag file.

---

# 17. Manual Override

ComputeWarden should support an optional manual blocker.

Example:

```text
set_manual_busy(
    reason = "Running disk maintenance"
)
```

And:

```text
clear_manual_busy()
```

This is useful for work that ComputeWarden cannot automatically detect.

Manual state should be clearly distinguishable from agent reservations.

---

# 18. MCP Interface

ComputeWarden shall expose a small MCP tool surface.

Recommended tools:

## `computewarden_status`

Returns complete current state.

Example:

```json
{
    "state": "BUSY",
    "can_run_intensive": false,
    "blockers": [
        {
            "type": "PROCESS",
            "description": "MSBuild.exe is running",
            "pid": 14280
        }
    ]
}
```

---

## `computewarden_can_run`

Convenience operation for agents that only need an availability check.

Example:

```json
{
    "can_run": true
}
```

This operation is informational only.

It MUST NOT imply that availability remains guaranteed after the call returns.

Agents intending to begin protected work should use `computewarden_acquire`.

---

## `computewarden_acquire`

Atomically attempts to acquire permission for intensive work.

Parameters:

```text
owner
description
lease_seconds
metadata (optional)
```

Returns either a reservation or current blockers.

---

## `computewarden_renew`

Renews an existing reservation.

Parameters:

```text
reservation_id
lease_seconds (optional)
```

---

## `computewarden_release`

Releases an existing reservation.

Parameters:

```text
reservation_id
```

---

## `computewarden_set_manual_busy`

Creates a manual blocker.

Parameters:

```text
reason
```

This operation may optionally be disabled by configuration if only administrators should control manual state.

---

## `computewarden_clear_manual_busy`

Removes the manual blocker.

---

# 19. Agent Usage Pattern

A cooperating coding agent should follow this sequence:

```text
1. Determine that upcoming work will be intensive.

2. Call computewarden_acquire.

3. If acquisition fails:
      Do not begin intensive work.
      Report/wait/retry as appropriate.

4. If acquisition succeeds:
      Save reservation_id.

5. Begin intensive work.

6. Periodically renew the lease if necessary.

7. Finish intensive work.

8. Release reservation.
```

Agents should release reservations using `finally`/cleanup semantics whenever possible.

---

# 20. Waiting

ComputeWarden itself does not need to block an MCP request until the machine becomes available.

Agents may poll:

```text
computewarden_acquire
```

using reasonable intervals.

Suggested agent behavior:

```text
Retry every 5–15 seconds.
```

Agents should avoid aggressive polling.

A future version could expose notifications or subscriptions.

---

# 21. Machine-Wide State

ComputeWarden must work across independent user processes on the same machine.

The coordination mechanism therefore cannot rely solely on static state inside an individual MCP server process if multiple server instances could exist.

Preferred architecture:

```text
                  ┌────────────────────┐
Claude Code ─────►│                    │
                  │                    │
Other Agent ─────►│   ComputeWarden    │
                  │      Service       │
Developer Tool ──►│                    │
                  └─────────┬──────────┘
                            │
                    Process Monitor
                            │
                      Windows OS
```

A single machine-wide ComputeWarden service should be authoritative.

MCP should act as the interface to that authority.

---

# 22. Windows Service

For Windows, the preferred implementation is a lightweight background service.

Responsibilities:

- Maintain reservations.
- Monitor configured processes.
- Maintain manual blockers.
- Expire leases.
- Answer local IPC requests.
- Maintain diagnostic logs.

The MCP server may either:

1. Run inside the same process as the service, if architecture permits.

or:

2. Act as a thin MCP-to-ComputeWarden IPC adapter.

Option 2 provides better separation and allows non-MCP applications to use ComputeWarden later.

---

# 23. Local IPC

If the MCP adapter and service are separate processes, use local IPC.

Preferred Windows options include:

```text
Named Pipes
```

Named pipes are preferable to opening a TCP port for a purely local service.

The IPC protocol should remain small and versioned.

Example operations:

```text
STATUS
ACQUIRE
RENEW
RELEASE
SET_MANUAL_BUSY
CLEAR_MANUAL_BUSY
```

---

# 24. Persistence

Active reservations should generally NOT need permanent persistence.

Reservations are ephemeral leases.

After a ComputeWarden service restart, stale reservations should disappear rather than potentially leaving the machine permanently blocked.

Process blockers will naturally be rediscovered.

Manual blockers MAY optionally be persisted depending on configuration.

Configuration should be persisted normally.

---

# 25. Configuration

Suggested configuration structure:

```yaml
server:
  poll_interval_ms: 2000

leases:
  default_seconds: 300
  minimum_seconds: 30
  maximum_seconds: 3600

process_detection:
  available_after_ms: 3000

process_rules:
  - name: MSBuild
    executable: MSBuild.exe
    enabled: true

  - name: CodeCompass
    executable: CodeCompass.exe
    enabled: true

logging:
  level: info
```

Configuration changes should ideally be reloadable without restarting the service, though this is not required for the first implementation.

---

# 26. Security

ComputeWarden is intended for local-machine coordination.

The service should:

- Listen only through local IPC.
- Not expose an unauthenticated network port.
- Validate all client inputs.
- Place limits on string and metadata sizes.
- Restrict lease durations to configured bounds.
- Generate reservation IDs itself.
- Never trust client-provided reservation IDs as proof of ownership without validating them.
- Avoid accepting arbitrary executable commands.
- Never allow MCP parameters to become shell commands.
- Treat process information as untrusted OS data.

If IPC permissions can restrict access to the current machine/user or approved local users, they should.

---

# 27. Concurrency

ComputeWarden's internal state must be thread-safe.

Operations affecting reservations must be atomic.

In particular:

```text
ACQUIRE
```

must execute logically as:

```text
LOCK STATE

REMOVE EXPIRED RESERVATIONS

CHECK PROCESS BLOCKERS
CHECK RESERVATION BLOCKERS
CHECK MANUAL BLOCKERS

IF no blocker:
    CREATE RESERVATION
    RETURN SUCCESS
ELSE:
    RETURN BLOCKERS

UNLOCK STATE
```

No race condition may allow two exclusive reservations to be granted simultaneously.

---

# 28. Process Race Handling

A process can start immediately after ComputeWarden grants a reservation.

Perfect prevention is impossible when external non-cooperating processes are involved.

ComputeWarden should therefore distinguish between:

```text
Known safe at acquisition time
```

and:

```text
Guaranteed exclusive control of the machine
```

Only the first can be promised.

If a configured blocking process starts while a reservation already exists, ComputeWarden should report both blockers.

It should NOT automatically revoke an existing reservation unless future policy explicitly supports that behavior.

---

# 29. Status Diagnostics

Status should provide enough information to understand the machine immediately.

Example:

```text
ComputeWarden: BUSY

Reservations:
  CodeCarver
  "Repository reachability analysis"
  Started: 14:32:17
  Lease remaining: 183 seconds

Detected intensive processes:
  CodeCarver.exe
  PID: 18224

Manual blockers:
  None
```

Diagnostic information is particularly important because coding agents must be able to explain why they are waiting.

---

# 30. Logging

ComputeWarden should log important lifecycle events:

```text
Service started
Service stopped
Process blocker detected
Process blocker cleared
Reservation acquired
Reservation renewed
Reservation released
Reservation expired
Manual blocker created
Manual blocker cleared
Configuration error
IPC error
```

Routine process polling should NOT generate log spam.

For example, do not log every two seconds that MSBuild is still running.

Log state transitions instead.

---

# 31. Observability

Useful counters should be maintained where inexpensive:

```text
total_acquisitions
failed_acquisitions
expired_reservations
current_reservations
current_process_blockers
uptime
```

These may be returned through status/debug operations.

---

# 32. Failure Philosophy

ComputeWarden should generally fail conservatively.

If ComputeWarden cannot reliably determine whether intensive processing is safe, it should report:

```text
UNKNOWN
```

rather than incorrectly reporting:

```text
AVAILABLE
```

Therefore the complete internal state model may be:

```text
AVAILABLE
BUSY
UNKNOWN
```

Examples that may produce `UNKNOWN`:

- Process enumeration failure.
- Internal monitor failure.
- Corrupt configuration affecting process rules.

Agents should treat `UNKNOWN` as:

```text
Do not begin protected intensive work.
```

---

# 33. Suggested Internal Architecture

```text
ComputeWarden
│
├── ComputeWarden.Service
│   ├── ProcessMonitor
│   ├── ReservationManager
│   ├── BlockerManager
│   ├── LeaseManager
│   ├── ConfigurationManager
│   ├── StatusProvider
│   └── IPCServer
│
├── ComputeWarden.Client
│   └── IPCClient
│
├── ComputeWarden.Mcp
│   ├── StatusTool
│   ├── AcquireTool
│   ├── RenewTool
│   ├── ReleaseTool
│   └── ManualStateTools
│
└── ComputeWarden.Tests
```

Keep resource-detection logic separate from reservation logic.

This makes it possible to add new blocker providers later.

---

# 34. Blocker Provider Architecture

Consider defining an abstraction similar to:

```text
IBlockerProvider
{
    GetBlockers()
}
```

Initial implementations:

```text
ProcessBlockerProvider
ReservationBlockerProvider
ManualBlockerProvider
```

Future implementations could include:

```text
CpuLoadBlockerProvider
DiskLoadBlockerProvider
MemoryPressureBlockerProvider
GpuLoadBlockerProvider
BatteryBlockerProvider
ThermalBlockerProvider
```

The machine state then becomes conceptually:

```text
blockers = all providers.GetBlockers()

if provider failure affecting confidence:
    UNKNOWN
else if blockers.Any():
    BUSY
else:
    AVAILABLE
```

This keeps the architecture extensible without requiring those features in version 1.

---

# 35. Optional Resource Detection

Do NOT make dynamic resource thresholds mandatory for version 1.

However, the architecture should leave room for rules such as:

```yaml
resources:
  disk:
    block_if_utilization_above_percent: 90

  cpu:
    block_if_utilization_above_percent: 95
    sustained_seconds: 10
```

These should be considered later enhancements rather than prerequisites.

Known-process detection plus cooperative reservations solves the initial problem with far less complexity.

---

# 36. Testing Requirements

Tests should include at minimum:

### Reservation tests

- Acquire while available.
- Acquire while another reservation exists.
- Release reservation.
- Renew reservation.
- Reservation expiration.
- Invalid reservation ID.
- Double release.
- Concurrent acquisition attempts.

### Process tests

- Configured process appears.
- Configured process disappears.
- Multiple configured processes.
- Unconfigured process ignored.
- Process-monitor failure.
- Availability debounce.

### Combined tests

- Process blocker + reservation.
- Manual blocker + process blocker.
- Process begins during reservation.
- Reservation expires while process blocker exists.

### Failure tests

- MCP client disappears.
- Service restarts.
- Malformed IPC request.
- Corrupt configuration.
- Extremely long metadata.
- Rapid acquire/release cycles.

### Concurrency test

Launch many simultaneous acquisition requests.

Exactly one must succeed when exclusive reservations are enabled.

This should be considered a critical correctness test.

---

# 37. Performance Requirements

ComputeWarden itself must remain lightweight.

Targets:

- Negligible idle CPU usage.
- Minimal memory footprint.
- No continuous disk writes.
- No busy-wait loops.
- No excessive process enumeration.
- No high-frequency logging.
- No database requirement.

Ironically, the tool designed to prevent resource contention must not become a meaningful source of resource contention itself.

---

# 38. Agent-Friendly Responses

MCP responses should favor structured information over prose.

Bad:

```text
Sorry, the machine appears to be busy right now.
```

Better:

```json
{
    "state": "BUSY",
    "can_run_intensive": false,
    "blockers": [
        {
            "type": "RESERVATION",
            "owner": "Claude Code",
            "description": "CodeCompass full repository indexing",
            "lease_remaining_seconds": 117
        }
    ]
}
```

This allows agents to make their own decisions and produce useful explanations.

---

# 39. Version 1 Minimum Viable Product

Version 1 should remain intentionally small.

Required:

- Windows support.
- Background service.
- Process-name monitoring.
- Configurable blocking process list.
- Machine state calculation.
- Exclusive reservations.
- Lease expiration.
- Reservation renewal.
- Reservation release.
- Manual blocker.
- Local IPC.
- MCP adapter.
- Structured status reporting.
- Logging.
- Concurrency-safe acquisition.
- Automated tests.

Not required:

- CPU monitoring.
- GPU monitoring.
- Disk utilization monitoring.
- Network monitoring.
- Multiple reservation classes.
- Distributed coordination.
- GUI.
- Web interface.
- Cloud services.
- Automatic process throttling.

---

# 40. Future Enhancements

Potential future additions include:

- CPU-intensive reservations.
- Disk-intensive reservations.
- GPU-intensive reservations.
- Shared vs exclusive reservations.
- Reservation priorities.
- Waiting queues.
- Fairness/FIFO acquisition.
- MCP notifications when availability changes.
- Real-time disk utilization detection.
- CPU-pressure detection.
- Memory-pressure detection.
- GPU utilization detection.
- Thermal-state detection.
- Battery-aware policies.
- Per-repository coordination.
- Per-drive coordination.
- Linux daemon support.
- CLI client.
- System-tray status application.

Example future resource-specific acquisition:

```text
acquire(
    resources = ["DISK"],
    owner = "CodeCompass"
)
```

This could permit:

```text
Agent A -> CPU intensive
Agent B -> Disk intensive
```

while preventing:

```text
Agent A -> Disk intensive
Agent B -> Disk intensive
```

Do not implement this complexity until there is a demonstrated need.

---

# 41. Design Principles

ComputeWarden should follow these principles:

### Simple for clients

The normal workflow should be:

```text
acquire
work
release
```

### Safe after crashes

Every reservation expires.

### Atomic

Checking and acquiring must be one operation.

### Explainable

Every BUSY state has identifiable blockers.

### Local

No external infrastructure is required.

### Lightweight

ComputeWarden should consume essentially nothing while idle.

### Extensible

Resource detection and blocker types should be replaceable/additive.

### Cooperative

ComputeWarden coordinates well-behaved tools rather than attempting to control the entire operating system.

---

# 42. Definition of Done

ComputeWarden version 1 is complete when the following scenario works reliably:

```text
Agent A asks ComputeWarden for permission to perform intensive work.

ComputeWarden verifies:
- no configured blocking processes are running,
- no other reservation exists,
- no manual blocker exists.

Agent A atomically receives a reservation.

Agent B attempts to acquire a reservation.

ComputeWarden rejects Agent B and identifies Agent A as the blocker.

Agent A performs intensive work and periodically renews its lease.

Agent A releases the reservation.

Agent B can now acquire the machine.

If Agent A crashes without releasing the reservation, its lease expires and the machine eventually becomes available again.

If a configured external intensive process is running, both agents are prevented from acquiring a reservation until that process exits.
```

That behavior is the core contract of ComputeWarden.