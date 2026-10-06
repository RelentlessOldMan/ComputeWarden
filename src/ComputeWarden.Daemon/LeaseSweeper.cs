using ComputeWarden.Core;
using ComputeWarden.Core.Diagnostics;

namespace ComputeWarden.Daemon;

/// <summary>
/// Periodically forces expiry of stale reservations so leases disappear even with no client
/// traffic. (The warden also prunes lazily on every read; this guarantees eventual cleanup.)
/// </summary>
public sealed class LeaseSweeper : IAsyncDisposable
{
    private readonly Warden _warden;
    private readonly TimeSpan _interval;
    private readonly ILog _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public LeaseSweeper(Warden warden, TimeSpan interval, ILog log)
    {
        _warden = warden;
        _interval = interval;
        _log = log;
    }

    public void Start() => _loop = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                try
                {
                    _warden.Sweep();
                }
                catch (Exception ex)
                {
                    _log.Error("Lease sweep failed; will retry next interval", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop; }
            catch { /* already logged */ }
        }
        _cts.Dispose();
    }
}
