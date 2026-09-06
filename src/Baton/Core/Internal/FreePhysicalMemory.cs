using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Baton.Core.Internal;

/// <summary>
/// Thin P/Invoke wrapper over <c>GlobalMemoryStatusEx</c> — the free-physical-memory reading the
/// conductor queue's floor gate is compared against (#1934 slice 1). Like
/// <see cref="SafeJobObjectHandle"/> beside it this is Win32 surface, not the deleted aer-core Rust
/// FFI that CLAUDE.md's Architecture Rule 3 scopes; the rule names the two exceptions it allows and
/// this is the second.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never throws, and null is a real answer.</b> Off Windows there is no reading at all (this whole
/// deployment is one Windows box, #1405), and on Windows the call can still fail. Either way the
/// caller gets null and, per <c>QueueScheduler.Decide</c>'s <c>freeGb</c> contract, the floor is not
/// applied and the ledger records the reading as absent rather than as zero — a fabricated zero would
/// halt the queue permanently, and a fabricated large value would disable the gate silently.
/// </para>
/// <para>
/// The same reading <c>tools/gates/gates.py</c>'s <c>_free_physical_mb</c> takes, by the same API, for
/// the same purpose (deciding whether it is safe to start work) — two languages, one Win32 call, no
/// shared record to keep in step.
/// </para>
/// </remarks>
public static class FreePhysicalMemory
{
    private const double BytesPerGiB = 1024d * 1024d * 1024d;

    /// <summary>Free physical RAM in GiB, or null when it could not be measured.</summary>
    public static double? TryReadGiB()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return TryReadWindowsGiB();
    }

    [SupportedOSPlatform("windows")]
    private static double? TryReadWindowsGiB()
    {
        try
        {
            var status = new MemoryStatusEx { DwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? status.UllAvailPhys / BytesPerGiB : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    // [DllImport], not [LibraryImport], matching SafeJobObjectHandle beside it: the source-generated
    // form requires <AllowUnsafeBlocks> for the whole assembly, which is a far larger change than this
    // one call is worth.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>The <c>MEMORYSTATUSEX</c> struct, field-for-field and in order — the layout is the
    /// contract, so the unread fields are present rather than trimmed.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint DwLength;
        public uint DwMemoryLoad;
        public ulong UllTotalPhys;
        public ulong UllAvailPhys;
        public ulong UllTotalPageFile;
        public ulong UllAvailPageFile;
        public ulong UllTotalVirtual;
        public ulong UllAvailVirtual;
        public ulong UllAvailExtendedVirtual;
    }
}
