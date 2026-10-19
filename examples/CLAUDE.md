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

Projects don't need ComputeWarden notes of their own. At most, a project's instructions can
name which of its jobs are big-memory or solo, and how to cap a job's memory; they shouldn't
restate or contradict the policy below.

---

## ComputeWarden (shared machine)
`computewarden_*` MCP tools share this machine with other agent sessions and the user's games. Advisory: nothing enforces it, so follow the advice. If the tools aren't there, or a call returns `error` and one retry does too, work normally.
- **Claim every build, test run, and app run:** call `computewarden_acquire` right before the command, with an owner like "Claude Code (<repo>)", a description, and `estimateSeconds` (err high: it sets the lease). Release as soon as it exits, even if it failed; renew within the lease during long runs (if a renew says `renewed: false`, acquire again). Don't hold a claim while reading, editing, or waiting on the user.
- **Big memory:** set `bigMemory: true` only if the job needs more than ~8 GB and can't cap its memory (cap it if you can). Only one big-memory job runs at a time.
- **Timing runs:** `kind: "solo"` only for a timing test or benchmark, at most 10 minutes (`estimateSeconds` ≤ 600). It waits for a quiet machine; release the moment the measured run ends. A longer solo run (up to 60 minutes) needs the user's explicit approval for that run: only then set `userApproved: true`. Without it, split the run into timed pieces of 10 minutes or less, or run it as shared.
- **Follow `advice.grant`:** `full`: run normally. `throttled` (the user is gaming, or has put on the hold): run small per `advice.how`; when a renew says `full`, go full speed. `queued`: don't start; run `cw wait <reservation_id>` in the background and do other work. When it exits 0, renew and start right away; exit 3 or 4 means run it again, exit 1 means acquire again. Without background commands, renew with `waitSeconds: 60` until the grant changes. If you've waited more than 10 minutes, ask the user. The manual hold (`computewarden_set_manual_busy`) is the user's call only.
