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
    private const string ResourcesHelp =
        "Resources the work will saturate: any of cpu, gpu, ram, network, disk. Holds conflict " +
        "only where they overlap, so e.g. cpu+network work can run alongside gpu+ram work. " +
        "Omit for the default: cpu+ram+disk for kind=shared, the whole machine otherwise. " +
        "Declare every resource it really saturates — " +
        "under-declaring lets conflicting work start.";

    private const string OneCallHelp =
        " Request ALL resources the job needs in this ONE call: a later acquire is a separate, " +
        "unrelated reservation (holding one resource while waiting on another can stall agents " +
        "on each other, and it can be refused by your own hold). If needs change, release and " +
        "re-acquire the full set.";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool(Name = "computewarden_status")]
    [Description("Returns the complete current machine coordination state: AVAILABLE, BUSY, or " +
                 "UNKNOWN, whether intensive work can run, and the list of blockers (detected " +
                 "processes, reservations, manual holds, the user mode) explaining why. busy_resources " +
                 "lists which of cpu/gpu/ram/network/disk are currently held. mode is the user's current " +
                 "override; claims lists all agent work (shared, holding, and waiting in line).")]
    public static Task<string> Status()
        => Run(async client =>
        {
            var s = await client.StatusAsync();
            var now = DateTimeOffset.UtcNow;
            return new
            {
                state = StateString(s.State),
                canRunIntensive = s.CanRunIntensive,
                busyResources = Resources.ToNames(s.BusyResources),
                mode = s.Mode is { } m ? ModeDto(m, now) : null,
                blockers = Blockers(s.Blockers),
                claims = s.Claims?.Select(c => ClaimDto(c, now)).ToArray(),
                daemonVersion = s.DaemonVersion,
            };
        });

    [McpServerTool(Name = "computewarden_can_run")]
    [Description("Convenience availability check. Informational ONLY — it does not reserve the " +
                 "machine and availability is not guaranteed after the call returns. Before " +
                 "beginning protected intensive work, call computewarden_acquire instead.")]
    public static Task<string> CanRun(
        [Description(ResourcesHelp)] string[]? resources = null)
        => Run(async client =>
        {
            var canRun = await client.CanRunAsync(resources);
            return new
            {
                canRun,
                note = "Informational only; not a guarantee. Use computewarden_acquire before protected work.",
            };
        });

    [McpServerTool(Name = "computewarden_acquire")]
    [Description("Reports work to the warden and returns its advice: grant full (run normally), " +
                 "throttled (run small: follow advice.throttle / advice.how), or queued (don't start; " +
                 "check back by renewing). kind=shared is for everyday builds, tests, and runs: never " +
                 "refused, only told how big to run. kind=exclusive is for heavy jobs that saturate " +
                 "their resources: one holder per resource, others wait in a first-come-first-served " +
                 "line. Configured priority processes (the user's games) always limit everyone on their " +
                 "resources. Omitting kind is the legacy all-or-nothing acquire (refused when busy). " +
                 "Save reservation_id; renew it during the work (renew returns updated advice, e.g. " +
                 "full once a game exits) and release it when done (finally/cleanup semantics)." + OneCallHelp)]
    public static Task<string> Acquire(
        [Description("Who is acquiring, e.g. 'Claude Code'.")] string owner,
        [Description("What the work is, e.g. 'dotnet test ComputeWarden.slnx'.")] string description,
        [Description("Requested lease seconds; clamped to server bounds. Omit to derive it from estimateSeconds (or the server default).")] int? leaseSeconds = null,
        [Description("Optional small string metadata.")] Dictionary<string, string>? metadata = null,
        [Description(ResourcesHelp)] string[]? resources = null,
        [Description("shared (everyday build/test/run) or exclusive (heavy job). Omit only for the legacy all-or-nothing acquire.")] string? kind = null,
        [Description("Exclusive only, when the resources are taken: throttle (default: run small now, stay in line for full), wait (don't start until it's your turn), or refuse (create nothing).")] string? whenBusy = null,
        [Description("Your estimate of how long the work takes at full size, in seconds. Helps others' wait estimates.")] int? estimateSeconds = null,
        [Description("Cores the work would use at full size, if known.")] int? cores = null,
        [Description("Peak memory in GB the work would use at full size, if known.")] double? ramGb = null,
        [Description("Skip the line. ONLY when the user explicitly told you this work has priority; give their reason, e.g. 'user: release build first'. Never set it on your own.")] string? userPriority = null)
        => Run(async client =>
        {
            AcquireResult r;
            if (string.IsNullOrWhiteSpace(kind))
            {
                r = await client.AcquireAsync(owner, description, leaseSeconds, metadata, resources);
            }
            else
            {
                r = await client.AcquireAsync(new ClaimRequest(
                    owner, description, ParseKind(kind),
                    Resources.ParseClaim(resources),
                    ParseWhenBusy(whenBusy), leaseSeconds, estimateSeconds, cores, ramGb, userPriority, metadata));
            }

            var now = DateTimeOffset.UtcNow;
            return new
            {
                acquired = r.Acquired,
                reservationId = r.ReservationId,
                kind = r.Kind is { } k ? Warden.KindName(k) : null,
                resources = r.Acquired ? Resources.ToNames(r.Resources) : null,
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
        [Description("The reservation_id returned by computewarden_acquire.")] string reservationId,
        [Description("New lease seconds; clamped to server bounds. Omit to keep the claim's lease (legacy holds: the server default).")] int? leaseSeconds = null,
        [Description("Wait up to this many seconds (max 60) for the grant to change before answering.")] int? waitSeconds = null,
        CancellationToken cancellationToken = default) // the host cancelling closes the pipe, which ends the daemon's wait
        => Run(async client =>
        {
            var r = await client.RenewAsync(reservationId, leaseSeconds, waitSeconds, cancellationToken);
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

    [McpServerTool(Name = "computewarden_set_mode")]
    [Description("Sets the machine-wide user mode. The USER's control: call this only when the user " +
                 "explicitly asks for it, never on your own. auto = normal rules; steady = everyone runs " +
                 "throttled ('y'all go slow and steady'); open = no throttling between agents, exclusive " +
                 "holds still keep heavy jobs apart ('full bore allowed with a lease'); hold = nobody " +
                 "starts new work. No mode ever overrides a running priority process (a game).")]
    public static Task<string> SetMode(
        [Description("auto, steady, open, or hold.")] string mode,
        [Description("Revert to auto after this many seconds (e.g. 7200 for '2 hours'; at most 7 days). Omit to keep it until changed.")] int? durationSeconds = null,
        [Description("The user's reason, e.g. 'gaming tonight'.")] string? reason = null,
        [Description("Resources the mode covers (cpu, gpu, ram, network, disk). Omit for all.")] string[]? resources = null)
        => Run(async client =>
        {
            var m = await client.SetModeAsync(ParseMode(mode), resources, durationSeconds, reason);
            return new { ok = true, mode = ModeDto(m, DateTimeOffset.UtcNow) };
        });

    [McpServerTool(Name = "computewarden_release")]
    [Description("Releases a reservation immediately, freeing the machine for other agents. " +
                 "Idempotent: releasing an unknown or already-expired reservation is not an error.")]
    public static Task<string> Release(
        [Description("The reservation_id to release.")] string reservationId)
        => Run(async client =>
        {
            var r = await client.ReleaseAsync(reservationId);
            return new { released = r.Released, reason = r.Reason };
        });

    [McpServerTool(Name = "computewarden_set_manual_busy")]
    [Description("Creates a manual blocker marking the machine BUSY for work ComputeWarden cannot " +
                 "auto-detect (e.g. disk maintenance). The USER's control: call this only when the user " +
                 "explicitly asks for it, never on your own. May be disabled by server configuration.")]
    public static Task<string> SetManualBusy(
        [Description("Human-readable reason the machine is manually held busy.")] string reason,
        [Description("Resources to hold (cpu, gpu, ram, network, disk). Omit for all.")] string[]? resources = null)
        => Run(async client =>
        {
            var b = await client.SetManualBusyAsync(reason, resources);
            return new { ok = true, blocker = BlockerDto(b, DateTimeOffset.UtcNow) };
        });

    [McpServerTool(Name = "computewarden_clear_manual_busy")]
    [Description("Removes the manual blocker previously set by computewarden_set_manual_busy. The USER's " +
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
            // A bad argument checked here before sending (an unknown resource, kind, or mode).
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
        resources = Resources.ToNames(b.Resources),
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
            Grant.Throttled => "Start now, but small: follow throttle/how. Between steps, renew (waitSeconds keeps the claim alive while it waits) to hear when you can go full.",
            _ => "Don't start yet. Renew with waitSeconds: 60 to wait for your turn and keep your place.",
        },
        reason = a.Reason,
        contended = a.Contended == ResourceSet.None ? null : Resources.ToNames(a.Contended),
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
        resources = Resources.ToNames(c.Resources),
        grant = Warden.GrantName(c.Grant),
        holding = c.Kind == ClaimKind.Exclusive ? c.Holding : (bool?)null,
        position = c.Position,
        estimateSeconds = c.EstimateSeconds,
        userPriority = c.UserPriority,
        leaseRemainingSeconds = Remaining(c.ExpiresAt, now),
    };

    private static object ModeDto(ModeState m, DateTimeOffset now) => new
    {
        mode = Warden.ModeName(m.Mode),
        resources = m.Mode == UserMode.Auto ? null : Resources.ToNames(m.Resources),
        reason = m.Reason,
        remainingSeconds = Remaining(m.ExpiresAt, now),
    };

    private static ClaimKind ParseKind(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        "shared" => ClaimKind.Shared,
        "exclusive" => ClaimKind.Exclusive,
        _ => throw new WardenClientException($"Unknown kind '{kind}' (expected shared or exclusive)"),
    };

    private static WhenBusy ParseWhenBusy(string? whenBusy) => (whenBusy ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "" or "throttle" => WhenBusy.Throttle,
        "wait" => WhenBusy.Wait,
        "refuse" => WhenBusy.Refuse,
        _ => throw new WardenClientException($"Unknown whenBusy '{whenBusy}' (expected throttle, wait, or refuse)"),
    };

    private static UserMode ParseMode(string mode) => mode.Trim().ToLowerInvariant() switch
    {
        "auto" => UserMode.Auto,
        "steady" => UserMode.Steady,
        "open" => UserMode.Open,
        "hold" => UserMode.Hold,
        _ => throw new WardenClientException($"Unknown mode '{mode}' (expected auto, steady, open, or hold)"),
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
