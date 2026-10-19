# ComputeWarden 🚦

A small Windows background process that AI coding agents and build tools ask before starting
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
shared claim it means cpu+ram instead).

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
| **Shared** | An agent's everyday build, test or run | Never refused. Full when nothing heavy overlaps; throttled when a game or an exclusive holder does. Defaults to `cpu, ram`. |
| **Solo** | An agent's timing test or benchmark | A quiet machine. Waits in line until nothing else runs, then has the whole machine; nothing new starts while it waits or holds. Never runs small. |

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
  Agents with background shells run `cw wait <reservation_id>` instead: it keeps the ticket
  alive, exits when the claim may start, and leaves the agent free to do other work meanwhile.
  `cw wait` only checks the ticket in: the claim starts when its owner renews. A woken claim
  has `queue_checkin_seconds` (default 2 minutes) to do that, or its slot passes to the next in line.
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
| `steady` | Everyone runs throttled; nobody goes full. The 3-per-resource cap applies, GPU work queues if another agent is on the GPU, and `whenBusy: wait` claims wait for the mode to end. |
| `open` | No throttling between agents. Exclusive holds still keep two heavy jobs apart. A game's throttling and cap still apply. |
| `hold` | New work is queued; work already running is throttled. |

A mode can take a duration (90m, 2h, 1h30m, 1d; a unit is required, at most 7d), a reason
(shown to agents in their advice), and `--resources cpu,ram` (or `-r`) to cover only some
resources. `normal`, `slow`, `full`, and `stop` also work for auto, steady, open, and hold. **No mode overrides a running
game.** Inside a Claude Code session you can run it as `! cw mode open`.

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
   `%ProgramData%\ComputeWarden\config.yaml` and list the programs that should count as busy.

You don't need to start anything yourself. The first time an agent calls a tool (or you run
`cw`), the daemon starts in the background, and it then runs until reboot.

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
- **Everyday work:** before a build, test run, or app run, call `computewarden_acquire` with `kind: "shared"`, an owner like "Claude Code (<repo>)", a description, and `estimateSeconds`. The default is cpu+ram; add `disk` only for heavy disk I/O (clean rebuilds, full scans, large copies), and `gpu` only if it uses the GPU. Release when it finishes, even if it failed. Claim only for the run itself: acquire right before the command, release as soon as it exits. Don't hold a claim while reading, editing, waiting on the user, or between runs; a held claim fills a slot others queue behind.
- **Heavy work:** for jobs that saturate a resource (full indexing, clean parallel builds, big test fan-outs, ML, encodes), use `kind: "exclusive"` with `estimateSeconds`. Pass every resource it saturates in ONE call, and `whenBusy: "throttle"` (run small now) or `"wait"`. Release it even if it fails.
- **Timing tests and benchmarks:** use `kind: "solo"`. It waits until nothing else runs, and nothing new starts while it waits or holds, so acquire right before the measured run and release as soon as it ends. A `queued` grant works as below.
- **Follow `advice.grant`:**
  - `full`: run normally.
  - `throttled`: run small per `advice.how` (e.g. `-m:1`, 1 core, below-normal priority).
  - `queued`: don't start. Run `cw wait <reservation_id>` in the background and do other work meanwhile. When it exits 0, renew and start right away: the slot passes on if you don't renew within the time it prints. Exit 3 (still queued) or 4 (daemon unreachable) means run it again; exit 1 means the claim is gone, so acquire again. If you can't run background commands, call `computewarden_renew` with `waitSeconds: 60` until the grant changes. If `est_wait_seconds` is missing, or you've waited more than 10 minutes, tell the user and ask whether to keep waiting.
- **Long work:** `estimateSeconds` sets the lease, so err high for the run itself (not the session). Run long jobs in the background and renew within the lease. When the grant changes, size the next step to match; a switch to `full` means go full speed. If renew returns `renewed: false`, the claim is gone: acquire again.
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
30s–1h (default 5 min), and no process rules. If the file can't be read or parsed at startup,
the daemon logs the error and starts with defaults.

The part you'll edit most is the process list:

