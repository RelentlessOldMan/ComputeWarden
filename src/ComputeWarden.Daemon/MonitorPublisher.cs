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
    void SetLayout(object layout);
    void Set(string id, object? value);
    void Log(string id, string text, string level = "info");
    void Provide(string name, Func<JsonElement?, object?> handler, string? description = null);
    void OnAction(string widgetId, Func<JsonElement?, object?> handler);
    void OnChoice(string widgetId, Action<string> handler);
    void OnFlags(string widgetId, Action<IReadOnlyList<string>> handler);
    void OnPick(string widgetId, Action<string> handler);

    /// <summary>Inside a handler: who sent it, as the hub stamped it. Null outside one or with an old client.</summary>
    MonitorSender? Sender { get; }
}

/// <summary>
/// Optional live view in RelentlessEye: machine state, blockers and claims, refreshed every
/// couple of seconds, plus a <c>status</c> request that answers like the pipe's STATUS. Daemon
/// log lines reach the panel's feed through <see cref="MonitorLog"/>. The user can also turn the
/// manual hold on and off and set how many jobs may run beside a game from the panel; only a
/// monitor may, never another app, since both are the user's call. With no monitor installed the
/// sink is disabled and none of this runs.
/// </summary>
public sealed class MonitorPublisher : IAsyncDisposable
{
    public const string App = "ComputeWarden";
    public const string EventsFeed = "events";

    // Before Layout, which reads them: static initializers run in order.

    private static readonly int[] GameJobsOptions = { 0, 1, 2, 3, 4 };

    private static readonly object[] ClaimColumns =
    {
        new { key = "who", label = "Claim" },
        new { key = "state", label = "State" },
        new { key = "lease", label = "Lease", align = "right" },
        new { key = "checked", label = "Last check", align = "right" },
        new { key = "what", label = "Doing" }, // last: takes the rest of the width, cut with a tooltip
    };

