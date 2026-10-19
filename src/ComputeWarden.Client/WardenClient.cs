using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ComputeWarden.Core;
using ComputeWarden.Core.Ipc;
using ComputeWarden.Core.Model;

namespace ComputeWarden.Client;

/// <summary>Result of probing for a daemon on the pipe.</summary>
public enum DaemonProbe
{
    Running,
    NotRunning,
    /// <summary>A daemon is listening but its pipe ACL rejects us (e.g. it runs elevated or as another user).</summary>
    AccessDenied,
}

/// <summary>
/// Client for the ComputeWarden daemon's named-pipe protocol. Each call opens a short-lived
/// connection, sends one request line, and reads one response line — simple and robust for a
/// low-frequency coordination service. Safe to reuse across calls; not tied to a connection.
/// </summary>
public sealed class WardenClient
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _pipeName;
    private readonly int _connectTimeoutMs;
    private readonly TimeSpan _responseTimeout;

    /// <param name="responseTimeout">
    /// How long to wait for the daemon's reply once connected (default 10s). Every operation is
    /// an in-memory lookup, so hitting this means the daemon is wedged — better to fail the
    /// call than hang the agent forever.
    /// </param>
    public WardenClient(string? pipeName = null, int connectTimeoutMs = 3000, TimeSpan? responseTimeout = null)
    {
        _pipeName = pipeName ?? IpcProtocol.PipeName;
        _connectTimeoutMs = connectTimeoutMs;
        _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<StatusResult> StatusAsync(CancellationToken ct = default)
        => ResponseParser.Status(await SendAsync(new IpcRequest { Op = IpcOps.Status }, ct));

    public async Task<bool> CanRunAsync(CancellationToken ct = default)
    {
        var root = await SendAsync(new IpcRequest { Op = IpcOps.CanRun }, ct);
        return root.TryGetProperty("canRun", out var v) && v.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Reports work and gets the warden's advice (full, throttled, or queued). A valid claim is
    /// always created; the advice says how big to run, or to wait.
    /// </summary>
    public async Task<AcquireResult> AcquireAsync(ClaimRequest claim, CancellationToken ct = default)
    {
        var request = new IpcRequest
        {
            Op = IpcOps.Acquire,
            Owner = claim.Owner,
            Description = claim.Description,
            Kind = Warden.KindName(claim.Kind),
            BigMemory = claim.BigMemory ? true : null,
            UserApproved = claim.UserApproved ? true : null,
            LeaseSeconds = claim.LeaseSeconds,
            EstimateSeconds = claim.EstimateSeconds,
            Metadata = claim.Metadata is null ? null : new Dictionary<string, string>(claim.Metadata),
        };
        return ResponseParser.Acquire(await SendAsync(request, ct));
    }

    /// <param name="waitSeconds">
    /// Wait (up to 60s) for the claim's grant to change before answering, e.g. for a game to
    /// exit. Lets a throttled or queued agent react at once without busy polling.
    /// </param>
    /// <param name="probe">Check in without starting the claim: for a background waiter, not the owner.</param>
    public async Task<RenewResult> RenewAsync(string reservationId, int? leaseSeconds = null, int? waitSeconds = null,
        CancellationToken ct = default, bool probe = false)
    {
        var request = new IpcRequest
        {
            Op = IpcOps.Renew,
            ReservationId = reservationId,
            LeaseSeconds = leaseSeconds,
            WaitSeconds = waitSeconds is > 0 ? Math.Min(waitSeconds.Value, RequestDispatcher.MaxWaitSeconds) : null,
            Probe = probe ? true : null,
        };
        var wait = request.WaitSeconds is int w ? TimeSpan.FromSeconds(w) : TimeSpan.Zero;
        return ResponseParser.Renew(await SendAsync(request, ct, _responseTimeout + wait));
    }

    public async Task<ReleaseResult> ReleaseAsync(string reservationId, CancellationToken ct = default)
    {
        var request = new IpcRequest { Op = IpcOps.Release, ReservationId = reservationId };
        return ResponseParser.Release(await SendAsync(request, ct));
    }

    /// <summary>Turns on the manual hold. The user's call only.</summary>
    public async Task<Blocker> SetManualBusyAsync(string reason, CancellationToken ct = default)
    {
        var root = await SendAsync(new IpcRequest { Op = IpcOps.SetManualBusy, Reason = reason }, ct);
        if (!root.TryGetProperty("blocker", out var blocker))
            throw new WardenClientException("Response missing 'blocker'");
        return ResponseParser.Blocker(blocker);
    }

    public async Task<bool> ClearManualBusyAsync(CancellationToken ct = default)
    {
        var root = await SendAsync(new IpcRequest { Op = IpcOps.ClearManualBusy }, ct);
        return root.TryGetProperty("cleared", out var v) && v.ValueKind == JsonValueKind.True;
    }

    public async Task<WardenStats> StatsAsync(CancellationToken ct = default)
        => ResponseParser.Stats(await SendAsync(new IpcRequest { Op = IpcOps.Stats }, ct));

    /// <summary>Attempts to connect; returns false if no usable daemon is listening within the timeout.</summary>
    public async Task<bool> IsDaemonRunningAsync(CancellationToken ct = default)
        => await ProbeAsync(ct) == DaemonProbe.Running;

    /// <summary>
    /// Attempts to connect, distinguishing "nothing listening" from "listening but we're not
    /// allowed in" — the latter can't be fixed by spawning another daemon.
    /// </summary>
    public async Task<DaemonProbe> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(_connectTimeoutMs, ct);
            return DaemonProbe.Running;
        }
        catch (TimeoutException) { return DaemonProbe.NotRunning; }
        catch (IOException) { return DaemonProbe.NotRunning; }
        catch (UnauthorizedAccessException) { return DaemonProbe.AccessDenied; }
    }

    private async Task<JsonElement> SendAsync(IpcRequest request, CancellationToken ct, TimeSpan? responseTimeout = null)
    {
        var limit = responseTimeout ?? _responseTimeout;
        // The pipe stream owns its own lifetime via `using`; reader/writer leave it open so
        // disposal happens exactly once, in a well-defined order, regardless of which throws.
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(_connectTimeoutMs, ct);

        using var reader = new StreamReader(client, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        using var writer = new StreamWriter(client, Utf8NoBom, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);

        string? line;
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, SerializeOptions).AsMemory(), timeout.Token);
            line = await reader.ReadLineAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"ComputeWarden daemon did not respond within {limit.TotalSeconds:0}s");
        }

        // A transport failure (the daemon restarting, say), not a bad answer: callers retry it like one.
        if (line is null)
            throw new IOException("Daemon closed the connection without responding");

        return ResponseParser.Root(line);
    }
}
