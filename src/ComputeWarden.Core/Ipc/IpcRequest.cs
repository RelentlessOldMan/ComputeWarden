namespace ComputeWarden.Core.Ipc;

/// <summary>Operation names carried in the IPC request envelope.</summary>
public static class IpcOps
{
    public const string Status = "STATUS";
    public const string CanRun = "CAN_RUN";
    public const string Acquire = "ACQUIRE";
    public const string Renew = "RENEW";
    public const string Release = "RELEASE";
    public const string SetManualBusy = "SET_MANUAL_BUSY";
    public const string ClearManualBusy = "CLEAR_MANUAL_BUSY";
    public const string Stats = "STATS";
}

/// <summary>
/// Single request envelope for the newline-delimited JSON pipe protocol. Fields not relevant
/// to a given op are left null. <see cref="V"/> is the protocol version.
/// </summary>
public sealed class IpcRequest
{
    public int V { get; set; } = IpcProtocol.Version;
    public string Op { get; set; } = string.Empty;

    public string? Owner { get; set; }
    public string? Description { get; set; }
    public int? LeaseSeconds { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public string? ReservationId { get; set; }
    public string? Reason { get; set; }

    /// <summary>
    /// Resource names (cpu, gpu, ram, network, disk) for acquire/can_run/set_manual_busy.
    /// Absent means all — so older clients keep exclusive-machine semantics, and an older
    /// daemon that ignores this field errs conservative.
    /// </summary>
    public List<string>? Resources { get; set; }
}

public static class IpcProtocol
{
    public const int Version = 1;

    /// <summary>Default machine-wide pipe name (Windows: \\.\pipe\ComputeWarden).</summary>
    public const string PipeName = "ComputeWarden";
}
