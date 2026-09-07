using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Baton.Store;

/// <summary>
/// Best-effort "who currently holds this file" probe, for enriching a sharing-violation diagnostic
/// (#816's <see cref="FlowJournalHeldException"/>, and the #398 Windows-CI flake class it belongs to).
/// When the ledger open loses a race to a transient external holder, the standing question has been
/// "held by <em>what</em>?" — a scanner, a sibling command, a not-yet-torn-down process — and the
/// exception could only say a holder exists, not name it. This names it, via the Windows Restart Manager
/// API (<c>rstrtmgr.dll</c>), which is built into Windows and needs no Sysinternals download.
/// </summary>
/// <remarks>
/// <para>
/// <b>The probe must run while the handle is still held</b> — i.e. at the catch site, in-process — which
/// is why this is not a post-hoc CI step: a transient holder is long gone by the time a failed job's
/// cleanup runs. It is called only from an already-failing error path, so its own cost is irrelevant.
/// </para>
/// <para>
/// <b>Windows-only and fully swallowing.</b> Off Windows it returns a marker (Unix does not enforce
/// <see cref="FileShare"/>, so this class of violation cannot arise there — see
/// <see cref="FlowJournalHeldException"/>). On Windows, every failure mode — the API missing, a non-zero
/// return, no holder found because the handle already released — returns a descriptive marker rather than
/// throwing, so a diagnostic can never turn a flake into a hard error or mask the original exception.
/// </para>
/// <para>
/// <b>Public rather than internal since #1951</b>, only so <see cref="IsSharingViolation"/> stays one
/// home: <c>QueueLauncher</c> (Baton.Cli) has to tell a held <c>snapshot.json</c> from a corrupt one,
/// and the alternative was a second copy of <see cref="ErrorSharingViolationHResult"/> in another
/// assembly. Nothing else here is meant for callers outside Baton.
/// </para>
/// </remarks>
public static class FileHolderProbe
{
    /// <summary>
    /// The Win32 HRESULT for ERROR_SHARING_VIOLATION. .NET assigns this same HRESULT to the
    /// equivalent IOException on every OS it runs on — including Unix, where it comes from the
    /// runtime's own flock-based FileShare enforcement rather than a real Win32 call — so checking
    /// it is portable and does not depend on OS-localized exception text.
    /// </summary>
    public const int ErrorSharingViolationHResult = unchecked((int)0x80070020);

    /// <summary>
    /// Returns true if <paramref name="ex"/> is an <see cref="IOException"/> representing a sharing violation.
    /// </summary>
    public static bool IsSharingViolation(IOException ex) => ex.HResult == ErrorSharingViolationHResult;

    /// <summary>
    /// A human-readable description of the process(es) currently holding <paramref name="path"/> open, or
    /// a marker string when the probe cannot run or finds no holder. Never throws.
    /// </summary>
    public static string DescribeHolders(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "(holder probe: Windows-only)";
        }

        try
        {
            return DescribeHoldersWindows(path);
        }
        catch (Exception ex)
        {
            // A diagnostic in an error path must never eclipse the error it describes.
            return $"(holder probe failed: {ex.GetType().Name}: {ex.Message})";
        }
    }

    private const int ErrorMoreData = 234;
    private const int MaxAppName = 255;
    private const int MaxSvcName = 63;
    private const int SessionKeyLength = 32; // CCH_RM_SESSION_KEY

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxAppName + 1)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxSvcName + 1)]
        public string ServiceShortName;

        public uint ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RmUniqueProcess[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RmProcessInfo[]? rgAffectedApps, out uint lpdwRebootReasons);

    [SupportedOSPlatform("windows")]
    private static string DescribeHoldersWindows(string path)
    {
        // strSessionKey is an [out] parameter: Restart Manager GENERATES the key into a caller-allocated
        // buffer of CCH_RM_SESSION_KEY+1 wchars. A StringBuilder is the correct marshal for a buffer
        // native code writes into — never a `string`, whose backing store the CLR does not expect native
        // code to mutate.
        var sessionKey = new StringBuilder(SessionKeyLength + 1);
        var result = RmStartSession(out var session, 0, sessionKey);
        if (result != 0)
        {
            return $"(holder probe: RmStartSession rc={result})";
        }

        try
        {
            result = RmRegisterResources(session, 1, [path], 0, null, 0, null);
            if (result != 0)
            {
                return $"(holder probe: RmRegisterResources rc={result})";
            }

            // Two-call idiom: the first RmGetList reports how many process entries the buffer needs.
            uint needed = 0;
            uint have = 0;
            result = RmGetList(session, out needed, ref have, null, out _);
            if (result == 0 && needed == 0)
            {
                return "(no holder found — the handle released before the probe ran)";
            }

            if (result != ErrorMoreData)
            {
                return $"(holder probe: RmGetList(size) rc={result})";
            }

            var processes = new RmProcessInfo[needed];
            have = needed;
            result = RmGetList(session, out needed, ref have, processes, out _);
            if (result != 0)
            {
                return $"(holder probe: RmGetList(list) rc={result})";
            }

            if (have == 0)
            {
                return "(no holder found)";
            }

            var holders = new List<string>((int)have);
            for (var i = 0; i < have; i++)
            {
                var name = string.IsNullOrEmpty(processes[i].AppName) ? "(unnamed)" : processes[i].AppName;
                holders.Add($"{name} (pid {processes[i].Process.ProcessId})");
            }

            return string.Join("; ", holders);
        }
        finally
        {
            RmEndSession(session);
        }
    }
}
