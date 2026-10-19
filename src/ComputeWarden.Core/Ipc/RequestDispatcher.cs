using System.Text.Json;
using System.Text.Json.Nodes;
using ComputeWarden.Core.Model;

namespace ComputeWarden.Core.Ipc;

/// <summary>
/// Translates a single JSON request line into a JSON response line by dispatching to the
/// <see cref="Warden"/>. Pure transport-agnostic logic: no pipes here, so it is fully unit
/// testable. The named-pipe server is a thin wrapper that reads a line, calls
/// <see cref="HandleAsync"/>, and writes the result.
/// </summary>
public sealed class RequestDispatcher
{
    /// <summary>Longest a renew may wait for its grant to change. The claim is kept alive while it waits.</summary>
    public const int MaxWaitSeconds = 60;

    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Warden _warden;
    private readonly bool _allowManualBlocker;
    private readonly TimeSpan _pollInterval;

    public RequestDispatcher(Warden warden, bool allowManualBlocker = true, TimeSpan? pollInterval = null)
    {
        _warden = warden;
        _allowManualBlocker = allowManualBlocker;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// Like <see cref="Handle"/>, but a renew with <c>waitSeconds</c> first waits (up to
    /// <see cref="MaxWaitSeconds"/>) for the claim's grant to change from the one it was last
    /// given, so a throttled or queued agent hears about a game exiting right away instead of
    /// polling.
    /// </summary>
    public async Task<string> HandleAsync(string requestLine, CancellationToken cancellationToken = default)
    {
        if (TryReadWait(requestLine) is { } w)
        {
            var seconds = Math.Min(w.Seconds, MaxWaitSeconds);
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds);
            var last = _warden.BeginWait(w.Id, seconds);
            while (last is not null)
            {
                var now = _warden.PeekGrant(w.Id);
                if (now is null || now != last) break;
                var left = deadline - DateTimeOffset.UtcNow;
                if (left <= TimeSpan.Zero) break;
                await Task.Delay(left < _pollInterval ? left : _pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        return Handle(requestLine);
    }

    private static (string Id, int Seconds)? TryReadWait(string requestLine)
    {
        try
        {
            var request = JsonSerializer.Deserialize<IpcRequest>(requestLine, ParseOptions);
            if (request is { WaitSeconds: > 0, ReservationId.Length: > 0 } &&
                string.Equals(request.Op, IpcOps.Renew, StringComparison.OrdinalIgnoreCase))
                return (request.ReservationId, request.WaitSeconds.Value);
        }
        catch (JsonException)
        {
            // Handle reports it.
        }
        return null;
    }

    public string Handle(string requestLine)
    {
        IpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<IpcRequest>(requestLine, ParseOptions);
        }
        catch (JsonException ex)
        {
            // Valid JSON with a field of the wrong type has a path; say which field.
            return Error(ex.Path is { Length: > 1 } path
                ? $"Malformed request: '{path.TrimStart('$', '.')}' has the wrong type"
                : "Malformed request (invalid JSON)");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Op))
            return Error("Missing 'op'");

        // Version tolerance: the daemon is long-lived and shared while adapters are per-session,
        // so mismatched versions are normal during a rollout. Treat the protocol as additive —
        // serve any version we know (<= ours); reject only a FUTURE version we can't understand.
        // Combined with System.Text.Json ignoring unknown fields, this keeps old and new
        // components interoperable without a coordinated restart.
        if (request.V > IpcProtocol.Version)
            return Error($"Unsupported protocol version {request.V} (daemon speaks up to {IpcProtocol.Version})");

        try
        {
            return request.Op.ToUpperInvariant() switch
            {
                IpcOps.Status => HandleStatus(),
                IpcOps.CanRun => HandleCanRun(request),
                IpcOps.Acquire => HandleAcquire(request),
                IpcOps.Renew => HandleRenew(request),
                IpcOps.Release => HandleRelease(request),
                IpcOps.SetManualBusy => HandleSetManual(request),
                IpcOps.ClearManualBusy => HandleClearManual(),
                IpcOps.Stats => HandleStats(),
                _ => Error($"Unknown op '{request.Op}'"),
            };
        }
        catch (ArgumentException ex)
        {
            // Client-side validation failure (e.g. an over-long field): report it as such,
            // not as an internal fault.
            return Error(ex.Message);
        }
        catch (Exception ex)
        {
            // Never let a handler take down the pipe loop; report a structured error.
            return Error($"Internal error: {ex.Message}");
        }
    }

    private string HandleStatus()
    {
        var status = _warden.GetStatus();
        return Ok(o =>
        {
            o["state"] = StateString(status.State);
            o["canRunIntensive"] = status.CanRunIntensive;
            o["blockers"] = Blockers(status.Blockers);
            if (status.GameJobs is { } jobs) o["gameJobs"] = jobs;
            o["claims"] = ClaimsJson(status.Claims ?? Array.Empty<ClaimInfo>());
            o["daemonVersion"] = IpcProtocol.ProductVersion;
        });
    }

    private string HandleCanRun(IpcRequest r)
        => Ok(o => o["canRun"] = _warden.CanRun());

    private string HandleAcquire(IpcRequest r)
    {
        var result = _warden.Acquire(new ClaimRequest(
            r.Owner ?? string.Empty,
            r.Description ?? string.Empty,
            ParseKind(r.Kind),
            r.BigMemory == true,
            r.LeaseSeconds,
            r.EstimateSeconds,
            r.Metadata,
            r.UserApproved == true));

        return Ok(o =>
        {
            o["acquired"] = result.Acquired;
            if (result.Kind is { } kind) o["kind"] = Warden.KindName(kind);
            if (result.Advice is { } advice) o["advice"] = AdviceJson(advice);
            if (result.Acquired) o["bigMemory"] = result.BigMemory;
            o["reservationId"] = result.ReservationId;
            o["expiresAt"] = Iso(result.ExpiresAt);
            o["machineState"] = StateString(result.MachineState);
            o["reason"] = result.Reason;
            o["blockers"] = Blockers(result.Blockers);
        });
    }

    private string HandleRenew(IpcRequest r)
    {
        var result = _warden.Renew(r.ReservationId ?? string.Empty, r.LeaseSeconds, r.Probe == true);
        return Ok(o =>
        {
            o["renewed"] = result.Renewed;
            o["expiresAt"] = Iso(result.ExpiresAt);
            o["reason"] = result.Reason;
            if (result.Advice is { } advice) o["advice"] = AdviceJson(advice);
        });
    }

    private string HandleRelease(IpcRequest r)
    {
        var result = _warden.Release(r.ReservationId ?? string.Empty);
        return Ok(o =>
        {
            o["released"] = result.Released;
            o["reason"] = result.Reason;
        });
    }

    private string HandleSetManual(IpcRequest r)
    {
        if (!_allowManualBlocker)
            return Error("Manual blocker control is disabled by configuration");
        if (string.IsNullOrWhiteSpace(r.Reason))
            return Error("'reason' is required");

        var blocker = _warden.SetManualBusy(r.Reason);
        return Ok(o => o["blocker"] = BlockerJson(blocker));
    }

    private string HandleClearManual()
    {
        if (!_allowManualBlocker)
            return Error("Manual blocker control is disabled by configuration");
        var cleared = _warden.ClearManualBusy();
        return Ok(o => o["cleared"] = cleared);
    }

    private string HandleStats()
    {
        var s = _warden.GetStats();
        return Ok(o =>
        {
            o["totalAcquisitions"] = s.TotalAcquisitions;
            o["failedAcquisitions"] = s.FailedAcquisitions;
            o["expiredReservations"] = s.ExpiredReservations;
            o["currentReservations"] = s.CurrentReservations;
            o["currentProcessBlockers"] = s.CurrentProcessBlockers;
            o["uptimeSeconds"] = (long)s.Uptime.TotalSeconds;
        });
    }

    // ---- Parsing --------------------------------------------------------

    private static ClaimKind ParseKind(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "" or "shared" => ClaimKind.Shared,
        "solo" => ClaimKind.Solo,
        "exclusive" => throw new ArgumentException(
            "kind exclusive was removed: use shared (with bigMemory: true only if it needs more than ~8 GB of RAM and can't cap it, or more than half the GPU's memory), " +
            "or solo for a short timing run"),
        _ => throw new ArgumentException($"Unknown kind '{value}' (expected shared or solo)"),
    };

    // ---- JSON helpers ---------------------------------------------------

    private static JsonObject AdviceJson(Advice a)
    {
        var o = new JsonObject
        {
            ["grant"] = Warden.GrantName(a.Grant),
            ["reason"] = a.Reason,
        };
        if (a.Throttle is { } t)
        {
            o["throttle"] = new JsonObject
            {
                ["maxCores"] = t.MaxCores,
                ["priority"] = t.Priority,
                ["maxRamGb"] = t.MaxRamGb,
            };
        }
        if (a.How is { } how) o["how"] = how;
        if (a.Queue is { } q)
        {
            o["queue"] = new JsonObject
            {
                ["position"] = q.Position,
                ["ahead"] = ClaimsJson(q.Ahead),
                ["estWaitSeconds"] = q.EstWaitSeconds,
            };
        }
        return o;
    }

    private static JsonArray ClaimsJson(IReadOnlyList<ClaimInfo> claims)
    {
        var array = new JsonArray();
        foreach (var c in claims)
        {
            array.Add(new JsonObject
            {
                ["id"] = c.Id,
                ["owner"] = c.Owner,
                ["description"] = c.Description,
                ["kind"] = Warden.KindName(c.Kind),
                ["bigMemory"] = c.BigMemory,
                ["grant"] = Warden.GrantName(c.Grant),
                ["phase"] = PhaseString(c.Phase),
                ["expiresAt"] = Iso(c.ExpiresAt),
                ["estimateSeconds"] = c.EstimateSeconds,
                ["position"] = c.Position,
                ["lastCheckIn"] = Iso(c.LastCheckIn),
            });
        }
        return array;
    }

    private static string Ok(Action<JsonObject> fill)
    {
        var o = new JsonObject { ["v"] = IpcProtocol.Version, ["ok"] = true };
        fill(o);
        return o.ToJsonString();
    }

    private static string Error(string message)
    {
        var o = new JsonObject { ["v"] = IpcProtocol.Version, ["ok"] = false, ["error"] = message };
        return o.ToJsonString();
    }

    private static JsonArray Blockers(IReadOnlyList<Blocker> blockers)
    {
        var array = new JsonArray();
        foreach (var b in blockers)
            array.Add(BlockerJson(b));
        return array;
    }

    private static JsonObject BlockerJson(Blocker b)
    {
        var o = new JsonObject
        {
            ["id"] = b.Id,
            ["type"] = TypeString(b.Type),
            ["source"] = b.Source,
            ["description"] = b.Description,
            ["owner"] = b.Owner,
            ["createdAt"] = Iso(b.CreatedAt),
            ["expiresAt"] = Iso(b.ExpiresAt),
        };
        if (b.Metadata is { Count: > 0 })
        {
            var meta = new JsonObject();
            foreach (var kv in b.Metadata)
                meta[kv.Key] = kv.Value;
            o["metadata"] = meta;
        }
        return o;
    }

    private static string? Iso(DateTimeOffset? value) => value?.ToString("o");

    private static string StateString(MachineState state) => state switch
    {
        MachineState.Available => "AVAILABLE",
        MachineState.Busy => "BUSY",
        _ => "UNKNOWN",
    };

    private static string PhaseString(ClaimPhase phase) => phase switch
    {
        ClaimPhase.Running => "running",
        ClaimPhase.Ready => "ready",
        _ => "waiting",
    };

    private static string TypeString(BlockerType type) => type switch
    {
        BlockerType.Process => "PROCESS",
        BlockerType.Reservation => "RESERVATION",
        _ => "MANUAL",
    };
}
