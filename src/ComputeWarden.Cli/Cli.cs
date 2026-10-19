using System.Globalization;
using System.Text.RegularExpressions;
using ComputeWarden.Client;
using ComputeWarden.Core;
using ComputeWarden.Core.Ipc;
using ComputeWarden.Core.Model;

namespace ComputeWarden.Cli;

/// <summary>
/// <c>cw</c>: the user's own controls. Shows what's running and turns the manual hold on and off
/// (which agents only do on the user's word). Also <c>cw wait</c>, which agents run in the
/// background to hear when a queued claim may start.
/// </summary>
internal static class Cli
{
    public const string Usage = """
        Usage:
          cw                         Show status (same as 'cw status').
          cw status                  Games, the hold, and every agent claim.
          cw hold [<reason>...]      Turn the manual hold on: nothing new starts and running work
                                     is told to run small, as if a game ran. Example:
                                       cw hold recording a video
          cw hold off                Turn the manual hold off.
          cw wait <reservationId> [--max <duration>]
                                     Wait until a queued claim may start, keeping it alive in line,
                                     then print its advice. For agents: run it in the background.
                                     Gives up after --max (default 1h). Exit codes: 0 your turn,
                                     3 still queued (run it again), 1 claim gone (acquire again),
                                     4 daemon unreachable (run it again).
        How many jobs may run while you game is set on the panel (jobs while gaming).
        """;

    private static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(8);

    /// <param name="launch">Starts the daemon when none is running (null: never launch).</param>
    public static async Task<int> RunAsync(string[] args, WardenClient client, TextWriter stdout, TextWriter stderr,
        Func<bool>? launch = null)
    {
        try
        {
            var command = args.Length == 0 ? "status" : args[0].ToLowerInvariant();
            if (launch is not null && command is "status" or "hold")
                await EnsureDaemonAsync(client, launch);
            switch (command)
            {
                case "status":
                    Print(await client.StatusAsync(), stdout, DateTimeOffset.UtcNow);
                    return 0;

                case "hold" when args.Length == 2 && args[1].ToLowerInvariant() is "off" or "clear":
                    stdout.WriteLine(await client.ClearManualBusyAsync() ? "Hold off." : "No hold was on.");
                    return 0;

                case "hold":
                    var reason = args.Length > 1 ? string.Join(' ', args.Skip(1)) : "held by the user";
                    await client.SetManualBusyAsync(reason);
                    stdout.WriteLine($"Hold on: {reason}. Nothing new starts until 'cw hold off'.");
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
    /// Starts the daemon if it isn't running, so <c>cw hold</c> works before any agent
    /// session has started one. A failed start falls through to the normal "could not reach".
    /// </summary>
    private static async Task EnsureDaemonAsync(WardenClient client, Func<bool> launch)
    {
        if (await client.ProbeAsync() != DaemonProbe.NotRunning || !launch()) return;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (waited.Elapsed < StartupWait && await client.ProbeAsync() == DaemonProbe.NotRunning)
            await Task.Delay(150);
    }

    private static readonly Regex DurationPattern = new(
        @"^(?:(?<d>\d+)d)?(?:(?<h>\d+)h)?(?:(?<m>\d+)m)?(?:(?<s>\d+)s)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
        w.WriteLine($"Machine: {s.State.ToString().ToUpperInvariant()}");
        if (s.DaemonVersion is { } version) w.WriteLine($"Daemon:  v{version}");

        var games = s.Blockers.Where(b => b.Type == BlockerType.Process).ToList();
        var hold = s.Blockers.FirstOrDefault(b => b.Type == BlockerType.Manual);
        if (games.Count > 0)
        {
            w.WriteLine();
            var jobs = s.GameJobs is int j ? $" ({j} job(s) may run, throttled)" : "";
            w.WriteLine($"Gaming{jobs}:");
            foreach (var b in games) w.WriteLine($"  {b.Description}");
        }
        if (hold is not null)
        {
            w.WriteLine();
            w.WriteLine($"Hold: {hold.Description} (nothing new starts; 'cw hold off' to end it)");
        }

        var claims = s.Claims ?? Array.Empty<ClaimInfo>();
        w.WriteLine();
        if (claims.Count == 0) { w.WriteLine("No agent work reported."); return; }

        w.WriteLine("Agent work:");
        foreach (var c in claims)
        {
            var state = c.Phase switch
            {
                ClaimPhase.Waiting => c.Position is int p ? $"queued #{p}" : "queued",
                ClaimPhase.Ready => "its turn, not started",
                _ => $"running {Warden.GrantName(c.Grant)}",
            };
            var kind = c.Kind == ClaimKind.Solo ? "solo" : c.BigMemory ? "big memory" : "shared";
            var tail = c.EstimateSeconds is int e ? $" (est {Span(e)})" : "";
            w.WriteLine($"  {state,-22} {kind,-10} {c.Owner}: {c.Description} {Left(c.ExpiresAt, now)}{tail}");
        }
    }

    private static string Left(DateTimeOffset? expiresAt, DateTimeOffset now)
        => expiresAt is { } e ? $"[lease {Span(Math.Max(0, (int)Math.Ceiling((e - now).TotalSeconds)))}]" : "";

    public static string Span(int seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600) return $"{seconds / 60}m";
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        return m == 0 ? $"{h}h" : $"{h}h{m}m";
    }
}
