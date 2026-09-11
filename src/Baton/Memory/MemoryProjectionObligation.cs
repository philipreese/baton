using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Status;

namespace Baton.Memory;

/// <summary>The two open states of a canonical-store projection obligation (#2138).</summary>
public enum MemoryProjectionObligationStatus
{
    /// <summary>The daemon may retry once <see cref="MemoryProjectionObligation.NextAttemptUtc"/> arrives.</summary>
    Pending,

    /// <summary>The bounded automatic retry budget is exhausted; an operator must repair and run sync.</summary>
    Escalated,
}

/// <summary>
/// One durable statement that a canonical memory store still needs projection. It never states that
/// a vendor loaded or consumed the generated file.
/// </summary>
public sealed record MemoryProjectionObligation(
    string AttemptId,
    string Repository,
    string RepositorySlug,
    MemoryProjectionObligationStatus Status,
    int FailedAttempts,
    DateTime CreatedAtUtc,
    DateTime? LastAttemptUtc,
    DateTime? NextAttemptUtc,
    string? LastError,
    string? NextAction);

/// <summary>
/// Owns <c>sync-pending.json</c> and the publication fence for one canonical memory store.
/// </summary>
/// <remarks>
/// A projection attempt first replaces the obligation, then builds its snapshot. Publication takes
/// this file's mutex and re-checks the attempt id in the same critical section as the target-file
/// replacements. Therefore an older snapshot can either publish before a newer attempt is claimed,
/// or observe that newer claim and publish nothing; it cannot silently overwrite the newer result.
/// The file is removed only by the matching attempt after publication. A crash anywhere earlier
/// leaves it for the daemon sweep.
/// </remarks>
public static class MemoryProjectionObligationStore
{
    /// <summary>
    /// Five failures includes the immediate write-triggered attempt plus four daemon retries. With
    /// the delays below that gives a transient at least fifteen minutes to clear while bounding noisy
    /// retries against a persistently denied root. Escalation then requires the printed manual action.
    /// </summary>
    // FailAsync can produce Pending/0..4 or Escalated/5 only; durable reads reject all other counts.
    public const int EscalationAttemptCount = 5;

    public static readonly TimeSpan InitialBackoff = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(15);

    private const string LockNamePrefix = "baton-memory-projection";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Replaces any older obligation because this attempt represents a newer canonical state.</summary>
    public static Task<MemoryProjectionObligation> ReplaceAsync(
        string repository,
        string repositorySlug,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentException.ThrowIfNullOrEmpty(repositorySlug);
        if (!string.Equals(FleetMemory.SlugFor(repository), repositorySlug, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Repository '{repository}' does not belong to projection obligation slug '{repositorySlug}'.",
                nameof(repositorySlug));
        }

        return Task.Run(
            () => MutexGuardedFileLock.RunUnderLock(
                BatonPaths.MemorySyncPendingFile(repositorySlug),
                LockNamePrefix,
                LockTimeout,
                () =>
                {
                    var obligation = new MemoryProjectionObligation(
                        Guid.NewGuid().ToString("N"),
                        repository,
                        repositorySlug,
                        MemoryProjectionObligationStatus.Pending,
                        FailedAttempts: 0,
                        CreatedAtUtc: nowUtc,
                        LastAttemptUtc: null,
                        NextAttemptUtc: nowUtc,
                        LastError: null,
                        NextAction: null);
                    WriteUnlocked(obligation);
                    return obligation;
                }),
            cancellationToken);
    }

    /// <summary>Reads the current open obligation, or null when the latest projection completed.</summary>
    public static Task<MemoryProjectionObligation?> ReadAsync(
        string repositorySlug,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(repositorySlug);
        var path = BatonPaths.MemorySyncPendingFile(repositorySlug);
        return Task.Run(
            () => MutexGuardedFileLock.RunUnderLock(
                path, LockNamePrefix, LockTimeout, () => ReadValidatedUnlocked(path, repositorySlug)),
            cancellationToken);
    }

    /// <summary>
    /// Runs all target replacements only if <paramref name="obligation"/> is still current. The
    /// current-id test and replacements share one lock. The outer canonical generation transaction
    /// rejects changes to any snapshot input and excludes mutations throughout target replacement.
    /// </summary>
    public static bool TryPublishCurrent(MemoryProjectionObligation obligation, string generation, Action publish)
    {
        ArgumentNullException.ThrowIfNull(obligation);
        ArgumentNullException.ThrowIfNull(publish);
        Validate(obligation, obligation.RepositorySlug);
        var path = BatonPaths.MemorySyncPendingFile(obligation.RepositorySlug);

        return MemoryCanonicalGeneration.ReadCurrent(BatonPaths.Root, generation, () => MutexGuardedFileLock.RunUnderLock(
            path,
            LockNamePrefix,
            LockTimeout,
            () =>
            {
                var current = ReadValidatedUnlocked(path, obligation.RepositorySlug);
                if (current is null || !string.Equals(current.AttemptId, obligation.AttemptId, StringComparison.Ordinal))
                {
                    return false;
                }

                RequireSameOwner(obligation, current);

                if (MemoryImportOperationStore.BlocksProjection(obligation.RepositorySlug))
                {
                    throw new IOException(
                        $"Canonical memory store '{obligation.RepositorySlug}' belongs to an unsettled import; " +
                        "publication is fenced until durable recovery completes.");
                }

                publish();
                return true;
            }));
    }

