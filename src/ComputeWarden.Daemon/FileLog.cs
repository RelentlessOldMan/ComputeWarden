using ComputeWarden.Core.Diagnostics;

namespace ComputeWarden.Daemon;

/// <summary>
/// Appends log lines to a file, with a simple size cap + single-backup rollover so the log
/// can never grow without bound (spec §37). Logging never throws into callers — a failing log
/// sink must not take down the daemon.
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    private readonly LogLevel _minimum;
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private bool _disposed;

    public FileLog(string path, string level = "info", long maxBytes = 5L * 1024 * 1024)
    {
        _path = path;
        _minimum = LogLevels.Parse(level);
        _maxBytes = maxBytes;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _writer = Open(path);
    }

    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warn, message);

    public void Error(string message, Exception? exception = null)
        => Write(LogLevel.Error, exception is null ? message : $"{message}: {exception}");

    private static StreamWriter Open(string path)
        => new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };

    private void Write(LogLevel level, string message)
    {
        if (level < _minimum) return;
        var line = LogFormat.Line(level, message);
        lock (_gate)
        {
            try
            {
                if (_disposed) return;
                _writer ??= Open(_path); // recover from an earlier failed open/rollover
                _writer.WriteLine(line);
                RollIfNeeded();
            }
            catch
            {
                // A logging failure must never propagate into the daemon's real work.
            }
        }
    }

    private void RollIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length <= _maxBytes) return;

        _writer!.Dispose();
        _writer = null;
        try
        {
            var backup = _path + ".1";
            File.Delete(backup);    // no-op if absent
            File.Move(_path, backup);
        }
        finally
        {
            // Reopen even if the move failed (e.g. a reader holds the file without delete
            // sharing): keep appending past the cap rather than going silent, and retry the
            // rollover on a later write.
            _writer = Open(_path);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
