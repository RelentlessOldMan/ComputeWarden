using System.Globalization;
using System.Text.Json;
using ComputeWarden.Core;
using ComputeWarden.Core.Model;

namespace ComputeWarden.Client;

/// <summary>Parses daemon JSON responses into Core result types.</summary>
internal static class ResponseParser
{
    /// <summary>Parses the response, throwing if the envelope reports failure.</summary>
    public static JsonElement Root(string responseLine)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(responseLine);
        }
        catch (JsonException ex)
        {
            throw new WardenClientException("Daemon returned an unparseable response", ex);
        }

        var root = doc.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown error";
            throw new WardenClientException($"Daemon error: {error}");
        }
        return root;
    }

    public static StatusResult Status(JsonElement root) => new(
        State: State(root, "state"),
        CanRunIntensive: Bool(root, "canRunIntensive"),
        Blockers: Blockers(root),
        BusyResources: ResourcesOpt(root, "busyResources") ?? ResourceSet.None,
        // A daemon predating sharing sends neither.
        Mode: root.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.Object ? Mode(m) : null,
        Claims: root.TryGetProperty("claims", out var c) ? Claims(c) : null,
        DaemonVersion: String(root, "daemonVersion"));

    public static AcquireResult Acquire(JsonElement root) => new(
        Acquired: Bool(root, "acquired"),
        ReservationId: String(root, "reservationId"),
        ExpiresAt: DateOpt(root, "expiresAt"),
        MachineState: State(root, "machineState"),
        Reason: String(root, "reason"),
        Blockers: Blockers(root),
        Resources: ResourcesOpt(root, "resources") ?? ResourceSet.None,
        Advice: AdviceOpt(root),
        Kind: String(root, "kind") switch { "shared" => ClaimKind.Shared, "exclusive" => ClaimKind.Exclusive, _ => null });

    public static RenewResult Renew(JsonElement root) => new(
        Renewed: Bool(root, "renewed"),
        ExpiresAt: DateOpt(root, "expiresAt"),
        Reason: String(root, "reason"),
        Advice: AdviceOpt(root));

    public static ModeState Mode(JsonElement e) => new(
        Mode: String(e, "mode") switch
        {
            "steady" => UserMode.Steady,
            "open" => UserMode.Open,
            "hold" => UserMode.Hold,
            _ => UserMode.Auto,
        },
        Resources: ResourcesOpt(e, "resources") ?? ResourceSet.All,
        Reason: String(e, "reason"),
        SetAt: DateOpt(e, "setAt") ?? DateTimeOffset.MinValue,
        ExpiresAt: DateOpt(e, "expiresAt"));

    private static Advice? AdviceOpt(JsonElement root)
    {
        if (!root.TryGetProperty("advice", out var a) || a.ValueKind != JsonValueKind.Object)
            return null;

        ThrottleAdvice? throttle = null;
        if (a.TryGetProperty("throttle", out var t) && t.ValueKind == JsonValueKind.Object)
            throttle = new ThrottleAdvice(IntOpt(t, "maxCores"), String(t, "priority"), DoubleOpt(t, "maxRamGb"));

        QueueInfo? queue = null;
        if (a.TryGetProperty("queue", out var q) && q.ValueKind == JsonValueKind.Object)
        {
            var ahead = q.TryGetProperty("ahead", out var arr) ? Claims(arr) : Array.Empty<ClaimInfo>();
            queue = new QueueInfo(IntOpt(q, "position"), ahead, IntOpt(q, "estWaitSeconds"));
        }

        return new Advice(
            GrantFrom(String(a, "grant")),
            String(a, "reason"),
            throttle,
            String(a, "how"),
            queue,
            ResourcesOpt(a, "contended") ?? ResourceSet.None);
    }

    private static IReadOnlyList<ClaimInfo> Claims(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array) return Array.Empty<ClaimInfo>();
        var list = new List<ClaimInfo>(arr.GetArrayLength());
        foreach (var e in arr.EnumerateArray())
        {
            list.Add(new ClaimInfo(
                Id: String(e, "id") ?? string.Empty,
                Owner: String(e, "owner") ?? string.Empty,
                Description: String(e, "description") ?? string.Empty,
                Kind: String(e, "kind") == "shared" ? ClaimKind.Shared : ClaimKind.Exclusive,
                Resources: ResourcesOpt(e, "resources") ?? ResourceSet.All,
                Grant: GrantFrom(String(e, "grant")),
                Holding: Bool(e, "holding"),
                ExpiresAt: DateOpt(e, "expiresAt") ?? DateTimeOffset.MinValue,
                EstimateSeconds: IntOpt(e, "estimateSeconds"),
                Cores: IntOpt(e, "cores"),
                RamGb: DoubleOpt(e, "ramGb"),
                UserPriority: String(e, "userPriority"),
                Position: IntOpt(e, "position")));
        }
        return list;
    }

    // Unknown grants from a newer daemon read as queued: the cautious answer.
    private static Grant GrantFrom(string? grant) => grant switch
    {
        "full" => Grant.Full,
        "throttled" => Grant.Throttled,
        _ => Grant.Queued,
    };

    private static int? IntOpt(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static double? DoubleOpt(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static ReleaseResult Release(JsonElement root) => new(
        Released: Bool(root, "released"),
        Reason: String(root, "reason"));

    public static WardenStats Stats(JsonElement root) => new(
        TotalAcquisitions: Long(root, "totalAcquisitions"),
        FailedAcquisitions: Long(root, "failedAcquisitions"),
        ExpiredReservations: Long(root, "expiredReservations"),
        CurrentReservations: (int)Long(root, "currentReservations"),
        CurrentProcessBlockers: (int)Long(root, "currentProcessBlockers"),
        Uptime: TimeSpan.FromSeconds(Long(root, "uptimeSeconds")));

    public static Blocker Blocker(JsonElement e) => new(
        Id: String(e, "id") ?? string.Empty,
        Type: BlockerTypeFrom(String(e, "type")),
        Source: String(e, "source") ?? string.Empty,
        Description: String(e, "description") ?? string.Empty,
        Owner: String(e, "owner"),
        CreatedAt: DateOpt(e, "createdAt"),
        ExpiresAt: DateOpt(e, "expiresAt"),
        Metadata: Metadata(e),
        // A daemon predating resources sends none: its blockers hold the whole machine.
        Resources: ResourcesOpt(e, "resources") ?? ResourceSet.All);

    private static IReadOnlyList<Blocker> Blockers(JsonElement root)
    {
        if (!root.TryGetProperty("blockers", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<Blocker>();
        var list = new List<Blocker>(arr.GetArrayLength());
        foreach (var e in arr.EnumerateArray())
            list.Add(Blocker(e));
        return list;
    }

    private static IReadOnlyDictionary<string, string>? Metadata(JsonElement e)
    {
        if (!e.TryGetProperty("metadata", out var m) || m.ValueKind != JsonValueKind.Object)
            return null;
        var dict = new Dictionary<string, string>();
        foreach (var prop in m.EnumerateObject())
            dict[prop.Name] = prop.Value.GetString() ?? string.Empty;
        return dict;
    }

    private static ResourceSet? ResourcesOpt(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        var set = ResourceSet.None;
        foreach (var item in arr.EnumerateArray())
        {
            // Tolerate names from a newer daemon that this client doesn't know.
            try { set |= Resources.Parse(new[] { item.GetString() ?? string.Empty }); }
            catch (ArgumentException) { }
        }
        return set;
    }

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? String(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0L;

    private static DateTimeOffset? DateOpt(JsonElement e, string name)
    {
        var s = String(e, name);
        return s is null
            ? null
            : DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static MachineState State(JsonElement e, string name) => String(e, name) switch
    {
        "AVAILABLE" => MachineState.Available,
        "BUSY" => MachineState.Busy,
        _ => MachineState.Unknown,
    };

    private static BlockerType BlockerTypeFrom(string? type) => type switch
    {
        "PROCESS" => BlockerType.Process,
        "RESERVATION" => BlockerType.Reservation,
        _ => BlockerType.Manual,
    };
}
