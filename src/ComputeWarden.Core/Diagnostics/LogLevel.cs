namespace ComputeWarden.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error }

public static class LogLevels
{
    public static LogLevel Parse(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "warn" or "warning" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Info,
    };
}
