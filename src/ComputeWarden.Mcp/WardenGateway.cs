using ComputeWarden.Client;

namespace ComputeWarden.Mcp;

/// <summary>
/// Provides the shared <see cref="WardenClient"/> to the tools, auto-spawning the daemon the
/// first time it's needed if none is listening. Tests can inject a client pointed at an
/// in-process host and disable auto-spawn.
/// </summary>
public static class WardenGateway
{
    private static WardenClient _client = new(connectTimeoutMs: 2000);
    private static bool _autoSpawn = true;
    private static readonly SemaphoreSlim _spawnLock = new(1, 1);

    /// <summary>Overrides the client (and auto-spawn) — used by tests.</summary>
    public static void Configure(WardenClient client, bool autoSpawn)
    {
        _client = client;
        _autoSpawn = autoSpawn;
    }

    /// <summary>Returns a ready client, launching the daemon if necessary.</summary>
    public static async Task<WardenClient> EnsureAsync(CancellationToken ct = default)
    {
        if (!_autoSpawn) return _client;
        if (await _client.IsDaemonRunningAsync(ct)) return _client;

        await _spawnLock.WaitAsync(ct);
        try
        {
            // Re-check under the lock: another tool call may have already spawned it.
            if (await _client.IsDaemonRunningAsync(ct)) return _client;

            DaemonLauncher.TryLaunch();

            // Wait for the daemon to begin serving the pipe.
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (await _client.IsDaemonRunningAsync(ct)) break;
                await Task.Delay(150, ct);
            }
        }
        finally
        {
            _spawnLock.Release();
        }

        return _client;
    }
}
