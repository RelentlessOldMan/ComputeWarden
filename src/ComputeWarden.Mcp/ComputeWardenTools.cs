using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComputeWarden.Client;
using ComputeWarden.Core;
using ComputeWarden.Core.Model;
using ModelContextProtocol.Server;

namespace ComputeWarden.Mcp;

/// <summary>
/// The MCP tool surface for ComputeWarden. Each tool forwards to the daemon over IPC and
/// returns compact structured JSON so agents can make their own decisions and
/// produce useful explanations. Errors are returned as JSON, never thrown, so a transient
/// daemon hiccup surfaces as data the agent can act on.
/// </summary>
[McpServerToolType]
public static class ComputeWardenTools
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool(Name = "computewarden_status")]
    [Description("Returns the machine coordination state: AVAILABLE, BUSY (a game, the user's manual " +
                 "hold, or a solo timing run), or UNKNOWN; the blockers explaining why; game_jobs (how many " +
                 "jobs may run while the user games); and claims, all agent work running and waiting in line.")]
    public static Task<string> Status()
        => Run(async client =>
        {
            var s = await client.StatusAsync();
            var now = DateTimeOffset.UtcNow;
            return new
            {
                state = StateString(s.State),
                canRunIntensive = s.CanRunIntensive,
                gameJobs = s.GameJobs,
                blockers = Blockers(s.Blockers),
                claims = s.Claims?.Select(c => ClaimDto(c, now)).ToArray(),
                daemonVersion = s.DaemonVersion,
            };
        });

    [McpServerTool(Name = "computewarden_can_run")]
    [Description("Convenience check: would new work run full right now? Informational ONLY — it reserves " +
                 "nothing. Before a build, test, or run, call computewarden_acquire instead.")]
    public static Task<string> CanRun()
        => Run(async client =>
        {
            var canRun = await client.CanRunAsync();
            return new
            {
                canRun,
                note = "Informational only; not a guarantee. Use computewarden_acquire before the work.",
            };
        });

    [McpServerTool(Name = "computewarden_acquire")]
    [Description("Claim before a build, test run, or app run; release when it ends. Returns the warden's " +
                 "advice: grant full (run normally), throttled (the user is gaming: run small, follow " +
                 "advice.throttle / advice.how), or queued (don't start yet; see advice.next). kind=shared " +
                 "(the default) is for all everyday work. Set bigMemory=true ONLY if the job needs more than " +
                 "~8 GB and can't cap its memory: only one big-memory job runs at a time. kind=solo is ONLY for " +
                 "short timing runs and benchmarks (at most 10 minutes, or up to 60 with userApproved, set only " +
                 "when the user explicitly approved that run): it waits until nothing else runs, and " +
                 "nothing new starts while it runs, so release it the moment the measured run ends. While one " +
                 "waits, only work whose estimateSeconds ends before the running work does may start. " +
                 "Save reservation_id; renew it during long work (renew returns new advice, e.g. full once a " +
                 "game exits) and release it when done, even if the work failed.")]
    public static Task<string> Acquire(
        [Description("Who is acquiring, e.g. 'Claude Code (MyRepo)'.")] string owner,
        [Description("What the work is, e.g. 'dotnet test MyRepo.slnx'.")] string description,
        [Description("shared (default: every build, test, and run) or solo (a timing run or benchmark under 10 minutes).")] string? kind = null,
        [Description("True only for a job that needs more than ~8 GB of memory and can't cap itself. Only one runs at a time.")] bool? bigMemory = null,
        [Description("How long the work takes, in seconds. Sets the lease (err high) and others' wait estimates.")] int? estimateSeconds = null,
        [Description("Requested lease seconds; clamped to server bounds. Omit to derive it from estimateSeconds.")] int? leaseSeconds = null,
        [Description("Optional small string metadata.")] Dictionary<string, string>? metadata = null,
        [Description("USER ONLY: true only if the user explicitly approved THIS solo run lasting longer than 10 minutes (up to 60). Never set it on your own.")] bool? userApproved = null)
        => Run(async client =>
        {
            var r = await client.AcquireAsync(new ClaimRequest(
                owner, description, ParseKind(kind), bigMemory == true, leaseSeconds, estimateSeconds, metadata, userApproved == true));

            var now = DateTimeOffset.UtcNow;
            return new
            {
                acquired = r.Acquired,
                reservationId = r.ReservationId,
                kind = r.Kind is { } k ? Warden.KindName(k) : null,
                bigMemory = r.Acquired && r.BigMemory ? true : (bool?)null,
                advice = r.Advice is { } a ? AdviceDto(a, now) : null,
                expiresAt = r.ExpiresAt,
                leaseRemainingSeconds = Remaining(r.ExpiresAt, now),
                machineState = StateString(r.MachineState),
                reason = r.Reason,
                blockers = Blockers(r.Blockers),
            };
        });

    [McpServerTool(Name = "computewarden_renew")]
    [Description("Renews a reservation's lease and returns the warden's current advice for it: this " +
                 "is how running work learns a game started (drop to throttled) or ended (go full), and " +
                 "how a queued claim checks back (renew within the check-in window or it drops out of " +
                 "line). Pass waitSeconds to wait for the advice to change instead of polling. Returns " +
                 "renewed=false if the reservation no longer exists (e.g. it already expired).")]
    public static Task<string> Renew(
        [Description("The reservation_id returned by computewarden_acquire.")] string? reservationId = null,
        [Description("New lease seconds; clamped to server bounds. Omit to keep the claim's lease.")] int? leaseSeconds = null,
        [Description("Wait up to this many seconds (max 60) for the grant to change before answering.")] int? waitSeconds = null,
        [Description(IdAliasHelp)] string? reservation_id = null,
        CancellationToken cancellationToken = default) // the host cancelling closes the pipe, which ends the daemon's wait
        => Run(async client =>
        {
            var r = await client.RenewAsync(Id(reservationId, reservation_id), leaseSeconds, waitSeconds, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            return new
            {
                renewed = r.Renewed,
                advice = r.Advice is { } a ? AdviceDto(a, now) : null,
                expiresAt = r.ExpiresAt,
                leaseRemainingSeconds = Remaining(r.ExpiresAt, now),
                reason = r.Reason,
            };
        });

    [McpServerTool(Name = "computewarden_release")]
    [Description("Releases a reservation immediately, freeing the machine for other agents. " +
                 "Idempotent: releasing an unknown or already-expired reservation is not an error.")]
    public static Task<string> Release(
        [Description("The reservation_id to release.")] string? reservationId = null,
        [Description(IdAliasHelp)] string? reservation_id = null)
        => Run(async client =>
        {
            var r = await client.ReleaseAsync(Id(reservationId, reservation_id));
            return new { released = r.Released, reason = r.Reason };
        });

    [McpServerTool(Name = "computewarden_set_manual_busy")]
    [Description("Turns on the manual hold: nothing new starts and running work is told to run small, " +
                 "as if a game ran. The USER's control: call this only when the user explicitly asks for it, " +
                 "never on your own. May be disabled by server configuration.")]
    public static Task<string> SetManualBusy(
        [Description("Human-readable reason for the hold.")] string reason)
        => Run(async client =>
        {
            var b = await client.SetManualBusyAsync(reason);
            return new { ok = true, blocker = BlockerDto(b, DateTimeOffset.UtcNow) };
        });

    [McpServerTool(Name = "computewarden_clear_manual_busy")]
    [Description("Turns off the manual hold set by computewarden_set_manual_busy. The USER's " +
                 "control, like setting it: only when the user explicitly asks.")]
    public static Task<string> ClearManualBusy()
        => Run(async client =>
        {
            var cleared = await client.ClearManualBusyAsync();
            return new { cleared };
        });

    [McpServerTool(Name = "computewarden_stats")]
    [Description("Usage counters since the daemon started: total/failed acquisitions, expired " +
                 "reservations (a high count means agents acquire but don't release — a misuse " +
                 "signal), current reservations and process blockers, and uptime.")]
    public static Task<string> Stats()
        => Run(async client =>
        {
            var s = await client.StatsAsync();
            return new
            {
                totalAcquisitions = s.TotalAcquisitions,
                failedAcquisitions = s.FailedAcquisitions,
                expiredReservations = s.ExpiredReservations,
                currentReservations = s.CurrentReservations,
                currentProcessBlockers = s.CurrentProcessBlockers,
                uptimeSeconds = (long)s.Uptime.TotalSeconds,
            };
        });

    // ---- helpers --------------------------------------------------------

    // Acquire answers with reservation_id, so agents pass it back under that name; a missing
    // required argument fails in the SDK with no message, so both spellings are taken.
    private const string IdAliasHelp = "Same as reservationId (either name works).";

    private static string Id(string? reservationId, string? alias)
        => !string.IsNullOrWhiteSpace(reservationId) ? reservationId
            : !string.IsNullOrWhiteSpace(alias) ? alias
            : throw new WardenClientException("reservationId is required (the reservation_id from computewarden_acquire)");

    private static async Task<string> Run(Func<WardenClient, Task<object>> operation)
    {
        try
        {
            var client = await WardenGateway.EnsureAsync();
            var result = await operation(client);
            return JsonSerializer.Serialize(result, Json);
        }
        catch (WardenClientException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, Json);
        }
        catch (TimeoutException ex)
        {
            // Connect timeouts carry no useful message; a response timeout explains itself.
            return JsonSerializer.Serialize(new
            {
                error = ex.Message.Contains("did not respond") ? ex.Message : "Could not reach the ComputeWarden daemon",
                hint = "Retry shortly; if it persists, check %ProgramData%\\ComputeWarden\\logs\\daemon.log.",
            }, Json);
        }
        catch (IOException ex)
        {
            var message = string.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message;
            return JsonSerializer.Serialize(new { error = $"IPC error: {message}" }, Json);
        }
        catch (UnauthorizedAccessException)
        {
            return JsonSerializer.Serialize(new
            {
                error = "Access denied to the ComputeWarden pipe",
                hint = "The daemon belongs to another user or an elevated session; stop it so a normal session can start its own.",
            }, Json);
        }
        catch (ArgumentException ex)
        {
            // A bad argument checked here before sending.
            return JsonSerializer.Serialize(new { error = ex.Message }, Json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Last resort: keep the "errors are JSON" contract instead of surfacing the SDK's
            // opaque "An error occurred invoking ..." text.
            return JsonSerializer.Serialize(new { error = $"Unexpected error: {ex.Message}" }, Json);
        }
    }

    private static object[] Blockers(IReadOnlyList<Blocker> blockers)
    {
        var now = DateTimeOffset.UtcNow;
        var result = new object[blockers.Count];
        for (var i = 0; i < blockers.Count; i++)
            result[i] = BlockerDto(blockers[i], now);
        return result;
    }

    private static object BlockerDto(Blocker b, DateTimeOffset now) => new
    {
        type = TypeString(b.Type),
        description = b.Description,
        owner = b.Owner,
        leaseRemainingSeconds = Remaining(b.ExpiresAt, now),
        metadata = b.Metadata is { Count: > 0 } ? b.Metadata : null,
    };

    private static object AdviceDto(Advice a, DateTimeOffset now) => new
    {
        grant = Warden.GrantName(a.Grant),
        next = a.Grant switch
        {
            Grant.Full => "Run at full size. Renew before the lease runs out; renew returns new advice if a game starts.",
            Grant.Throttled => "Start now, but small: follow throttle/how. Between steps, renew to hear when you can go full.",
            _ => "Don't start yet. Run `cw wait <reservation_id>` in the background and do other work; it keeps your " +
                 "place and exits when it's your turn; then renew to start. Without background commands, renew with waitSeconds: 60 instead.",
        },
        reason = a.Reason,
        throttle = a.Throttle is { } t ? new { maxCores = t.MaxCores, priority = t.Priority, maxRamGb = t.MaxRamGb } : null,
        how = a.How,
        queue = a.Queue is { } q
            ? new
            {
                position = q.Position,
                estWaitSeconds = q.EstWaitSeconds,
                ahead = q.Ahead.Select(c => ClaimDto(c, now)).ToArray(),
            }
            : null,
    };

    private static object ClaimDto(ClaimInfo c, DateTimeOffset now) => new
    {
        owner = c.Owner,
        description = c.Description,
        kind = Warden.KindName(c.Kind),
        bigMemory = c.BigMemory ? true : (bool?)null,
        grant = Warden.GrantName(c.Grant),
        phase = c.Phase switch { ClaimPhase.Running => "running", ClaimPhase.Ready => "ready", _ => "waiting" },
        position = c.Position,
        estimateSeconds = c.EstimateSeconds,
        leaseRemainingSeconds = Remaining(c.ExpiresAt, now),
    };

    private static ClaimKind ParseKind(string? kind) => (kind ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "" or "shared" => ClaimKind.Shared,
        "solo" => ClaimKind.Solo,
        "exclusive" => throw new WardenClientException(
            "kind exclusive was removed: use shared (with bigMemory: true only if it needs more than ~8 GB and can't cap its memory), " +
            "or solo for a short timing run"),
        _ => throw new WardenClientException($"Unknown kind '{kind}' (expected shared or solo)"),
    };

    private static int? Remaining(DateTimeOffset? expiresAt, DateTimeOffset now)
        => expiresAt is null ? null : Math.Max(0, (int)(expiresAt.Value - now).TotalSeconds);

    private static string StateString(MachineState state) => state switch
    {
        MachineState.Available => "AVAILABLE",
        MachineState.Busy => "BUSY",
        _ => "UNKNOWN",
    };

    private static string TypeString(BlockerType type) => type switch
    {
        BlockerType.Process => "PROCESS",
        BlockerType.Reservation => "RESERVATION",
        _ => "MANUAL",
    };
}