    /// <summary>Closes only the obligation that actually published; a newer one is left untouched.</summary>
    public static Task<bool> CompleteAsync(
        MemoryProjectionObligation obligation,
        CancellationToken cancellationToken = default) =>
        MutateIfCurrentAsync(
            obligation,
            current =>
            {
                File.Delete(BatonPaths.MemorySyncPendingFile(current.RepositorySlug));
                return null;
            },
            cancellationToken);

    /// <summary>Records one failed, safe-to-repeat publication and its bounded retry decision.</summary>
    public static Task<bool> FailAsync(
        MemoryProjectionObligation obligation,
        Exception error,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        return MutateIfCurrentAsync(
            obligation,
            current =>
            {
                if (current.Status != MemoryProjectionObligationStatus.Pending)
                {
                    throw new InvalidDataException("An escalated projection obligation cannot record another automatic failure.");
                }

                var failures = checked(current.FailedAttempts + 1);
                var escalated = failures >= EscalationAttemptCount;
                DateTime? next = escalated ? null : nowUtc + BackoffAfter(failures);
                var updated = current with
                {
                    Status = escalated
                        ? MemoryProjectionObligationStatus.Escalated
                        : MemoryProjectionObligationStatus.Pending,
                    FailedAttempts = failures,
                    LastAttemptUtc = nowUtc,
                    NextAttemptUtc = next,
                    LastError = $"{error.GetType().Name}: {error.Message}",
                    NextAction = escalated
                        ? $"Repair the target/discovery failure, then run 'baton memory sync --repository {current.Repository} --apply'."
                        : null,
                };
                WriteUnlocked(updated);
                return updated;
            },
            cancellationToken);
    }

    /// <summary>The daemon retry predicate; escalated obligations never retry automatically.</summary>
    public static bool IsDue(MemoryProjectionObligation obligation, DateTime nowUtc) =>
        obligation.Status == MemoryProjectionObligationStatus.Pending
        && obligation.NextAttemptUtc is { } due
        && due <= nowUtc;

    public static TimeSpan BackoffAfter(int failedAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(failedAttempts, EscalationAttemptCount);
        var multiplier = Math.Pow(2, failedAttempts - 1);
        return TimeSpan.FromTicks(Math.Min(
            (long)(InitialBackoff.Ticks * multiplier),
            MaximumBackoff.Ticks));
    }

    private static Task<bool> MutateIfCurrentAsync(
        MemoryProjectionObligation obligation,
        Func<MemoryProjectionObligation, MemoryProjectionObligation?> mutation,
        CancellationToken cancellationToken)
    {
        Validate(obligation, obligation.RepositorySlug);
        var path = BatonPaths.MemorySyncPendingFile(obligation.RepositorySlug);
        return Task.Run(
            () => MutexGuardedFileLock.RunUnderLock(
                path,
                LockNamePrefix,
                LockTimeout,
                () =>
                {
                    var current = ReadValidatedUnlocked(path, obligation.RepositorySlug);
                    if (current is null || !string.Equals(current.AttemptId, obligation.AttemptId, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    RequireSameOwner(obligation, current);

                    mutation(current);
                    return true;
                }),
            cancellationToken);
    }

    private static MemoryProjectionObligation? ReadUnlocked(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<MemoryProjectionObligation>(File.ReadAllText(path, Encoding.UTF8), Json)
            ?? throw new InvalidDataException($"Memory projection obligation '{path}' contains JSON null.");
    }

    private static MemoryProjectionObligation? ReadValidatedUnlocked(string path, string expectedSlug)
    {
        var obligation = ReadUnlocked(path);
        if (obligation is not null)
        {
            Validate(obligation, expectedSlug);
        }

        return obligation;
    }

    private static void Validate(MemoryProjectionObligation obligation, string expectedSlug)
    {
        if (obligation.AttemptId is not { Length: > 0 }
            || obligation.Repository is not { Length: > 0 }
            || obligation.RepositorySlug is not { Length: > 0 }
            || obligation.FailedAttempts < 0
            || !Enum.IsDefined(obligation.Status)
            || (obligation.Status == MemoryProjectionObligationStatus.Pending
                && (obligation.NextAttemptUtc is null
                    || obligation.FailedAttempts >= EscalationAttemptCount))
            || (obligation.Status == MemoryProjectionObligationStatus.Escalated
                && (obligation.NextAttemptUtc is not null
                    || obligation.FailedAttempts != EscalationAttemptCount)))
        {
            throw new InvalidDataException("Memory projection obligation has an incomplete or invalid shape.");
        }

        _ = MemoryStoreIdentity.Resolve(expectedSlug, null, [], obligation);
    }

    private static void RequireSameOwner(
        MemoryProjectionObligation expected,
        MemoryProjectionObligation current)
    {
        if (!string.Equals(expected.Repository, current.Repository, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.RepositorySlug, current.RepositorySlug, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Projection obligation '{current.AttemptId}' changed identity while owned by the same attempt.");
        }
    }

    private static void WriteUnlocked(MemoryProjectionObligation obligation)
    {
        Validate(obligation, obligation.RepositorySlug);
        var path = BatonPaths.MemorySyncPendingFile(obligation.RepositorySlug);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(obligation, Json) + "\n", new UTF8Encoding(false));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
