using ComputeWarden.Core.Diagnostics;

namespace ComputeWarden.Daemon;

/// <summary>Simple leveled logger writing to the console with UTC timestamps.</summary>
public sealed class ConsoleLog : ILog
{
    private readonly LogLevel _minimum;
    private readonly object _gate = new();

    public ConsoleLog(string level = "info") => _minimum = LogLevels.Parse(level);

    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warn, message);

    public void Error(string message, Exception? exception = null)
        => Write(LogLevel.Error, exception is null ? message : $"{message}: {exception}");

    private void Write(LogLevel level, string message)
    {
        if (level < _minimum) return;
        var line = LogFormat.Line(level, message);
        lock (_gate)
        {
            if (level == LogLevel.Error) Console.Error.WriteLine(line);
            else Console.Out.WriteLine(line);
        }
    }
}
