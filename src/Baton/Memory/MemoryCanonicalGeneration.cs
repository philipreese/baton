using Baton.Status;

namespace Baton.Memory;

/// <summary>
/// Durable publication authority shared by fleet, repository ledgers and import records in one home.
/// Mutations invalidate snapshots before touching canonical bytes, while publication compares and
/// writes under the same mutex. A crash between invalidation and mutation only forces a fresh read.
/// </summary>
public static class MemoryCanonicalGeneration
{
    private const string FileName = "memory-generation";

    // Lock order: operation (when replaying/reversing), generation, then one ledger or obligation.
    // Snapshot reads release their ledger locks before publication. No caller may acquire generation
    // while holding a ledger mutex, or acquire operation while holding generation/metadata/ledger.
    private static T Locked<T>(string root, Func<T> action) => MutexGuardedFileLock.RunUnderLock(
        Path.Combine(root, FileName), "baton-memory-canonical", TimeSpan.FromSeconds(30), action);

    public static string Capture(string root) => Locked(root, () => Read(root));

    internal static (string Generation, T Value) Inspect<T>(string root, Func<T> read) =>
        Locked(root, () => (Read(root), read()));

    public static T ReadCurrent<T>(string root, string generation, Func<T> action) => Locked(root, () =>
    {
        if (!string.Equals(Read(root), generation, StringComparison.Ordinal))
            throw new IOException("Canonical memory changed during the snapshot; publication requires a fresh retry.");
        return action();
    });

    internal static T Mutate<T>(string root, Func<T> action) => Locked(root, () =>
    {
        // Validate existing authority before replacing it: corruption must not disappear on a write.
        _ = Read(root);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, FileName);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, Guid.NewGuid().ToString("N"));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
        return action();
    });

    internal static T MutateLedger<T>(string path, Func<T> action) => Mutate(LedgerRoot(path), action);

    internal static Task<IReadOnlyList<T>> AppendAsync<T>(
        JsonLinesLedger<T> ledger, IReadOnlyList<T> entries, string path, CancellationToken cancellationToken)
        where T : class => ledger.AppendAndGetAppendedAsync(entries, path, cancellationToken,
            transaction: append => MutateLedger(path, append));

    private static string LedgerRoot(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        // Only the current canonical home's exact layout shares its generation. Standalone ledger
        // callers keep authority beside their file; never infer a home above an arbitrary fixture.
        var root = Path.GetFullPath(BatonPaths.Root);
        var relative = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relative.Length == 3 && relative[0] != ".."
            && string.Equals(relative[1], BatonPaths.MemoryDirectoryName, StringComparison.OrdinalIgnoreCase)
            ? root : directory;
    }

    private static string Read(string root)
    {
        var path = Path.Combine(root, FileName);
        try
        {
            var value = File.ReadAllText(path);
            if (!Guid.TryParseExact(value, "N", out _))
                throw new InvalidDataException($"Canonical memory generation '{path}' is corrupt.");
            return value;
        }
        catch (FileNotFoundException) { return string.Empty; }
        catch (DirectoryNotFoundException) { return string.Empty; }
    }
}
