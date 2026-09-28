namespace ComputeWarden.Core.Diagnostics;

/// <summary>
/// Minimal logging seam. Kept tiny so Core stays dependency-free; the daemon supplies a
/// console/file implementation. Routine polling must not log — only lifecycle events and
/// state transitions (spec §30).
/// </summary>
public interface ILog
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

/// <summary>Discards all log output. Useful in tests.</summary>
public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? exception = null) { }
}
