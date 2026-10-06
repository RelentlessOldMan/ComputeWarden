# ComputeWarden 🚦

A small Windows background service that AI coding agents and build tools ask before starting
heavy work, so they take turns instead of all saturating one machine at once.

Two agents, one machine: agent A asks for the CPU and gets it. Agent B asks for the CPU and is
told "no, A is indexing the repo", so it waits or asks its human. Meanwhile agent C asks for the
GPU and goes ahead, because nobody holds it. When A finishes (or crashes), its hold expires
and B can start.

ComputeWarden coordinates; it never enforces. Nothing is killed, throttled, or
reprioritized. It only helps tools that ask (over [MCP](https://modelcontextprotocol.io)) and
respect the answer.

## How it works

ComputeWarden tracks **blockers**, meaning reasons some resource is in use:

- **Reservations**: an agent asked for resources and got them, on an expiring lease.
- **Processes**: a program you listed in the config is running (a game, `MSBuild.exe`, an indexer).
- **Manual holds**: you marked resources busy for something it can't detect.

Each blocker occupies some of `cpu`, `gpu`, `ram`, `network`, and `disk`. An agent calls
**acquire** with the resources its job will saturate, and it gets a reservation only if no
blocker overlaps them. A CPU+network job and a GPU+RAM job can run side by side; two GPU
jobs take turns. Leaving out `resources` means all of them, i.e. the whole machine.

**Ask for everything in one call.** Request every resource a job needs up front (`[ram, disk]`,
not `ram` now and `disk` later). Holding one resource while waiting on another is how two
agents end up stuck on each other. Each acquire is a separate, unrelated reservation, so a
second request can even be refused by your own first hold. If a job's needs change, release
and re-acquire the full set.

Reservations are leases: renew during long work, release when done. If the agent crashes,
the lease runs out and the resources free themselves. There are no stuck flags to clean up.

## Install

Windows x64. Self-contained, so you don't need the .NET runtime.

1. Download the latest `ComputeWarden-vX.Y.Z-win-x64.zip` from
   [Releases](https://github.com/RelentlessOldMan/ComputeWarden/releases).
2. Extract `ComputeWarden.Mcp.exe` and `ComputeWarden.Daemon.exe` into one folder, e.g.
   `%LOCALAPPDATA%\ComputeWarden\bin`. The two must stay side by side.
3. Register the MCP server with your agent:

   **Claude Code** (user scope = available in every project):
   ```powershell
   claude mcp add --scope user computewarden "$env:LOCALAPPDATA\ComputeWarden\bin\ComputeWarden.Mcp.exe"
   ```

   **Codex** (`~/.codex/config.toml`):
   ```toml
   [mcp_servers.computewarden]
   command = 'C:\Users\you\AppData\Local\ComputeWarden\bin\ComputeWarden.Mcp.exe'
   ```

   Other MCP clients: point them at `ComputeWarden.Mcp.exe` as a stdio server (see
   [`.mcp.json.example`](.mcp.json.example)).
4. Optional: copy [`config.example.yaml`](config.example.yaml) to
   `%ProgramData%\ComputeWarden\config.yaml` and list the programs that should count as busy.

You don't need to start anything yourself. The first time an agent calls a tool, the adapter
starts the daemon in the background, and it then runs until reboot.

**Check it works:** start a new agent session and ask it to call `computewarden_status`.
You should get something like:

```json
{"state":"AVAILABLE","can_run_intensive":true,"busy_resources":[],"blockers":[]}
```

## Getting agents to use it

Agents only help if they ask, and you don't want them gating every `git status`. What works
well is making it **opt-in per project**, limited to work that really saturates the machine.
Add this to your agent's global instructions (for Claude Code, `~/.claude/CLAUDE.md`):

```markdown
## ComputeWarden (heavy-compute gate)
`computewarden_*` MCP tools cooperatively coordinate machine-saturating work. Default OFF —
never gate normal work. Gate only when the project's own CLAUDE.md has
`ComputeWarden: gate intensive work` (or the user asks) AND the op is one known to be
resource-heavy — pegging most/all cores, holding a large share of RAM, or sustaining heavy
disk I/O; any duration (full indexing, clean parallel builds, large test fan-outs, ML
training/inference, media encode, big in-memory analysis). Intensity is about any saturated
resource, not just CPU. Then computewarden_acquire (owner, description, leaseSeconds,
resources = all of cpu/gpu/ram/network/disk it saturates, in ONE call — never add more via a
second acquire); if not acquired or UNKNOWN, don't start — report the blocker, ask whether to
wait/retry or proceed; computewarden_release when done (crash-safe via lease expiry).
```

Then add one line to the CLAUDE.md of each project you want gated:

```markdown
ComputeWarden: gate intensive work
```

## MCP tools

| Tool | What it does |
|---|---|
| `computewarden_acquire` | Atomically checks the requested `resources` (default: all) and reserves them. Returns `reservation_id` + expiry, or the conflicting blockers. |
| `computewarden_renew` | Extends a reservation's lease during long work. |
| `computewarden_release` | Releases a reservation. Safe to call twice. |
| `computewarden_status` | `AVAILABLE` / `BUSY` / `UNKNOWN`, `busy_resources`, and every blocker with its owner and resources. |
| `computewarden_can_run` | Quick check for the given `resources`. Informational only: it reserves nothing, so use acquire before real work. |
| `computewarden_set_manual_busy` | Holds the machine (or given `resources`) busy for work ComputeWarden can't detect. |
| `computewarden_clear_manual_busy` | Clears that manual hold. |
| `computewarden_stats` | Counters since the daemon started. A high `expired_reservations` means agents acquire but don't release. |

All tools return compact JSON, and errors come back as JSON with an `error` field too.

`UNKNOWN` means ComputeWarden couldn't read the process list, so it can't tell what's free.
Acquire refuses everything until that clears.

## Configuration

The daemon reads `%ProgramData%\ComputeWarden\config.yaml` (or wherever the
`COMPUTEWARDEN_CONFIG` environment variable points). Without one it uses defaults: lease
30s–1h (default 5 min), and MSBuild + CodeCompass as busy processes.

The part you'll edit most is the process list:

```yaml
process_rules:
  - name: Overwatch
    executable: Overwatch.exe          # blocks everything while running
  - name: CodeCarver
    executable: CodeCarver.exe
    resources: [ram, disk]             # memory-heavy: CPU/GPU work can still run
```

**Edits apply live.** The daemon watches the file, so changes to `process_rules` and the
debounce settings take effect within about a second. A broken edit is logged and ignored,
and the previous rules stay active. Changing the poll interval, lease bounds,
`allow_manual_blocker`, or logging needs a daemon restart.

[`config.example.yaml`](config.example.yaml) documents every setting.

## Troubleshooting

**Where are the logs?** `%ProgramData%\ComputeWarden\logs\daemon.log` (capped at 5 MB, with one
`.1` backup). It records every acquire, release, and lease expiry, plus process blockers
appearing and clearing, and config reloads.

**Restart the daemon:** `Stop-Process -Name ComputeWarden.Daemon`. The next tool call starts
a fresh one. Reservations are in memory, so a restart clears them.

**"Access denied to the ComputeWarden pipe":** the running daemon was started by another user
or from an elevated (admin) session, and normal sessions can't connect to it. Stop it (from
that session, or an elevated prompt) and let a normal session start its own.

**"daemon is not running and could not be launched":** `ComputeWarden.Daemon.exe` isn't next
to `ComputeWarden.Mcp.exe`. Put it there, or set `COMPUTEWARDEN_DAEMON` to its full path.

**Updating:** each open agent session keeps `ComputeWarden.Mcp.exe` locked, so it can't be
overwritten, but Windows does allow renaming it. Stop the daemon, rename the old adapter
aside (e.g. to `ComputeWarden.Mcp.exe.old`), copy both new exes in, and delete the old one
later. `./publish.ps1 -Install` does all of this when building from source. Open sessions
keep using the old adapter until they restart.

## Limitations

- **Cooperative only.** A program that never asks does whatever it wants.
- **Resources are declared, not measured.** ComputeWarden doesn't watch CPU or GPU load. It
  trusts agents to name what they'll saturate, and only knows programs you've listed.
- **All-or-nothing per resource.** Two jobs that each need 30% of RAM still take turns.
- **Windows, one user.** The daemon serves the user who started it; there's no network or
  multi-machine coordination.

## Building from source

Requires the .NET 10 SDK.

```powershell
dotnet build
dotnet test
./publish.ps1            # self-contained win-x64 exes -> publish/
./publish.ps1 -Install   # ...and copy them to %LOCALAPPDATA%\ComputeWarden\bin
./release.ps1            # test, bump patch version, zip, tag, and create a GitHub release
```

The version lives in `Directory.Build.props`.

```
Claude Code ─┐
Codex       ─┼─ stdio MCP ─► ComputeWarden.Mcp.exe   (one per agent session)
Other client ┘                        │
                         named pipe \\.\pipe\ComputeWarden (current user only)
                                      ▼
                           ComputeWarden.Daemon.exe   (one per machine; holds all state)
                           ├─ Warden: blockers + atomic acquire
                           ├─ ProcessMonitor: polls processes against config rules
                           ├─ LeaseSweeper: expires abandoned reservations
                           └─ config watcher: hot-reloads process rules
```

| Project | Contents |
|---|---|
| `src/ComputeWarden.Core` | The domain logic, with no I/O: `Warden`, blockers, resources, leases, process matching, IPC dispatcher. |
| `src/ComputeWarden.Daemon` | Background process: pipe server, process polling, YAML config, logging, single-instance guard. |
| `src/ComputeWarden.Client` | `WardenClient`, a typed client for the pipe protocol. |
| `src/ComputeWarden.Mcp` | The stdio MCP adapter; starts the daemon on demand. |
| `tests/ComputeWarden.Tests` | xUnit tests. |

[`DESIGN.md`](DESIGN.md) explains the design decisions (state model, acquire algorithm,
protocol). [`OriginalSpec.md`](OriginalSpec.md) is the original specification.

## Contributing

This is a personal tool, published as-is — **issues and pull requests aren't accepted** (PRs auto-close). Fork it and make it your own. 🚦

## License

MIT — see [LICENSE](LICENSE). © 2026 RelentlessOldMan.
