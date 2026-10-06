using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComputeWarden.Client;
using ComputeWarden.Core.Model;
using ModelContextProtocol.Server;

namespace ComputeWarden.Mcp;

/// <summary>
/// The MCP tool surface for ComputeWarden. Each tool forwards to the daemon over IPC and
/// returns compact structured JSON (spec §38) so agents can make their own decisions and
/// produce useful explanations. Errors are returned as JSON, never thrown, so a transient
/// daemon hiccup surfaces as data the agent can act on.
/// </summary>
[McpServerToolType]
public static class ComputeWardenTools
{
    private const string ResourcesHelp =
        "Resources the work will saturate: any of cpu, gpu, ram, network, disk. Holds conflict " +
        "only where they overlap, so e.g. cpu+network work can run alongside gpu+ram work. " +
        "Omit for all (the whole machine). Declare every resource it really saturates — " +
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
                 "processes, reservations, manual holds) explaining why. busy_resources lists which " +
                 "of cpu/gpu/ram/network/disk are currently held.")]
    public static Task<string> Status()
        => Run(async client =>
        {
            var s = await client.StatusAsync();
            return new
            {
                state = StateString(s.State),
                canRunIntensive = s.CanRunIntensive,
                busyResources = Resources.ToNames(s.BusyResources),
                blockers = Blockers(s.Blockers),
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
    [Description("Atomically checks blockers on the requested resources and, if none are held, " +
                 "reserves them for intensive work. Returns the reservation (with expiry) on success, " +
                 "or the conflicting blockers on failure. Save reservation_id; renew it during long work and release it " +
                 "when done (use finally/cleanup semantics)." + OneCallHelp)]
    public static Task<string> Acquire(
        [Description("Who is acquiring, e.g. 'Claude Code'.")] string owner,
        [Description("What the intensive work is, e.g. 'Indexing repository'.")] string description,
        [Description("Requested lease seconds; clamped to server bounds. Omit for the server default.")] int? leaseSeconds = null,
        [Description("Optional small string metadata.")] Dictionary<string, string>? metadata = null,
        [Description(ResourcesHelp)] string[]? resources = null)
        => Run(async client =>
        {
            var r = await client.AcquireAsync(owner, description, leaseSeconds, metadata, resources);
            var now = DateTimeOffset.UtcNow;
            return new
            {
                acquired = r.Acquired,
                reservationId = r.ReservationId,
                resources = r.Acquired ? Resources.ToNames(r.Resources) : null,
                expiresAt = r.ExpiresAt,
                leaseRemainingSeconds = Remaining(r.ExpiresAt, now),
                machineState = StateString(r.MachineState),
                reason = r.Reason,
                blockers = Blockers(r.Blockers),
            };
        });

    [McpServerTool(Name = "computewarden_renew")]
    [Description("Renews an existing reservation's lease so long-running work keeps its hold. " +
                 "Returns renewed=false if the reservation no longer exists (e.g. it already expired).")]
    public static Task<string> Renew(
        [Description("The reservation_id returned by computewarden_acquire.")] string reservationId,
        [Description("New lease seconds; clamped to server bounds. Omit for the server default.")] int? leaseSeconds = null)
        => Run(async client =>
        {
            var r = await client.RenewAsync(reservationId, leaseSeconds);
            return new
            {
                renewed = r.Renewed,
                expiresAt = r.ExpiresAt,
                leaseRemainingSeconds = Remaining(r.ExpiresAt, DateTimeOffset.UtcNow),
                reason = r.Reason,
            };
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
                 "auto-detect (e.g. disk maintenance). May be disabled by server configuration.")]
    public static Task<string> SetManualBusy(
        [Description("Human-readable reason the machine is manually held busy.")] string reason,
        [Description("Resources to hold (cpu, gpu, ram, network, disk). Omit for all.")] string[]? resources = null)
        => Run(async client =>
        {
            var b = await client.SetManualBusyAsync(reason, resources);
            return new { ok = true, blocker = BlockerDto(b, DateTimeOffset.UtcNow) };
        });

    [McpServerTool(Name = "computewarden_clear_manual_busy")]
    [Description("Removes the manual blocker previously set by computewarden_set_manual_busy.")]
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
            return JsonSerializer.Serialize(new { error = $"IPC error: {ex.Message}" }, Json);
        }
        catch (UnauthorizedAccessException)
        {
            return JsonSerializer.Serialize(new
            {
                error = "Access denied to the ComputeWarden pipe",
                hint = "The daemon belongs to another user or an elevated session; stop it so a normal session can start its own.",
            }, Json);
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
