# ComputeWarden 🚦

A small Windows background process that AI coding agents ask before their builds, tests and
runs, so a shared machine stays usable. It does three things:

1. **While you game, agent work runs small.** Each job runs on one core at low priority, and only
   a few run at once (3 by default), so your fps holds. When the game exits, every agent's next
   check-in says full and they go full speed, without you telling each session by hand.
2. **Only one big-memory job at a time.** Two sessions that each want 20 GB don't run together.
3. **Short quiet-machine timing runs.** A session that needs to time something gets the machine
   to itself for up to 10 minutes (longer only with your approval).

The rest of the time, nobody waits: agent work runs side by side at full speed.

ComputeWarden coordinates; it never enforces. Nothing is killed, capped, or reprioritized:
"throttled" is advice the agent applies to its own next command. It only helps tools that
ask (over [MCP](https://modelcontextprotocol.io)) and respect the answer.

## How it works

An agent **claims** before each build, test or run and releases when it ends. The answer is
**advice**:

- `full`: run normally.
- `throttled`: run small. It comes with `max_cores`, `priority`, `max_ram_gb` and a concrete
  `how` (e.g. `dotnet build -m:1`, `start /belownormal`).
- `queued`: don't start yet. It comes with the place in line, who's ahead, and an estimated wait.

| Claim | For | Rule |
|---|---|---|
| **Shared** (the default) | Every build, test and run | Full on an idle machine, side by side with any other work. Throttled while you game. |
| **Big memory** (`bigMemory: true`) | A job that needs more than ~8 GB of RAM and can't cap it, or more than half the GPU's memory | Like shared, but only one big-memory job runs at a time; the next waits. Everyday work isn't held up. |
| **Solo** (`kind: "solo"`) | A timing test or benchmark | Waits until nothing else runs and no game is on, then has the machine for at most 10 minutes (up to 60 with `userApproved: true`, set only on your explicit approval of that run). While it waits, only work whose `estimateSeconds` ends before the running work does may start; nothing starts while it runs. |

**Gaming** means a program from your config's `process_rules` is running. While one runs, work
already running is told to shrink (never to stop), and at most `game_jobs` claims run (3 by
default; you can change it on the panel). Later ones wait their turn. Your **manual hold**
(`cw hold`) works the same way, except nothing new starts at all. If ComputeWarden can't read
the process list (state `UNKNOWN`), it treats that as gaming too.

- **Advice is live.** Each renew returns the current advice. A game starting turns full into
  throttled; a game exiting turns throttled into full; a queued claim becomes full when its
  turn comes.
- **The line is first come, first served.** A freed slot goes to the earliest claim waiting for it.
- **Waiting is cheap.** `renew` with `waitSeconds` (max 60) holds the answer until the advice
  changes. Queue tickets must check in at least every 2 minutes, or they drop out of line.
  Agents with background shells run `cw wait <reservation_id>` instead: it keeps the ticket
  alive, exits when the claim may start, and leaves the agent free to do other work meanwhile.
  `cw wait` only checks the ticket in: the claim starts when its owner renews. A woken claim
  has `queue_checkin_seconds` (default 2 minutes) to do that, or its slot passes to the next in line.
- **Leases.** `estimateSeconds` sets the claim's lease (estimate × 1.25 + 30 s) and others' wait
  estimates. Renew during long work. If an agent crashes, its lease runs out and the claim
  frees itself; there are no stuck flags to clean up.

### `cw`: your controls

```powershell
cw                        # status: games, the hold, and every agent claim
cw hold recording a video # nothing new starts; running work is told to run small
cw hold off               # back to normal
```

Inside a Claude Code session you can run it as `! cw hold`. Agents turn the hold on or off
(`computewarden_set_manual_busy`) only when you tell them to.

### `cw run`: claim, run, throttle, release

```powershell
cw run --estimate 10m --owner "Claude Code (MyRepo)" -- dotnet test
```

Agents run heavy jobs through `cw run` instead of claiming by hand. It acquires (with
`--big-memory` or `--solo` if asked), waits its turn, runs the command and releases when it
exits, passing the command's exit code back. The command runs in its own Windows job object, so
everything it starts (test hosts, compiler servers) is in it too. It turns off the shared
compiler and MSBuild servers for the command: one started earlier by another build runs outside
the job, so work handed to it would keep full speed through a game. While the claim says
`throttled` (a game, or the hold), the whole job is held to the throttle's cores (never core 0)
and priority; when it says `full` again, the job goes back to every core at normal priority.
Work started before the game slows down the moment the game starts, not at the agent's next
renew. If the daemon isn't there, the command runs unclaimed. Ending `cw run` ends the job.

`--low` holds the job to one core at below-normal priority from start to finish, whatever the
advice. Reviews and mutation testing use it: they
fan out many subagents, and ComputeWarden only limits how many jobs run while you game.

## Install

Windows x64. Self-contained, so you don't need the .NET runtime.

1. Download the latest `ComputeWarden-vX.Y.Z-win-x64.zip` from
   [Releases](https://github.com/RelentlessOldMan/ComputeWarden/releases).
2. Extract `ComputeWarden.Mcp.exe`, `ComputeWarden.Daemon.exe` and `cw.exe` into one
   folder, e.g. `%LOCALAPPDATA%\ComputeWarden\bin`. Keep all three side by side: the adapter
   and `cw` start the daemon from their own folder. Add the folder to your PATH to run `cw`
   from any terminal (open a new terminal afterwards so it sees the change):
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
   `%ProgramData%\ComputeWarden\config.yaml` and list your games and other programs agents should yield to.

You don't need to start anything yourself. The first time an agent calls a tool (or you run
`cw`), the daemon starts in the background, and it then runs until reboot.

**Check it works:** start a new agent session and ask it to call `computewarden_status`.
You should get something like:

```json
{"state":"AVAILABLE","can_run_intensive":true,"game_jobs":3,"blockers":[],"claims":[]}
```

## Getting agents to use it

Agents only help if they report their work. Add this to your agent's **global** instructions
(Claude Code: `~/.claude/CLAUDE.md`; Codex: `~/.codex/AGENTS.md`), so every session on the
machine follows it, not just one project's. A copy is in [`examples/CLAUDE.md`](examples/CLAUDE.md).
Each command costs two small tool calls (acquire and release), or none through `cw run`:

```markdown
## ComputeWarden (shared machine)
`computewarden_*` MCP tools share this machine with other agent sessions and the user's games. Advisory: nothing enforces it, so follow the advice. If the tools aren't there, or a call returns `error` and one retry does too, work normally.
- **Claim every build, test run, and app run:** call `computewarden_acquire` right before the command, with an owner like "Claude Code (<repo>)", a description, and `estimateSeconds` (err high: it sets the lease). Release as soon as it exits, even if it failed; renew within the lease during long runs (if a renew says `renewed: false`, acquire again). Don't hold a claim while reading, editing, or waiting on the user.
- **Heavy jobs:** for a build, test run or app run longer than a minute, run `cw run --estimate <duration> --owner "<owner>" -- <command>` (add `--big-memory` or `--solo` per the rules below) instead of acquiring by hand. It waits its turn, slows the command down the moment a game starts and back up after, and releases when the command exits; its exit code is the command's.
- **Reviews and mutation testing:** run every build and test they start, however short, as `cw run --low --estimate <duration> --owner "<owner>" -- <command>`, and tell review subagents to do the same. `--low` holds the job to one core at below-normal priority the whole time, so a fan-out of subagents can't take the machine.
- **Big memory:** set `bigMemory: true` only if the job needs more than ~8 GB of RAM and can't cap it (cap it if you can), or needs more than half the GPU's memory. Only one big-memory job runs at a time.
- **Timing runs:** `kind: "solo"` only for a timing test or benchmark, at most 10 minutes (`estimateSeconds` ≤ 600). It waits for a quiet machine; release the moment the measured run ends. A longer solo run (up to 60 minutes) needs the user's explicit approval for that run: only then set `userApproved: true`. Without it, split the run into timed pieces of 10 minutes or less, or run it as shared.
- **Follow `advice.grant`:** `full`: run normally. `throttled` (the user is gaming, or has put on the hold): run small per `advice.how`; when a renew says `full`, go full speed. `queued`: don't start; run `cw wait <reservation_id>` in the background and do other work. When it exits 0, renew and start right away; exit 3 or 4 means run it again, exit 1 means acquire again. Without background commands, renew with `waitSeconds: 60` until the grant changes. If you've waited more than 10 minutes, ask the user. The manual hold (`computewarden_set_manual_busy`) is the user's call only.
- **Project notes:** a project's CLAUDE.md, AGENTS.md or memory may record only facts about its jobs' resource needs: how long a job takes, its peak memory or memory cap, GPU use, heavy disk I/O. No ComputeWarden or priority rules there (claim kinds, owners, which job is solo, bigMemory or `--low`, how to claim, "always run on one core" or "at low priority"); this section is the only copy of those. When the user asks for a job to run small, do it for that job; don't save it as a standing rule. Decide each claim from these rules and those facts. If you find ComputeWarden or priority rules in a project, delete them and keep the facts.
```

Projects don't need their own ComputeWarden notes. At most, a project's instructions can name
which of its jobs are big-memory or solo, and how to cap a job's memory.

## MCP tools

| Tool | What it does |
|---|---|
| `computewarden_acquire` | Claims work and returns `advice` (`full` / `throttled` / `queued`, with reason, throttle limits, `how`, and place in line). `kind` is `shared` (default) or `solo`; `bigMemory: true` marks a big-memory job. `userApproved: true` (user only) lets a solo claim run up to 60 minutes. |
| `computewarden_renew` | Extends the lease and returns current advice. `waitSeconds` (max 60) waits for the advice to change first. |
| `computewarden_release` | Releases a claim. Safe to call twice. |
| `computewarden_status` | `AVAILABLE` / `BUSY` / `UNKNOWN`, every blocker (games, the hold, a solo run), `game_jobs`, and every agent claim. |
| `computewarden_can_run` | Would new work run full right now? Informational only: it reserves nothing. |
| `computewarden_set_manual_busy` | Turns on the manual hold. **User only:** agents call it only on your word. |
| `computewarden_clear_manual_busy` | Turns the hold off. **User only.** |
| `computewarden_stats` | Counters since the daemon started. A high `expired_reservations` means agents acquire but don't release. |

All tools return compact JSON, and errors come back as JSON with an `error` field too.
`kind: "exclusive"` (before 2.0) is refused with an error that says what to use instead.

## Configuration

The daemon reads `%ProgramData%\ComputeWarden\config.yaml` (or wherever the
`COMPUTEWARDEN_CONFIG` environment variable points). Without one it uses defaults: lease
30s–1h (default 5 min), and no process rules. If the file can't be read or parsed at startup,
the daemon logs the error and starts with defaults.

The part you'll edit most is the process list, the programs agents yield to:

```yaml
process_rules:
  - name: Overwatch
    executable: Overwatch.exe
  - name: Video editor
    executable: Resolve.exe
```

A rule can also set `enabled: false` to switch it off, and a `description` as a note to
yourself.

**Edits apply live.** The daemon watches the file, so changes to `process_rules`, the
debounce settings, and `sharing` take effect within a few seconds. A broken edit is logged
and ignored, and the previous settings stay active. Changing the poll interval, lease bounds,
`allow_manual_blocker`, or logging needs a daemon restart.

How small "throttled" is, and how many jobs run while you game, are in `sharing`:

```yaml
sharing:
  throttle_max_cores: 1
  throttle_priority: below_normal   # or idle
  throttle_max_ram_gb: 2
  game_jobs: 3                      # jobs that may run while gaming (0..12)
  queue_checkin_seconds: 120
```

[`config.example.yaml`](config.example.yaml) documents every setting.

## Live panel (optional)

If [RelentlessEye](https://github.com/RelentlessOldMan/RelentlessEye) is installed, the daemon
shows up in it as a Compute Warden panel with:

- the machine state;
- the **Hold** switch (off/on; turning it on asks you to confirm);
- **Jobs while gaming**;
- running games and other blockers;
- a table of claims: who, where each one is (running full or throttled, its turn but not
  started, or queued with its place in line, plus solo or big memory), the lease left, when it
  last checked in, and what it's doing. A last check-in 15 minutes or more ago is highlighted:
  the work may be finished or stuck;
- a feed of the daemon's log (claims, releases, promotions, expiries).

`reye request ComputeWarden.status` returns the same JSON as `computewarden_status`.

**Jobs while gaming** (0 to 4) sets how many jobs may run beside a game. Each runs on one core,
so it's also how many cores of background work the game shares the machine with. A game suffers
mostly from the memory traffic of background work, which priority doesn't stop; fewer jobs is
what helps. Lowering it makes the next jobs wait; work already running carries on. 0 means
nothing new starts until the game closes. It starts at `game_jobs` from the config and, once
picked, stays across restarts.

Only a monitor (the panel or `reye`) can set the hold or jobs while gaming. Requests from apps
connected to the hub, such as agent sessions, are refused and logged, since both are the user's
call. The panel needs RelentlessEye 1.2 or later.

## Troubleshooting

**Where are the logs?** `%ProgramData%\ComputeWarden\logs\daemon.log` (capped at 5 MB, with one
`.1` backup). It records every acquire, release, and lease expiry, plus process blockers
appearing and clearing, and config reloads.

**Restart the daemon:** `Stop-Process -Name ComputeWarden.Daemon`. The next tool call starts
a fresh one. Claims (with their place in line and leases), the manual hold and jobs while gaming carry
over: the daemon saves them to `%LOCALAPPDATA%\ComputeWarden\state.json` within a second of any
change and loads them when it starts. Claims whose lease ran out while it was down are dropped.
The file holds claim ids, so it stays in your own profile. To start clean, stop the daemon and
delete the file. If the file can't be read, it's moved to `state.json.bad` and the daemon starts
empty.

**What is everyone doing?** Run `cw`. It shows running games, the hold, and every agent
claim with its advice, place in line, and lease.

**"Access denied to the ComputeWarden pipe":** the running daemon was started by another user
or from an elevated (admin) session, and normal sessions can't connect to it. Stop it (from
that session, or an elevated prompt) and let a normal session start its own.

**"daemon is not running and could not be launched":** `ComputeWarden.Daemon.exe` isn't next
to `ComputeWarden.Mcp.exe` (or `cw.exe`). Put it there, or set `COMPUTEWARDEN_DAEMON` to its full path.

**Updating:** each open agent session keeps `ComputeWarden.Mcp.exe` locked, so it can't be
overwritten, but Windows does allow renaming it. Stop the daemon, rename the old adapter
aside (e.g. to `ComputeWarden.Mcp.exe.old`), copy the new exes in, and delete the old one
later. `./publish.ps1 -Install` does all of this when building from source (it leaves
`*.old-<timestamp>` files, cleaned up on the next install). Claims and the hold survive the
restart. Open sessions keep using the old
adapter until they restart.

## Limitations

- **Cooperative only.** A program that never asks does whatever it wants.
- **Declared, not measured.** ComputeWarden doesn't watch CPU, GPU or memory load. It trusts
  agents to say which jobs are big-memory, and only knows programs you've listed.
- **Advice, not limits.** A throttled agent shrinks its *next* command; a process already
  running at full size keeps going until it finishes.
- **Coarse sharing.** Throttled means a fixed small size (1 core by default), not a fair split
  of what's free.
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
                           ├─ Warden: blockers, claims, the line, advice, hold
                           ├─ ProcessMonitor: polls processes against config rules
                           ├─ LeaseSweeper: expires abandoned reservations
                           └─ config watcher: hot-reloads process rules and sharing
```

| Project | Contents |
|---|---|
| `src/ComputeWarden.Core` | The domain logic, with no I/O: `Warden`, blockers, claims, leases, process matching, IPC dispatcher. |
| `src/ComputeWarden.Daemon` | Background process: pipe server, process polling, YAML config, logging, single-instance guard. |
| `src/ComputeWarden.Client` | `WardenClient`, a typed client for the pipe protocol. |
| `src/ComputeWarden.Mcp` | The stdio MCP adapter; starts the daemon on demand. |
| `src/ComputeWarden.Cli` | `cw`: status, the manual hold, `cw wait` and `cw run`, from a terminal. |
| `tests/ComputeWarden.Tests` | xUnit tests. |

[`DESIGN.md`](DESIGN.md) explains the design decisions (state model, acquire algorithm,
protocol).

## Contributing

This is a personal tool, published as-is — **issues and pull requests aren't accepted** (PRs auto-close). Fork it and make it your own. 🚦

## License

MIT — see [LICENSE](LICENSE). © 2026 RelentlessOldMan.