    /// <summary>The panel, as plain data in RelentlessEye's layout shape.</summary>
    public static object Layout { get; } = new
    {
        title = "Compute Warden",
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
                    new { id = HoldPick, kind = "choice", label = "Hold",
                          states = new { off = "green", on = "red" },
                          options = new object[]
                          {
                              "off",
                              new { value = "on", label = "on", confirm = "Hold: no new work starts and running work is told to run small. Turn it on?" },
                          } },
                    new { id = GameJobsPick, kind = "choice", label = "Jobs while gaming",
                          options = GameJobsOptions.Select(n => (object)n.ToString()).ToArray() },
                    new { id = "blockers", kind = "text", label = "Blockers" },
                },
            },
            new
            {
                title = "Claims",
                widgets = new object[] { new { id = ClaimsTable, kind = "table", columns = ClaimColumns } },
            },
            new
            {
                title = "Log",
                widgets = new object[] { new { id = EventsFeed, kind = "feed", limit = 200 } },
            },
        },
    };

    public const string HoldPick = "hold";
    public const string GameJobsPick = "game_jobs";
    public const string ClaimsTable = "claims";

    private const string StatusRequest = """{"v":1,"op":"STATUS"}""";

    private readonly IMonitorSink _sink;
    private readonly Warden _warden;
    private readonly IClock _clock;
    private readonly TimeSpan _interval;
    private readonly Timer _timer;
    private readonly Dictionary<string, string> _sent = new(); // id -> JSON last sent
    private readonly object _gate = new();
    private readonly ILog? _log;

    public MonitorPublisher(IMonitorSink sink, Warden warden, RequestDispatcher dispatcher, IClock clock,
        TimeSpan? interval = null, ILog? log = null)
    {
        _sink = sink;
        _warden = warden;
        _clock = clock;
        _log = log;
        _interval = interval ?? TimeSpan.FromSeconds(2);
        _timer = new Timer(_ => Publish(), null, Timeout.Infinite, Timeout.Infinite);

        // Same JSON an agent gets from the pipe, so `reye request ComputeWarden.status` needs no docs of its own.
        _sink.Provide("status", _ =>
            {
                using var doc = JsonDocument.Parse(dispatcher.Handle(StatusRequest));
                return doc.RootElement.Clone();
            },
            "machine state, blockers and claims (same as computewarden_status)");

        // A pick is answered by setting the daemon's real value back, so the panel never shows a
        // setting that didn't take (a refused sender, a bad value).
        _sink.OnAction(HoldPick, PickHold);
        _sink.OnAction(GameJobsPick, PickGameJobs);
    }

    /// <summary>Handles the hold switched on the panel. Public so tests can drive it.</summary>
    public object? PickHold(JsonElement? value)
    {
        var sender = RequireMonitor("set the hold");
        var name = value is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
        switch (name)
        {
            case "on":
                _log?.Info($"Hold from {sender} on the monitor panel");
                _warden.SetManualBusy("held from the panel");
                break;
            case "off":
                _log?.Info($"Hold cleared by {sender} on the monitor panel");
                _warden.ClearManualBusy();
                break;
            default:
                throw new ArgumentException($"Unknown hold '{name}' (expected on or off)");
        }
        Publish();
        return new { hold = name };
    }

    /// <summary>Handles how many jobs may run beside a game, picked on the panel. Public so tests can drive it.</summary>
    public object? PickGameJobs(JsonElement? value)
    {
        var sender = RequireMonitor("set the jobs while gaming");
        var name = value is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
        if (!int.TryParse(name, out var jobs) || !GameJobsOptions.Contains(jobs))
            throw new ArgumentException($"Unknown number of jobs '{name}' (expected {string.Join(", ", GameJobsOptions)})");
        _log?.Info($"Jobs while gaming change from {sender} on the monitor panel");
        var set = _warden.SetGameJobs(jobs);
        Publish();
        return new { jobs = set };
    }

    private MonitorSender RequireMonitor(string what)
    {
        // Agents connect to the hub as apps; only a monitor (the user's panel or reye) may do this.
        if (_sink.Sender is { IsMonitor: true } sender) return sender;
        var who = _sink.Sender?.ToString() ?? "an unknown sender";
        _log?.Warn($"Refused a request from {who} to {what}: only the user's monitor may");
        throw new InvalidOperationException($"Only the user's monitor can {what}.");
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
                    var json = JsonSerializer.Serialize(value);
                    if (_sent.TryGetValue(id, out var old) && old == json) continue;
                    _sink.Set(id, value);
                    _sent[id] = json; // only once sent, so a failed send is retried next time
                }
            }
            catch
            {
                // The monitor is optional; it must never take down the daemon.
            }
        }
    }

    /// <summary>The panel's values for one status snapshot.</summary>
    public static IReadOnlyList<(string Id, object Value)> Render(StatusResult status, DateTimeOffset now)
    {
        var claims = status.Claims ?? Array.Empty<ClaimInfo>();
        var values = new List<(string, object)>
        {
            ("state", status.State.ToString().ToLowerInvariant()),
            (HoldPick, status.Blockers.Any(b => b.Type == BlockerType.Manual) ? "on" : "off"),
            ("blockers", status.Blockers.Count == 0 ? "none" : string.Join("\n", status.Blockers.Select(b => b.Description))),
            (ClaimsTable, new Dictionary<string, object> { ["rows"] = ClaimRows(claims, now) }),
        };
        if (status.GameJobs is int jobs) values.Add((GameJobsPick, jobs.ToString()));
        return values;
    }

    /// <summary>One table row per claim: running ones first, then those whose turn has come, then the line in order.</summary>
    private static List<Dictionary<string, object>> ClaimRows(IEnumerable<ClaimInfo> claims, DateTimeOffset now)
        => claims
            .OrderBy(c => c.Phase)
            .ThenBy(c => c.Position ?? 0)
            .Select(c => ClaimRow(c, now))
            .ToList();

    // "Claude Code (CodeCarver)" -> "CodeCarver": the session's name is what tells claims apart.
    private static string ShortOwner(string owner)
    {
        var open = owner.LastIndexOf('(');
        return open > 0 && owner.EndsWith(')') && open < owner.Length - 2 ? owner[(open + 1)..^1] : owner;
    }

    // A claim this long without a check-in may be work that's finished or stuck; the panel says so.
    public static readonly TimeSpan NoCheckInAfter = TimeSpan.FromMinutes(15);

    // Minutes since the claim last checked in (acquired or renewed), e.g. "just now", "4m ago", "1h20m ago".
    private static string Ago(TimeSpan since)
    {
        var m = Math.Max(0, (int)since.TotalMinutes);
        return m == 0 ? "just now" : m < 60 ? $"{m}m ago" : $"{m / 60}h{(m % 60 == 0 ? "" : $"{m % 60}m")} ago";
    }

    private static Dictionary<string, object> ClaimRow(ClaimInfo c, DateTimeOffset now)
    {
        var (state, color) = c.Phase switch
        {
            ClaimPhase.Running => ($"running {Warden.GrantName(c.Grant)}", c.Grant == Grant.Full ? "green" : "orange"),
            ClaimPhase.Ready => ("its turn, not started", "blue"),
            _ => (c.Position is int p ? $"queued #{p}" : "queued", "gray"),
        };
        if (c.Kind == ClaimKind.Solo) state += ", solo";
        if (c.BigMemory) state += ", big memory";

        var row = new Dictionary<string, object>
        {
            ["id"] = c.Id,
            ["who"] = new { text = ShortOwner(c.Owner), tip = c.Owner },
            ["state"] = new { text = state, color },
            ["what"] = c.Description,
        };
        var lease = $"{Math.Max(0, (int)Math.Ceiling((c.ExpiresAt - now).TotalMinutes))}m";
        row["lease"] = lease;
        if (c.LastCheckIn is { } seen)
        {
            var ago = Ago(now - seen);
            row["checked"] = now - seen >= NoCheckInAfter
                ? new { text = ago, color = "warn", tip = $"no check-in for {ago.Replace(" ago", "")}: the work may be finished or stuck" }
                : ago;
        }
        return row;
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
