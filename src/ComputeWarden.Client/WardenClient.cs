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

    public async Task<bool> CanRunAsync(IEnumerable<string>? resources = null, CancellationToken ct = default)
    {
        var root = await SendAsync(new IpcRequest { Op = IpcOps.CanRun, Resources = resources?.ToList() }, ct);
        return root.TryGetProperty("canRun", out var v) && v.ValueKind == JsonValueKind.True;
    }

    public async Task<AcquireResult> AcquireAsync(
        string owner,
        string description,
        int? leaseSeconds = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        IEnumerable<string>? resources = null,
        CancellationToken ct = default)
    {
        var request = new IpcRequest
        {
            Op = IpcOps.Acquire,
            Owner = owner,
            Description = description,
            LeaseSeconds = leaseSeconds,
            Metadata = metadata is null ? null : new Dictionary<string, string>(metadata),
            Resources = resources?.ToList(),
        };
        return ResponseParser.Acquire(await SendAsync(request, ct));
    }

    public async Task<RenewResult> RenewAsync(string reservationId, int? leaseSeconds = null, CancellationToken ct = default)
    {
        var request = new IpcRequest { Op = IpcOps.Renew, ReservationId = reservationId, LeaseSeconds = leaseSeconds };
        return ResponseParser.Renew(await SendAsync(request, ct));
    }

    public async Task<ReleaseResult> ReleaseAsync(string reservationId, CancellationToken ct = default)
    {
        var request = new IpcRequest { Op = IpcOps.Release, ReservationId = reservationId };
        return ResponseParser.Release(await SendAsync(request, ct));
    }

    public async Task<Blocker> SetManualBusyAsync(string reason, IEnumerable<string>? resources = null, CancellationToken ct = default)
    {
        var root = await SendAsync(new IpcRequest { Op = IpcOps.SetManualBusy, Reason = reason, Resources = resources?.ToList() }, ct);
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

    private async Task<JsonElement> SendAsync(IpcRequest request, CancellationToken ct)
    {
        // The pipe stream owns its own lifetime via `using`; reader/writer leave it open so
        // disposal happens exactly once, in a well-defined order, regardless of which throws.
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(_connectTimeoutMs, ct);

        using var reader = new StreamReader(client, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        using var writer = new StreamWriter(client, Utf8NoBom, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_responseTimeout);

        string? line;
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, SerializeOptions).AsMemory(), timeout.Token);
            line = await reader.ReadLineAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"ComputeWarden daemon did not respond within {_responseTimeout.TotalSeconds:0}s");
        }

        if (line is null)
            throw new WardenClientException("Daemon closed the connection without responding");

        return ResponseParser.Root(line);
    }
}
