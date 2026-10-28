using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ComputeWarden.Cli;

/// <summary>
/// A command run inside its own Windows job object, so the job's limits reach the command and
/// everything it starts (a test host, a compiler server), including processes already running.
/// The process starts suspended and joins the job before its first instruction, so nothing it
/// spawns escapes. Closing the job (cw exiting, or being killed) ends the whole tree: work that
/// outlived its claim would be work nobody accounted for.
/// </summary>
internal sealed class ThrottledJob : IDisposable
{
    private readonly SafeFileHandle _job;
    private readonly SafeWaitHandle _process;
    private bool _limited;

    public int ProcessId { get; }

    private ThrottledJob(SafeFileHandle job, SafeWaitHandle process, int processId)
    {
        _job = job;
        _process = process;
        ProcessId = processId;
    }

    /// <summary>Starts <paramref name="commandLine"/> in a new job, with this process's console and standard handles.</summary>
    /// <param name="environment">Variables set for the command on top of this process's own (null: just this process's).</param>
    /// <exception cref="Win32Exception">The command couldn't be started (not found, access denied).</exception>
    public static ThrottledJob Start(string commandLine, IReadOnlyDictionary<string, string>? environment = null)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception();
        try
        {
            SetLimits(job, Native.KillOnJobClose, 0, 0);
            var si = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                dwFlags = Native.StartfUseStdHandles,
                hStdInput = GetStdHandle(Native.StdInput),
                hStdOutput = GetStdHandle(Native.StdOutput),
                hStdError = GetStdHandle(Native.StdError),
            };
            var block = environment is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(EnvironmentBlock(environment));
            ProcessInformation pi;
            try
            {
                if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true,
                        Native.CreateSuspended | Native.CreateUnicodeEnvironment, block, null, ref si, out pi))
                    throw new Win32Exception();
            }
            finally
            {
                if (block != IntPtr.Zero) Marshal.FreeHGlobal(block);
            }

            var process = new SafeWaitHandle(pi.hProcess, ownsHandle: true);
            using var thread = new SafeWaitHandle(pi.hThread, ownsHandle: true);
            if (!AssignProcessToJobObject(job, process))
            {
                var error = new Win32Exception();
                TerminateProcess(process, 1);
                process.Dispose();
                throw error;
            }
            ResumeThread(thread);
            return new ThrottledJob(job, process, pi.dwProcessId);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    /// <summary>
    /// This process's variables with <paramref name="overrides"/> on top, as CreateProcess wants
    /// them: sorted name=value strings, each ending in a null, then one more (the marshaller adds it).
    /// </summary>
    internal static string EnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            vars[(string)e.Key] = (string?)e.Value ?? "";
        foreach (var (name, value) in overrides) vars[name] = value;
        var sb = new StringBuilder();
        foreach (var (name, value) in vars) sb.Append(name).Append('=').Append(value).Append('\0');
        return sb.ToString();
    }

    /// <summary>Waits for the command (not its children) to exit and returns its exit code.</summary>
    public Task<int> WaitForExitAsync()
    {
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new ProcessWaitHandle(_process);
        RegisteredWaitHandle? registration = null;
        registration = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) =>
        {
            registration?.Unregister(null);
            done.TrySetResult(GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : 1);
        }, null, Timeout.Infinite, executeOnlyOnce: true);
        return done.Task;
    }

    /// <summary>Holds every process in the job to <paramref name="cores"/> cores at <paramref name="priority"/>.</summary>
    public void Throttle(int cores, string? priority)
    {
        SetLimits(_job, Native.KillOnJobClose | Native.LimitAffinity | Native.LimitPriorityClass,
            PickCores(cores, Environment.ProcessorCount, Random.Shared), PriorityClass(priority));
        _limited = true;
    }

    /// <summary>Back to every core at normal priority. A job never throttled is left as its command set itself.</summary>
    public void Unthrottle()
    {
        if (!_limited) return;
        SetLimits(_job, Native.KillOnJobClose | Native.LimitAffinity | Native.LimitPriorityClass,
            AllCores(Environment.ProcessorCount), Native.NormalPriority);
    }

    /// <summary>
    /// <paramref name="count"/> cores picked at random from all but core 0 (which the system and
    /// the user's foreground work lean on), so several throttled jobs spread out.
    /// </summary>
    internal static ulong PickCores(int count, int processors, Random random)
    {
        processors = Math.Clamp(processors, 1, 64);
        if (processors == 1) return 1;
        var candidates = Enumerable.Range(1, processors - 1).OrderBy(_ => random.Next()).Take(Math.Clamp(count, 1, processors - 1));
        return candidates.Aggregate(0UL, (mask, core) => mask | 1UL << core);
    }

    internal static ulong AllCores(int processors)
    {
        processors = Math.Clamp(processors, 1, 64);
        return processors == 64 ? ulong.MaxValue : (1UL << processors) - 1;
    }

    internal static uint PriorityClass(string? priority) => priority?.Replace("-", "_").ToLowerInvariant() switch
    {
        "idle" or "low" => Native.IdlePriority,
        "normal" => Native.NormalPriority,
        _ => Native.BelowNormalPriority,
    };

    private static void SetLimits(SafeFileHandle job, uint flags, ulong affinity, uint priorityClass)
    {
        var info = new ExtendedLimitInformation
        {
            BasicLimitInformation = new BasicLimitInformation
            {
                LimitFlags = flags,
                Affinity = (UIntPtr)affinity,
                PriorityClass = priorityClass,
            },
        };
        if (!SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimitInformation>()))
            throw new Win32Exception();
    }

    public void Dispose()
    {
        _process.Dispose();
        _job.Dispose();
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeWaitHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }

    // ---- Win32 ----------------------------------------------------------

    private static class Native
    {
        public const uint KillOnJobClose = 0x2000, LimitAffinity = 0x10, LimitPriorityClass = 0x20;
        public const uint CreateSuspended = 0x4, CreateUnicodeEnvironment = 0x400;
        public const int StartfUseStdHandles = 0x100;
        public const int StdInput = -10, StdOutput = -11, StdError = -12;
        public const int JobObjectExtendedLimitInformation = 9;
        public const uint IdlePriority = 0x40, BelowNormalPriority = 0x4000, NormalPriority = 0x20;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeWaitHandle process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcess(string? applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeWaitHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeWaitHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int which);
}
