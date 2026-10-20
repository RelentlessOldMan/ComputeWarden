using System.ComponentModel;
using System.Text;
using ComputeWarden.Client;
using ComputeWarden.Core;
using ComputeWarden.Core.Ipc;
using ComputeWarden.Core.Model;

namespace ComputeWarden.Cli;

/// <summary>
/// <c>cw run</c>: claims, waits its turn, runs the command, and releases when it exits. While it
/// runs, a renew long-polls the daemon, so the moment a game starts (or the hold goes on) the
/// whole process tree drops to the throttle's cores and priority, and goes back to full speed when
/// the game ends. It only ever touches the processes it started.
/// </summary>
internal static class RunCommand
{
    public const string UsageLine = "cw run --estimate <duration> [--owner <name>] [--description <text>] [--big-memory] [--solo [--user-approved]] -- <command> [args...]";

    /// <summary>The command couldn't be started (not found, access denied): the shell's convention.</summary>
    public const int CouldNotStart = 127;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan QueueNotice = TimeSpan.FromMinutes(10);

    private sealed record Options(ClaimRequest Claim, string[] Command);

    public static async Task<int> RunAsync(string[] args, WardenClient client, TextWriter stderr)
    {
        if (!TryParse(args, stderr, out var options)) return 2;
        if (!TryBuildCommandLine(options.Command, out var commandLine))
        {
            stderr.WriteLine($"[cw] '{options.Command[0]}' not found.");
            return CouldNotStart;
        }

        Console.CancelKeyPress += KeepRunning;
        try
        {
            var claim = await AcquireAsync(client, options.Claim, stderr);
            return await RunClaimedAsync(client, options.Claim, claim, commandLine, stderr);
        }
        finally
        {
            Console.CancelKeyPress -= KeepRunning;
        }
    }

    // Ctrl+C reaches the command too (it shares the console): let it end, then release as usual.
    private static void KeepRunning(object? sender, ConsoleCancelEventArgs e) => e.Cancel = true;

    private sealed class Claim
    {
        public string? Id;
        public Advice? Advice;
        public int Failures;
    }

    /// <summary>Acquires and waits in line. An unreachable daemon means work runs normally, unclaimed.</summary>
    private static async Task<Claim> AcquireAsync(WardenClient client, ClaimRequest request, TextWriter stderr)
    {
        AcquireResult acquired;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                acquired = await client.AcquireAsync(request);
                break;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                // The policy's rule: an error, and a retry erroring too, means work normally.
                if (attempt == 2)
                {
                    stderr.WriteLine($"[cw] ComputeWarden daemon not reachable ({ex.Message}): running without a claim.");
                    return new Claim();
                }
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
        if (!acquired.Acquired)
            throw new WardenClientException($"Claim refused: {acquired.Reason}");

        var claim = new Claim { Id = acquired.ReservationId, Advice = acquired.Advice };
        if (claim.Advice?.Grant != Grant.Queued) return claim;

        var position = claim.Advice.Queue?.Position is int p ? $" #{p}" : "";
        stderr.WriteLine($"[cw] Queued{position}: {claim.Advice.Reason ?? "waiting for a slot"}. Waiting for our turn.");
        var since = DateTimeOffset.UtcNow;
        var nextNotice = since + QueueNotice;
        while (claim.Advice?.Grant == Grant.Queued)
        {
            // The owner's renew: when our turn comes, this answer starts the claim.
            await RenewAsync(client, request, claim, stderr, CancellationToken.None);
            if (claim.Failures >= 2)
            {
                // The policy's rule: an error, and a retry erroring too, means work normally.
                stderr.WriteLine("[cw] Still can't reach the daemon: running without a claim.");
                claim.Id = null;
                return claim;
            }
            if (claim.Advice?.Grant == Grant.Queued && DateTimeOffset.UtcNow >= nextNotice)
            {
                stderr.WriteLine($"[cw] Still queued after {Cli.Span((int)(DateTimeOffset.UtcNow - since).TotalSeconds)}: {claim.Advice.Reason}");
                nextNotice += QueueNotice;
            }
        }
        return claim;
    }

