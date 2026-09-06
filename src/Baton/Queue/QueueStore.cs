using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Status;

namespace Baton.Queue;

/// <summary>The whole queue file: the items, in operator order, and the hold flag.</summary>
/// <param name="Items">Every item, whatever its state. Nothing is pruned automatically — a done item
/// stays visible to <c>baton queue list</c> until the operator clears it.</param>
/// <param name="Held">
/// <c>baton queue hold</c>'s flag — read by the scheduler and by nothing else, which is what confines
/// its effect to new launches (spec/baton.md §13).
/// </param>
public sealed record QueueSnapshot(
    [property: JsonPropertyName("items")] IReadOnlyList<QueueItem> Items,
    [property: JsonPropertyName("held")] bool Held = false)
{
    public static readonly QueueSnapshot Empty = new([]);
}

/// <summary>
/// Reads and writes <c>BatonPaths.QueueFile</c> (#1934 slice 1).
/// </summary>
/// <remarks>
/// <para>
/// The locking rule and the two-writer situation behind it are spec/baton.md §13's. What it means for
/// this type's API: <see cref="MutateAsync"/> exists so that a caller CANNOT do a read and a write as
/// two calls — there is no public write method to pair with <see cref="LoadAsync"/>.
/// </para>
/// <para>
/// <b>Unlike the ledgers, this store does not fail open.</b> A quota-ledger write that fails costs a
/// row; a queue write that fails and is swallowed costs the operator's actual work list. So every
/// failure here raises <see cref="QueueStoreException"/> — with one exception, stated in §13: an
/// absent file is a legitimate empty queue, a malformed one is not.
/// </para>
/// </remarks>
public static class QueueStore
{
    /// <summary>This store's own <see cref="MutexGuardedFileLock"/> prefix — distinct from every
    /// ledger's, so the queue and an accounting append never contend. What renaming it would cost is
    /// on <see cref="MutexGuardedFileLock"/> itself; the same warning applies here.</summary>
    public const string LockNamePrefix = "baton-queue";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The queue as it stands. An absent file is <see cref="QueueSnapshot.Empty"/>.</summary>
    /// <exception cref="QueueStoreException">The file exists but is not readable as a queue.</exception>
    public static Task<QueueSnapshot> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Task.Run(() => MutexGuardedFileLock.RunUnderLock(path, LockNamePrefix, LockTimeout, () => ReadUnlocked(path)), cancellationToken);
    }

    /// <summary>
    /// Applies <paramref name="mutate"/> to the queue and writes the result back, all inside one lock
    /// acquisition, and returns what was written.
    /// </summary>
    /// <param name="mutate">
    /// <b>Must be synchronous and pure with respect to the file.</b> <see cref="Mutex"/> ownership is
    /// thread-affine (<see cref="MutexGuardedFileLock"/>'s own remarks), so an <c>await</c> inside the
    /// critical section would make the release throw; and a delegate that itself reads or writes the
    /// queue file would deadlock on the lock this call already holds.
    /// </param>
    public static Task<QueueSnapshot> MutateAsync(
        string path, Func<QueueSnapshot, QueueSnapshot> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(mutate);

        EnsureParentDirectory(path);
        return Task.Run(
            () => MutexGuardedFileLock.RunUnderLock(path, LockNamePrefix, LockTimeout, () =>
            {
                var updated = mutate(ReadUnlocked(path));
                WriteUnlocked(path, updated);
                return updated;
            }),
            cancellationToken);
    }

    private static QueueSnapshot ReadUnlocked(string path)
    {
        if (!File.Exists(path))
        {
            return QueueSnapshot.Empty;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new QueueStoreException($"Could not read the queue at '{path}': {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return QueueSnapshot.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<QueueSnapshot>(text, SerializerOptions) ?? QueueSnapshot.Empty;
        }
        catch (JsonException ex)
        {
            throw new QueueStoreException(
                $"The queue at '{path}' is not valid queue JSON: {ex.Message}. Refusing to read it as an empty "
                + "queue — that would silently discard every item in it.",
                ex);
        }
    }

    private static void WriteUnlocked(string path, QueueSnapshot snapshot)
    {
        // Written to a temp sibling and moved, not written in place: a torn write here is the
        // operator's whole work list, and a reader (`baton queue list`, the next tick) that catches a
        // half-written file would throw rather than degrade.
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot, SerializerOptions));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
                // The move failed and so did the cleanup; the temp file is inert (it is not `queue.json`
                // and nothing reads `*.tmp`), so losing it is strictly better than masking the real
                // failure below with a cleanup one.
            }

            throw new QueueStoreException($"Could not write the queue at '{path}': {ex.Message}", ex);
        }
    }

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}

/// <summary>A queue file that could not be read or written. Domain-level rather than a bare
/// <see cref="InvalidOperationException"/>, per the repo's error-handling rule.</summary>
public sealed class QueueStoreException : BatonFlowException
{
    public QueueStoreException(string message)
        : base(message)
    {
    }

    public QueueStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
