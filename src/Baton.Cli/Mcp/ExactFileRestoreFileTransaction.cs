using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Baton.Cli.Mcp;

/// <summary>
/// A single-use, repository-root-anchored transaction for replacing one leaf. Traversal never
/// follows a link/reparse point and the final parent handle remains open through commit.
/// </summary>
internal sealed class ExactFileRestoreFileTransaction : IDisposable
{
    private readonly SafeFileHandle _root;
    private readonly SafeFileHandle _parent;
    private readonly string _leaf;
    private readonly bool _windows;
    private readonly IReadOnlyList<string> _parentParts;
    private readonly string _parentIdentity;
    private string? _temporaryLeaf;
    private string? _quarantineLeaf;
    private string? _failedLeaf;
    private SafeFileHandle? _capturedLeaf;
    private SafeFileHandle? _replacementLeaf;
    private ExactFileSnapshot? _replacementExpected;

    private ExactFileRestoreFileTransaction(
        SafeFileHandle root,
        SafeFileHandle parent,
        string leaf,
        bool windows,
        IReadOnlyList<string> parentParts)
    {
        _root = root;
        _parent = parent;
        _leaf = leaf;
        _windows = windows;
        _parentParts = parentParts;
        _parentIdentity = windows
            ? WindowsNative.Identity(parent)
            : UnixNative.Identity(parent, requireRegular: false);
    }

    internal static ExactFileRestoreFileTransaction Open(
        string repositoryRoot,
        IReadOnlyList<string> pathParts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(pathParts.Count, 1);

        if (OperatingSystem.IsWindows())
        {
            var root = WindowsNative.OpenRoot(repositoryRoot);
            var parent = root;
            try
            {
                WindowsNative.RefuseReparseOrNonDirectory(root, repositoryRoot);
                for (var index = 0; index < pathParts.Count - 1; index++)
                {
                    var child = WindowsNative.OpenRelative(parent, pathParts[index], directory: true, create: false);
                    try
                    {
                        WindowsNative.RefuseReparseOrNonDirectory(child, pathParts[index]);
                    }
                    catch
                    {
                        child.Dispose();
                        throw;
                    }

                    if (!ReferenceEquals(parent, root))
                    {
                        parent.Dispose();
                    }

                    parent = child;
                }

                return new ExactFileRestoreFileTransaction(
                    root, parent, pathParts[^1], windows: true, pathParts.Take(pathParts.Count - 1).ToArray());
            }
            catch
            {
                if (!ReferenceEquals(parent, root))
                {
                    parent.Dispose();
                }

                root.Dispose();
                throw;
            }
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "exact-file restore requires Windows, Linux, or macOS no-follow and atomic no-replace primitives");
        }

