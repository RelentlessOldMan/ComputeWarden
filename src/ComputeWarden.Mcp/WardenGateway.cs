using System.Diagnostics;
using ComputeWarden.Client;

namespace ComputeWarden.Mcp;

/// <summary>
/// Provides the shared <see cref="WardenClient"/> to the tools, auto-spawning the daemon the
/// first time it's needed if none is listening. Tests can inject a client pointed at an
/// in-process host and disable auto-spawn.
/// </summary>
public static class WardenGateway
{
    /// <summary>Total time to wait for a freshly launched daemon to start serving the pipe.</summary>
    private static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(8);

    private static WardenClient _client = new(connectTimeoutMs: 2000);
    private static bool _autoSpawn = true;
    private static Func<bool> _launch = DaemonLauncher.TryLaunch;
    private static readonly SemaphoreSlim _spawnLock = new(1, 1);

    /// <summary>Overrides the client, auto-spawn, and launcher — used by tests.</summary>
    public static void Configure(WardenClient client, bool autoSpawn, Func<bool>? launch = null)
    {
        _client = client;
        _autoSpawn = autoSpawn;
        _launch = launch ?? DaemonLauncher.TryLaunch;
    }

    /// <summary>
    /// Returns a ready client, launching the daemon if necessary. Throws
    /// <see cref="WardenClientException"/> with an actionable message when no daemon can be
    /// reached, rather than letting each tool call time out on its own.
    /// </summary>
    public static async Task<WardenClient> EnsureAsync(CancellationToken ct = default)
    {
        if (!_autoSpawn) return _client;
        var probe = await _client.ProbeAsync(ct);
        if (probe == DaemonProbe.Running) return _client;
        if (probe == DaemonProbe.AccessDenied) throw AccessDenied();

        await _spawnLock.WaitAsync(ct);
        try
        {
            // Re-check under the lock: another tool call may have already spawned it.
            probe = await _client.ProbeAsync(ct);
            if (probe == DaemonProbe.Running) return _client;
            if (probe == DaemonProbe.AccessDenied) throw AccessDenied();

            if (!_launch())
                throw new WardenClientException(
                    "ComputeWarden daemon is not running and could not be launched. Make sure " +
                    "ComputeWarden.Daemon.exe sits next to the MCP adapter, or set COMPUTEWARDEN_DAEMON to its path.");

            // Bound the total wait by time, not attempts: each probe can itself block for the
            // connect timeout while the pipe doesn't exist yet.
            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < StartupWait)
            {
                probe = await _client.ProbeAsync(ct);
                if (probe == DaemonProbe.Running) return _client;
                if (probe == DaemonProbe.AccessDenied) throw AccessDenied();
                await Task.Delay(150, ct);
            }

            throw new WardenClientException(
                $"ComputeWarden daemon was launched but did not start serving within {StartupWait.TotalSeconds:0}s; " +
                "check %ProgramData%\\ComputeWarden\\logs\\daemon.log.");
        }
        finally
        {
            _spawnLock.Release();
        }
    }

    private static WardenClientException AccessDenied() => new(
        "Access denied to the ComputeWarden pipe: the running daemon belongs to another user or an " +
        "elevated (admin) session. Stop that daemon so a normal session can start its own.");
}
