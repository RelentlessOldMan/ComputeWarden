using ComputeWarden.Core.Diagnostics;

namespace ComputeWarden.Daemon;

/// <summary>Fans log calls out to several sinks (e.g. console + file). Sinks are expected not to throw.</summary>
public sealed class CompositeLog : ILog, IDisposable
{
    private readonly ILog[] _sinks;

    public CompositeLog(params ILog[] sinks) => _sinks = sinks;

    public void Debug(string message) { foreach (var s in _sinks) s.Debug(message); }
    public void Info(string message) { foreach (var s in _sinks) s.Info(message); }
    public void Warn(string message) { foreach (var s in _sinks) s.Warn(message); }
    public void Error(string message, Exception? exception = null) { foreach (var s in _sinks) s.Error(message, exception); }

    public void Dispose()
    {
        foreach (var s in _sinks) (s as IDisposable)?.Dispose();
    }
}
