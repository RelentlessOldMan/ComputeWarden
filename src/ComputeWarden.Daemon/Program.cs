using ComputeWarden.Core.Diagnostics;
using ComputeWarden.Daemon;

// Load configuration first so the logger honors the configured level.
var loaded = ConfigLoader.Load();

// Log to console (useful when run in the foreground) AND to a capped file, since the daemon
// is normally spawned detached — its console output goes to a dead handle, so the file is the
// only durable record of how ComputeWarden is being used.
ILog log;
try
{
    var logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ComputeWarden", "logs", "daemon.log");
    log = new CompositeLog(
        new ConsoleLog(loaded.Config.LogLevel),
        new FileLog(logPath, loaded.Config.LogLevel));
}
catch
{
    // If the log file can't be opened (permissions, etc.), fall back to console only.
    log = new ConsoleLog(loaded.Config.LogLevel);
}

if (loaded.IsError) log.Error(loaded.Message);
else log.Info(loaded.Message);

// Single-instance guard: only one daemon should be the machine-wide authority. If another
// daemon already holds the mutex, exit quietly — that one is serving the pipe.
using var singleInstance = new Mutex(initiallyOwned: false, "Global\\ComputeWardenDaemon", out _);
bool owns;
try
{
    owns = singleInstance.WaitOne(TimeSpan.Zero);
}
catch (AbandonedMutexException)
{
    owns = true; // previous owner crashed; we inherit it
}

if (!owns)
{
    log.Info("Another ComputeWarden daemon is already running; exiting.");
    return 0;
}

// NOTE: we deliberately do NOT call ReleaseMutex(). After `await`, the continuation runs on a
// threadpool thread, and Mutex.ReleaseMutex() throws when called from a thread other than the one
// that acquired it. Disposing the Mutex (via `using`) on process exit releases the OS handle from
// any thread; if it ends up "abandoned", the next daemon start handles AbandonedMutexException above.
try
{
    await using var host = new WardenHost(loaded.Config, log);
    host.Start();

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true; // let us shut down gracefully instead of hard-killing
        shutdown.Cancel();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

    try
    {
        await Task.Delay(Timeout.Infinite, shutdown.Token);
    }
    catch (OperationCanceledException)
    {
        // graceful shutdown requested
    }

    return 0;
}
catch (Exception ex)
{
    log.Error("Daemon terminated with an unhandled exception", ex);
    return 1;
}
