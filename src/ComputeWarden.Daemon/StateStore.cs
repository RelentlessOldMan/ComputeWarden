using System.Text.Json;
using System.Text.Json.Serialization;
using ComputeWarden.Core;
using ComputeWarden.Core.Diagnostics;
using ComputeWarden.Core.Model;

namespace ComputeWarden.Daemon;

/// <summary>
/// Keeps the warden's claims, mode and manual hold on disk so a daemon restart (an install, a
/// crash) doesn't drop them. Installs kill the daemon outright, so there's no saving at exit:
/// the state is written within a second of any change, to a temp file swapped in whole, so
/// the file is never half-written.
/// <para>The file holds claim ids, which are bearer tokens, so it lives in the user's own
/// profile (%LOCALAPPDATA%), not ProgramData.</para>
/// </summary>
public sealed class StateStore : IAsyncDisposable
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComputeWarden", "state.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly string _path;
    private readonly Warden _warden;
    private readonly ILog _log;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private Timer? _timer;
    private string? _written;
    private bool _failing;

    public StateStore(string path, Warden warden, ILog log, TimeSpan? interval = null)
    {
        _path = path;
        _warden = warden;
        _log = log;
        _interval = interval ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// Restores the last run's state, if any. A file that can't be read is moved aside (so it
    /// isn't overwritten and can be looked at) and the daemon starts empty.
    /// </summary>
    public void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var text = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize<SavedState>(text, Json)
                        ?? throw new JsonException("the file is empty");
            if (state.V != SavedState.Version)
                throw new JsonException($"version {state.V}, expected {SavedState.Version}");
            _warden.Restore(state);
            _written = text;
        }
        catch (Exception ex)
        {
            var aside = _path + ".bad";
            try { File.Move(_path, aside, overwrite: true); } catch { }
            _log.Warn($"Couldn't restore the last run's claims ({ex.Message}); starting empty. The file was moved to {aside}");
        }
    }

    public void Start() => _timer = new Timer(_ => Save(), null, _interval, _interval);

    /// <summary>Writes the current state if it changed since the last write. Public so tests can drive it.</summary>
    public void Save()
    {
        lock (_gate)
        {
            try
            {
                var text = JsonSerializer.Serialize(_warden.Save(), Json);
                if (text == _written) return;
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, text);
                File.Move(tmp, _path, overwrite: true);
                _written = text;
                if (_failing) _log.Info("Saving claims works again");
                _failing = false;
            }
            catch (Exception ex)
            {
                // Log once per run of failures, not every second.
                if (!_failing) _log.Error($"Couldn't save claims to {_path}; a restart would drop them", ex);
                _failing = true;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_timer is not null) await _timer.DisposeAsync();
        Save();
    }
}
