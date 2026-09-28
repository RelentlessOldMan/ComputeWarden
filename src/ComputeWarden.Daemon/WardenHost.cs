using ComputeWarden.Core;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Diagnostics;
using ComputeWarden.Core.Ipc;
using ComputeWarden.Core.Providers;
using ComputeWarden.Core.Time;

namespace ComputeWarden.Daemon;

/// <summary>
/// Composition root: wires the warden, providers, process monitor, lease sweeper, and pipe
/// server together from a <see cref="WardenConfig"/>, and manages their lifetime.
/// </summary>
public sealed class WardenHost : IAsyncDisposable
{
    private readonly ProcessMonitor _monitor;
    private readonly LeaseSweeper _sweeper;
    private readonly NamedPipeServer _pipeServer;
    private readonly ILog _log;

    public Warden Warden { get; }

    public WardenHost(
        WardenConfig config,
        ILog log,
        IProcessLister? processLister = null,
        IClock? clock = null,
        string? pipeName = null)
    {
        _log = log;
        clock ??= SystemClock.Instance;
        processLister ??= new SystemProcessLister();

        var manual = new ManualBlockerProvider(clock);
        var processProvider = new ProcessBlockerProvider(
            processLister, config.ProcessRules, config.ProcessDetection, clock);

        Warden = new Warden(clock, config.Leases, config.Limits, manual, new[] { processProvider }, log);

        var dispatcher = new RequestDispatcher(Warden, config.AllowManualBlocker);

        _monitor = new ProcessMonitor(processProvider, config.ProcessDetection.PollIntervalMs, log);
        _sweeper = new LeaseSweeper(Warden, TimeSpan.FromSeconds(5), log);
        _pipeServer = new NamedPipeServer(pipeName ?? IpcProtocol.PipeName, dispatcher, log);
    }

    public void Start()
    {
        _log.Info("ComputeWarden daemon starting");
        _monitor.Start();
        _sweeper.Start();
        _pipeServer.Start();
        _log.Info("ComputeWarden daemon started");
    }

    public async ValueTask DisposeAsync()
    {
        _log.Info("ComputeWarden daemon stopping");
        await _pipeServer.DisposeAsync();
        await _sweeper.DisposeAsync();
        await _monitor.DisposeAsync();
        _log.Info("ComputeWarden daemon stopped");
    }
}
