using System.Globalization;
using System.Text.RegularExpressions;
using ComputeWarden.Client;
using ComputeWarden.Core;
using ComputeWarden.Core.Ipc;
using ComputeWarden.Core.Model;

namespace ComputeWarden.Cli;

/// <summary>
/// <c>cw</c>: the user's own controls. Shows what's running and sets the user mode
/// ("y'all go slow and steady", "full bore allowed"), which agents only do on the user's word.
/// Also <c>cw wait</c>, which agents run in the background to hear when a queued claim may start.
/// </summary>
internal static class Cli
{
    public const string Usage = """
        Usage:
          cw                         Show status (same as 'cw status').
          cw status                  Mode, what holds each resource, and every agent claim.
          cw mode                    Show the current mode.
          cw mode <mode> [for] [<duration>] [<reason>...] [--resources cpu,ram,...]
                                     Set the mode. Duration like 90m, 2h, 1h30m, 1d (at most 7d);
                                     omit to keep it until changed. Examples:
                                       cw mode steady 2h gaming
                                       cw mode open
                                       cw mode hold 30m "recording a video"
                                       cw mode auto
          cw wait <reservationId> [--max <duration>]
                                     Wait until a queued claim may start, keeping it alive in line,
                                     then print its advice. For agents: run it in the background.
                                     Gives up after --max (default 1h). Exit codes: 0 your turn,
                                     3 still queued (run it again), 1 claim gone (acquire again),
                                     4 daemon unreachable (run it again).
        Modes:
          auto     Normal rules: games limit everyone; agents share by the line.
          steady   Everyone runs throttled (slow and steady). Nobody goes full.
          open     No throttling between agents; exclusive holds still keep heavy jobs apart.
          hold     Nobody starts new work; work already running is throttled.
        No mode ever overrides a running game (configured priority process).
        """;

    private static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(8);

    /// <param name="launch">Starts the daemon when none is running (null: never launch).</param>
    public static async Task<int> RunAsync(string[] args, WardenClient client, TextWriter stdout, TextWriter stderr,
        Func<bool>? launch = null)
    {
        try
        {
            var command = args.Length == 0 ? "status" : args[0].ToLowerInvariant();
            if (launch is not null && command is "status" or "mode")
                await EnsureDaemonAsync(client, launch);
            switch (command)
            {
                case "status":
                    Print(await client.StatusAsync(), stdout, DateTimeOffset.UtcNow);
                    return 0;

                case "mode" when args.Length == 1:
                    var status = await client.StatusAsync();
                    stdout.WriteLine(status.Mode is { } current
                        ? $"Mode: {Describe(current, DateTimeOffset.UtcNow)}"
                        : "Mode: (daemon is too old to report a mode)");
                    return 0;

                case "mode":
                    if (!TryParseMode(args.Skip(1).ToArray(), out var req, out var error))
                    {
                        stderr.WriteLine(error);
                        stderr.WriteLine("Run 'cw help' for usage.");
                        return 2;
                    }
                    var set = await client.SetModeAsync(req.Mode, req.Resources, req.DurationSeconds, req.Reason);
                    stdout.WriteLine($"Mode set: {Describe(set, DateTimeOffset.UtcNow)}");
                    return 0;

                case "wait":
                    return await WaitAsync(args.Skip(1).ToArray(), client, stdout, stderr);

                case "help" or "-h" or "--help" or "/?":
                    stdout.WriteLine(Usage);
                    return 0;

                default:
                    stderr.WriteLine($"Unknown command '{args[0]}'.");
                    stderr.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            stderr.WriteLine("Could not reach the ComputeWarden daemon, and starting it didn't work. Is ComputeWarden.Daemon.exe next to cw.exe?");
            return 1;
        }
        catch (UnauthorizedAccessException)
        {
            stderr.WriteLine("Access denied to the ComputeWarden pipe: the daemon belongs to another user or an elevated session.");
            return 1;
        }
        catch (WardenClientException ex)
        {
            stderr.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"cw failed: {ex.Message}");
            return 1;
        }
    }

    public const int StillQueued = 3;
    public const int Unreachable = 4;
    private const int WaitRetries = 3;

