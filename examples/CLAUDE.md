# ComputeWarden: example global agent instructions

Copy the section below into your agent's **global** instructions so every session on the
machine reports its work, not just sessions in one project:

- Claude Code: `~/.claude/CLAUDE.md`
- Codex: `~/.codex/AGENTS.md`

Register the MCP server globally too (user scope), so the tools exist in every project:

```powershell
claude mcp add --scope user computewarden "$env:LOCALAPPDATA\ComputeWarden\bin\ComputeWarden.Mcp.exe"
```

For Codex, add to `~/.codex/config.toml`:

```toml
[mcp_servers.computewarden]
command = 'C:\Users\<you>\AppData\Local\ComputeWarden\bin\ComputeWarden.Mcp.exe'
# a renew can wait up to 60 s for new advice, longer than Codex's default tool timeout
tool_timeout_sec = 120
```

Project instructions can add specifics (which commands here are heavy, which resources they
saturate), but they shouldn't restate or contradict the policy below.

---

## ComputeWarden (shared machine)
`computewarden_*` MCP tools share this machine with other agent sessions and the user's games. Advisory: nothing enforces it, so follow the advice. If the tools aren't there, or a call returns `error` and one retry does too, work normally.
- **Everyday work:** before a build, test run, or app run, call `computewarden_acquire` with `kind: "shared"`, an owner like "Claude Code (<repo>)", a description, and `estimateSeconds`. The default is cpu+ram; add `disk` only for heavy disk I/O (clean rebuilds, full scans, large copies), and `gpu` only if it uses the GPU. Big or long jobs (full builds, test fan-outs, renders, encodes) are shared too. `kind: "exclusive"` needs the user's express approval for that job; without it, run the job shared, wait, or do other work that isn't exclusive. Release when it finishes, even if it failed. Claim only for the run itself: acquire right before the command, release as soon as it exits. Don't hold a claim while reading, editing, waiting on the user, or between runs; a held claim fills a slot others queue behind.
- **Timing tests and benchmarks:** the only time you need the machine to yourself. Use `kind: "solo"`. It waits until nothing else runs, and nothing new starts while it waits or holds, so acquire right before the measured run and release as soon as it ends. A `queued` grant works as below.
- **Follow `advice.grant`:**
  - `full`: run normally.
  - `throttled`: run small per `advice.how` (e.g. `-m:1`, 1 core, below-normal priority).
  - `queued`: don't start. Run `cw wait <reservation_id>` in the background and do other work meanwhile. When it exits 0, renew and start right away: the slot passes on if you don't renew within the time it prints. Exit 3 (still queued) or 4 (daemon unreachable) means run it again; exit 1 means the claim is gone, so acquire again. If you can't run background commands, call `computewarden_renew` with `waitSeconds: 60` until the grant changes. If `est_wait_seconds` is missing, or you've waited more than 10 minutes, tell the user and ask whether to keep waiting.
- **Long work:** `estimateSeconds` sets the lease, so err high for the run itself (not the session). Run long jobs in the background and renew within the lease. When the grant changes, size the next step to match; a switch to `full` means go full speed. If renew returns `renewed: false`, the claim is gone: acquire again.
- **User only:** set `userPriority` (skip the line), call `computewarden_set_mode`, or set a manual hold only when the user explicitly says so.
