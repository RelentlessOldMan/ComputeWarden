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
- **Heavy jobs:** for a build, test run or app run longer than a minute, run `cw run --estimate <duration> --owner "<owner>" -- <command>` (add `--big-memory` or `--solo` per the rules below) instead of acquiring by hand. It waits its turn, slows the command down the moment a game starts and back up after, and releases when the command exits; its exit code is the command's.
- **Reviews and mutation testing:** run every build and test they start, however short, as `cw run --low --estimate <duration> --owner "<owner>" -- <command>`, and tell review subagents to do the same. `--low` holds the job to one core at below-normal priority the whole time, so a fan-out of subagents can't take the machine.
- **Big memory:** set `bigMemory: true` only if the job needs more than ~8 GB of RAM and can't cap it (cap it if you can), or needs more than half the GPU's memory. Only one big-memory job runs at a time.
- **Timing runs:** `kind: "solo"` only for a timing test or benchmark, at most 10 minutes (`estimateSeconds` ≤ 600). It waits for a quiet machine; release the moment the measured run ends. A longer solo run (up to 60 minutes) needs the user's explicit approval for that run: only then set `userApproved: true`. Without it, split the run into timed pieces of 10 minutes or less, or run it as shared.
- **Follow `advice.grant`:** `full`: run normally. `throttled` (the user is gaming, or has put on the hold): run small per `advice.how`; when a renew says `full`, go full speed. `queued`: don't start; run `cw wait <reservation_id>` in the background and do other work. When it exits 0, renew and start right away; exit 3 or 4 means run it again, exit 1 means acquire again. Without background commands, renew with `waitSeconds: 60` until the grant changes. If you've waited more than 10 minutes, ask the user. The manual hold (`computewarden_set_manual_busy`) is the user's call only.
- **Project notes:** a project's CLAUDE.md, AGENTS.md or memory may record only facts about its jobs' resource needs: how long a job takes, its peak memory or memory cap, GPU use, heavy disk I/O. No ComputeWarden or priority rules there (claim kinds, owners, which job is solo, bigMemory or `--low`, how to claim, "always run on one core" or "at low priority"); this section is the only copy of those. When the user asks for a job to run small, do it for that job; don't save it as a standing rule. Decide each claim from these rules and those facts. If you find ComputeWarden or priority rules in a project, delete them and keep the facts.
