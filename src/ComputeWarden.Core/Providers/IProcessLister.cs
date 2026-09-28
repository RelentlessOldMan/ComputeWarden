namespace ComputeWarden.Core.Providers;

/// <summary>A running process, reduced to the cheap-to-obtain fields we need.</summary>
public readonly record struct RunningProcess(int Pid, string Name);

/// <summary>
/// OS seam for enumerating processes. Kept as an interface so the core stays OS-agnostic
/// (a future Linux implementation slots in here) and tests can supply a fake. Implementations
/// should read only what is cheap (executable name), avoiding expensive per-process inspection.
/// May throw; callers treat a throw as a confidence failure.
/// </summary>
public interface IProcessLister
{
    IReadOnlyList<RunningProcess> List();
}
