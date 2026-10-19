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
/// Optional live view in RelentlessEye: machine state, mode, blockers and claims, refreshed
/// every couple of seconds, plus a <c>status</c> request that answers like the pipe's STATUS.
/// Daemon log lines reach the panel's feed through <see cref="MonitorLog"/>. The user can also set
/// the mode and how many jobs may run beside a game from the panel; only a monitor may, never another app, since the mode is the user's
/// call. The same goes for reordering the jobs waiting to start: pick one in the claims table,
/// then move it. With no monitor installed the sink is disabled and none of this runs.
/// </summary>
public sealed class MonitorPublisher : IAsyncDisposable
{
    public const string App = "ComputeWarden";
    public const string EventsFeed = "events";

    // Before Layout, which reads them: static initializers run in order.

    private static readonly (string Id, string Label, LineMove Move)[] LineButtons =
    {
        ("line_front", "Move picked to front", LineMove.Front), ("line_up", "Move up", LineMove.Up), ("line_down", "Move down", LineMove.Down),
    };

    private static readonly int[] GameJobsOptions = { 0, 1, 2, 3, 4 };

    private static readonly (ResourceSet Set, string Key)[] ResourceColumns =
    {
        (ResourceSet.Cpu, "cpu"), (ResourceSet.Gpu, "gpu"), (ResourceSet.Ram, "ram"), (ResourceSet.Network, "net"), (ResourceSet.Disk, "disk"),
    };

    private static readonly object[] ClaimColumns = new object[]
        {
            new { key = "who", label = "Claim" },
            new { key = "state", label = "State" },
        }
        .Concat(ResourceColumns.Select(r => (object)new { key = r.Key, label = r.Key, align = "center" }))
        .Concat(new object[]
        {
            new { key = "lease", label = "Lease", align = "right" },
            new { key = "checked", label = "Last check", align = "right" },
            new { key = "what", label = "Doing" }, // last: takes the rest of the width, cut with a tooltip
        })
        .ToArray();

    /// <summary>The panel with nobody waiting, as plain data in RelentlessEye's layout shape.</summary>
    public static object Layout { get; } = BuildLayout(waiting: false);