        var unixRoot = UnixNative.OpenRoot(repositoryRoot);
        var unixParent = unixRoot;
        try
        {
            for (var index = 0; index < pathParts.Count - 1; index++)
            {
                var child = UnixNative.OpenRelative(unixParent, pathParts[index], directory: true, create: false);
                if (!ReferenceEquals(unixParent, unixRoot))
                {
                    unixParent.Dispose();
                }

                unixParent = child;
            }

            return new ExactFileRestoreFileTransaction(
                unixRoot,
                unixParent,
                pathParts[^1],
                windows: false,
                pathParts.Take(pathParts.Count - 1).ToArray());
        }
        catch
        {
            if (!ReferenceEquals(unixParent, unixRoot))
            {
                unixParent.Dispose();
            }

            unixRoot.Dispose();
            throw;
        }
    }

    internal ExactFileSnapshot Capture(bool dirtyFile)
    {
        _capturedLeaf = TryOpenLeaf(excludeWriters: dirtyFile && OperatingSystem.IsWindows());
        if (_capturedLeaf is null)
        {
            return ExactFileSnapshot.Missing;
        }

        if (dirtyFile && !OperatingSystem.IsWindows())
        {
            _capturedLeaf.Dispose();
            _capturedLeaf = null;
            throw new PlatformNotSupportedException(
                "dirty exact-file restore is refused on this platform because concurrent writers cannot be excluded");
        }

        return Snapshot(_capturedLeaf);
    }

    /// <summary>
    /// Performs the final CAS and calls the durable journal callbacks at their required transition
    /// points. A callback exception is treated exactly like an I/O failure.
    /// </summary>
    internal ExactFileCommitResult Commit(
        ExactFileSnapshot expected,
        ReadOnlySpan<byte> replacement,
        Action<ExactFileRecoveryNames> writePrepared,
        Action writeCommitted,
        Action writeRolledBack,
        Action<string, string?> writeRecoveryRequired,
        Action? beforeCompareAndSwap = null,
        Action? beforeRollbackRestore = null,
        Action? beforeDurableCommit = null,
        Action? beforeDelete = null)
    {
        _temporaryLeaf = $".{_leaf}.baton-restore-{Guid.NewGuid():N}.tmp";
        _quarantineLeaf = expected.Exists
            ? $".{_leaf}.baton-quarantine-{Guid.NewGuid():N}"
            : null;
        _failedLeaf = $".{_leaf}.baton-uncommitted-{Guid.NewGuid():N}";
        StageReplacement(replacement);
        ProbeNoReplace();
        FlushParent();
        writePrepared(new ExactFileRecoveryNames(_temporaryLeaf, _quarantineLeaf, _failedLeaf));
        beforeCompareAndSwap?.Invoke();
        if (!ParentStillAnchored())
        {
            CleanupTemporary();
            writeRolledBack();
            return ExactFileCommitResult.Conflict(
                "a repository ancestor changed after the dirty-file check");
        }

        if (!expected.Exists)
        {
            using var current = TryOpenLeaf();
            if (current is not null)
            {
                CleanupTemporary();
                writeRolledBack();
                return ExactFileCommitResult.Conflict("the target was created after the dirty-file check");
            }

            try
            {
                RenameReplacementNoReplace(_temporaryLeaf!, _leaf);
                _temporaryLeaf = null;
                FlushParent();
            }
            catch (IOException ex)
            {
                CleanupTemporary();
                writeRolledBack();
                return ExactFileCommitResult.Conflict(
                    $"the target changed while the restore was committing: {ex.Message}");
            }

            try
            {
                beforeDurableCommit?.Invoke();
                if (!InstalledMatches())
                {
                    TryWriteRecovery(writeRecoveryRequired,
                        "the installed replacement changed before durable commit", null);
                    return ExactFileCommitResult.Recovery(
                        "the installed replacement changed before durable commit", null);
                }
                writeCommitted();
                return ExactFileCommitResult.Committed;
            }
            catch (Exception commitEvidenceFailure) when (IsRecoverable(commitEvidenceFailure))
            {
                return RollBackNewTargetWithoutOld(
                    commitEvidenceFailure,
                    writeRolledBack,
                    writeRecoveryRequired,
                    beforeRollbackRestore);
            }
        }

        if (!NamedMatches(_leaf, _capturedLeaf!, expected))
        {
            CleanupTemporary();
            writeRolledBack();
            return ExactFileCommitResult.Conflict(
                "the target identity or content changed after the dirty-file check");
        }
        try
        {
            RenameCapturedNoReplace(_leaf, _quarantineLeaf!);
            FlushParent();
        }
        catch (IOException ex)
        {
            CleanupTemporary();
            writeRolledBack();
            return ExactFileCommitResult.Conflict(
                $"the target changed while the restore was committing: {ex.Message}");
        }

        ExactFileSnapshot quarantined;
        try
        {
            quarantined = Snapshot(_capturedLeaf!);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return RestoreQuarantineAfterFailure(
                $"the quarantined target could not be verified: {ex.Message}",
                writeRolledBack,
                writeRecoveryRequired,
                beforeRollbackRestore);
        }

        if (quarantined != expected || !NamedMatches(_quarantineLeaf!, _capturedLeaf!, expected))
        {
            return RestoreQuarantineAfterFailure(
                "the target identity or content changed after the dirty-file check",
                writeRolledBack,
                writeRecoveryRequired,
                beforeRollbackRestore);
        }

        try
        {
            RenameReplacementNoReplace(_temporaryLeaf!, _leaf);
            _temporaryLeaf = null;
            FlushParent();
        }
        catch (IOException ex)
        {
            return RestoreQuarantineAfterFailure(
                $"a concurrent target prevented the restore commit: {ex.Message}",
                writeRolledBack,
                writeRecoveryRequired,
                beforeRollbackRestore);
        }

        try
        {
            beforeDurableCommit?.Invoke();
            if (!InstalledMatches()
                || !NamedMatches(_quarantineLeaf!, _capturedLeaf!, expected))
            {
                TryWriteRecovery(writeRecoveryRequired,
                    "the installed replacement or quarantined original changed before durable commit",
                    _quarantineLeaf);
                return ExactFileCommitResult.Recovery(
                    "the installed replacement or quarantined original changed before durable commit",
                    _quarantineLeaf);
            }
            writeCommitted();
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return RollBackCommittedReplacement(
                ex,
                writeRolledBack,
                writeRecoveryRequired,
                beforeRollbackRestore);
        }

        beforeDelete?.Invoke();
        if (!InstalledMatches()
            || !NamedMatches(_quarantineLeaf!, _capturedLeaf!, expected))
        {
            TryWriteRecovery(writeRecoveryRequired,
                "terminal revalidation failed before deleting the quarantined original", _quarantineLeaf);
            return ExactFileCommitResult.Recovery(
                "terminal revalidation failed before deleting the quarantined original", _quarantineLeaf);
        }
        DeleteRetainedLeaf(_quarantineLeaf, _capturedLeaf!);
        FlushParent();
        _quarantineLeaf = null;
        return ExactFileCommitResult.Committed;
    }

    private ExactFileCommitResult RollBackNewTargetWithoutOld(
        Exception evidenceFailure,
        Action writeRolledBack,
        Action<string, string?> writeRecoveryRequired,
        Action? beforeRollbackRestore)
    {
        if (!InstalledMatches())
        {
            TryWriteRecovery(writeRecoveryRequired,
                "the installed name was replaced before rollback", null);
            return ExactFileCommitResult.Recovery(
                "audit failed and the installed name was replaced before rollback", null);
        }
        try
        {
            RenameReplacementNoReplace(_leaf, _failedLeaf!);
            FlushParent();
            beforeRollbackRestore?.Invoke();
            if (!NamedMatches(_failedLeaf!, _replacementLeaf!, _replacementExpected!))
            {
                throw new IOException("the uncommitted replacement changed before rollback deletion");
            }
            DeleteRetainedLeaf(_failedLeaf, _replacementLeaf!);
            FlushParent();
            writeRolledBack();
            return ExactFileCommitResult.Refused(
                $"committed audit evidence failed and the new target was removed: {evidenceFailure.Message}");
        }
        catch (Exception rollbackFailure) when (IsRecoverable(rollbackFailure))
        {
            TryWriteRecovery(writeRecoveryRequired, rollbackFailure.Message, _failedLeaf);
            return ExactFileCommitResult.Recovery(
                $"audit failed and rollback could not remove the uncommitted target: {rollbackFailure.Message}",
                _failedLeaf);
        }
    }

    private ExactFileCommitResult RollBackCommittedReplacement(
        Exception evidenceFailure,
        Action writeRolledBack,
        Action<string, string?> writeRecoveryRequired,
        Action? beforeRollbackRestore)
    {
        if (!InstalledMatches())
        {
            TryWriteRecovery(writeRecoveryRequired,
                "the installed name was replaced before rollback", _quarantineLeaf);
            return ExactFileCommitResult.Recovery(
                "audit failed and the installed name was replaced before rollback", _quarantineLeaf);
        }
        try
        {
            RenameReplacementNoReplace(_leaf, _failedLeaf!);
            FlushParent();
            beforeRollbackRestore?.Invoke();
            RenameCapturedNoReplace(_quarantineLeaf!, _leaf);
            FlushParent();
            _quarantineLeaf = null;
            if (!NamedMatches(_failedLeaf!, _replacementLeaf!, _replacementExpected!))
            {
                throw new IOException("the uncommitted replacement changed before rollback deletion");
            }
            DeleteRetainedLeaf(_failedLeaf, _replacementLeaf!);
            FlushParent();
            writeRolledBack();
            return ExactFileCommitResult.Refused(
                $"committed audit evidence failed and the original target was restored: {evidenceFailure.Message}");
        }
        catch (Exception rollbackFailure) when (IsRecoverable(rollbackFailure))
        {
            TryWriteRecovery(writeRecoveryRequired, rollbackFailure.Message, _quarantineLeaf);
            return ExactFileCommitResult.Recovery(
                $"audit failed and rollback requires recovery: {rollbackFailure.Message}",
                _quarantineLeaf);
        }
    }

    private ExactFileCommitResult RestoreQuarantineAfterFailure(
        string reason,
        Action writeRolledBack,
        Action<string, string?> writeRecoveryRequired,
        Action? beforeRollbackRestore)
    {
        CleanupTemporary();
        try
        {
            beforeRollbackRestore?.Invoke();
            RenameCapturedNoReplace(_quarantineLeaf!, _leaf);
            FlushParent();
            _quarantineLeaf = null;
            writeRolledBack();
            return ExactFileCommitResult.Conflict(reason);
        }
        catch (Exception rollbackFailure) when (IsRecoverable(rollbackFailure))
        {
            TryWriteRecovery(writeRecoveryRequired, rollbackFailure.Message, _quarantineLeaf);
            return ExactFileCommitResult.Recovery(
                $"{reason}; restoring the quarantined original requires recovery: {rollbackFailure.Message}",
                _quarantineLeaf);
        }
    }

    private static void TryWriteRecovery(
        Action<string, string?> writeRecoveryRequired,
        string reason,
        string? quarantine)
    {
        try
        {
            writeRecoveryRequired(reason, quarantine);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            throw new IOException(
                $"recovery-required audit evidence could not be made durable; preserved quarantine '{quarantine ?? "none"}'",
                ex);
        }
    }

    private void StageReplacement(ReadOnlySpan<byte> replacement)
    {
        _replacementLeaf = CreateNamedLeaf(_temporaryLeaf!, excludeWriters: true);
        var offset = 0L;
        while (!replacement.IsEmpty)
        {
            RandomAccess.Write(_replacementLeaf, replacement, offset);
            offset += replacement.Length;
            replacement = [];
        }

        RandomAccess.FlushToDisk(_replacementLeaf);
        _replacementExpected = Snapshot(_replacementLeaf);
    }

    private void ProbeNoReplace()
    {
        var first = $".{_leaf}.baton-probe-{Guid.NewGuid():N}.a";
        var second = $".{_leaf}.baton-probe-{Guid.NewGuid():N}.b";
        using (CreateNamedLeaf(first)) { }
        using (CreateNamedLeaf(second)) { }

        try
        {
            try
            {
                RenameNoReplace(first, second);
                throw new PlatformNotSupportedException(
                    "the filesystem replaced an existing destination during an atomic no-replace probe");
            }
            catch (ExactFileAlreadyExistsException)
            {
                // The required result: the existing destination refused the rename.
            }
        }
        finally
        {
            DeleteNamedLeaf(first);
            DeleteNamedLeaf(second);
        }
    }

    private SafeFileHandle? TryOpenLeaf(bool excludeWriters = false)
    {
        try
        {
            return OpenNamedLeaf(_leaf, excludeWriters);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private SafeFileHandle OpenNamedLeaf(
        string name,
        bool excludeWriters = false,
        bool deleteAccess = true) =>
        _windows
            ? WindowsNative.OpenRelative(
                _parent, name, directory: false, create: false, excludeWriters, deleteAccess)
            : UnixNative.OpenRelative(_parent, name, directory: false, create: false);

    private SafeFileHandle CreateNamedLeaf(string name, bool excludeWriters = false) =>
        _windows
            ? WindowsNative.OpenRelative(_parent, name, directory: false, create: true, excludeWriters)
            : UnixNative.OpenRelative(_parent, name, directory: false, create: true);

    private string Identity(SafeFileHandle handle) =>
        _windows ? WindowsNative.Identity(handle) : UnixNative.Identity(handle, requireRegular: true);

    private ExactFileSnapshot Snapshot(SafeFileHandle handle) =>
        new(true, Identity(handle), Hash(handle));

    private bool NamedMatches(string name, SafeFileHandle retained, ExactFileSnapshot expected)
    {
        try
        {
            using var named = OpenNamedLeaf(name, deleteAccess: false);
            return string.Equals(Identity(named), Identity(retained), StringComparison.Ordinal)
                && Snapshot(retained) == expected
                && Snapshot(named) == expected;
        }
        catch (Exception ex) when (IsRecoverable(ex) || ex is FileNotFoundException)
        {
            return false;
        }
    }

    private bool InstalledMatches() =>
        _replacementLeaf is not null
        && _replacementExpected is not null
        && NamedMatches(_leaf, _replacementLeaf, _replacementExpected);

    private bool ParentStillAnchored()
    {
        SafeFileHandle current = _root;
        try
        {
            foreach (var part in _parentParts)
            {
                var next = _windows
                    ? WindowsNative.OpenRelative(current, part, directory: true, create: false)
                    : UnixNative.OpenRelative(current, part, directory: true, create: false);
                if (!ReferenceEquals(current, _root))
                {
                    current.Dispose();
                }

                current = next;
            }

            var identity = _windows
                ? WindowsNative.Identity(current)
                : UnixNative.Identity(current, requireRegular: false);
            return string.Equals(identity, _parentIdentity, StringComparison.Ordinal);
        }
        catch (Exception ex) when (IsRecoverable(ex) || ex is FileNotFoundException)
        {
            return false;
        }
        finally
        {
            if (!ReferenceEquals(current, _root))
            {
                current.Dispose();
            }
        }
    }

    private void RenameNoReplace(string source, string destination)
    {
        if (_windows)
        {
            using var sourceHandle = OpenNamedLeaf(source);
            WindowsNative.RenameNoReplace(sourceHandle, _parent, destination);
        }
        else
        {
            UnixNative.RenameNoReplace(_parent, source, destination);
        }
    }

    private void RenameCapturedNoReplace(string source, string destination)
    {
        if (_windows)
        {
            WindowsNative.RenameNoReplace(_capturedLeaf!, _parent, destination);
        }
        else
        {
            UnixNative.RenameNoReplace(_parent, source, destination);
        }
    }

    private void RenameReplacementNoReplace(string source, string destination)
    {
        if (_windows)
        {
            WindowsNative.RenameNoReplace(_replacementLeaf!, _parent, destination);
        }
        else
        {
            UnixNative.RenameNoReplace(_parent, source, destination);
        }
    }

    private void DeleteRetainedLeaf(string? name, SafeFileHandle retained)
    {
        if (name is null)
        {
            return;
        }

        if (_windows)
        {
            WindowsNative.Delete(retained);
        }
        else
        {
            UnixNative.Delete(_parent, name);
        }
    }

    private void FlushParent()
    {
        if (_windows)
        {
            WindowsNative.Flush(_parent);
        }
        else
        {
            UnixNative.Flush(_parent);
        }
    }

    private void DeleteNamedLeaf(string? name)
    {
        if (name is null)
        {
            return;
        }

        if (_windows)
        {
            try
            {
                using var handle = OpenNamedLeaf(name);
                WindowsNative.Delete(handle);
            }
            catch (FileNotFoundException)
            {
            }
        }
        else
        {
            UnixNative.Delete(_parent, name);
        }
    }

    private static string Hash(SafeFileHandle handle)
    {
        var length = RandomAccess.GetLength(handle);
        var buffer = new byte[64 * 1024];
        var header = System.Text.Encoding.UTF8.GetBytes($"blob {length}\0");
        using var gitHash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA1);
        gitHash.AppendData(header);
        var offset = 0L;
        while (offset < length)
        {
            var read = RandomAccess.Read(handle, buffer, offset);
            if (read == 0)
            {
                throw new IOException("the target changed while its Git blob hash was being captured");
            }

            gitHash.AppendData(buffer, 0, read);
            offset += read;
        }

        if (RandomAccess.GetLength(handle) != length)
        {
            throw new IOException("the target changed length while its Git blob hash was being captured");
        }

        return Convert.ToHexString(gitHash.GetHashAndReset()).ToLowerInvariant();
    }

    private void CleanupTemporary()
    {
        if (_temporaryLeaf is not null && _replacementLeaf is not null)
        {
            DeleteRetainedLeaf(_temporaryLeaf, _replacementLeaf);
            FlushParent();
        }
        else
        {
            DeleteNamedLeaf(_temporaryLeaf);
        }
        _temporaryLeaf = null;
    }

    private static bool IsRecoverable(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or PlatformNotSupportedException or Win32Exception;

    public void Dispose()
    {
        CleanupTemporary();
        // A non-null quarantine at disposal means no terminal path proved it safe to delete. Leave
        // it in place; recovery admission will block the requested path rather than guessing.

        _capturedLeaf?.Dispose();
        _replacementLeaf?.Dispose();
        if (!ReferenceEquals(_parent, _root))
        {
            _parent.Dispose();
        }

        _root.Dispose();
    }

    private static class UnixNative
    {
        private const int Eexist = 17;
        private const int Enoent = 2;
        private const int EloopLinux = 40;
        private const int EloopMac = 62;
        private const uint RenameNoReplaceLinux = 1;
        private const uint RenameExclusiveMac = 4;

        internal static SafeFileHandle OpenRoot(string path)
        {
            var flags = OpenFlags(directory: true, create: false);
            var fd = open(path, flags, 0);
            return WrapOpen(fd, path);
        }

        internal static SafeFileHandle OpenRelative(
            SafeFileHandle parent,
            string name,
            bool directory,
            bool create)
        {
            var fd = openat(
                checked((int)parent.DangerousGetHandle()),
                name,
                OpenFlags(directory, create),
                Convert.ToUInt32("600", 8));
            return WrapOpen(fd, name);
        }

        internal static string Identity(SafeFileHandle handle, bool requireRegular)
        {
            var buffer = Marshal.AllocHGlobal(256);
            try
            {
                if (fstat(checked((int)handle.DangerousGetHandle()), buffer) != 0)
                {
                    throw Error("fstat");
                }

                var device = OperatingSystem.IsMacOS()
                    ? unchecked((uint)Marshal.ReadInt32(buffer, 0))
                    : unchecked((ulong)Marshal.ReadInt64(buffer, 0));
                var inode = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
                var mode = OperatingSystem.IsMacOS()
                    ? unchecked((uint)Marshal.ReadInt32(buffer, 4))
                    : unchecked((uint)Marshal.ReadInt32(buffer, 24));
                if (requireRegular && (mode & 0xF000) != 0x8000)
                {
                    throw new IOException("the target is not a regular file");
                }

                return $"{device:x}:{inode:x}";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        internal static void RenameNoReplace(
            SafeFileHandle parent,
            string source,
            string destination)
        {
            var fd = checked((int)parent.DangerousGetHandle());
            int result;
            try
            {
                result = OperatingSystem.IsLinux()
                    ? renameat2(fd, source, fd, destination, RenameNoReplaceLinux)
                    : renameatx_np(fd, source, fd, destination, RenameExclusiveMac);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new PlatformNotSupportedException(
                    "the platform does not expose an atomic no-replace rename primitive", ex);
            }

            if (result != 0)
            {
                if (Marshal.GetLastPInvokeError() == Eexist)
                {
                    throw new ExactFileAlreadyExistsException(destination);
                }

                throw Error($"atomic no-replace rename '{source}' to '{destination}'");
            }
        }

        internal static void Delete(SafeFileHandle parent, string name)
        {
            if (unlinkat(checked((int)parent.DangerousGetHandle()), name, 0) != 0
                && Marshal.GetLastPInvokeError() != Enoent)
            {
                throw Error($"unlink '{name}'");
            }
        }

        internal static void Flush(SafeFileHandle directory)
        {
            if (fsync(checked((int)directory.DangerousGetHandle())) != 0)
            {
                throw Error("flush transaction directory");
            }
        }

        private static int OpenFlags(bool directory, bool create)
        {
            if (OperatingSystem.IsLinux())
            {
                return (create ? 0x2 | 0x40 | 0x80 : 0)
                    | 0x80000 | 0x20000 | (directory ? 0x10000 : 0x800);
            }

            return (create ? 0x2 | 0x200 | 0x800 : 0)
                | 0x1000000 | 0x100 | (directory ? 0x100000 : 0x4);
        }

        private static SafeFileHandle WrapOpen(int fd, string name)
        {
            if (fd >= 0)
            {
                return new SafeFileHandle(fd, ownsHandle: true);
            }

            var error = Marshal.GetLastPInvokeError();
            if (error == Enoent)
            {
                throw new FileNotFoundException($"'{name}' does not exist");
            }

            if (error is EloopLinux or EloopMac)
            {
                throw new IOException($"'{name}' is a symbolic link");
            }

            throw Error($"open '{name}'");
        }

        private static IOException Error(string operation) =>
            new($"{operation} failed: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");

        [DllImport("libc", SetLastError = true)]
        private static extern int open(string pathname, int flags, uint mode);

        [DllImport("libc", SetLastError = true)]
        private static extern int openat(int directory, string pathname, int flags, uint mode);

        [DllImport("libc", SetLastError = true)]
        private static extern int fstat(int fd, nint stat);

        [DllImport("libc", SetLastError = true, EntryPoint = "renameat2")]
        private static extern int renameat2(
            int oldDirectory,
            string oldName,
            int newDirectory,
            string newName,
            uint flags);

        [DllImport("libc", SetLastError = true, EntryPoint = "renameatx_np")]
        private static extern int renameatx_np(
            int oldDirectory,
            string oldName,
            int newDirectory,
            string newName,
            uint flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int unlinkat(int directory, string pathname, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int fsync(int fileDescriptor);
    }

    private static class WindowsNative
    {
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint DeleteAccess = 0x00010000;
        private const uint Synchronize = 0x00100000;
        private const uint FileReadAttributes = 0x80;
        private const uint FileShareAll = 0x7;
        private const uint FileShareRead = 0x1;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileOpen = 1;
        private const uint FileCreate = 2;
        private const uint FileDirectoryFile = 0x1;
        private const uint FileNonDirectoryFile = 0x40;
        private const uint FileSynchronousIoNonAlert = 0x20;
        private const uint FileOpenReparsePoint = 0x00200000;
        private const uint FileAttributeReparsePoint = 0x400;
        private const uint FileAttributeDirectory = 0x10;
        private const uint FileDispositionDelete = 1;
        private const int FileAttributeTagInfo = 9;
        private const int FileIdInfo = 18;
        private const int FileDispositionInfoEx = 21;
        private const int FileRenameInformationClass = 10;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;

        internal static SafeFileHandle OpenRoot(string path)
        {
            var handle = CreateFileW(
                path,
                GenericRead | GenericWrite,
                FileShareAll,
                0,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                0);
            if (handle.IsInvalid)
            {
                throw Error($"open repository root '{path}'");
            }

            return handle;
        }

        internal static SafeFileHandle OpenRelative(
            SafeFileHandle parent,
            string name,
            bool directory,
            bool create,
            bool excludeWriters = false,
            bool deleteAccess = true)
        {
            using var objectName = new NativeUnicodeString(name);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = objectName.Pointer,
                Attributes = 0x40,
            };
            var desired = GenericRead | FileReadAttributes | Synchronize;
            if (directory)
            {
                desired |= GenericWrite;
            }
            if (!directory && deleteAccess)
            {
                desired |= DeleteAccess;
            }
            if (create)
            {
                desired |= GenericWrite;
            }

            var status = NtCreateFile(
                out var handle,
                desired,
                ref attributes,
                out _,
                0,
                0,
                excludeWriters ? FileShareRead : FileShareAll,
                create ? FileCreate : FileOpen,
                FileSynchronousIoNonAlert | FileOpenReparsePoint
                    | (directory ? FileDirectoryFile : FileNonDirectoryFile),
                0,
                0);
            if (status < 0)
            {
                handle?.Dispose();
                var error = unchecked((int)RtlNtStatusToDosError(status));
                if (error is ErrorFileNotFound or ErrorPathNotFound)
                {
                    throw new FileNotFoundException($"'{name}' does not exist");
                }

                throw new IOException(
                    $"open '{name}' relative to its retained parent failed: {new Win32Exception(error).Message}");
            }

            RefuseReparse(handle, name);
            return handle;
        }

        internal static void RefuseReparseOrNonDirectory(SafeFileHandle handle, string name)
        {
            var info = AttributeTag(handle);
            if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new IOException($"'{name}' is a reparse point");
            }

            if ((info.FileAttributes & FileAttributeDirectory) == 0)
            {
                throw new IOException($"'{name}' is not a directory");
            }
        }

        internal static string Identity(SafeFileHandle handle)
        {
            var size = Marshal.SizeOf<FileIdInformation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetFileInformationByHandleEx(handle, FileIdInfo, buffer, checked((uint)size)))
                {
                    throw Error("read file identity");
                }

                var info = Marshal.PtrToStructure<FileIdInformation>(buffer);
                return $"{info.VolumeSerialNumber:x}:{Convert.ToHexString(info.FileId ?? [])}";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        internal static void RenameNoReplace(
            SafeFileHandle source,
            SafeFileHandle parent,
            string destination)
        {
            var nameBytes = System.Text.Encoding.Unicode.GetBytes(destination);
            var headerSize = Marshal.OffsetOf<FileRenameInformation>("FileName").ToInt32();
            var buffer = Marshal.AllocHGlobal(headerSize + nameBytes.Length);
            try
            {
                Marshal.WriteInt32(buffer, 0, 0);
                Marshal.WriteIntPtr(buffer, 8, parent.DangerousGetHandle());
                Marshal.WriteInt32(buffer, 16, nameBytes.Length);
                Marshal.Copy(nameBytes, 0, buffer + headerSize, nameBytes.Length);
                var status = NtSetInformationFile(
                    source,
                    out _,
                    buffer,
                    checked((uint)(headerSize + nameBytes.Length)),
                    FileRenameInformationClass);
                if (status < 0)
                {
                    var error = unchecked((int)RtlNtStatusToDosError(status));
                    if (error is 80 or 183)
                    {
                        throw new ExactFileAlreadyExistsException(destination);
                    }

                    throw new IOException(
                        $"atomic no-replace rename to '{destination}' failed: {new Win32Exception(error).Message}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        internal static void Delete(SafeFileHandle handle)
        {
            var value = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                Marshal.WriteInt32(value, unchecked((int)FileDispositionDelete));
                if (!SetFileInformationByHandle(handle, FileDispositionInfoEx, value, sizeof(uint)))
                {
                    throw Error("delete transaction file");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(value);
            }
        }

        internal static void Flush(SafeFileHandle directory)
        {
            var status = NtFlushBuffersFile(directory, out _);
            if (status < 0)
            {
                var error = unchecked((int)RtlNtStatusToDosError(status));
                throw new IOException(
                    $"flush transaction directory failed: {new Win32Exception(error).Message}");
            }
        }

        private static void RefuseReparse(SafeFileHandle handle, string name)
        {
            if ((AttributeTag(handle).FileAttributes & FileAttributeReparsePoint) != 0)
            {
                handle.Dispose();
                throw new IOException($"'{name}' is a reparse point");
            }
        }

        private static FileAttributeTagInformation AttributeTag(SafeFileHandle handle)
        {
            var size = Marshal.SizeOf<FileAttributeTagInformation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetFileInformationByHandleEx(
                        handle, FileAttributeTagInfo, buffer, checked((uint)size)))
                {
                    throw Error("read file attributes");
                }

                return Marshal.PtrToStructure<FileAttributeTagInformation>(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static IOException Error(string operation) =>
            new($"{operation} failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes
        {
            internal int Length;
            internal nint RootDirectory;
            internal nint ObjectName;
            internal uint Attributes;
            internal nint SecurityDescriptor;
            internal nint SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            internal nint Status;
            internal nuint Information;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInformation
        {
            internal uint FileAttributes;
            internal uint ReparseTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdInformation
        {
            internal ulong VolumeSerialNumber;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            internal byte[]? FileId;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileRenameInformation
        {
            internal uint Flags;
            internal nint RootDirectory;
            internal uint FileNameLength;
            internal char FileName;
        }

        private sealed class NativeUnicodeString : IDisposable
        {
            private readonly nint _buffer;
            internal nint Pointer { get; }

            internal NativeUnicodeString(string value)
            {
                _buffer = Marshal.StringToHGlobalUni(value);
                var unicode = new UnicodeString
                {
                    Length = checked((ushort)(value.Length * sizeof(char))),
                    MaximumLength = checked((ushort)((value.Length + 1) * sizeof(char))),
                    Buffer = _buffer,
                };
                Pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
                Marshal.StructureToPtr(unicode, Pointer, fDeleteOld: false);
            }

            public void Dispose()
            {
                Marshal.FreeHGlobal(Pointer);
                Marshal.FreeHGlobal(_buffer);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            internal ushort Length;
            internal ushort MaximumLength;
            internal nint Buffer;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            nint securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            nint templateFile);

        [DllImport("ntdll.dll")]
        private static extern int NtCreateFile(
            out SafeFileHandle fileHandle,
            uint desiredAccess,
            ref ObjectAttributes objectAttributes,
            out IoStatusBlock ioStatusBlock,
            nint allocationSize,
            uint fileAttributes,
            uint shareAccess,
            uint createDisposition,
            uint createOptions,
            nint eaBuffer,
            uint eaLength);

        [DllImport("ntdll.dll")]
        private static extern uint RtlNtStatusToDosError(int status);

        [DllImport("ntdll.dll")]
        private static extern int NtSetInformationFile(
            SafeFileHandle fileHandle,
            out IoStatusBlock ioStatusBlock,
            nint fileInformation,
            uint length,
            int fileInformationClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            int informationClass,
            nint information,
            uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            int informationClass,
            nint information,
            uint bufferSize);

        [DllImport("ntdll.dll")]
        private static extern int NtFlushBuffersFile(
            SafeFileHandle fileHandle,
            out IoStatusBlock ioStatusBlock);
    }

    private sealed class ExactFileAlreadyExistsException(string destination)
        : IOException($"the destination '{destination}' already exists");
}

internal sealed record ExactFileSnapshot(bool Exists, string? Identity, string? Blob)
{
    internal static ExactFileSnapshot Missing { get; } = new(false, null, null);
}

internal sealed record ExactFileRecoveryNames(
    string Temporary,
    string? Quarantine,
    string Uncommitted);

internal sealed record ExactFileCommitResult(
    bool Succeeded,
    bool RecoveryRequired,
    string? Reason,
    string? Quarantine)
{
    internal static ExactFileCommitResult Committed { get; } = new(true, false, null, null);

    internal static ExactFileCommitResult Conflict(string reason) => new(false, false, reason, null);

    internal static ExactFileCommitResult Refused(string reason) => new(false, false, reason, null);

    internal static ExactFileCommitResult Recovery(string reason, string? quarantine) =>
        new(false, true, reason, quarantine);
}