    /// <summary>
    /// Long-polls a queued claim until it may start. Each poll renews the ticket, so the claim
    /// keeps its place for as long as this runs; <c>--max</c> bounds that, so a waiter whose
    /// session died can't keep a dead claim in line forever.
    /// </summary>
    private static async Task<int> WaitAsync(string[] args, WardenClient client, TextWriter stdout, TextWriter stderr)
    {
        string? id = null;
        var maxSeconds = 3600;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--max")
            {
                if (i + 1 >= args.Length || !TryParseDuration(args[++i], out maxSeconds))
                {
                    stderr.WriteLine("--max needs a duration, e.g. 30m or 2h (at most 7d).");
                    return 2;
                }
            }
            else if (id is null) id = args[i];
            else { stderr.WriteLine($"Unexpected '{args[i]}'. Usage: cw wait <reservationId> [--max <duration>]"); return 2; }
        }
        if (string.IsNullOrWhiteSpace(id))
        {
            stderr.WriteLine("Usage: cw wait <reservationId> [--max <duration>]");
            return 2;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(maxSeconds);
        var failures = 0;
        while (true)
        {
            var left = deadline - DateTimeOffset.UtcNow;
            var wait = (int)Math.Clamp(Math.Ceiling(left.TotalSeconds), 0, RequestDispatcher.MaxWaitSeconds);
            RenewResult renewed;
            try
            {
                // A probe: it keeps the ticket checked in but leaves starting the claim to its owner.
                renewed = await client.RenewAsync(id, waitSeconds: wait > 0 ? wait : null, probe: true);
                failures = 0;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                // A busy pipe or a slow reply isn't a lost claim: retry, since the ticket outlives a few seconds.
                if (++failures > WaitRetries)
                {
                    stderr.WriteLine($"Could not reach the ComputeWarden daemon ({ex.Message}). The claim may still be in line: run cw wait again.");
                    return Unreachable;
                }
                await Task.Delay(TimeSpan.FromSeconds(1 << (failures - 1)));
                continue;
            }
            if (!renewed.Renewed)
            {
                stderr.WriteLine($"Claim gone ({renewed.Reason ?? "no reason given"}): acquire again.");
                return 1;
            }

            var advice = renewed.Advice;
            if (advice is null || advice.Grant != Grant.Queued)
            {
                var grant = advice is null ? "full" : Warden.GrantName(advice.Grant);
                stdout.WriteLine($"Your turn: {grant}.");
                if (advice?.Reason is { } why) stdout.WriteLine($"Because: {why}");
                if (advice?.How is { } how) stdout.WriteLine($"How: {how}");
                if (renewed.ExpiresAt is { } expires)
                    stdout.WriteLine($"Start now and renew within {Span(Math.Max(0, (int)(expires - DateTimeOffset.UtcNow).TotalSeconds))}, " +
                                     "or the slot passes to the next in line.");
                return 0;
            }

            if (wait == 0)
            {
                stdout.WriteLine($"Still queued after {Span(maxSeconds)}{(advice.Reason is { } r ? $" ({r})" : "")}. Run cw wait again to keep waiting.");
                return StillQueued;
            }
        }
    }

    /// <summary>
    /// Starts the daemon if it isn't running, so <c>cw mode steady</c> works before any agent
    /// session has started one. A failed start falls through to the normal "could not reach".
    /// </summary>
    private static async Task EnsureDaemonAsync(WardenClient client, Func<bool> launch)
    {
        if (await client.ProbeAsync() != DaemonProbe.NotRunning || !launch()) return;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (waited.Elapsed < StartupWait && await client.ProbeAsync() == DaemonProbe.NotRunning)
            await Task.Delay(150);
    }

    public sealed record ModeRequest(UserMode Mode, int? DurationSeconds, string? Reason, string[]? Resources);

    public static bool TryParseMode(string[] args, out ModeRequest request, out string? error)
    {
        request = new ModeRequest(UserMode.Auto, null, null, null);
        error = null;

        UserMode? mode = null;
        int? duration = null;
        string[]? resources = null;
        var reason = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "--resources" or "-r")
            {
                if (i + 1 >= args.Length) { error = "--resources needs a list, e.g. cpu,ram"; return false; }
                resources = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                try { Resources.Parse(resources); }
                catch (ArgumentException ex) { error = ex.Message; return false; }
                continue;
            }

            if (mode is null)
            {
                mode = a.ToLowerInvariant() switch
                {
                    "auto" or "normal" => UserMode.Auto,
                    "steady" or "slow" => UserMode.Steady,
                    "open" or "full" => UserMode.Open,
                    "hold" or "stop" => UserMode.Hold,
                    _ => null,
                };
                if (mode is null) { error = $"Unknown mode '{a}' (expected auto, steady, open, or hold)."; return false; }
                continue;
            }

            // The duration can go anywhere ("steady 2h gaming", "steady gaming for 2h"). Anything
            // that looks like a duration must be one: a typo must not make the mode permanent.
            var afterFor = a.Equals("for", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && LooksLikeDuration(args[i + 1]);
            var token = afterFor ? args[i + 1] : a;
            if (afterFor || LooksLikeDuration(token))
            {
                if (duration is not null) { error = $"Two durations ('{token}'): give one."; return false; }
                if (!TryParseDuration(token, out var seconds))
                {
                    error = $"Bad duration '{token}' (use e.g. 90m, 2h, 1h30m, 1d; at most 7d).";
                    return false;
                }
                duration = seconds;
                if (afterFor) i++;
                continue;
            }
            reason.Add(a);
        }

        if (mode is null) { error = "Missing mode (auto, steady, open, or hold)."; return false; }
        if (mode == UserMode.Auto && (duration is not null || resources is not null))
        {
            error = "auto takes no duration or resources: it just ends the current mode.";
            return false;
        }

