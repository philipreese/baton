using System.Text;
using System.Text.Json;

namespace Baton.Status;

/// <summary>
/// The append-only JSON-lines store <c>QuotaLedgerStore</c> (spec/baton.md §7's burn ledger, #1570)
/// and <c>CostLedgerStore</c> (§7's cost ledger, #1849 phase A) both are — extracted here (#1884) so
/// the two share one implementation rather than two near-verbatim copies of it, the same way
/// <see cref="MutexGuardedFileLock"/> is shared rather than copied. A defect fixed in the
/// read-check-then-append critical section now reaches both ledgers; before the extraction, a fix that
/// split that section into two lock acquisitions in one file would have left the other's gap open with
/// no test relating them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fails open, never gates.</b> <see cref="AppendAsync"/> throws
/// (<see cref="IOException"/>/<see cref="UnauthorizedAccessException"/>/<see cref="WaitHandleCannotBeOpenedException"/>)
/// rather than swallowing internally — each store's caller is where the log-on-stderr-and-swallow
/// happens, and each store's own remarks state that contract. <see cref="ReadAllAsync"/> never throws.
/// </para>
/// <para>
/// <b>The lock name is load-bearing.</b> <paramref name="lockNamePrefix"/> is passed straight through
/// to <see cref="MutexGuardedFileLock"/>, whose own remarks state what changing an existing caller's
/// prefix costs: an older and a newer <c>baton</c> build would take out two different mutexes against
/// the same file. Each store keeps the exact prefix string it shipped with; distinct prefixes are also
/// what keeps two stores from ever contending on each other's locks.
/// </para>
/// </remarks>
/// <typeparam name="TEntry">The row type, serialized one per line with <see cref="SerializerOptions"/>.</typeparam>
/// <param name="lockNamePrefix">This store's own <see cref="MutexGuardedFileLock"/> prefix.</param>
/// <param name="ledgerDisplayName">
/// How <see cref="ReadAllAsync"/> names this ledger on stderr when a read fails open — e.g.
/// <c>"quota ledger"</c>, so the message reads as the store's own rather than a generic one.
/// </param>
/// <param name="executionIdSelector">
/// The dedupe key: an entry's execution id, or <see langword="null"/>/empty for an entry that carries
/// none. Both current stores select their <c>Execution</c> field, compared with
/// <see cref="StringComparer.Ordinal"/>; parameterised rather than assumed so a future store's key is a
/// construction-site choice instead of a hidden convention.
/// </param>
/// <param name="serializerOptions">Defaults to the compact, non-indented options both stores use.</param>
/// <param name="keyComparer">
/// The comparer for non-empty dedupe keys. Defaults to ordinal for execution ids; path-keyed stores
/// pass the path comparer their public contract documents.
/// </param>
internal sealed class JsonLinesLedger<TEntry>(
    string lockNamePrefix,
    string ledgerDisplayName,
    Func<TEntry, string?> executionIdSelector,
    JsonSerializerOptions? serializerOptions = null,
    IEqualityComparer<string>? keyComparer = null)
    where TEntry : class
{
    /// <summary>Same generous timeout <c>RoomRegistryStore</c> uses, for the same reason: every critical
    /// section here is one small append, or one whole-file read/rewrite, never long-running.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// This ledger's <see cref="MutexGuardedFileLock"/> prefix, exposed so a store's own test can pin
    /// the literal string the append path actually locks on — not for a production caller to build a
    /// <see cref="Mutex"/> of its own.
    /// </summary>
    internal string LockNamePrefix { get; } = lockNamePrefix;

    /// <summary>
    /// The one definition of this ledger's wire format, exposed so a store-specific write that is not
    /// one of the shared operations here serializes through it rather than through options of its own —
    /// <c>QuotaLedgerStore.WriteAllUnlocked</c>'s remarks state what that buys for the one such caller.
    /// </summary>
    internal JsonSerializerOptions SerializerOptions { get; } = serializerOptions ?? new JsonSerializerOptions { WriteIndented = false };

    /// <summary>Test-only observation of each shared read-check-then-append operation.</summary>
    internal Action<int>? AppendOperationObserver { get; set; }

    /// <summary>
    /// Appends the subset of <paramref name="entries"/> whose execution id is not already present in
    /// <paramref name="ledgerFilePath"/>, in ONE read-check-then-append critical section — two lock
    /// acquisitions would let a concurrent writer land in the gap. An entry whose id is absent cannot be
    /// deduplicated against anything and is always appended. Creates the file and its parent directory
    /// if neither exists; a no-op when nothing survives the filter, never opening the file to write zero
    /// bytes. Each store's own <c>AppendAsync</c> documents why its ledger needs the skip and against
    /// which repeated-settle shapes.
    /// </summary>
    public async Task AppendAsync(IReadOnlyList<TEntry> entries, string ledgerFilePath, CancellationToken cancellationToken = default) =>
        _ = await AppendAndGetAppendedAsync(entries, ledgerFilePath, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Appends the same subset <see cref="AppendAsync"/> does and returns the rows this call actually
    /// wrote, in input order. The result is decided inside the same ledger lock as the dedupe check,
    /// so a caller recording ownership can never claim a row a concurrent append won first.
    /// </summary>
    internal Task<IReadOnlyList<TEntry>> AppendAndGetAppendedAsync(
        IReadOnlyList<TEntry> entries, string ledgerFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(ledgerFilePath);

        if (entries.Count == 0)
        {
            return Task.FromResult((IReadOnlyList<TEntry>)[]);
        }

        AppendOperationObserver?.Invoke(entries.Count);
        EnsureParentDirectory(ledgerFilePath);

        return RunUnderLockAsync(ledgerFilePath, () =>
        {
            var alreadyRecorded = ReadAllUnlocked(ledgerFilePath)
                .Select(executionIdSelector)
                .Where(id => id is { Length: > 0 })
                .Select(id => id!)
                .ToHashSet(keyComparer ?? StringComparer.Ordinal);

            var toAppend = new List<TEntry>(entries.Count);
            foreach (var entry in entries)
            {
                var id = executionIdSelector(entry);
                if (id is not { Length: > 0 } || alreadyRecorded.Add(id))
                {
                    toAppend.Add(entry);
                }
            }
            if (toAppend.Count == 0)
            {
                return (IReadOnlyList<TEntry>)toAppend;
            }

            var builder = new StringBuilder();
            foreach (var entry in toAppend)
            {
                builder.Append(JsonSerializer.Serialize(entry, SerializerOptions)).Append('\n');
            }

            var bytes = Encoding.UTF8.GetBytes(builder.ToString());
            using var stream = new FileStream(
                ledgerFilePath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: false);
            stream.Write(bytes);
            stream.Flush();

            return (IReadOnlyList<TEntry>)toAppend;
        }, cancellationToken);
    }

    /// <summary>
    /// Reads every parseable line in <paramref name="ledgerFilePath"/>, in file (= write) order, under
    /// the <see cref="MutexGuardedFileLock"/> keyed on this file. A missing file resolves to an empty
    /// list; a malformed line is skipped rather than failing the whole read. Never throws — a
    /// lock-acquire timeout or an I/O failure resolves to an empty list, same as a missing file, and is
    /// reported on stderr under <c>ledgerDisplayName</c>.
    /// </summary>
    public Task<IReadOnlyList<TEntry>> ReadAllAsync(string ledgerFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(ledgerFilePath);

        return Task.Run(
            () =>
            {
                try
                {
                    return MutexGuardedFileLock.RunUnderLock(
                        ledgerFilePath, LockNamePrefix, LockTimeout, () => ReadAllUnlocked(ledgerFilePath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
                {
                    Console.Error.WriteLine($"Could not read the {ledgerDisplayName} at '{ledgerFilePath}': {ex.Message}.");
                    return (IReadOnlyList<TEntry>)[];
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// The read half, factored out so a read-then-write happens inside ONE lock acquisition rather than
    /// two — two separate acquisitions would let a concurrent writer land in the gap between them,
    /// silently truncated away by whichever finishes second. Callers must already hold the
    /// <see cref="MutexGuardedFileLock"/> on <paramref name="ledgerFilePath"/>; this method takes none.
    /// </summary>
    internal IReadOnlyList<TEntry> ReadAllUnlocked(string ledgerFilePath)
    {
        if (!File.Exists(ledgerFilePath))
        {
            return [];
        }

        string text;
        using (var stream = new FileStream(ledgerFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            text = reader.ReadToEnd();
        }

        var result = new List<TEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            TEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<TEntry>(line, SerializerOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is not null)
            {
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>Creates <paramref name="ledgerFilePath"/>'s parent directory if it does not exist yet.</summary>
    internal static void EnsureParentDirectory(string ledgerFilePath)
    {
        var directory = Path.GetDirectoryName(ledgerFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a worker thread holding this ledger's
    /// <see cref="MutexGuardedFileLock"/>, for a store-specific critical section that is not one of the
    /// shared operations above (<c>QuotaLedgerStore.RebuildAsync</c>'s read-merge-rewrite).
    /// <b><paramref name="body"/> must stay synchronous</b> — <see cref="Mutex"/> ownership is
    /// thread-affine, so an <c>await</c> between acquire and release makes
    /// <see cref="Mutex.ReleaseMutex"/> throw; <see cref="MutexGuardedFileLock"/>'s own remarks state
    /// this, and the one-<c>Task.Run</c>-from-the-outside shape here is what honours it.
    /// </summary>
    internal Task<T> RunUnderLockAsync<T>(string ledgerFilePath, Func<T> body, CancellationToken cancellationToken) =>
        Task.Run(() => MutexGuardedFileLock.RunUnderLock(ledgerFilePath, LockNamePrefix, LockTimeout, body), cancellationToken);

    /// <summary>Action-returning overload of <see cref="RunUnderLockAsync{T}"/>.</summary>
    private Task RunUnderLockAsync(string ledgerFilePath, Action body, CancellationToken cancellationToken) =>
        Task.Run(() => MutexGuardedFileLock.RunUnderLock(ledgerFilePath, LockNamePrefix, LockTimeout, body), cancellationToken);
}
