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
- **Everyday work:** before a build, test run, or app run, call `computewarden_acquire` with `kind: "shared"`, an owner like "Claude Code (<repo>)", a description, and `estimateSeconds`. Pass `resources` only if the work isn't cpu+ram+disk. Release when it finishes, even if it failed.
- **Heavy work:** for jobs that saturate a resource (full indexing, clean parallel builds, big test fan-outs, ML, encodes), use `kind: "exclusive"` with `estimateSeconds`. Pass every resource it saturates in ONE call, and `whenBusy: "throttle"` (run small now) or `"wait"`. Release it even if it fails.
- **Follow `advice.grant`:**
  - `full`: run normally.
  - `throttled`: run small per `advice.how` (e.g. `-m:1`, 1 core, below-normal priority).
  - `queued`: don't start. Call `computewarden_renew` with `waitSeconds: 60` until the grant changes. If `est_wait_seconds` is missing, or you've waited more than 10 minutes, tell the user and ask whether to keep waiting.
- **Long work:** `estimateSeconds` sets the lease, so err high. Run long jobs in the background and renew within the lease. When the grant changes, size the next step to match; a switch to `full` means go full speed. If renew returns `renewed: false`, the claim is gone: acquire again.
- **User only:** set `userPriority` (skip the line), call `computewarden_set_mode`, or set a manual hold only when the user explicitly says so.
