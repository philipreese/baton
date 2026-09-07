using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Baton.Core.Internal;

/// <summary>
/// Clears <c>HANDLE_FLAG_INHERIT</c> on this process's own stdout and stderr handles, so nothing
/// Baton spawns can keep them open after Baton exits (#2030). Like <see cref="SafeJobObjectHandle"/>
/// and <see cref="FreePhysicalMemory"/> beside it this is plain Win32 surface, not the deleted
/// aer-core Rust FFI that CLAUDE.md's Architecture Rule 3 scopes; the rule names the exceptions it
/// allows and this is the third.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why redirecting every child is not the fix.</b> .NET's <c>CreateProcess</c> call passes
/// <c>bInheritHandles: true</c> unconditionally whenever any stream is redirected, and that
/// duplicates <i>every inheritable handle in this process's table</i> into the child — regardless of
/// what the child's own <c>STARTUPINFO</c> says its stdout is. So a child with all three streams
/// piped still holds a duplicate of Baton's stdout, and any descendant that outlives Baton holds it
/// open. That is what wedges a wrapper shell running
/// <c>pwsh -Command "baton dispatch ... *&gt; lane.log"</c>: the shell reads the lane's output to
/// EOF, and EOF never arrives while some straggler still owns the write end. Making the handle
/// non-inheritable before the first spawn is what actually closes that path.
/// </para>
/// <para>
/// <b>Only stdout and stderr, deliberately.</b> Those are the two the wrapper waits on. Stdin is
/// left inheritable because clearing it would hand an invalid stdin to the several children that
/// redirect only stdout/stderr (the git and gh spawns), changing behavior this has no reason to
/// touch.
/// </para>
/// <para>
/// <b>The other half of the same guarantee is elsewhere.</b> A grandchild under a dispatched worker
/// is killed by the Job Object <c>BatonProcessRunner</c> terminates at settle, pinned by
/// <c>ProcessTreeAndPanicSafetyTests.Run_GrandchildOutlivesRoot_TreeIsFullyCleanedUpOnReturn</c>.
/// This type covers what that one cannot: a process outside any of those jobs — Baton's own direct
/// children and whatever they leave behind.
/// </para>
/// <para>
/// <b>Never throws.</b> Called for its effect on the way into a command; a host where the call fails
/// is exactly the host that behaved this way before, so there is nothing to report and nothing to
/// abort for.
/// </para>
/// </remarks>
public static class StandardHandleInheritance
{
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint HandleFlagInherit = 0x00000001;

    /// <summary>
    /// Makes this process's stdout and stderr non-inheritable. Idempotent, and a no-op off Windows.
    /// </summary>
    public static void Disable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DisableWindows();
    }

    [SupportedOSPlatform("windows")]
    private static void DisableWindows()
    {
        foreach (int id in new[] { StdOutputHandle, StdErrorHandle })
        {
            nint handle;
            try
            {
                handle = GetStdHandle(id);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return;
            }

            // 0 is "this process has no such handle"; -1 (INVALID_HANDLE_VALUE) is the failure return.
            if (handle == nint.Zero || handle == -1)
            {
                continue;
            }

            DisableFor(handle);
        }
    }

    /// <summary>
    /// Test-only seam (Baton.Tests, via <c>InternalsVisibleTo</c>): clears the inherit flag on one
    /// arbitrary handle, so the two-arm test can prove the mechanism against a pipe it owns instead
    /// of mutating the test host's real standard streams. Returns false when the call failed.
    /// </summary>
    internal static bool DisableFor(nint handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return SetHandleInformation(handle, HandleFlagInherit, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    // [DllImport], not [LibraryImport], for the reason FreePhysicalMemory beside it records: the
    // source-generated form requires <AllowUnsafeBlocks> assembly-wide.
    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static extern bool SetHandleInformation(nint hObject, uint dwMask, uint dwFlags);
}