    /// <summary>The panel. The move buttons are there only while jobs are waiting to start.</summary>
    public static object BuildLayout(bool waiting) => new
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
                    new { id = ModePick, kind = "choice", label = "Mode",
                          states = new { auto = "green", steady = "orange", open = "accent", hold = "red" },
                          options = new object[]
                          {
                              "auto",
                              "steady",
                              new { value = "open", label = "open", confirm = "Open: agents stop throttling each other (games still win). Set open?" },
                              new { value = "hold", label = "hold", confirm = "Hold: no new work starts and running work is throttled. Set hold?" },
                          } },
                    new { id = "busy", kind = "text", label = "Busy" },
                    new { id = ModeDetail, kind = "text", label = "Now" },
                    new { id = GameJobsPick, kind = "choice", label = "Jobs while gaming",
                          options = GameJobsOptions.Select(n => (object)n.ToString()).ToArray() },
                    new { id = "blockers", kind = "text", label = "Blockers" },
                },
            },
            new
            {
                title = "Claims",
                columns = 3,
                widgets = new object[]
                    {
                        new { id = ClaimsTable, kind = "table", pick = true, span = 3, columns = ClaimColumns },
                    }
                    .Concat(waiting ? LineButtons.Select(b => (object)new { id = b.Id, kind = "button", label = b.Label }) : Array.Empty<object>())
                    .ToArray(),
            },
            new
            {
                title = "Log",
                widgets = new object[] { new { id = EventsFeed, kind = "feed", limit = 200 } },
            },
        },
    };

    public const string ModePick = "mode";
    public const string GameJobsPick = "game_jobs";
    public const string ModeDetail = "mode_detail";
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

    // The waiting job picked in the claims table, and whether the move buttons are showing.
    private string? _pickJob;
    private bool _sentWaiting;

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
            "machine state, mode, blockers and claims (same as computewarden_status)");

        // The pick is answered by setting the daemon's real mode back, so the panel never shows a
        // mode that didn't take (a refused sender, a bad value).
        _sink.OnAction(ModePick, PickMode);
        _sink.OnAction(GameJobsPick, PickGameJobs);
        _sink.OnPick(ClaimsTable, id =>
        {
            RequireMonitor("pick a job in the line");
            if (!_warden.GetWaiting().Any(w => w.Id == id))
                throw new InvalidOperationException("Only a job waiting to start can be moved: pick a queued one.");
            lock (_gate) _pickJob = id;
        });
        foreach (var (id, _, move) in LineButtons) _sink.OnAction(id, _ => MoveJob(move));
    }

    /// <summary>Moves the job picked in the claims table. Public so tests can drive it.</summary>
    public object? MoveJob(LineMove move)
    {
        var sender = RequireMonitor("reorder the line");
        string? id;
        lock (_gate) id = _pickJob;
        if (id is null) throw new InvalidOperationException("Pick a waiting job first.");
        _log?.Info($"Line change from {sender} on the monitor panel");
        var place = _warden.MoveInLine(id, move) ?? throw new InvalidOperationException("That job isn't waiting any more.");
        Publish();
        return new { place };
    }

    /// <summary>Handles a mode picked on the panel. Public so tests can drive it.</summary>
    public object? PickMode(JsonElement? value)
    {
        var sender = RequireMonitor("set the mode");
        var name = value is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
        var mode = name switch
        {
            "auto" => UserMode.Auto,
            "steady" => UserMode.Steady,
            "open" => UserMode.Open,
            "hold" => UserMode.Hold,
            _ => throw new ArgumentException($"Unknown mode '{name}' (expected auto, steady, open, or hold)"),
        };

        // The panel keeps it simple: the whole machine, until changed. `cw mode` can narrow it.
        _log?.Info($"Mode change from {sender} on the monitor panel");
        var set = _warden.SetMode(mode);
        Publish();
        return new { mode = Warden.ModeName(set.Mode), resources = Resources.ToNames(set.Resources), expiresAt = set.ExpiresAt };
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
                var waiting = _warden.GetWaiting();
                if (waiting.Count > 0 != _sentWaiting)
                {
                    _sink.SetLayout(BuildLayout(waiting.Count > 0));
                    _sentWaiting = waiting.Count > 0;
                }
                if (_pickJob is not null && !waiting.Any(w => w.Id == _pickJob)) _pickJob = null; // it started or left

                foreach (var (id, value) in Render(_warden.GetStatus(), _clock.UtcNow, waiting, _pickJob, _warden.GameJobs))
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
    public static IReadOnlyList<(string Id, object Value)> Render(
        StatusResult status, DateTimeOffset now, IReadOnlyList<ClaimInfo>? waiting = null, string? picked = null, int? gameJobs = null)
    {
        var places = (waiting ?? Array.Empty<ClaimInfo>()).Select((c, i) => (c.Id, Place: i + 1)).ToDictionary(p => p.Id, p => p.Place);
        var claims = status.Claims ?? Array.Empty<ClaimInfo>();
        var values = new List<(string, object)>
        {
            ("state", status.State.ToString().ToLowerInvariant()),
            (ModePick, Warden.ModeName(status.Mode?.Mode ?? UserMode.Auto)),
            (ModeDetail, ModeText(status.Mode)),
            ("busy", status.BusyResources == ResourceSet.None ? "none" : string.Join(", ", Resources.ToNames(status.BusyResources))),
            ("blockers", status.Blockers.Count == 0 ? "none" : string.Join("\n", status.Blockers.Select(b => b.Description))),
            (ClaimsTable, ClaimsValue(ClaimRows(claims, now, places), picked)),
        };
        if (gameJobs is int jobs) values.Add((GameJobsPick, jobs.ToString()));
        return values;
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

    // The table's value: its rows, and the picked row when there is one (leaving it out clears it).
    private static Dictionary<string, object> ClaimsValue(List<Dictionary<string, object>> rows, string? picked)
    {
        var value = new Dictionary<string, object> { ["rows"] = rows };
        if (picked is not null) value["picked"] = picked;
        return value;
    }

    /// <summary>
    /// One table row per claim: running ones first, then those whose turn has come, then the line in
    /// order. A dot under each resource the claim needs, colored by where the claim is.
    /// </summary>
    private static List<Dictionary<string, object>> ClaimRows(IEnumerable<ClaimInfo> claims, DateTimeOffset now, IReadOnlyDictionary<string, int> places)
        => claims
            .OrderBy(c => c.Phase)
            .ThenBy(c => places.TryGetValue(c.Id, out var p) ? p : 0)
            .Select(c => ClaimRow(c, now, places.TryGetValue(c.Id, out var p) ? p : null))
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

    private static Dictionary<string, object> ClaimRow(ClaimInfo c, DateTimeOffset now, int? place)
    {
        var (state, color) = c.Phase switch
        {
            ClaimPhase.Running => ($"running {Warden.GrantName(c.Grant)}", c.Grant == Grant.Full ? "green" : "orange"),
            ClaimPhase.Ready => ("its turn, not started", "blue"),
            _ => (place is int p ? $"queued #{p}" : "queued", "gray"),
        };
        if (c.Kind != ClaimKind.Shared) state += $", {Warden.KindName(c.Kind)}";

        var row = new Dictionary<string, object>
        {
            ["id"] = c.Id,
            ["who"] = new { text = ShortOwner(c.Owner), tip = c.Owner },
            ["state"] = new { text = state, color },
            ["what"] = c.Description,
        };
        foreach (var (set, key) in ResourceColumns)
            if ((c.Resources & set) != 0) row[key] = new { dot = true, color, tip = $"{key}: {state}" };

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
