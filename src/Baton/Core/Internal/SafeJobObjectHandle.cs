using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Baton.Core.Internal;

/// <summary>
/// Thin P/Invoke wrapper over the Windows Job Object API, used by <see cref="BatonTask"/> to hold the
/// spawned process's entire descendant tree (the "no orphans" guarantee aer-core's M3 milestone
/// established) — this is Win32 surface, not the aer-core Rust FFI that CLAUDE.md's Architecture
/// Rule 3 (P/Invoke Layer) scopes to the deleted ABI; CreateJobObject/AssignProcessToJobObject/
/// TerminateJobObject calling Windows directly is one of the exceptions it enumerates.
/// </summary>
/// <remarks>
/// A <see cref="SafeHandle"/>, not a raw <c>nint</c>, for the same reason the deleted FFI binding used
/// one: every native call below add-refs the handle for its duration, so a concurrent
/// <see cref="Dispose"/> cannot race a call already in flight into a use-after-close.
/// </remarks>
internal sealed class SafeJobObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeJobObjectHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    /// <summary>
    /// Creates a Job Object configured with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: when the last
    /// handle to the job closes, every process still in it is terminated — the mechanism aer-core's
    /// behavioral spec named for Windows process-tree containment.
    /// </summary>
    public static SafeJobObjectHandle Create()
    {
        SafeJobObjectHandle job = CreateJobObjectW(nint.Zero, null);
        if (job.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            job.SetHandleAsInvalid();
            throw new System.ComponentModel.Win32Exception(error, "CreateJobObjectW failed.");
        }

        JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = default;
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(
            job,
            JOBOBJECTINFOCLASS.ExtendedLimitInformation,
            ref info,
            (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            int error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new System.ComponentModel.Win32Exception(error, "SetInformationJobObject failed.");
        }

        return job;
    }

    /// <summary>Assigns a live process to this job. Returns false (does not throw) on failure.</summary>
    public bool TryAssign(SafeProcessHandle process) => AssignProcessToJobObject(this, process);

    /// <summary>
    /// Kills every process currently in the job. There is no graceful phase on Windows —
    /// <c>TerminateJobObject</c> is unconditional and immediate; unlike Unix's SIGTERM-then-SIGKILL
    /// escalation there is nothing to wait out here. Errors are
    /// swallowed: the job may already be empty (a prior timeout/cancel/natural-exit teardown already
    /// terminated it), which is harmless.
    /// </summary>
    public void Terminate() => TerminateJobObject(this, 1);

    /// <summary>
    /// True if any process in the job is still alive — including a grandchild outstanding after the
    /// root has exited: the whole tree, not just the root, must be gone. On query failure,
    /// fails toward "alive": killing an already-dead tree is harmless, skipping a kill on a live one
    /// is not (mirrors aer-core's <c>tree_alive</c>).
    /// </summary>
    public bool IsTreeAlive()
    {
        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info = default;
        bool ok = QueryInformationJobObject(
            this,
            JOBOBJECTINFOCLASS.BasicAccountingInformation,
            ref info,
            (uint)Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(),
            out _);
        return !ok || info.ActiveProcesses > 0;
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private enum JOBOBJECTINFOCLASS
    {
        BasicAccountingInformation = 1,
        ExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeJobObjectHandle CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        SafeJobObjectHandle hJob,
        JOBOBJECTINFOCLASS jobObjectInformationClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        SafeJobObjectHandle hJob,
        JOBOBJECTINFOCLASS jobObjectInformationClass,
        ref JOBOBJECT_BASIC_ACCOUNTING_INFORMATION lpJobObjectInformation,
        uint cbJobObjectInformationLength,
        out uint lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeJobObjectHandle hJob, SafeProcessHandle hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeJobObjectHandle hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
