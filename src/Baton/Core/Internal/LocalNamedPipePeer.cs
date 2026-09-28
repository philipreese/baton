using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Baton.Core.Internal;

/// <summary>
/// Windows-only local named-pipe boundary for steering. Native creation sets
/// PIPE_REJECT_REMOTE_CLIENTS; managed CurrentUserOnly alone does neither that nor an exact
/// user-SID check (it can use the token owner). The connected peer is impersonated after its
/// request bytes arrive, and any unavailable identity fails closed.
/// </summary>
public static class LocalNamedPipePeer
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x00000008;

    public static NamedPipeServerStream CreateServer(string pipeName)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Verified local steering pipes require Windows.");
        return CreateServerWindows(pipeName);
    }

    public static string? CurrentUserSid() => OperatingSystem.IsWindows()
        ? CurrentUserSidWindows() : null;

    public static bool IsSameUser(NamedPipeServerStream pipe, string serverUserSid)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            string? clientSid = null;
            pipe.RunAsClient(() => clientSid = OperatingSystem.IsWindows() ? CurrentUserSidWindows() : null);
            return string.Equals(clientSid, serverUserSid, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
            or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateServerWindows(string pipeName)
    {
        var handle = CreateNamedPipeW(@"\\.\pipe\" + pipeName,
            PipeAccessDuplex | FileFlagOverlapped, PipeRejectRemoteClients,
            1, 4096, 4096, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Could not create local-only steering pipe.");
        }
        try
        {
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true,
                isConnected: false, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? CurrentUserSidWindows() => WindowsIdentity.GetCurrent().User?.Value;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeOut,
        IntPtr securityAttributes);
}
