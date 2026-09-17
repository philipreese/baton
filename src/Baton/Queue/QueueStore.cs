using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Status;

namespace Baton.Queue;

/// <summary>
/// The whole queue file: items in operator order, the hold flag, and independent current-PR
/// observations. Observations are top-level so refreshing forge evidence cannot rewrite a lane's
/// lifecycle history.
/// </summary>
/// <param name="Items">Every item, whatever its state. Nothing is pruned automatically — a done item
/// stays visible to <c>baton queue list</c> until the operator clears it.</param>
/// <param name="Held">
/// <c>baton queue hold</c>'s flag — read by the scheduler and by nothing else, which is what confines
/// its effect to new launches (spec/baton.md §13).
/// </param>
public sealed record QueueSnapshot(
    [property: JsonPropertyName("items")] IReadOnlyList<QueueItem> Items,
    [property: JsonPropertyName("held")] bool Held = false,
    [property: JsonPropertyName("pullRequestObservations")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<QueuePullRequestObservation>? PullRequestObservations = null,
    [property: JsonPropertyName("pendingFleetEvents")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<JsonElement>? PendingFleetEvents = null,
    [property: JsonPropertyName("worktreeCleanupClaims")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<QueueWorktreeCleanupClaim>? WorktreeCleanupClaims = null,
    [property: JsonPropertyName("worktreeCleanupReceipts")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<QueueWorktreeCleanupReceipt>? WorktreeCleanupReceipts = null)
{
    public static readonly QueueSnapshot Empty = new([]);
}

/// <summary>A durable exclusive authority to evaluate one resolved worktree for removal.</summary>
public sealed record QueueWorktreeCleanupClaim(
    string Id,
    string Path,
    string Repository,
    string Branch,
    string Head,
    DateTimeOffset ClaimedAt,
    string QueueRevision = "",
    string Classification = "candidate");

/// <summary>The durable, idempotent outcome of one cleanup claim.</summary>
public sealed record QueueWorktreeCleanupReceipt(
    string ClaimId,
    string Path,
    string Repository = "",
    string Branch = "",
    string Head = "",
    long? ObservedBytes = null,
    DateTimeOffset StartedAt = default,
    DateTimeOffset CompletedAt = default,
    string Disposition = "",
    string ReasonCode = "");

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
/// failure here raises <see cref="QueueStoreException"/> — with one exception, stated in spec/baton.md §13: an
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

    /// <summary>
    /// Persists a claim for one exact resolved path. Existing claims and completed receipts win every race;
    /// callers must treat a null result as an instruction to leave the workspace untouched.
    /// Non-success receipts (refused, retained, race-lost) do not permanently prevent future cleanup.
    /// </summary>
    public static async Task<QueueWorktreeCleanupClaim?> TryClaimWorktreeCleanupAsync(
        string queueFile,
        string path,
        string repository,
        string branch,
        string head,
        CancellationToken cancellationToken = default,
        string? queueRevision = null,
        string? classification = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueFile);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentException.ThrowIfNullOrEmpty(branch);
        ArgumentException.ThrowIfNullOrEmpty(head);

        var canonicalPath = Path.GetFullPath(path);
        QueueWorktreeCleanupClaim? acquired = null;
        await MutateAsync(queueFile, snapshot =>
        {
            var claims = snapshot.WorktreeCleanupClaims ?? [];
            var receipts = snapshot.WorktreeCleanupReceipts ?? [];

            // A past successful removal or already-absent receipt permanently prevents re-claiming.
            if (receipts.Any(receipt => QueueWorktreePathComparer.Equals(Path.GetFullPath(receipt.Path), canonicalPath)
                && receipt.Disposition is "removed" or "already-absent"))
            {
                return snapshot;
            }

            // An active (unreceipted) claim for this path wins every race.
            if (claims.Any(claim => QueueWorktreePathComparer.Equals(Path.GetFullPath(claim.Path), canonicalPath)
                && !receipts.Any(receipt => receipt.ClaimId == claim.Id)))
            {
                return snapshot;
            }

            var rev = queueRevision ?? ComputeRevision(snapshot.Items);
            var cls = classification ?? "candidate";
            acquired = new QueueWorktreeCleanupClaim(
                Guid.NewGuid().ToString("N"),
                canonicalPath,
                repository,
                branch,
                head,
                DateTimeOffset.UtcNow,
                rev,
                cls);
            return snapshot with { WorktreeCleanupClaims = claims.Append(acquired).ToList() };
        }, cancellationToken).ConfigureAwait(false);
        return acquired;
    }

    /// <summary>
    /// Returns any active (unreceipted) claim for <paramref name="path"/> to adopt for recheck and settlement.
    /// </summary>
    public static async Task<QueueWorktreeCleanupClaim?> TryAdoptWorktreeCleanupClaimAsync(
        string queueFile,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueFile);
        ArgumentException.ThrowIfNullOrEmpty(path);
        var canonicalPath = Path.GetFullPath(path);
        var snapshot = await LoadAsync(queueFile, cancellationToken).ConfigureAwait(false);
        var receipts = snapshot.WorktreeCleanupReceipts ?? [];
        return (snapshot.WorktreeCleanupClaims ?? []).LastOrDefault(claim =>
            QueueWorktreePathComparer.Equals(Path.GetFullPath(claim.Path), canonicalPath)
            && !receipts.Any(receipt => receipt.ClaimId == claim.Id));
    }

    /// <summary>
    /// Returns all active (unreceipted) claims in the queue store.
    /// </summary>
    public static async Task<IReadOnlyList<QueueWorktreeCleanupClaim>> GetActiveWorktreeCleanupClaimsAsync(
        string queueFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueFile);
        var snapshot = await LoadAsync(queueFile, cancellationToken).ConfigureAwait(false);
        var receipts = snapshot.WorktreeCleanupReceipts ?? [];
        return (snapshot.WorktreeCleanupClaims ?? [])
            .Where(claim => !receipts.Any(receipt => receipt.ClaimId == claim.Id))
            .ToList();
    }

    /// <summary>Whether a path is fenced by a claim that has not yet received a receipt.</summary>
    public static async Task<bool> HasActiveWorktreeCleanupClaimAsync(
        string queueFile,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueFile);
        ArgumentException.ThrowIfNullOrEmpty(path);
        var canonicalPath = Path.GetFullPath(path);
        var snapshot = await LoadAsync(queueFile, cancellationToken).ConfigureAwait(false);
        var receipts = snapshot.WorktreeCleanupReceipts ?? [];
        return (snapshot.WorktreeCleanupClaims ?? []).Any(claim =>
            QueueWorktreePathComparer.Equals(Path.GetFullPath(claim.Path), canonicalPath)
            && !receipts.Any(receipt => receipt.ClaimId == claim.Id));
    }

    /// <summary>Records a terminal cleanup observation without deleting the claim that fenced it.</summary>
    public static Task CompleteWorktreeCleanupAsync(
        string queueFile,
        QueueWorktreeCleanupClaim claim,
        string disposition,
        string reasonCode,
        CancellationToken cancellationToken = default,
        long? observedBytes = null,
        DateTimeOffset? completedAt = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueFile);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentException.ThrowIfNullOrEmpty(disposition);
        ArgumentException.ThrowIfNullOrEmpty(reasonCode);
        var now = completedAt ?? DateTimeOffset.UtcNow;
        return MutateAsync(queueFile, snapshot =>
        {
            var claims = snapshot.WorktreeCleanupClaims ?? [];
            if (!claims.Any(current => current.Id == claim.Id)) return snapshot;
            var receipts = snapshot.WorktreeCleanupReceipts ?? [];
            if (receipts.Any(receipt => receipt.ClaimId == claim.Id)) return snapshot;
            var receipt = new QueueWorktreeCleanupReceipt(
                claim.Id,
                claim.Path,
                claim.Repository,
                claim.Branch,
                claim.Head,
                observedBytes,
                claim.ClaimedAt,
                now,
                disposition,
                reasonCode);
            return snapshot with
            {
                WorktreeCleanupReceipts = receipts.Append(receipt).ToList(),
            };
        }, cancellationToken);
    }

    /// <summary>Computes the complete, canonical queue-row observation a cleanup claim was based on.</summary>
    public static string ComputeRevision(IReadOnlyList<QueueItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var content = JsonSerializer.Serialize(items, SerializerOptions);
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static readonly StringComparer QueueWorktreePathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    /// Commits a queue mutation and its durable record under the same queue ordering seam.
    /// The queue is written before <paramref name="record"/> runs, so a record failure never rolls
    /// back a committed lifecycle mutation; holding the lock only prevents a successor mutation from
    /// making that record stale before it is appended.
    /// </summary>
    public static Task<QueueSnapshot> MutateAndRecordAsync(
        string path,
        Func<QueueSnapshot, QueueSnapshot> mutate,
        Action record,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(mutate);
        ArgumentNullException.ThrowIfNull(record);

        EnsureParentDirectory(path);
        return Task.Run(
            () => MutexGuardedFileLock.RunUnderLock(path, LockNamePrefix, LockTimeout, () =>
            {
                var updated = mutate(ReadUnlocked(path));
                WriteUnlocked(path, updated);
                record();
                return updated;
            }),
            cancellationToken);
    }

    /// <summary>
    /// Appends a scheduler record only while its row remains current under the queue ordering seam.
    /// A record action is synchronous because the file mutex is thread-affine; callers may bridge an
    /// asynchronous ledger append only after the queue mutation has committed.
    /// </summary>
    public static Task<bool> RecordIfCurrentAsync(
        string path,
        Func<QueueSnapshot, bool> isCurrent,
        Action record,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(record);

        return Task.Run(
            () => MutexGuardedFileLock.RunUnderLock(path, LockNamePrefix, LockTimeout, () =>
            {
                if (!isCurrent(ReadUnlocked(path)))
                {
                    return false;
                }

                record();
                return true;
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
            var snapshot = JsonSerializer.Deserialize<QueueSnapshot>(text, SerializerOptions) ?? QueueSnapshot.Empty;
            return NormalizeCleanupSchema(snapshot);
        }
        catch (JsonException ex)
        {
            throw new QueueStoreException(
                $"The queue at '{path}' is not valid queue JSON: {ex.Message}. Refusing to read it as an empty "
                + "queue — that would silently discard every item in it.",
                ex);
        }
    }

    // #2386 initially wrote claims and receipts with fewer fields. Loading those durable observations
    // must preserve their known facts while making the expanded schema explicit on the next write.
    private static QueueSnapshot NormalizeCleanupSchema(QueueSnapshot snapshot)
    {
        var claims = snapshot.WorktreeCleanupClaims?.Select(claim => claim with
        {
            QueueRevision = claim.QueueRevision ?? string.Empty,
            Classification = string.IsNullOrWhiteSpace(claim.Classification) ? "candidate" : claim.Classification,
        }).ToList();
        var receipts = snapshot.WorktreeCleanupReceipts?.Select(receipt => receipt with
        {
            Repository = receipt.Repository ?? string.Empty,
            Branch = receipt.Branch ?? string.Empty,
            Head = receipt.Head ?? string.Empty,
            StartedAt = receipt.StartedAt == default ? receipt.CompletedAt : receipt.StartedAt,
            Disposition = receipt.Disposition ?? string.Empty,
            ReasonCode = receipt.ReasonCode ?? string.Empty,
        }).ToList();
        return snapshot with { WorktreeCleanupClaims = claims, WorktreeCleanupReceipts = receipts };
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
