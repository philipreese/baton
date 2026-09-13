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
    /// Creates a new script rather than overwriting an existing path. The returned artifact owns its
    /// cleanup, including when the command cannot be started.
    /// </summary>
    internal static WindowsCommandScript Create(
        string scriptPath, string commandLine, Action<string, bool> ensureNoReparsePoint)
    {
        ArgumentNullException.ThrowIfNull(ensureNoReparsePoint);
        ensureNoReparsePoint(scriptPath, includeLeaf: false);
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
                DeleteScript(scriptPath, ensureNoReparsePoint);
            }
            throw;
        }

        return new WindowsCommandScript(scriptPath, ensureNoReparsePoint);
    }

    private static void DeleteScript(string scriptPath, Action<string, bool> ensureNoReparsePoint)
    {
        // Check the leaf immediately before deletion so a replaced transport artifact never follows
        // a reparse point during cleanup.
        ensureNoReparsePoint(scriptPath, includeLeaf: true);
        File.Delete(scriptPath);
    }

    internal sealed class WindowsCommandScript : IDisposable
    {
        private readonly Action<string, bool> _ensureNoReparsePoint;

        internal WindowsCommandScript(string path, Action<string, bool> ensureNoReparsePoint)
        {
            Path = path;
            _ensureNoReparsePoint = ensureNoReparsePoint;
        }

        internal string Path { get; }

        public void Dispose() => DeleteScript(Path, _ensureNoReparsePoint);
    }
}
