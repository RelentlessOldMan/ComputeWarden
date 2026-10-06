using System.Text.Json;
using System.Text.Json.Nodes;
using ComputeWarden.Core.Model;

namespace ComputeWarden.Core.Ipc;

/// <summary>
/// Translates a single JSON request line into a JSON response line by dispatching to the
/// <see cref="Warden"/>. Pure transport-agnostic logic: no pipes here, so it is fully unit
/// testable. The named-pipe server is a thin wrapper that reads a line, calls
/// <see cref="Handle"/>, and writes the result.
/// </summary>
public sealed class RequestDispatcher
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Warden _warden;
    private readonly bool _allowManualBlocker;

    public RequestDispatcher(Warden warden, bool allowManualBlocker = true)
    {
        _warden = warden;
        _allowManualBlocker = allowManualBlocker;
    }

    public string Handle(string requestLine)
    {
        IpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<IpcRequest>(requestLine, ParseOptions);
        }
        catch (JsonException)
        {
            return Error("Malformed request (invalid JSON)");
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
            o["busyResources"] = ResourceArray(status.BusyResources);
            o["blockers"] = Blockers(status.Blockers);
        });
    }

    private string HandleCanRun(IpcRequest r)
        => Ok(o => o["canRun"] = _warden.CanRun(Resources.Parse(r.Resources)));

    private string HandleAcquire(IpcRequest r)
    {
        var result = _warden.Acquire(
            r.Owner ?? string.Empty, r.Description ?? string.Empty, r.LeaseSeconds, r.Metadata,
            Resources.Parse(r.Resources));
        return Ok(o =>
        {
            o["acquired"] = result.Acquired;
            if (result.Acquired) o["resources"] = ResourceArray(result.Resources);
            o["reservationId"] = result.ReservationId;
            o["expiresAt"] = Iso(result.ExpiresAt);
            o["machineState"] = StateString(result.MachineState);
            o["reason"] = result.Reason;
            o["blockers"] = Blockers(result.Blockers);
        });
    }

    private string HandleRenew(IpcRequest r)
    {
        var result = _warden.Renew(r.ReservationId ?? string.Empty, r.LeaseSeconds);
        return Ok(o =>
        {
            o["renewed"] = result.Renewed;
            o["expiresAt"] = Iso(result.ExpiresAt);
            o["reason"] = result.Reason;
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

        var blocker = _warden.SetManualBusy(r.Reason, Resources.Parse(r.Resources));
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

    // ---- JSON helpers ---------------------------------------------------

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
            ["resources"] = ResourceArray(b.Resources),
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

    private static JsonArray ResourceArray(ResourceSet set)
    {
        var array = new JsonArray();
        foreach (var name in Resources.ToNames(set))
            array.Add(name);
        return array;
    }

    private static string? Iso(DateTimeOffset? value) => value?.ToString("o");

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
