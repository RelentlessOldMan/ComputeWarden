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

    /// <summary>Ignored since 2.0 (claims no longer name resources); kept so older clients still parse.</summary>
    public List<string>? Resources { get; set; }

    /// <summary>
    /// Claim kind for acquire: "shared" (the default when absent) or "solo". "exclusive" was
    /// removed in 2.0 and is refused with an error saying what to use instead.
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>Acquire: a job that needs so much memory that only one may run at a time.</summary>
    public bool? BigMemory { get; set; }

    /// <summary>Acquire: the user explicitly approved a solo run longer than 10 minutes (up to 60).</summary>
    public bool? UserApproved { get; set; }

    public int? EstimateSeconds { get; set; }

    /// <summary>Renew: wait up to this long for the claim's grant to change before answering.</summary>
    public int? WaitSeconds { get; set; }

    /// <summary>Renew: check in without starting the claim (a background waiter, not the owner).</summary>
    public bool? Probe { get; set; }
}

public static class IpcProtocol
{
    public const int Version = 1;

    /// <summary>Default machine-wide pipe name (Windows: \\.\pipe\ComputeWarden).</summary>
    public const string PipeName = "ComputeWarden";

    /// <summary>The release this build is, e.g. "1.1.2": STATUS reports it so an install can be checked.</summary>
    public static string ProductVersion { get; } =
        (typeof(IpcProtocol).Assembly
             .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
             .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
             .FirstOrDefault()?.InformationalVersion ?? "unknown")
        .Split('+')[0];
}
