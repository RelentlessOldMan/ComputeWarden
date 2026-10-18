using System.Diagnostics;

namespace ComputeWarden.Client;

/// <summary>
/// Locates and launches the ComputeWarden daemon detached, so it outlives this adapter and
/// becomes the machine-wide authority. The daemon's own single-instance mutex makes a
/// duplicate launch harmless (the extra process exits immediately). Used by the MCP adapter
/// and by cw, both of which ship next to the daemon.
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
            var workingDirectory = Path.GetDirectoryName(fileName) is { Length: > 0 } d && Directory.Exists(d)
                ? d
                : AppContext.BaseDirectory;

            // Shell execute, not CreateProcess: CreateProcess would hand the daemon our inheritable
            // handles, including the MCP host's stdio pipes, and the daemon lives until reboot, so
            // the host would never see this adapter's stdout close.
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = workingDirectory,
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

        // 2. Alongside this exe (the normal published layout). Check the exe's own folder first:
        //    a self-extracting single-file app's BaseDirectory is its temp extraction folder.
        var exeName = OperatingSystem.IsWindows() ? "ComputeWarden.Daemon.exe" : "ComputeWarden.Daemon";
        foreach (var dir in SearchDirectories())
        {
            var exe = Path.Combine(dir, exeName);
            if (File.Exists(exe)) return (exe, Array.Empty<string>());
        }

        // 3. Framework-dependent build: run the dll via the dotnet host.
        foreach (var dir in SearchDirectories())
        {
            var dll = Path.Combine(dir, "ComputeWarden.Daemon.dll");
            if (File.Exists(dll)) return ("dotnet", new[] { dll });
        }

        return (null, Array.Empty<string>());
    }

    private static IEnumerable<string> SearchDirectories()
    {
        if (Path.GetDirectoryName(Environment.ProcessPath) is { Length: > 0 } exeDir) yield return exeDir;
        yield return AppContext.BaseDirectory;
    }

    private static (string?, string[]) FromPath(string path)
    {
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return ("dotnet", new[] { path });
        return File.Exists(path) ? (path, Array.Empty<string>()) : (null, Array.Empty<string>());
    }
}
