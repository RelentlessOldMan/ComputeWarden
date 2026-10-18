# ComputeWarden 🚦

A small Windows background service that AI coding agents and build tools ask before starting
heavy work, so they take turns instead of all saturating one machine at once.

Two agents, one machine: agent A asks for the CPU and gets it. Agent B asks for the CPU and is
told "no, A is indexing the repo", so it waits or asks its human. Meanwhile agent C asks for the
GPU and goes ahead, because nobody holds it. When A finishes (or crashes), its hold expires
and B can start.

It also handles everyday work. Agents report their builds, tests and runs, and the warden says
how big each may run right now: **full**, **throttled** (one core, low priority) or
**queued**. While you're gaming, everyone keeps it small. When the game exits, their next
check-in says full and they go full speed, without you telling each session by hand.

ComputeWarden coordinates; it never enforces. Nothing is killed, capped, or reprioritized:
"throttled" is advice the agent applies to its own next command. It only helps tools that
ask (over [MCP](https://modelcontextprotocol.io)) and respect the answer.

## How it works

ComputeWarden tracks **blockers**, meaning reasons some resource is in use:

- **Reservations**: an agent asked for resources and got them, on an expiring lease.
- **Processes**: a program you listed in the config is running, such as a game.
- **Manual holds**: you marked resources busy for something it can't detect.

Each blocker occupies some of `cpu`, `gpu`, `ram`, `network`, and `disk`. An agent calls
**acquire** with the resources its job will saturate, and it gets a reservation only if no
blocker overlaps them. A CPU+network job and a GPU+RAM job can run side by side; two GPU
jobs take turns. Leaving out `resources` means all of them, i.e. the whole machine (for a
shared claim it means cpu+ram+disk instead).

**Ask for everything in one call.** Request every resource a job needs up front (`[ram, disk]`,
not `ram` now and `disk` later). Holding one resource while waiting on another is how two
agents end up stuck on each other. Each acquire is a separate, unrelated reservation, so a
second request can even be refused by your own first hold. If a job's needs change, release
and re-acquire the full set.

Reservations are leases: renew during long work, release when done. If the agent crashes,
the lease runs out and the resources free themselves. There are no stuck flags to clean up.

## Sharing the machine

An acquire with a `kind` is a **claim**, and the answer is **advice** rather than a plain
yes or no:

| Claim | For | Gets |
|---|---|---|
| **Priority** | Programs in your config (games, etc.) | **Your lease.** While one runs, every agent on its resources is limited. Nothing overrides it. |
| **Exclusive** | An agent's heavy job (indexing, test fan-out) | One holder per resource. Others wait in line or run throttled (`whenBusy: throttle` / `wait`). |
| **Shared** | An agent's everyday build, test or run | Never refused. Full when nothing heavy overlaps; throttled when a game or an exclusive holder does. Defaults to `cpu, ram, disk`. |

- **Advice:**
  - `full` means run normally.
  - `throttled` comes with `max_cores`, `priority`, `max_ram_gb` and a concrete `how`
    (e.g. `dotnet build -m:1`).
  - `queued` gives your place in line and an estimated wait.
- **Advice is live.** Each renew returns the current advice:
  - A game starting turns full into throttled.
  - A game exiting turns throttled into full.
  - A queued claim becomes full when its turn comes.
- **Waiting is cheap.** `renew` with `waitSeconds` (max 60) holds the answer until the advice
  changes. Queue tickets must check in at least every 2 minutes, or they drop out of line.
- **The line is first come, first served.** At most 3 throttled claims are allowed on each
  contended resource; later ones queue, and a freed slot goes to the earliest of them. Work
  already running is never told to stop, only to shrink. The GPU can't be shared, so GPU
  work queues behind a game (or another agent's GPU job) instead.
- **Skipping the line:** `userPriority` puts a claim at the front of the line, but it never
  preempts a holder. Agents set it only when you tell them your work comes first.
- **Estimates:** `estimateSeconds` feeds others' wait estimates and sets the claim's lease.
  `cores` and `ramGb` are optional and recorded only.

### The mode: your override

`cw` is a small CLI for you. Agents set the mode (`computewarden_set_mode`) only when you
tell them to.

```powershell
cw                               # status: mode, games, and every agent claim
cw mode steady 2h gaming tonight # "y'all go slow and steady" for two hours
cw mode open                     # "full bore allowed with a lease"
cw mode hold 30m recording       # nobody starts anything new
cw mode auto                     # back to normal rules
```

| Mode | Effect |
|---|---|
| `auto` | The rules above. |
| `steady` | Everyone runs throttled; nobody goes full. The 3-per-resource cap applies, and `whenBusy: wait` claims wait for the mode to end. |
| `open` | No throttling between agents. Exclusive holds still keep two heavy jobs apart. A game's throttling and cap still apply. |
| `hold` | New work is queued; work already running is throttled. |

A mode can take a duration (90m, 2h, 1h30m, 1d; a unit is required, at most 7d), a reason (shown to agents in their advice),
and `--resources cpu,ram` to cover only some resources. **No mode overrides a running
game.** Inside a Claude Code session you can run it as `! cw mode open`.

## Install

Windows x64. Self-contained, so you don't need the .NET runtime.

1. Download the latest `ComputeWarden-vX.Y.Z-win-x64.zip` from
   [Releases](https://github.com/RelentlessOldMan/ComputeWarden/releases).
2. Extract `ComputeWarden.Mcp.exe`, `ComputeWarden.Daemon.exe` and `cw.exe` into one
   folder, e.g. `%LOCALAPPDATA%\ComputeWarden\bin`. The adapter and daemon must stay side by
   side. Add the folder to your PATH to run `cw` from any terminal:
   ```powershell
   [Environment]::SetEnvironmentVariable("Path", [Environment]::GetEnvironmentVariable("Path", "User") + ";$env:LOCALAPPDATA\ComputeWarden\bin", "User")
   ```
3. Register the MCP server with your agent:

   **Claude Code** (user scope = available in every project):
   ```powershell
   claude mcp add --scope user computewarden "$env:LOCALAPPDATA\ComputeWarden\bin\ComputeWarden.Mcp.exe"
   ```

   **Codex** (`~/.codex/config.toml`):
   ```toml
   [mcp_servers.computewarden]
   command = 'C:\Users\<you>\AppData\Local\ComputeWarden\bin\ComputeWarden.Mcp.exe'
   # a renew can wait up to 60 s for new advice, longer than Codex's default tool timeout
   tool_timeout_sec = 120
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
{"state":"AVAILABLE","can_run_intensive":true,"busy_resources":[],"mode":{"mode":"auto"},"blockers":[],"claims":[]}
```

## Getting agents to use it

Agents only help if they report their work. Add this to your agent's **global** instructions
(Claude Code: `~/.claude/CLAUDE.md`; Codex: `~/.codex/AGENTS.md`), so every session on the
machine follows it, not just one project's. A copy is in [`examples/CLAUDE.md`](examples/CLAUDE.md).
Each command costs two small tool calls (acquire and release):

```markdown
## ComputeWarden (shared machine)
`computewarden_*` MCP tools share this machine with other agent sessions and the user's games. Advisory: nothing enforces it, so follow the advice. If the tools aren't there, or a call returns `error` and one retry does too, work normally.
- **Everyday work:** before a build, test run, or app run, call `computewarden_acquire` with `kind: "shared"`, an owner like "Claude Code (<repo>)", a description, and `estimateSeconds`. Pass `resources` only if the work isn't cpu+ram+disk. Release when it finishes, even if it failed.
- **Heavy work:** for jobs that saturate a resource (full indexing, clean parallel builds, big test fan-outs, ML, encodes), use `kind: "exclusive"` with `estimateSeconds`. Pass every resource it saturates in ONE call, and `whenBusy: "throttle"` (run small now) or `"wait"`. Release it even if it fails.
- **Follow `advice.grant`:**
  - `full`: run normally.
  - `throttled`: run small per `advice.how` (e.g. `-m:1`, 1 core, below-normal priority).
  - `queued`: don't start. Call `computewarden_renew` with `waitSeconds: 60` until the grant changes. If `est_wait_seconds` is missing, or you've waited more than 10 minutes, tell the user and ask whether to keep waiting.
- **Long work:** `estimateSeconds` sets the lease, so err high. Run long jobs in the background and renew within the lease. When the grant changes, size the next step to match; a switch to `full` means go full speed. If renew returns `renewed: false`, the claim is gone: acquire again.
- **User only:** set `userPriority` (skip the line), call `computewarden_set_mode`, or set a manual hold only when the user explicitly says so.
```

Older wording that only gates heavy work with a plain acquire (no `kind`) still works.
Those acquires are all-or-nothing and are refused while anyone is in line for their resources.

## MCP tools

| Tool | What it does |
|---|---|
| `computewarden_acquire` | Reports work. With `kind` (`shared` / `exclusive`), always creates a claim and returns `advice` (`full` / `throttled` / `queued`, with reason, throttle limits, `how`, and place in line). Without `kind`, it's the legacy all-or-nothing reserve: `reservation_id` or the conflicting blockers. |
| `computewarden_renew` | Extends the lease and returns current advice. `waitSeconds` (max 60) waits for the advice to change first. |
| `computewarden_release` | Releases a claim. Safe to call twice. |
| `computewarden_status` | `AVAILABLE` / `BUSY` / `UNKNOWN`, `busy_resources`, every blocker, the current `mode`, and every agent claim. |
| `computewarden_can_run` | Quick check for the given `resources`. Informational only: it reserves nothing. |
| `computewarden_set_mode` | Sets the user mode (`auto` / `steady` / `open` / `hold`), with optional duration, reason, and resources. **User only:** agents call it only on your word. |
| `computewarden_set_manual_busy` | Holds the machine (or given `resources`) busy for work ComputeWarden can't detect. **User only.** |
| `computewarden_clear_manual_busy` | Clears that manual hold. **User only.** |
| `computewarden_stats` | Counters since the daemon started. A high `expired_reservations` means agents acquire but don't release. |

All tools return compact JSON, and errors come back as JSON with an `error` field too.

`UNKNOWN` means ComputeWarden couldn't read the process list, so it can't tell what's free.
Shared work is throttled (as if a game held its resources), new exclusive claims are queued,
and legacy acquires are refused until that clears. Manual holds and hold mode still apply.

## Configuration

The daemon reads `%ProgramData%\ComputeWarden\config.yaml` (or wherever the
`COMPUTEWARDEN_CONFIG` environment variable points). Without one it uses defaults: lease
30s–1h (default 5 min), and no process rules.

The part you'll edit most is the process list:

```yaml
process_rules:
  - name: Overwatch
    executable: Overwatch.exe          # blocks everything while running
  - name: Video editor
    executable: Resolve.exe
    resources: [cpu, gpu, ram]         # disk and network work can still run
```

**Edits apply live.** The daemon watches the file, so changes to `process_rules`, the
debounce settings, and `sharing` take effect within about a second. A broken edit is logged and ignored,
and the previous rules stay active. Changing the poll interval, lease bounds,
`allow_manual_blocker`, or logging needs a daemon restart.

How small "throttled" is, and how many throttled claims fit, are in `sharing`:

```yaml
sharing:
  throttle_max_cores: 1
  throttle_priority: below_normal   # or idle
  throttle_max_ram_gb: 2
  max_throttled_per_resource: 3     # beyond this, new claims queue
  unshareable: [gpu]                # queued, not throttled, while contended
  queue_checkin_seconds: 120
```

[`config.example.yaml`](config.example.yaml) documents every setting.

## Troubleshooting

**Where are the logs?** `%ProgramData%\ComputeWarden\logs\daemon.log` (capped at 5 MB, with one
`.1` backup). It records every acquire, release, and lease expiry, plus process blockers
appearing and clearing, and config reloads.

**Restart the daemon:** `Stop-Process -Name ComputeWarden.Daemon`. The next tool call starts
a fresh one. Claims and the mode are in memory, so a restart clears them.

**What is everyone doing?** Run `cw`. It shows the mode, running games, and every agent
claim with its advice, place in line, and lease.

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
- **Advice, not limits.** A throttled agent shrinks its *next* command; a process already
  running at full size keeps going until it finishes.
- **Coarse sharing.** Exclusive claims are one holder per resource. Throttled means a fixed
  small size (1 core by default), not a fair split of what's free.
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
Other client ┘                        │       cw.exe (you) ─┐
                                      │◄────────────────────┘
                         named pipe \\.\pipe\ComputeWarden (current user only)
                                      ▼
                           ComputeWarden.Daemon.exe   (one per machine; holds all state)
                           ├─ Warden: blockers, claims, the line, advice, mode
                           ├─ ProcessMonitor: polls processes against config rules
                           ├─ LeaseSweeper: expires abandoned reservations
                           └─ config watcher: hot-reloads process rules and sharing
```

| Project | Contents |
|---|---|
| `src/ComputeWarden.Core` | The domain logic, with no I/O: `Warden`, blockers, resources, leases, process matching, IPC dispatcher. |
| `src/ComputeWarden.Daemon` | Background process: pipe server, process polling, YAML config, logging, single-instance guard. |
| `src/ComputeWarden.Client` | `WardenClient`, a typed client for the pipe protocol. |
| `src/ComputeWarden.Mcp` | The stdio MCP adapter; starts the daemon on demand. |
| `src/ComputeWarden.Cli` | `cw`: status and the user mode from a terminal. |
| `tests/ComputeWarden.Tests` | xUnit tests. |

[`DESIGN.md`](DESIGN.md) explains the design decisions (state model, acquire algorithm,
protocol).

## Contributing

This is a personal tool, published as-is — **issues and pull requests aren't accepted** (PRs auto-close). Fork it and make it your own. 🚦

## License

MIT — see [LICENSE](LICENSE). © 2026 RelentlessOldMan.