    private static async Task<int> RunClaimedAsync(WardenClient client, ClaimRequest request, Claim claim, string commandLine, TextWriter stderr)
    {
        ThrottledJob job;
        try
        {
            job = ThrottledJob.Start(commandLine);
        }
        catch (Win32Exception ex)
        {
            stderr.WriteLine($"[cw] Could not start {commandLine}: {ex.Message}");
            await ReleaseAsync(client, claim);
            return CouldNotStart;
        }

        using (job)
        {
            var throttled = false;
            void Apply()
            {
                var advice = claim.Advice;
                // Queued while running (a lost claim acquired again) is the same as throttled: we're already going.
                if (advice?.Grant is Grant.Throttled or Grant.Queued)
                {
                    var cores = advice.Throttle?.MaxCores ?? 1;
                    var priority = advice.Throttle?.Priority ?? "below_normal";
                    if (throttled) return;
                    job.Throttle(cores, priority);
                    throttled = true;
                    stderr.WriteLine($"[cw] Throttled to {cores} core{(cores == 1 ? "" : "s")} at {priority.Replace('_', '-')} priority: {advice.Reason ?? "the machine is busy"}.");
                }
                else if (throttled)
                {
                    job.Unthrottle();
                    throttled = false;
                    stderr.WriteLine("[cw] Full speed again.");
                }
            }

            var grant = claim.Advice is { } a ? Warden.GrantName(a.Grant) : "unclaimed";
            stderr.WriteLine($"[cw] Started pid {job.ProcessId} ({grant}).");
            Apply();

            var exited = job.WaitForExitAsync();
            using var stop = new CancellationTokenSource();
            var watching = claim.Id is null ? Task.CompletedTask : WatchAsync(client, request, claim, Apply, stderr, stop.Token);
            var exitCode = await exited;
            stop.Cancel();
            try { await watching; } catch (OperationCanceledException) { }

            await ReleaseAsync(client, claim);
            stderr.WriteLine($"[cw] Exited with code {exitCode}.");
            return exitCode;
        }
    }

