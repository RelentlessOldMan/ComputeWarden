using System.Diagnostics;

namespace ComputeWarden.Mcp;

/// <summary>
/// Locates and launches the ComputeWarden daemon detached, so it outlives this adapter and
/// becomes the machine-wide authority. The daemon's own single-instance mutex makes a
/// duplicate launch harmless (the extra process exits immediately).
/// </summary>
public static class DaemonLauncher
{
    /// <summary>Best-effort launch. Returns true if a process was started.</summary>
    public static bool TryLaunch()
    {
        try
        {
            var (fileName, arguments) = Resolve();
            if (fileName is null) return false;

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach (var arg in arguments) psi.ArgumentList.Add(arg);

            return Process.Start(psi) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Where the daemon would be launched from; internal for tests.</summary>
    internal static (string? fileName, string[] arguments) Resolve()
    {
        // 1. Explicit override.
        var overridePath = Environment.GetEnvironmentVariable("COMPUTEWARDEN_DAEMON");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return FromPath(overridePath);

        // 2. Alongside this adapter (the normal published layout).
        var dir = AppContext.BaseDirectory;
        var exeName = OperatingSystem.IsWindows() ? "ComputeWarden.Daemon.exe" : "ComputeWarden.Daemon";
        var exe = Path.Combine(dir, exeName);
        if (File.Exists(exe)) return (exe, Array.Empty<string>());

        // 3. Framework-dependent build: run the dll via the dotnet host.
        var dll = Path.Combine(dir, "ComputeWarden.Daemon.dll");
        if (File.Exists(dll)) return ("dotnet", new[] { dll });

        return (null, Array.Empty<string>());
    }

    private static (string?, string[]) FromPath(string path)
    {
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return ("dotnet", new[] { path });
        return File.Exists(path) ? (path, Array.Empty<string>()) : (null, Array.Empty<string>());
    }
}
