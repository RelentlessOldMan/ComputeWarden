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
///
/// If a config file path is supplied, the host watches it and hot-reloads the process rules +
/// debounce on change — so editing the exe list takes effect without a restart. (Lease bounds,
/// manual-blocker toggle, log level, and the poll interval are still applied only at startup.)
/// </summary>
public sealed class WardenHost : IAsyncDisposable
{
    private readonly ProcessMonitor _monitor;
    private readonly LeaseSweeper _sweeper;
    private readonly NamedPipeServer _pipeServer;
    private readonly ProcessBlockerProvider _processProvider;
    private readonly ILog _log;
    private readonly string? _configPath;

    private FileSystemWatcher? _configWatcher;
    private Timer? _reloadDebounce;
    private readonly object _reloadGate = new();

    public Warden Warden { get; }

    public WardenHost(
        WardenConfig config,
        ILog log,
        IProcessLister? processLister = null,
        IClock? clock = null,
        string? pipeName = null,
        string? configPath = null)
    {
        _log = log;
        _configPath = configPath;
        clock ??= SystemClock.Instance;
        processLister ??= new SystemProcessLister();

        var manual = new ManualBlockerProvider(clock);
        _processProvider = new ProcessBlockerProvider(
            processLister, config.ProcessRules, config.ProcessDetection, clock);

        Warden = new Warden(clock, config.Leases, config.Limits, manual, new[] { _processProvider }, log);

        var dispatcher = new RequestDispatcher(Warden, config.AllowManualBlocker);

        _monitor = new ProcessMonitor(_processProvider, config.ProcessDetection.PollIntervalMs, log);
        _sweeper = new LeaseSweeper(Warden, TimeSpan.FromSeconds(5), log);
        _pipeServer = new NamedPipeServer(pipeName ?? IpcProtocol.PipeName, dispatcher, log);
    }

    public void Start()
    {
        _log.Info("ComputeWarden daemon starting");
        _monitor.Start();
        _sweeper.Start();
        _pipeServer.Start();
        StartConfigWatcher();
        _log.Info("ComputeWarden daemon started");
    }

    /// <summary>
    /// Re-reads the config file and applies the hot-reloadable parts (process rules + debounce).
    /// A missing/corrupt config is logged and ignored so a bad edit never wipes the live rules.
    /// Public so it can be driven directly (tests) as well as by the file watcher.
    /// </summary>
    public void ReloadConfig()
    {
        // Editors often save by renaming, so the file can be briefly absent. Treat that as "no
        // change" — falling through would swap the live rules for the built-in defaults.
        if (!File.Exists(ConfigLoader.ResolvePath()))
        {
            _log.Warn("Config reload skipped (keeping current rules): config file not found");
            return;
        }

        ConfigLoadResult loaded;
        try
        {
            loaded = ConfigLoader.Load();
        }
        catch (Exception ex)
        {
            _log.Error("Config reload failed; keeping current rules", ex);
            return;
        }

        if (loaded.IsError)
        {
            _log.Warn($"Config reload skipped (keeping current rules): {loaded.Message}");
            return;
        }

        _processProvider.UpdateRules(loaded.Config.ProcessRules, loaded.Config.ProcessDetection);
        var active = loaded.Config.ProcessRules.Count(r => r.Enabled);
        _log.Info($"Configuration reloaded: {active} process rule(s) active");
    }

    private void StartConfigWatcher()
    {
        if (string.IsNullOrEmpty(_configPath)) return;

        var dir = Path.GetDirectoryName(_configPath);
        var file = Path.GetFileName(_configPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file) || !Directory.Exists(dir))
        {
            _log.Info($"Config hot-reload disabled (directory not present: {dir})");
            return;
        }

        _configWatcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _configWatcher.Changed += OnConfigChanged;
        _configWatcher.Created += OnConfigChanged;
        _configWatcher.Renamed += OnConfigChanged;
        _log.Info($"Watching {_configPath} - process rules hot-reload on change");
    }

    private void OnConfigChanged(object sender, FileSystemEventArgs e)
    {
        // Editors fire several events per save (and may still hold the file); debounce and reload
        // once things settle.
        lock (_reloadGate)
        {
            if (_reloadDebounce is null)
                _reloadDebounce = new Timer(_ => ReloadConfig(), null, 500, Timeout.Infinite);
            else
                _reloadDebounce.Change(500, Timeout.Infinite);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _log.Info("ComputeWarden daemon stopping");
        if (_configWatcher is not null)
        {
            _configWatcher.EnableRaisingEvents = false;
            _configWatcher.Dispose();
        }
        lock (_reloadGate) { _reloadDebounce?.Dispose(); _reloadDebounce = null; }
        await _pipeServer.DisposeAsync();
        await _sweeper.DisposeAsync();
        await _monitor.DisposeAsync();
        _log.Info("ComputeWarden daemon stopped");
    }
}
