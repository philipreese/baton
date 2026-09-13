using System.Text;

namespace Baton.Vendors;

/// <summary>
/// Writes the one Windows <c>cmd.exe</c> transport artifact used by
/// <see cref="CodexDynamicToolPolicy"/>. The body is the permission-checked command, not a second
/// command representation. cmd reads batch source through Windows' ANSI code page, so the writer uses
/// that same strict encoding and refuses an unrepresentable command before it can be executed.
/// </summary>
internal static class WindowsCommandTransport
{
    private static readonly Encoding BatchFileEncoding = Encoding.Default.CodePage == 65_001
        ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
        : Encoding.GetEncoding(
            Encoding.Default.CodePage, new EncoderExceptionFallback(), new DecoderExceptionFallback());

    /// <summary>Returns the exact checked command text or refuses a value cmd cannot transport safely.</summary>
    internal static string ScriptText(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        if (commandLine.Contains('\0'))
        {
            throw new ArgumentException("Windows command transport does not accept NUL characters.", nameof(commandLine));
        }

        return commandLine;
    }

    /// <summary>
    /// Creates a new script rather than overwriting an existing path. On a partial-write failure it
    /// removes only that newly-created file before surfacing the failure, so no unchecked artifact is
    /// left for a later command.
    /// </summary>
    internal static void WriteScript(string scriptPath, string commandLine)
    {
        var text = ScriptText(commandLine);
        var created = false;
        try
        {
            using var stream = new FileStream(
                scriptPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 4096,
                FileOptions.WriteThrough | FileOptions.SequentialScan);
            created = true;
            using var writer = new StreamWriter(stream, BatchFileEncoding, bufferSize: 4096, leaveOpen: true);
            // A batch file echoes its command line by default, unlike cmd /c's direct command
            // path. The fixed @ belongs to the transport, not the checked command, and restores
            // that observable behavior without changing the command's text.
            writer.Write('@');
            writer.Write(text);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            if (created)
            {
                File.Delete(scriptPath);
            }
            throw;
        }
    }
}
