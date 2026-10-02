using System.Diagnostics;
using System.Runtime.ExceptionServices;
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

    internal static T Mutate<T>(string root, Func<T> action) =>
        Mutate(root, action, TimeSpan.FromSeconds(5));

    // Per-call test seam: the observer sees actual replacement failures, never a fabricated move.
    internal static T Mutate<T>(string root, Func<T> action, TimeSpan retryBudget,
        Action<Exception>? replacementFailed = null) => Locked(root, () =>
    {
        // Validate existing authority before replacing it: corruption must not disappear on a write.
        _ = Read(root);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, FileName);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Exception? publicationFailure = null;
        try
        {
            File.WriteAllText(temp, Guid.NewGuid().ToString("N"));
            ReplaceGeneration(temp, path, retryBudget, replacementFailed);
        }
        catch (Exception error)
        {
            publicationFailure = error;
            throw;
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception cleanupFailure) when (publicationFailure is not null)
            {
                // Keep the failed publication's original stack; retain a cleanup refusal as evidence.
                publicationFailure.Data["MemoryGenerationCleanupFailure"] = cleanupFailure;
            }
        }
        // Protected invariant: replacement is the only retried operation. Canonical mutation runs
        // once, after successful invalidation; exhaustion never touches canonical bytes.
        return action();
    });

    private static void ReplaceGeneration(string temp, string path, TimeSpan retryBudget,
        Action<Exception>? replacementFailed)
    {
        var elapsed = Stopwatch.StartNew();
        var backoffMs = 15.0;
        ExceptionDispatchInfo? firstFailure = null;
        while (true)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(error);
                try { replacementFailed?.Invoke(error); }
                catch (Exception observerFailure)
                {
                    observerFailure.Data["MemoryGenerationReplacementFailure"] = error;
                    throw;
                }
                // The first real attempt happens even with a zero budget. Observer time and waits
                // consume the same monotonic budget, rather than starting a fresh deadline.
                var remaining = retryBudget - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    firstFailure.Throw();

                // wait-ok: a bounded wait for a noncooperating file holder; no async suspension while
                // owning the thread-affine generation mutex. Never retry after the budget expires.
                Thread.Sleep(remaining < TimeSpan.FromMilliseconds(backoffMs)
                    ? remaining : TimeSpan.FromMilliseconds(backoffMs));
                if (elapsed.Elapsed >= retryBudget)
                    firstFailure.Throw();
                backoffMs = Math.Min(backoffMs * 2, 250);
            }
        }
    }

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
