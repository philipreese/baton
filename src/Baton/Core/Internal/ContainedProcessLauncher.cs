using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Baton.Core.Internal;

/// <summary>
/// Creates a Windows child suspended and already associated with its Job Object, then resumes it.
/// The ordering is the invariant: no instruction in the child can spawn a helper before containment.
/// Output handles are explicitly allowlisted, so redirected pipes cannot duplicate an unrelated
/// inheritable handle from Baton into the child (#2030).
/// </summary>
internal static class ContainedProcessLauncher
{
    [SupportedOSPlatform("windows")]
    public static ContainedProcessLaunch Start(
        ProcessStartInfo startInfo,
        SafeJobObjectHandle job,
        Action<SafeJobObjectHandle, Process, nint>? beforeResume)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(job);
        return StartWindows(startInfo, job, beforeResume);
    }

    private static ContainedProcessLaunch StartWindows(
        ProcessStartInfo startInfo,
        SafeJobObjectHandle job,
        Action<SafeJobObjectHandle, Process, nint>? beforeResume)
    {
        AnonymousPipeServerStream? stdoutPipe = null;
        AnonymousPipeServerStream? stderrPipe = null;
        SafeFileHandle? stdin = null;
        nint attributeList = nint.Zero;
        nint handleList = nint.Zero;
        nint jobList = nint.Zero;
        nint environment = nint.Zero;
        PROCESS_INFORMATION processInformation = default;
        Process? process = null;
        bool processCreated = false;

        try
        {
            stdoutPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            stderrPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            stdin = OpenInheritedNullInput();
            nint stdoutClient = ParseHandle(stdoutPipe.GetClientHandleAsString());
            nint stderrClient = ParseHandle(stderrPipe.GetClientHandleAsString());

            attributeList = CreateAttributeList(attributeCount: 2);
            handleList = Marshal.AllocHGlobal(3 * nint.Size);
            Marshal.WriteIntPtr(handleList, 0 * nint.Size, stdin.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, 1 * nint.Size, stdoutClient);
            Marshal.WriteIntPtr(handleList, 2 * nint.Size, stderrClient);
            UpdateAttribute(attributeList, ProcThreadAttributeHandleList, handleList, 3 * nint.Size);

            jobList = Marshal.AllocHGlobal(nint.Size);
            Marshal.WriteIntPtr(jobList, job.DangerousGetHandle());
            UpdateAttribute(attributeList, ProcThreadAttributeJobList, jobList, nint.Size);

            environment = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(startInfo));
            var startup = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    Cb = Marshal.SizeOf<STARTUPINFOEX>(),
                    DwFlags = StartfUseStdHandles,
                    HStdInput = stdin.DangerousGetHandle(),
                    HStdOutput = stdoutClient,
                    HStdError = stderrClient,
                },
                LpAttributeList = attributeList,
            };

            string commandLineText = BuildCommandLine(startInfo);
            var commandLine = new StringBuilder(commandLineText, commandLineText.Length + 1);
            uint creationFlags = CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent;
            if (startInfo.CreateNoWindow)
            {
                creationFlags |= CreateNoWindow;
            }

            if (!CreateProcessW(
                applicationName: null,
                commandLine,
                processAttributes: nint.Zero,
                threadAttributes: nint.Zero,
                inheritHandles: true,
                creationFlags,
                environment,
                string.IsNullOrWhiteSpace(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
                ref startup,
                out processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start contained process '{startInfo.FileName}'.");
            }

            processCreated = true;
            process = Process.GetProcessById(checked((int)processInformation.DwProcessId));

            // Test observation point: the process is suspended and in the Job; no user code ran.
            beforeResume?.Invoke(job, process, processInformation.HThread);

            if (ResumeThread(processInformation.HThread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not resume contained process '{startInfo.FileName}'.");
            }

            stdoutPipe.DisposeLocalCopyOfClientHandle();
            stderrPipe.DisposeLocalCopyOfClientHandle();
            var stdout = new StreamReader(stdoutPipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var stderr = new StreamReader(stderrPipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            stdoutPipe = null;
            stderrPipe = null;
            return new ContainedProcessLaunch(process, stdout, stderr);
        }
        catch
        {
            if (processCreated)
            {
                job.Terminate();
            }

            process?.Dispose();
            throw;
        }
        finally
        {
            stdin?.Dispose();
            TryDisposeClientHandle(stdoutPipe);
            TryDisposeClientHandle(stderrPipe);
            stdoutPipe?.Dispose();
            stderrPipe?.Dispose();

            if (processInformation.HThread != nint.Zero)
            {
                CloseHandle(processInformation.HThread);
            }

            if (processInformation.HProcess != nint.Zero)
            {
                CloseHandle(processInformation.HProcess);
            }

            if (environment != nint.Zero)
            {
                Marshal.FreeHGlobal(environment);
            }

            if (attributeList != nint.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
            }

            if (handleList != nint.Zero)
            {
                Marshal.FreeHGlobal(handleList);
            }

            if (jobList != nint.Zero)
            {
                Marshal.FreeHGlobal(jobList);
            }
        }
    }

    private static SafeFileHandle OpenInheritedNullInput()
    {
        var attributes = new SECURITY_ATTRIBUTES
        {
            NLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            BInheritHandle = 1,
        };
        SafeFileHandle handle = CreateFileW(
            "NUL",
            GenericRead,
            FileShareRead | FileShareWrite,
            ref attributes,
            OpenExisting,
            0,
            nint.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open NUL for contained process stdin.");
        }

        return handle;
    }

    /// <summary>
    /// Deterministic test observation for the protected suspended-launch invariant. Suspending once
    /// returns the prior suspend count; the paired resume restores it before production performs the
    /// real resume. An unsuspended mutant returns zero and therefore makes the control go red without
    /// relying on whether Windows happened to schedule child code during a callback.
    /// </summary>
    internal static bool IsPrimaryThreadSuspendedForTest(nint thread)
    {
        uint previousCount = SuspendThread(thread);
        if (previousCount == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not probe contained-process suspension.");
        }

        if (ResumeThread(thread) == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restore contained-process suspension.");
        }

        return previousCount > 0;
    }

    private static nint CreateAttributeList(int attributeCount)
    {
        nuint bytes = 0;
        _ = InitializeProcThreadAttributeList(nint.Zero, attributeCount, 0, ref bytes);
        if (bytes == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not size contained-process attributes.");
        }

        nint list = Marshal.AllocHGlobal(checked((nint)bytes));
        if (!InitializeProcThreadAttributeList(list, attributeCount, 0, ref bytes))
        {
            int error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(list);
            throw new Win32Exception(error, "Could not initialize contained-process attributes.");
        }

        return list;
    }

    private static void UpdateAttribute(nint list, nuint attribute, nint value, int bytes)
    {
        if (!UpdateProcThreadAttribute(list, 0, attribute, value, checked((nuint)bytes), nint.Zero, nint.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not configure contained-process attributes.");
        }
    }

    private static string BuildEnvironmentBlock(ProcessStartInfo startInfo)
    {
        var block = new StringBuilder();
        foreach (var pair in startInfo.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (pair.Value is not null)
            {
                block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
            }
        }

        block.Append('\0');
        return block.ToString();
    }

    private static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        var commandLine = new StringBuilder(QuoteArgument(startInfo.FileName));
        if (startInfo.ArgumentList.Count > 0)
        {
            foreach (string argument in startInfo.ArgumentList)
            {
                commandLine.Append(' ').Append(QuoteArgument(argument));
            }
        }
        else if (!string.IsNullOrWhiteSpace(startInfo.Arguments))
        {
            commandLine.Append(' ').Append(startInfo.Arguments);
        }

        return commandLine.ToString();
    }

    // Windows argv quoting: backslashes only double before a quote or the closing quote.
    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"'))
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            quoted.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }

    private static nint ParseHandle(string value) =>
        checked((nint)long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture));

    private static void TryDisposeClientHandle(AnonymousPipeServerStream? pipe)
    {
        try
        {
            pipe?.DisposeLocalCopyOfClientHandle();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const nuint ProcThreadAttributeHandleList = 0x00020002;
    private const nuint ProcThreadAttributeJobList = 0x0002000D;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int NLength;
        public nint LpSecurityDescriptor;
        public int BInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int Cb;
        public string? LpReserved;
        public string? LpDesktop;
        public string? LpTitle;
        public int DwX;
        public int DwY;
        public int DwXSize;
        public int DwYSize;
        public int DwXCountChars;
        public int DwYCountChars;
        public int DwFillAttribute;
        public uint DwFlags;
        public short WShowWindow;
        public short CbReserved2;
        public nint LpReserved2;
        public nint HStdInput;
        public nint HStdOutput;
        public nint HStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public nint LpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint HProcess;
        public nint HThread;
        public uint DwProcessId;
        public uint DwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? applicationName,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref STARTUPINFOEX startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        nint attributeList,
        int attributeCount,
        int flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        nint attributeList,
        uint flags,
        nuint attribute,
        nint value,
        nuint size,
        nint previousValue,
        nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint attributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(nint thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(nint thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SECURITY_ATTRIBUTES securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);
}

internal sealed record ContainedProcessLaunch(
    Process Process,
    StreamReader StandardOutput,
    StreamReader StandardError);
