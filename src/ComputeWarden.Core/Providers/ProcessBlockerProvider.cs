using ComputeWarden.Core.Config;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Time;

namespace ComputeWarden.Core.Providers;

/// <summary>
/// Detects configured intensive processes and exposes them as blockers. Enumeration happens
/// only on <see cref="Refresh"/> (called by the daemon's poll loop); <see cref="GetBlockers"/>
/// returns the last computed snapshot so acquire never triggers a fresh scan.
/// <para>Debounce: a matched process continues to count as a blocker for
/// <see cref="ProcessDetectionOptions.AvailableAfterMs"/> after it disappears, and only
/// becomes a blocker once <see cref="ProcessDetectionOptions.BusyAfterMs"/> has elapsed since
/// it first appeared.</para>
/// </summary>
public sealed class ProcessBlockerProvider : IBlockerProvider
{
    private readonly IProcessLister _lister;
    private readonly IClock _clock;
    private ProcessDetectionOptions _options;
    private Dictionary<string, RuleEntry> _rulesByExe;

    private readonly object _gate = new();
    private readonly Dictionary<string, MatchState> _state = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<Blocker> _snapshot = Array.Empty<Blocker>();
    private bool _confident = true;

    public string Name => "ProcessBlockerProvider";

    public ProcessBlockerProvider(
        IProcessLister lister,
        IEnumerable<ProcessRule> rules,
        ProcessDetectionOptions options,
        IClock clock)
    {
        _lister = lister;
        _clock = clock;
        _options = options;
        _rulesByExe = BuildRuleMap(rules);
    }

    /// <summary>
    /// Swaps in a new rule set and detection options at runtime (config hot-reload). The next
    /// <see cref="Refresh"/> uses them; debounce state for exes no longer configured is dropped
    /// so a removed rule stops blocking promptly. Note: the poll interval lives in the monitor,
    /// not here — changing it still requires a restart.
    /// </summary>
    public void UpdateRules(IEnumerable<ProcessRule> rules, ProcessDetectionOptions options)
    {
        var map = BuildRuleMap(rules);
        lock (_gate)
        {
            _rulesByExe = map;
            _options = options;

            List<string>? stale = null;
            foreach (var key in _state.Keys)
                if (!map.ContainsKey(key))
                    (stale ??= new()).Add(key);
            if (stale is not null)
                foreach (var key in stale)
                    _state.Remove(key);
        }
    }

    private static Dictionary<string, RuleEntry> BuildRuleMap(IEnumerable<ProcessRule> rules)
    {
        var map = new Dictionary<string, RuleEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            var key = Normalize(rule.Executable);
            if (key.Length == 0) continue;
            map[key] = new RuleEntry(rule.Name, rule.Executable);
        }
        return map;
    }

    public ProviderResult GetBlockers()
    {
        lock (_gate)
            return new ProviderResult(_snapshot, _confident);
    }

    /// <summary>
    /// Re-enumerates processes and recomputes the snapshot. Called on the poll interval.
    /// On enumeration failure the provider becomes non-confident (drives Unknown) and reports
    /// no blockers.
    /// </summary>
    public void Refresh()
    {
        IReadOnlyList<RunningProcess> running;
        try
        {
            running = _lister.List();
        }
        catch
        {
            lock (_gate)
            {
                _confident = false;
                _snapshot = Array.Empty<Blocker>();
            }
            return;
        }

        var now = _clock.UtcNow;

        // Snapshot the (immutable, reference-swapped) rule map once so a concurrent hot-reload
        // can't tear a single poll.
        var ruleMap = _rulesByExe;

        // Group matched processes by normalized executable name.
        var matched = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in running)
        {
            var key = Normalize(proc.Name);
            if (!ruleMap.ContainsKey(key)) continue;
            if (!matched.TryGetValue(key, out var pids))
            {
                pids = new List<int>();
                matched[key] = pids;
            }
            pids.Add(proc.Pid);
        }

        lock (_gate)
        {
            _confident = true;
            var opts = _options;

            // Refresh / create state for currently-present matches.
            foreach (var (key, pids) in matched)
            {
                if (_state.TryGetValue(key, out var state))
                {
                    state.LastSeen = now;
                    state.Pids = pids;
                }
                else if (ruleMap.TryGetValue(key, out var rule))
                {
                    _state[key] = new MatchState(rule.RuleName, rule.Executable, now, now, pids);
                }
            }

            // Drop entries whose linger window (available_after) has elapsed.
            List<string>? stale = null;
            foreach (var (key, state) in _state)
            {
                if (matched.ContainsKey(key)) continue;
                if ((now - state.LastSeen).TotalMilliseconds >= opts.AvailableAfterMs)
                    (stale ??= new()).Add(key);
            }
            if (stale is not null)
                foreach (var key in stale)
                    _state.Remove(key);

            // Build snapshot: an entry blocks once busy_after has elapsed since it first appeared.
            var blockers = new List<Blocker>(_state.Count);
            foreach (var (key, state) in _state)
            {
                if ((now - state.FirstSeen).TotalMilliseconds < opts.BusyAfterMs) continue;
                blockers.Add(BuildBlocker(state, present: matched.ContainsKey(key)));
            }

            _snapshot = blockers;
        }
    }

    private Blocker BuildBlocker(MatchState state, bool present) => new(
        Id: $"process:{state.Executable}",
        Type: BlockerType.Process,
        Source: Name,
        Description: $"{state.RuleName} ({state.Executable}) is running",
        Owner: null,
        CreatedAt: state.FirstSeen,
        ExpiresAt: null,
        Metadata: new Dictionary<string, string>
        {
            ["executable"] = state.Executable,
            ["pids"] = string.Join(",", state.Pids),
            ["present"] = present ? "true" : "false",
        });

    private static string Normalize(string? executable)
    {
        var value = executable?.Trim() ?? string.Empty;
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];
        return value;
    }

    private readonly record struct RuleEntry(string RuleName, string Executable);

    private sealed class MatchState
    {
        public MatchState(string ruleName, string executable, DateTimeOffset firstSeen, DateTimeOffset lastSeen, IReadOnlyList<int> pids)
        {
            RuleName = ruleName;
            Executable = executable;
            FirstSeen = firstSeen;
            LastSeen = lastSeen;
            Pids = pids;
        }

        public string RuleName { get; }
        public string Executable { get; }
        public DateTimeOffset FirstSeen { get; }
        public DateTimeOffset LastSeen { get; set; }
        public IReadOnlyList<int> Pids { get; set; }
    }
}
