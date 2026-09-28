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
        Blockers: Blockers(root));

    public static AcquireResult Acquire(JsonElement root) => new(
        Acquired: Bool(root, "acquired"),
        ReservationId: String(root, "reservationId"),
        ExpiresAt: DateOpt(root, "expiresAt"),
        MachineState: State(root, "machineState"),
        Reason: String(root, "reason"),
        Blockers: Blockers(root));

    public static RenewResult Renew(JsonElement root) => new(
        Renewed: Bool(root, "renewed"),
        ExpiresAt: DateOpt(root, "expiresAt"),
        Reason: String(root, "reason"));

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
        Metadata: Metadata(e));

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
