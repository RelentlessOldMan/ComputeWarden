# ComputeWarden 🚦

A lightweight, machine-wide **coordination service** that answers one question for cooperating
tools:

> **Is it currently safe to begin intensive processing on this machine?**

It stops multiple coding agents, build systems, and indexers from all hammering the same
machine at once — by *coordination*, not enforcement. ComputeWarden reports state and hands
out short-lived reservations; **cooperating** tools ask before starting heavy work and honor
the answer. It never kills, throttles, or reprioritizes anything.

## How it works

ComputeWarden derives a machine state — `AVAILABLE`, `BUSY`, or `UNKNOWN` — from a set of
**blockers**:

- **Process blockers** — a configured intensive process (e.g. `MSBuild.exe`) is running.
- **Reservation blockers** — a cooperating agent holds a reservation.
- **Manual blockers** — an operator marked the machine busy (e.g. disk maintenance).

An agent that wants to do heavy work calls **acquire**. If nothing is blocking, it atomically
receives an expiring **reservation** (a lease). It renews the lease during long work and
releases it when done. If it crashes, the lease expires and the machine frees itself — no
stuck flags to clean up.

```
Claude Code ─┐
Other agent ─┤─ MCP ─►  cw-mcp adapter (one per client)
Dev tool    ─┘                    │
                          named pipe \\.\pipe\ComputeWarden
                                   │
                                   ▼
                        ComputeWarden daemon   ◄── single machine-wide authority
                        ├─ ProcessMonitor (polls, applies config rules)
                        ├─ ReservationManager (atomic acquire, leases)
                        ├─ BlockerManager (process + reservation + manual)
                        └─ NamedPipe server
```

The **daemon** is the single source of truth. Each agent runs a thin **MCP adapter** that
forwards calls over a local named pipe; the first adapter to start auto-spawns the daemon if
it isn't already running. No Windows Service, no installer, no network port, no database.

## Projects

| Project | What it is |
|---|---|
| `ComputeWarden.Core` | Pure, OS-agnostic domain: `Warden` (atomic acquire), blockers, leases, process detection, IPC dispatcher. No I/O dependencies. |
| `ComputeWarden.Daemon` | The background authority: process monitor, lease sweeper, named-pipe server, YAML config, single-instance guard. |
| `ComputeWarden.Client` | `WardenClient` — typed IPC client library (also usable by a future CLI). |
| `ComputeWarden.Mcp` | The stdio MCP adapter agents talk to; auto-spawns the daemon. |
| `ComputeWarden.Tests` | xUnit test suite. |

## Build & test

```powershell
dotnet build
dotnet test
```

Requires the **.NET 10 SDK** (the MCP SDK depends on the .NET 10 base libraries).

## Publish (self-contained)

Publishes the daemon and MCP adapter as self-contained single-file executables into
`publish/` so target machines don't need the .NET runtime installed. Both land in the same
folder, which is how the adapter finds and auto-spawns the daemon.

```powershell
./publish.ps1              # win-x64, Release
```

## Wire into Claude Code

Point Claude Code at the published adapter (see `.mcp.json.example`):

```json
{
  "mcpServers": {
    "computewarden": {
      "command": "C:\\path\\to\\publish\\ComputeWarden.Mcp.exe"
    }
  }
}
```

## MCP tools

| Tool | Purpose |
|---|---|
| `computewarden_status` | Full state: `AVAILABLE`/`BUSY`/`UNKNOWN` + all blockers explaining why. |
| `computewarden_can_run` | Quick availability check. Informational only — **not** a guarantee; use acquire before protected work. |
| `computewarden_acquire` | Atomically check + reserve. Returns a reservation, or the current blockers. |
| `computewarden_renew` | Extend a reservation's lease during long work. |
| `computewarden_release` | Release a reservation (idempotent). |
| `computewarden_set_manual_busy` | Mark the machine busy for undetectable work. |
| `computewarden_clear_manual_busy` | Clear the manual blocker. |

All tools return compact structured JSON so an agent can decide and explain, rather than parse prose.

## Agent usage pattern

```
1. Decide the upcoming work is intensive.
2. computewarden_acquire.
3. If not acquired  -> don't start; report/wait/retry (every 5-15s, no aggressive polling).
4. If acquired      -> save reservation_id.
5. Do the work; periodically computewarden_renew.
6. computewarden_release (in a finally/cleanup block).
```

`UNKNOWN` means ComputeWarden can't reliably tell — treat it as "do not begin protected work."

### Wiring it into an AI coding agent (recommended convention)

ComputeWarden only helps if agents actually ask. To avoid gating *everything*, make it
**opt-in per project** and driven by whether the work truly saturates the machine. Drop this
snippet into your agent's global instructions (e.g. Claude Code's `~/.claude/CLAUDE.md`):

```markdown
## ComputeWarden (heavy-compute gate)
`computewarden_*` MCP tools cooperatively coordinate machine-saturating work. Default OFF —
never gate normal work. Gate only when the project's own CLAUDE.md has
`ComputeWarden: gate intensive work` (or the user asks) AND the op is one known to peg
most/all cores or sustain heavy disk I/O — any duration (full indexing, clean parallel
builds, large test fan-outs, ML/encode). Then computewarden_acquire (owner, description,
lease_seconds); if not acquired or UNKNOWN, don't start — report the blocker, ask whether to
wait/retry or proceed; computewarden_release when done (crash-safe via lease expiry).
```

Then, in each project you actually want gated, add a single line to *that project's* CLAUDE.md:

```markdown
ComputeWarden: gate intensive work
```

Everything else is left ungated, which is the point.

## Configuration

The daemon reads `%ProgramData%\ComputeWarden\config.yaml` (override with the
`COMPUTEWARDEN_CONFIG` environment variable). If absent, built-in defaults are used. See
[`config.example.yaml`](config.example.yaml) for the full shape: poll interval, lease bounds,
debounce, the blocking `process_rules` list, and log level.

## Design notes

See [`DESIGN.md`](DESIGN.md) for the full implementation plan and rationale, and
[`OriginalSpec.md`](OriginalSpec.md) for the original specification.

## Scope (v1)

**Included:** Windows support, background daemon, process-name monitoring, machine-state
calculation, exclusive reservations with lease expiry/renewal/release, manual blocker, local
named-pipe IPC, MCP adapter, structured status, logging, concurrency-safe acquire, tests.

**Not included (by design):** CPU/GPU/disk/network monitoring, multiple reservation classes,
distributed coordination, process throttling/killing, GUI, cloud services. The architecture
leaves room to add resource-based blocker providers later without rework.

## Contributing

This is a personal tool, published as-is — **issues and pull requests aren't accepted** (PRs auto-close). Fork it and make it your own. 🚦

## License

MIT — see [LICENSE](LICENSE). © 2026 RelentlessOldMan.
