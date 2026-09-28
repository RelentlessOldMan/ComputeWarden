using ComputeWarden.Core.Diagnostics;

namespace ComputeWarden.Daemon;

internal static class LogFormat
{
    public static string Line(LogLevel level, string message)
        => $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{level.ToString().ToUpperInvariant()}] {message}";
}
