using ComputeWarden.Core;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Diagnostics;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;
using ComputeWarden.Core.Time;

namespace ComputeWarden.Tests;

/// <summary>Records log messages so tests can assert on lifecycle logging.</summary>
public sealed class CapturingLog : ILog
{
    public List<string> Messages { get; } = new();

    public void Debug(string message) { lock (Messages) Messages.Add("DEBUG " + message); }
    public void Info(string message) { lock (Messages) Messages.Add("INFO " + message); }
    public void Warn(string message) { lock (Messages) Messages.Add("WARN " + message); }
    public void Error(string message, Exception? exception = null) { lock (Messages) Messages.Add("ERROR " + message); }

    public bool Contains(string substring)
    {
        lock (Messages) return Messages.Any(m => m.Contains(substring, StringComparison.Ordinal));
    }
}

/// <summary>Manually-advanced clock for deterministic lease/expiry tests.</summary>
public sealed class FakeClock : IClock
{
    private DateTimeOffset _now;

    public FakeClock(DateTimeOffset? start = null)
        => _now = start ?? DateTimeOffset.UnixEpoch;

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
    public void AdvanceSeconds(double seconds) => _now = _now.AddSeconds(seconds);
}

/// <summary>A blocker provider whose result the test controls directly.</summary>
public sealed class FakeBlockerProvider : IBlockerProvider
{
    public string Name => "FakeBlockerProvider";
    public ProviderResult Result { get; set; } = ProviderResult.Empty;
    public ProviderResult GetBlockers() => Result;

    public static Blocker Process(string exe, int pid) => new(
        Id: $"proc:{pid}",
        Type: BlockerType.Process,
        Source: "ProcessMonitor",
        Description: $"{exe} is running",
        Metadata: new Dictionary<string, string> { ["pid"] = pid.ToString() });
}

public static class TestFactory
{
    public static Warden NewWarden(
        IClock clock,
        LeaseOptions? lease = null,
        params IBlockerProvider[] extraProviders)
        => new(
            clock,
            lease ?? new LeaseOptions(),
            new WardenLimits(),
            new ManualBlockerProvider(clock),
            extraProviders);
}
