using ComputeWarden.Core.Diagnostics;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;

namespace ComputeWarden.Daemon;

/// <summary>
/// Drives <see cref="ProcessBlockerProvider.Refresh"/> on the configured interval and logs
/// only state transitions (blocker appeared/cleared, confidence lost/regained) — never a line
/// per poll (spec §30).
/// </summary>
public sealed class ProcessMonitor : IAsyncDisposable
{
    private readonly ProcessBlockerProvider _provider;
    private readonly TimeSpan _interval;
    private readonly ILog _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    private readonly Dictionary<string, string> _knownBlockers = new(); // id -> description
    private bool _confident = true;

    public ProcessMonitor(ProcessBlockerProvider provider, int pollIntervalMs, ILog log)
    {
        _provider = provider;
        _interval = TimeSpan.FromMilliseconds(Math.Max(250, pollIntervalMs));
        _log = log;
    }

    public void Start()
    {
        _provider.Refresh(); // prime a snapshot immediately so acquire has data at startup
        ReconcileForLogging();
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                // One bad poll must not end polling: a dead loop would freeze the last snapshot
                // and keep reporting it as confident, i.e. silently wrong state.
                try
                {
                    _provider.Refresh();
                    ReconcileForLogging();
                }
                catch (Exception ex)
                {
                    _log.Error("Process poll failed; will retry next interval", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private void ReconcileForLogging()
    {
        var result = _provider.GetBlockers();

        if (result.Confident != _confident)
        {
            _confident = result.Confident;
            if (_confident) _log.Info("Process enumeration recovered; state confidence restored");
            else _log.Warn("Process enumeration failing; machine state is UNKNOWN");
        }

        var current = new Dictionary<string, string>();
        foreach (var b in result.Blockers)
            if (b.Type == BlockerType.Process)
                current[b.Id] = b.Description;

        foreach (var (id, desc) in current)
            if (!_knownBlockers.ContainsKey(id))
                _log.Info($"Process blocker detected: {desc}");

        foreach (var (id, desc) in _knownBlockers)
            if (!current.ContainsKey(id))
                _log.Info($"Process blocker cleared: {desc}");

        _knownBlockers.Clear();
        foreach (var kv in current)
            _knownBlockers[kv.Key] = kv.Value;
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
