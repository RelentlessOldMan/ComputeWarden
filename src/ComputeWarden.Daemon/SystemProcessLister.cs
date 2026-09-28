using System.Diagnostics;
using ComputeWarden.Core.Providers;

namespace ComputeWarden.Daemon;

/// <summary>
/// Enumerates running processes using only cheap fields (id + name). Deliberately avoids
/// touching per-process modules, memory, or start time so polling stays negligible.
/// </summary>
public sealed class SystemProcessLister : IProcessLister
{
    public IReadOnlyList<RunningProcess> List()
    {
        var processes = Process.GetProcesses();
        var result = new List<RunningProcess>(processes.Length);
        foreach (var p in processes)
        {
            try
            {
                result.Add(new RunningProcess(p.Id, p.ProcessName));
            }
            catch
            {
                // Process exited between enumeration and access; skip it.
            }
            finally
            {
                p.Dispose();
            }
        }
        return result;
    }
}