        request = new ModeRequest(mode.Value, duration, reason.Count == 0 ? null : string.Join(' ', reason), resources);
        return true;
    }

    private static readonly Regex DurationPattern = new(
        @"^(?:(?<d>\d+)d)?(?:(?<h>\d+)h)?(?:(?<m>\d+)m)?(?:(?<s>\d+)s)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Starts with a digit: meant as a duration ("2h", "1.5h", "10d"), so it must parse as one.</summary>
    private static bool LooksLikeDuration(string text) => text.Length > 0 && char.IsAsciiDigit(text[0]);

    /// <summary>Parses 90m, 2h, 1h30m, 1d, 45s (a unit is required), at most 7 days.</summary>
    public static bool TryParseDuration(string text, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrEmpty(text)) return false;
        var m = DurationPattern.Match(text);
        if (!m.Success) return false;
        const long max = 7 * 24 * 3600;
        long total = 0;
        foreach (var (group, unit) in new[] { ("d", 86400L), ("h", 3600L), ("m", 60L), ("s", 1L) })
        {
            if (!m.Groups[group].Success) continue;
            if (!long.TryParse(m.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > max)
                return false;
            total += n * unit;
        }
        if (total <= 0 || total > max) return false;
        seconds = (int)total;
        return true;
    }

    // ---- Output ---------------------------------------------------------

    public static void Print(StatusResult s, TextWriter w, DateTimeOffset now)
    {
        if (s.Mode is { } mode) w.WriteLine($"Mode:    {Describe(mode, now)}");
        var busy = s.BusyResources == ResourceSet.None ? "" : $" {Names(s.BusyResources)}";
        w.WriteLine($"Machine: {s.State.ToString().ToUpperInvariant()}{busy}");
        if (s.DaemonVersion is { } version) w.WriteLine($"Daemon:  v{version}");

        var games = s.Blockers.Where(b => b.Type == BlockerType.Process).ToList();
        var manual = s.Blockers.Where(b => b.Type == BlockerType.Manual && b.Id != "mode").ToList();
        if (games.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("Priority (limits everyone):");
            foreach (var b in games) w.WriteLine($"  {b.Description} {Names(b.Resources)}");
        }
        if (manual.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("Manual holds:");
            foreach (var b in manual) w.WriteLine($"  {b.Description} {Names(b.Resources)}");
        }

        var claims = s.Claims ?? Array.Empty<ClaimInfo>();
        w.WriteLine();
        if (claims.Count == 0)
        {
            // Legacy daemon: reservations are only visible as blockers.
            var holds = s.Blockers.Where(b => b.Type == BlockerType.Reservation).ToList();
            if (holds.Count == 0) { w.WriteLine("No agent work reported."); return; }
            w.WriteLine("Holds:");
            foreach (var b in holds) w.WriteLine($"  {b.Description} {Names(b.Resources)} {Left(b.ExpiresAt, now)}");
            return;
        }

        w.WriteLine("Agent work:");
        foreach (var c in claims)
        {
            var state = c.Kind != ClaimKind.Shared && c.Holding ? "holding"
                : c.Position is int p && c.Grant == Grant.Queued ? $"queued #{p}"
                : c.Position is int q ? $"{Warden.GrantName(c.Grant)}, #{q} in line"
                : Warden.GrantName(c.Grant);
            var extras = new List<string>();
            if (c.EstimateSeconds is int e) extras.Add($"est {Span(e)}");
            if (c.UserPriority is { } up) extras.Add($"user priority: {up}");
            var tail = extras.Count == 0 ? "" : $" ({string.Join("; ", extras)})";
            w.WriteLine($"  {state,-22} {Warden.KindName(c.Kind),-9} {c.Owner}: {c.Description} {Names(c.Resources)} {Left(c.ExpiresAt, now)}{tail}");
        }
    }

    public static string Describe(ModeState m, DateTimeOffset now)
    {
        if (m.Mode == UserMode.Auto) return "auto (normal rules)";
        var text = Warden.ModeName(m.Mode);
        if (m.Resources != ResourceSet.All) text += $" {Names(m.Resources)}";
        if (m.ExpiresAt is { } e) text += $", {Span(Math.Max(0, (int)Math.Ceiling((e - now).TotalSeconds)))} left";
        if (m.Reason is { } r) text += $": {r}";
        return text;
    }

    private static string Left(DateTimeOffset? expiresAt, DateTimeOffset now)
        => expiresAt is { } e ? $"[lease {Span(Math.Max(0, (int)Math.Ceiling((e - now).TotalSeconds)))}]" : "";

    private static string Names(ResourceSet set) => "[" + string.Join(",", Resources.ToNames(set)) + "]";

    public static string Span(int seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600) return $"{seconds / 60}m";
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        return m == 0 ? $"{h}h" : $"{h}h{m}m";
    }
}