    /// <summary>Renews in long polls for as long as the command runs, applying each change of advice.</summary>
    private static async Task WatchAsync(WardenClient client, ClaimRequest request, Claim claim, Action apply,
        TextWriter stderr, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RenewAsync(client, request, claim, stderr, ct);
            apply();
        }
    }

    /// <summary>
    /// One long-poll renew. A lost claim (it lapsed, or the daemon restarted) is acquired again;
    /// an unreachable daemon is retried after a pause, leaving the job as it was.
    /// </summary>
    private static async Task RenewAsync(WardenClient client, ClaimRequest request, Claim claim, TextWriter stderr, CancellationToken ct)
    {
        try
        {
            if (claim.Id is null)
            {
                var again = await client.AcquireAsync(request, ct);
                if (again.Acquired) (claim.Id, claim.Advice) = (again.ReservationId, again.Advice);
                claim.Failures = 0;
                return;
            }
            var renewed = await client.RenewAsync(claim.Id, waitSeconds: RequestDispatcher.MaxWaitSeconds, ct: ct);
            claim.Failures = 0;
            if (renewed.Renewed)
            {
                claim.Advice = renewed.Advice ?? Advice.Full;
                return;
            }
            stderr.WriteLine($"[cw] Claim lost ({renewed.Reason ?? "no reason given"}): claiming again.");
            var acquired = await client.AcquireAsync(request, ct);
            (claim.Id, claim.Advice) = acquired.Acquired ? (acquired.ReservationId, acquired.Advice) : (null, claim.Advice);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or WardenClientException && !ct.IsCancellationRequested)
        {
            claim.Failures++;
            stderr.WriteLine($"[cw] ComputeWarden daemon not reachable ({ex.Message}): retrying.");
            await Task.Delay(RetryDelay, ct);
        }
    }

    private static async Task ReleaseAsync(WardenClient client, Claim claim)
    {
        if (claim.Id is null) return;
        try { await client.ReleaseAsync(claim.Id); }
        catch (Exception ex) when (ex is TimeoutException or IOException) { } // the lease runs out on its own
    }

    // ---- Arguments ------------------------------------------------------

    private static bool TryParse(string[] args, TextWriter stderr, out Options options)
    {
        options = null!;
        string? owner = null, description = null;
        int? estimate = null;
        bool bigMemory = false, solo = false, userApproved = false;
        var i = 0;
        for (; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--") { i++; break; }
            if (!arg.StartsWith("--", StringComparison.Ordinal)) break;
            switch (arg.ToLowerInvariant())
            {
                case "--estimate" when i + 1 < args.Length:
                    if (!Cli.TryParseDuration(args[++i], out var seconds))
                    {
                        stderr.WriteLine("--estimate needs a duration, e.g. 90s, 10m or 1h30m.");
                        return false;
                    }
                    estimate = seconds;
                    break;
                case "--owner" when i + 1 < args.Length: owner = args[++i]; break;
                case "--description" when i + 1 < args.Length: description = args[++i]; break;
                case "--big-memory": bigMemory = true; break;
                case "--solo": solo = true; break;
                case "--user-approved": userApproved = true; break;
                default:
                    stderr.WriteLine($"Unexpected '{arg}'. Usage: {UsageLine}");
                    return false;
            }
        }
        var command = args[i..];
        if (command.Length == 0 || estimate is null)
        {
            stderr.WriteLine(command.Length == 0 ? $"No command to run. Usage: {UsageLine}"
                                                 : "--estimate is required (err high: it sets the lease), e.g. --estimate 10m.");
            return false;
        }
        owner ??= $"cw run ({Path.GetFileName(Environment.CurrentDirectory.TrimEnd('\\', '/'))})";
        description ??= Truncate(string.Join(' ', command), 120);
        options = new Options(new ClaimRequest(owner, description, solo ? ClaimKind.Solo : ClaimKind.Shared, bigMemory,
            EstimateSeconds: estimate, UserApproved: userApproved), command);
        return true;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ---- Command line ---------------------------------------------------

    /// <summary>
    /// Finds the program on the PATH (with PATHEXT, as a shell would) and builds a Windows command
    /// line. Batch files (npm.cmd and the like) can only run under cmd, so they're run through it.
    /// </summary>
    internal static bool TryBuildCommandLine(string[] command, out string commandLine)
    {
        commandLine = string.Empty;
        var program = Resolve(command[0]);
        if (program is null) return false;
        var line = string.Join(' ', new[] { program }.Concat(command.Skip(1)).Select(Quote));
        var ext = Path.GetExtension(program);
        commandLine = ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            ? $"{Quote(Path.Combine(Environment.SystemDirectory, "cmd.exe"))} /d /s /c \"{line}\""
            : line;
        return true;
    }

    internal static string? Resolve(string name)
    {
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> Candidates(string path)
            => Path.HasExtension(path) ? new[] { path }.Concat(extensions.Select(e => path + e)) : extensions.Select(e => path + e);

        if (name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
            return Candidates(name).Where(File.Exists).Select(Path.GetFullPath).FirstOrDefault();

        var dirs = new[] { Environment.CurrentDirectory }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in dirs)
        {
            string full;
            try { full = Path.Combine(dir.Trim('"'), name); }
            catch (ArgumentException) { continue; }
            if (Candidates(full).FirstOrDefault(File.Exists) is { } found) return found;
        }
        return null;
    }

    /// <summary>Quotes one argument so CommandLineToArgvW (and the C runtime) reads it back unchanged.</summary>
    internal static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
