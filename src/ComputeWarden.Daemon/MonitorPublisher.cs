using System.Text.Json;
using ComputeWarden.Core;
using ComputeWarden.Core.Diagnostics;
using ComputeWarden.Core.Ipc;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Time;

namespace ComputeWarden.Daemon;

/// <summary>Where the daemon's live view goes: <see cref="MonitorBridge"/> in the daemon, a fake in tests.</summary>
public interface IMonitorSink : IDisposable
{
    /// <summary>False when no monitor client is installed; every other call then does nothing.</summary>
    bool Enabled { get; }
    void Start();
    void Set(string id, object? value);
    void Log(string id, string text, string level = "info");
    void Provide(string name, Func<JsonElement?, object?> handler, string? description = null);
}

/// <summary>
/// Optional live view in RelentlessMonitor: machine state, mode, blockers and claims, refreshed
/// every couple of seconds, plus a <c>status</c> request that answers like the pipe's STATUS.
/// Daemon log lines reach the panel's feed through <see cref="MonitorLog"/>. With no monitor
/// installed the sink is disabled and none of this runs.
/// </summary>
public sealed class MonitorPublisher : IAsyncDisposable
{
    public const string App = "ComputeWarden";
    public const string EventsFeed = "events";

    /// <summary>The panel, as plain data in RelentlessMonitor's layout shape.</summary>
    public static object Layout { get; } = new
    {
        title = "ComputeWarden",
        sections = new object[]
        {
            new
            {
                title = "Machine",
                columns = 2,
                widgets = new object[]
                {
                    new { id = "state", kind = "indicator", label = "State",
                          states = new { available = "green", busy = "orange", unknown = "gray" } },
                    new { id = "mode", kind = "text", label = "Mode" },
                    new { id = "busy", kind = "text", label = "Busy resources", span = 2 },
                    new { id = "blockers", kind = "text", label = "Blockers", span = 2 },
                },
            },
            new
            {
                title = "Claims",
                widgets = new object[]
                {
                    new { id = "claims", kind = "text", label = "Active claims" },
                    new { id = EventsFeed, kind = "feed", limit = 200 },
                },
            },
        },
    };

    private const string StatusRequest = """{"v":1,"op":"STATUS"}""";

    private readonly IMonitorSink _sink;
    private readonly Warden _warden;
    private readonly IClock _clock;
    private readonly TimeSpan _interval;
    private readonly Timer _timer;
    private readonly Dictionary<string, string> _sent = new();
    private readonly object _gate = new();

    public MonitorPublisher(IMonitorSink sink, Warden warden, RequestDispatcher dispatcher, IClock clock, TimeSpan? interval = null)
    {
        _sink = sink;
        _warden = warden;
        _clock = clock;
        _interval = interval ?? TimeSpan.FromSeconds(2);
        _timer = new Timer(_ => Publish(), null, Timeout.Infinite, Timeout.Infinite);

        // Same JSON an agent gets from the pipe, so `rmon request ComputeWarden.status` needs no docs of its own.
        _sink.Provide("status", _ =>
            {
                using var doc = JsonDocument.Parse(dispatcher.Handle(StatusRequest));
                return doc.RootElement.Clone();
            },
            "machine state, mode, blockers and claims (same as computewarden_status)");
    }

    public void Start()
    {
        if (!_sink.Enabled) return;
        try
        {
            _sink.Start();
        }
        catch
        {
            return; // the monitor is optional; a broken client must not stop the daemon starting
        }
        Publish();
        _timer.Change(_interval, _interval);
    }

    /// <summary>Pushes the current view, sending only values that changed. Public so tests can drive it.</summary>
    public void Publish()
    {
        lock (_gate)
        {
            try
            {
                foreach (var (id, value) in Render(_warden.GetStatus(), _clock.UtcNow))
                {
                    if (_sent.TryGetValue(id, out var old) && old == value) continue;
                    _sink.Set(id, value);
                    _sent[id] = value; // only once sent, so a failed send is retried next time
                }
            }
            catch
            {
                // The monitor is optional; it must never take down the daemon.
            }
        }
    }

    /// <summary>The panel's values for one status snapshot.</summary>
    public static IReadOnlyList<(string Id, string Value)> Render(StatusResult status, DateTimeOffset now)
    {
        var claims = status.Claims ?? Array.Empty<ClaimInfo>();
        return new[]
        {
            ("state", status.State.ToString().ToLowerInvariant()),
            ("mode", ModeText(status.Mode)),
            ("busy", status.BusyResources == ResourceSet.None ? "none" : string.Join(", ", Resources.ToNames(status.BusyResources))),
            ("blockers", status.Blockers.Count == 0 ? "none" : string.Join("\n", status.Blockers.Select(b => b.Description))),
            ("claims", claims.Count == 0 ? "none" : string.Join("\n", claims.Select(c => ClaimText(c, now)))),
        };
    }

    private static string ModeText(ModeState? mode)
    {
        if (mode is null) return "auto";
        var text = Warden.ModeName(mode.Mode);
        if (mode.Mode != UserMode.Auto && mode.Resources != ResourceSet.All)
            text += $" ({string.Join(", ", Resources.ToNames(mode.Resources))})";
        if (mode.ExpiresAt is { } until) text += $" until {until.ToLocalTime():HH:mm}";
        if (!string.IsNullOrWhiteSpace(mode.Reason)) text += $": {mode.Reason}";
        return text;
    }

    private static string ClaimText(ClaimInfo c, DateTimeOffset now)
    {
        var grant = c.Position is int p ? $"queued #{p}" : Warden.GrantName(c.Grant);
        var minutes = Math.Max(0, (int)Math.Ceiling((c.ExpiresAt - now).TotalMinutes));
        return $"{c.Owner}: {c.Description} ({Warden.KindName(c.Kind)}, {grant}, " +
               $"{string.Join("+", Resources.ToNames(c.Resources))}, lease {minutes}m)";
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) _timer.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Copies the daemon's info-and-above log lines into the monitor's event feed.</summary>
public sealed class MonitorLog : ILog
{
    private readonly IMonitorSink _sink;

    public MonitorLog(IMonitorSink sink) => _sink = sink;

    public void Debug(string message) { }
    public void Info(string message) => Send(message, "info");
    public void Warn(string message) => Send(message, "warn");

    public void Error(string message, Exception? exception = null)
        => Send(exception is null ? message : $"{message}: {exception.Message}", "error");

    // Called under the warden's lock: a broken monitor client must never throw into a claim.
    private void Send(string text, string level)
    {
        try
        {
            _sink.Log(MonitorPublisher.EventsFeed, text, level);
        }
        catch
        {
        }
    }
}
