// MonitorBridge.cs — copied from RelentlessMonitor (examples/optional/csharp/OptionalApp, v1.0.0)
// so the daemon can feed RelentlessMonitor WITHOUT referencing it. Local changes: the namespace and
// the IMonitorSink interface; keep the rest in step with upstream.
//
// If RelentlessMonitor.Client.dll can be found at runtime, the daemon shows up in the monitor. If it
// can't (or the hub isn't running), every call here does nothing — the daemon runs exactly the same.
//
// Where it looks for the DLL, first match wins:
//   1. already loaded (your app references the RelentlessMonitor.Client package after all)
//   2. %RELENTLESSMONITOR_CLIENT%          (the DLL itself, or the folder it's in)
//   3. next to your app                    (AppContext.BaseDirectory)
//   4. %LOCALAPPDATA%\RelentlessMonitor\client   (publish.ps1 -InstallClient puts it there)
//
// Usage:
//   using var monitor = MonitorBridge.Create("MyApp", new
//   {
//       title = "My App",
//       sections = new[] { new { title = "Job", widgets = new object[]
//       {
//           new { id = "stage", kind = "text", label = "Stage" },
//           new { id = "cancel", kind = "button", label = "Cancel" },
//       } } },
//   });
//   monitor.OnAction("cancel", () => cts.Cancel());   // handlers before Start so the hub lists them
//   monitor.Start();
//   monitor.Set("stage", "parsing");
//
// The layout is plain data in the shape docs/PROTOCOL.md describes, so you never touch the
// client's types. Unknown/old DLL versions just leave the bridge switched off (see Status).

#nullable enable
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace ComputeWarden.Daemon;

/// <summary>Optional RelentlessMonitor feed. Every member is safe to call whether or not the monitor exists.</summary>
public sealed class MonitorBridge : IMonitorSink
{
    private const string AssemblyName = "RelentlessMonitor.Client";

    private readonly object? _client;
    private readonly Type? _type;

    private MonitorBridge(object? client, Type? type, string status)
    {
        _client = client;
        _type = type;
        Status = status;
    }

    /// <summary>True when the client DLL was found and the client created (the hub may still be down).</summary>
    public bool Enabled => _client is not null;

    /// <summary>What happened at start, e.g. "client 1.0.0 from C:\…" or "RelentlessMonitor.Client.dll not found".</summary>
    public string Status { get; }

    /// <summary>True while connected to a hub.</summary>
    public bool Connected => Try(() => (bool)_type!.GetProperty("Connected")!.GetValue(_client)!, false);

    /// <summary>
    /// Finds the client; call <see cref="Start"/> after registering handlers. <paramref name="layout"/> is any object (or JSON
    /// string) shaped like a layout: <c>{ title, sections: [ { title, columns, widgets: [ { id, kind, label, … } ] } ] }</c>.
    /// </summary>
    public static MonitorBridge Create(string app, object layout, string? instance = null, string? hub = null, string? token = null)
    {
        try
        {
            var asm = FindClient(out var where);
            if (asm is null) return new(null, null, $"{AssemblyName}.dll not found");

            var clientType = asm.GetType("RelentlessMonitor.MonitorClient", throwOnError: true)!;
            var layoutType = asm.GetType("RelentlessMonitor.Wire.LayoutDef", throwOnError: true)!;
            var optionsType = asm.GetType("RelentlessMonitor.MonitorClientOptions", throwOnError: true)!;
            var json = (JsonSerializerOptions)asm.GetType("RelentlessMonitor.Wire.Protocol", throwOnError: true)!
                .GetField("Json")!.GetValue(null)!;

            var layoutJson = layout as string ?? JsonSerializer.Serialize(layout, json);
            var layoutDef = JsonSerializer.Deserialize(layoutJson, layoutType, json);

            var options = Activator.CreateInstance(optionsType)!;
            void Opt(string name, string? value) { if (value is not null) optionsType.GetProperty(name)?.SetValue(options, value); }
            Opt("Instance", instance);
            Opt("Hub", hub);
            Opt("Token", token);

            var client = Activator.CreateInstance(clientType, app, layoutDef, options)!;
            return new(client, clientType, $"client {asm.GetName().Version} from {where}");
        }
        catch (Exception e)
        {
            return new(null, null, $"monitor disabled: {Unwrap(e).Message}");
        }
    }

    /// <summary>Starts looking for the hub in the background. Returns immediately.</summary>
    public void Start() => Call("Start", Type.EmptyTypes);

    /// <summary>Sets a widget's value (charts and feeds append it).</summary>
    public void Set(string id, object? value) => Call("Set", [typeof(string), typeof(object)], id, value);

    /// <summary>Adds a line to a feed widget. <paramref name="level"/>: info, warn, error, debug.</summary>
    public void Log(string id, string text, string level = "info") => Call("Log", [typeof(string), typeof(string), typeof(string)], id, text, level);

    /// <summary>Runs when the monitor clicks a button (or flips a toggle). Return a result to show, or throw to report an error.</summary>
    public void OnAction(string widgetId, Func<JsonElement?, object?> handler)
        => Call("OnAction", [typeof(string), typeof(Func<JsonElement?, object?>)], widgetId, handler);

    /// <summary>Button handler with nothing to return.</summary>
    public void OnAction(string widgetId, Action handler) => OnAction(widgetId, _ => { handler(); return null; });

    /// <summary>Runs when a toggle is flipped; the toggle then shows the new value.</summary>
    public void OnToggle(string widgetId, Action<bool> handler) => Call("OnToggle", [typeof(string), typeof(Action<bool>)], widgetId, handler);

    /// <summary>Answers requests from other apps and tools (<c>rmon request MyApp.name</c>).</summary>
    public void Provide(string name, Func<JsonElement?, object?> handler, string? description = null)
        => Call("Provide", [typeof(string), typeof(Func<JsonElement?, object?>), typeof(string)], name, handler, description);

    /// <summary>Sends what's queued (up to the client's flush timeout) and disconnects.</summary>
    public void Dispose()
    {
        if (_client is IAsyncDisposable d) Try(() => d.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)), false);
    }

    // ---- plumbing ----

    private void Call(string method, Type[] signature, params object?[] args)
    {
        if (_client is null) return;
        Try(() => { _type!.GetMethod(method, signature)?.Invoke(_client, args); return true; }, false);
    }

    private static T Try<T>(Func<T> f, T fallback)
    {
        try { return f(); }
        catch { return fallback; } // the monitor must never break the app
    }

    private static Exception Unwrap(Exception e) => e is TargetInvocationException { InnerException: { } inner } ? inner : e;

    private static Assembly? FindClient(out string where)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            if (a.GetName().Name == AssemblyName) { where = "the app"; return a; }

        foreach (var path in Candidates())
        {
            if (!File.Exists(path)) continue;
            where = path;
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
        }
        where = "";
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        var file = AssemblyName + ".dll";
        if (Environment.GetEnvironmentVariable("RELENTLESSMONITOR_CLIENT") is { Length: > 0 } env)
            yield return env.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? env : Path.Combine(env, file);
        yield return Path.Combine(AppContext.BaseDirectory, file);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelentlessMonitor", "client", file);
    }
}