```yaml
process_rules:
  - name: Overwatch
    executable: Overwatch.exe          # blocks everything while running
  - name: Video editor
    executable: Resolve.exe
    resources: [cpu, gpu, ram]         # disk and network work can still run
```

A rule can also set `enabled: false` to switch it off, and a `description` as a note to
yourself.

**Edits apply live.** The daemon watches the file, so changes to `process_rules`, the
debounce settings, and `sharing` take effect within a few seconds. A broken edit is logged
and ignored, and the previous settings stay active. Changing the poll interval, lease bounds,
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

## Live panel (optional)

If [RelentlessEye](https://github.com/RelentlessOldMan/RelentlessEye) is installed, the daemon
shows up in it as a Compute Warden panel with:

- the machine state;
- the mode;
- busy resources;
- running games and other blockers;
- a table of claims: who, where each one is (running full or throttled, its turn but not
  started, or queued with its place in line), a colored dot under each resource it needs
  (cpu, gpu, ram, net, disk), the lease left, and what it's doing. A claim with no check-in
  for 15 minutes or more says "no check-in" and for how long: the work may be finished or stuck;
- a feed of the daemon's log (claims, releases, promotions, expiries).

`reye request ComputeWarden.status` returns the same JSON as `computewarden_status`.

You can also set the mode from the panel: auto, steady, open or hold, for the whole machine
until you change it. Open and hold ask you to confirm. The panel always shows the mode the daemon
actually has. (`cw mode` can also limit a mode to some resources or a set time.)

**Jobs while gaming** (0 to 4) sets how many jobs may run beside a game. Each runs on one core,
so it's also how many cores of background work the game shares the machine with. A game suffers
mostly from the memory traffic of background work, which priority doesn't stop; fewer jobs is
what helps. Lowering it makes the next jobs wait; work already running carries on. 0 means
nothing new starts until the game closes. It starts at `max_throttled_per_resource` from the
config and, once picked, stays across restarts.

While jobs are waiting to start, you can reorder the line: click a queued job's row, then **Move
picked to front**, **Move up** or **Move down**. The new order is the real one: the job at the
front gets the resources next. Moving a job ahead of one with user priority gives it priority
too; moving a priority job behind plain ones takes its priority away.

Only a monitor (the panel or `reye`) can set the mode, jobs while gaming, or reorder the line. Requests from apps
connected to the hub, such as agent sessions, are refused and logged, since all of these are the user's
call. Setting the mode needs RelentlessEye 1.1 or later; the claims table needs 1.2.

There is nothing to configure, and ComputeWarden takes no dependency on RelentlessEye. The daemon
looks for `RelentlessEye.Client.dll` when it starts. It checks `%RELENTLESSEYE_CLIENT%` first, then
the folder next to the daemon, then `%LOCALAPPDATA%\RelentlessEye\client` (RelentlessEye's
`publish.ps1 -InstallClient` puts it there). If the DLL isn't found, the panel is off and the
daemon runs exactly the same. The log's startup lines say which happened. After installing the
client, restart the daemon to pick it up.

## Troubleshooting

**Where are the logs?** `%ProgramData%\ComputeWarden\logs\daemon.log` (capped at 5 MB, with one
`.1` backup). It records every acquire, release, and lease expiry, plus process blockers
appearing and clearing, and config reloads.

**Restart the daemon:** `Stop-Process -Name ComputeWarden.Daemon`. The next tool call starts
a fresh one. Claims (with their place in line and leases), the mode and the manual hold carry
over: the daemon saves them to `%LOCALAPPDATA%\ComputeWarden\state.json` within a second of any
change and loads them when it starts. Claims whose lease ran out while it was down are dropped.
The file holds claim ids, so it stays in your own profile. To start clean, stop the daemon and
delete the file. If the file can't be read, it's moved to `state.json.bad` and the daemon starts
empty.

**What is everyone doing?** Run `cw`. It shows the mode, running games, and every agent
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
`*.old-<timestamp>` files, cleaned up on the next install). Claims and the mode survive the
restart. (Upgrading from a daemon older than 1.1.5, which didn't save them, it refuses while a
mode other than auto is set; pass `-Force` to install anyway.) Open sessions keep using the old
adapter until they restart.

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
