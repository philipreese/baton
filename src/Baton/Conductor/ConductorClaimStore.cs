using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Accounting;
using Baton.Status;

namespace Baton.Conductor;

/// <summary>
/// Authoritative store and state transition validator for repository claims by external conductors (spec/baton.md §14).
/// </summary>
public static class ConductorClaimStore
{
    public const string LockNamePrefix = "baton-conductor-claim";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Acquires a claim on <paramref name="identity"/> for <paramref name="holder"/> (spec/baton.md §14).
    /// </summary>
    public static Task<ConductorClaimRecord> ClaimAsync(
        RepositoryIdentity identity,
        string holder,
        string? batonRoot = null,
        DateTime? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(holder))
        {
            throw new ArgumentException("Conductor holder name must not be null or whitespace.", nameof(holder));
        }

        var normalizedHolder = holder.Trim();
        var root = batonRoot ?? BatonPaths.Root;
        var path = GetClaimFilePath(root, identity.FileSlug);
        var timestamp = now ?? DateTime.UtcNow;

        EnsureParentDirectory(path);

        return Task.Run(
            () => MutexGuardedFileLock.RunUnderLock(path, LockNamePrefix, LockTimeout, () =>
            {
                var existing = ReadUnlocked(path);
                if (existing is not null && !string.IsNullOrWhiteSpace(existing.Holder))
                {
                    if (string.Equals(existing.Holder, normalizedHolder, StringComparison.Ordinal))
                    {
                        // Idempotent claim by the same holder: do not invent a second acquisition.
                        return existing;
                    }

                    var takeoverCommand = $"baton conductor takeover {normalizedHolder} --reason <text>";
                    throw new ConductorClaimException(
                        $"Repository '{identity.Value}' is currently claimed by conductor '{existing.Holder}'. "
                        + $"Use '{takeoverCommand}' to replace the current holder.",
                        takeoverCommand);
                }

                var transitions = new List<ConductorClaimTransition>(existing?.Transitions ?? []);
                transitions.Add(new ConductorClaimTransition(
                    Kind: ConductorClaimTransitionKind.Claim,
                    Holder: normalizedHolder,
                    DisplacedHolder: null,
                    Reason: null,
                    Timestamp: timestamp));

                var record = new ConductorClaimRecord(
                    Repository: identity.Value,
                    RepositorySlug: identity.FileSlug,
                    Holder: normalizedHolder,
                    AcquiredAt: timestamp,
                    Takeover: null,
                    Transitions: transitions);

                WriteUnlocked(path, record);
                return record;
            }),
            cancellationToken);
    }

    /// <summary>
    /// Replaces an existing claim holder with <paramref name="holder"/> (spec/baton.md §14).
    /// </summary>
    public static Task<(ConductorClaimRecord Record, string DisplacedHolder)> TakeoverAsync(
        RepositoryIdentity identity,
        string holder,
        string reason,
        string? batonRoot = null,
        DateTime? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(holder))
        {
            throw new ArgumentException("Conductor holder name must not be null or whitespace.", nameof(holder));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ConductorClaimException(
                "Conductor takeover requires a non-blank --reason explaining why the existing holder is being displaced.");
        }

        var normalizedHolder = holder.Trim();
        var normalizedReason = reason.Trim();
        var root = batonRoot ?? BatonPaths.Root;
        var path = GetClaimFilePath(root, identity.FileSlug);
        var timestamp = now ?? DateTime.UtcNow;

        EnsureParentDirectory(path);

        return Task.Run(
            () => MutexGuardedFileLock.RunUnderLock(path, LockNamePrefix, LockTimeout, () =>
            {
                var existing = ReadUnlocked(path);
                if (existing is null || string.IsNullOrWhiteSpace(existing.Holder))
                {
                    throw new ConductorClaimException(
                        $"Repository '{identity.Value}' is not currently claimed by any conductor. "
                        + $"Use 'baton conductor claim {normalizedHolder}' to acquire it.");
                }

                if (string.Equals(existing.Holder, normalizedHolder, StringComparison.Ordinal))
                {
                    throw new ConductorClaimException(
                        $"Repository '{identity.Value}' is already claimed by conductor '{normalizedHolder}'. "
                        + "Takeover explicitly replaces another holder.");
                }

                var displaced = existing.Holder;
                var takeoverProvenance = new ConductorTakeoverProvenance(
                    DisplacedHolder: displaced,
                    Reason: normalizedReason,
                    TakenOverAt: timestamp);

                var transitions = new List<ConductorClaimTransition>(existing.Transitions ?? []);
                transitions.Add(new ConductorClaimTransition(
                    Kind: ConductorClaimTransitionKind.Takeover,
                    Holder: normalizedHolder,
                    DisplacedHolder: displaced,
                    Reason: normalizedReason,
                    Timestamp: timestamp));

                var record = new ConductorClaimRecord(
                    Repository: identity.Value,
                    RepositorySlug: identity.FileSlug,
                    Holder: normalizedHolder,
                    AcquiredAt: timestamp,
                    Takeover: takeoverProvenance,
                    Transitions: transitions);

                WriteUnlocked(path, record);
                return (record, displaced);
            }),
            cancellationToken);
    }

    /// <summary>
    /// Releases a claim currently held on <paramref name="identity"/> by <paramref name="holder"/> (spec/baton.md §14).
    /// </summary>
    public static Task<ConductorClaimRecord> ReleaseAsync(
        RepositoryIdentity identity,
        string holder,
        string reason,
        string? batonRoot = null,
        DateTime? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(holder))
        {
            throw new ArgumentException("Conductor holder name must not be null or whitespace.", nameof(holder));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ConductorClaimException(
                "Conductor release requires a non-blank --reason.");
        }

        var normalizedHolder = holder.Trim();
        var normalizedReason = reason.Trim();
        var root = batonRoot ?? BatonPaths.Root;
        var path = GetClaimFilePath(root, identity.FileSlug);
        var timestamp = now ?? DateTime.UtcNow;

        EnsureParentDirectory(path);

        return Task.Run(
            () => MutexGuardedFileLock.RunUnderLock(path, LockNamePrefix, LockTimeout, () =>
            {
                var existing = ReadUnlocked(path);
                if (existing is null || string.IsNullOrWhiteSpace(existing.Holder))
                {
                    throw new ConductorClaimException(
                        $"Repository '{identity.Value}' is not currently claimed by any conductor.");
                }

                if (!string.Equals(existing.Holder, normalizedHolder, StringComparison.Ordinal))
                {
                    throw new ConductorClaimException(
                        $"Repository '{identity.Value}' is currently claimed by conductor '{existing.Holder}', not '{normalizedHolder}'. "
                        + "Claims can only be released by their current holder.");
                }

                var transitions = new List<ConductorClaimTransition>(existing.Transitions ?? []);
                transitions.Add(new ConductorClaimTransition(
                    Kind: ConductorClaimTransitionKind.Release,
                    Holder: normalizedHolder,
                    DisplacedHolder: null,
                    Reason: normalizedReason,
                    Timestamp: timestamp));

                var record = new ConductorClaimRecord(
                    Repository: identity.Value,
                    RepositorySlug: identity.FileSlug,
                    Holder: null,
                    AcquiredAt: null,
                    Takeover: null,
                    Transitions: transitions);

                WriteUnlocked(path, record);
                return record;
            }),
            cancellationToken);
    }

    /// <summary>
    /// Reads the durable claim record for <paramref name="identity"/> if present, under lock.
    /// </summary>
    public static Task<ConductorClaimRecord?> GetClaimAsync(
        RepositoryIdentity identity,
        string? batonRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var root = batonRoot ?? BatonPaths.Root;
        var path = GetClaimFilePath(root, identity.FileSlug);

        if (!File.Exists(path))
        {
            return Task.FromResult<ConductorClaimRecord?>(null);
        }

        return Task.Run(
            () => MutexGuardedFileLock.RunUnderLock(path, LockNamePrefix, LockTimeout, () => ReadUnlocked(path)),
            cancellationToken);
    }

    /// <summary>
    /// Lists all currently held repository claims under <paramref name="batonRoot"/> (spec/baton.md §14).
    /// </summary>
    public static Task<IReadOnlyList<ConductorClaimSummary>> ListHeldClaimsAsync(
        string? batonRoot = null,
        CancellationToken cancellationToken = default)
    {
        var root = batonRoot ?? BatonPaths.Root;
        if (!Directory.Exists(root))
        {
            return Task.FromResult<IReadOnlyList<ConductorClaimSummary>>([]);
        }

        return Task.Run(
            () =>
            {
                var summaries = new List<ConductorClaimSummary>();

                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    var path = Path.Combine(directory, BatonPaths.ConductorClaimFileName);
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    // Read under lock to serialize against in-flight transitions.
                    var record = MutexGuardedFileLock.RunUnderLock(
                        path, LockNamePrefix, LockTimeout, () => ReadUnlocked(path));

                    if (record is not null && !string.IsNullOrWhiteSpace(record.Holder) && record.AcquiredAt.HasValue)
                    {
                        summaries.Add(new ConductorClaimSummary(
                            Repository: record.Repository,
                            RepositorySlug: record.RepositorySlug,
                            Holder: record.Holder,
                            AcquiredAt: record.AcquiredAt.Value,
                            Takeover: record.Takeover));
                    }
                }

                return (IReadOnlyList<ConductorClaimSummary>)summaries
                    .OrderBy(s => s.Repository, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            },
            cancellationToken);
    }

    private static string GetClaimFilePath(string root, string repositorySlug) =>
        Path.Combine(root, repositorySlug, BatonPaths.ConductorClaimFileName);

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    internal static ConductorClaimRecord? ReadUnlocked(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConductorClaimException(
                $"Could not read the conductor claim file at '{path}': {ex.Message}. "
                + "Refusing to proceed — state cannot be verified.",
                ex);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ConductorClaimException(
                $"The conductor claim file at '{path}' is empty. "
                + "Refusing to interpret it as an empty registry — empty content indicates corrupt state.");
        }

        ConductorClaimRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<ConductorClaimRecord>(text, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new ConductorClaimException(
                $"The conductor claim file at '{path}' is not valid JSON: {ex.Message}. "
                + "Refusing to interpret it as an empty registry to preserve existing claims.",
                ex);
        }

        if (record is null || string.IsNullOrWhiteSpace(record.Repository) || string.IsNullOrWhiteSpace(record.RepositorySlug))
        {
            throw new ConductorClaimException(
                $"The conductor claim file at '{path}' has an invalid or incomplete shape. Refusing to proceed.");
        }

        return record;
    }

    internal static void WriteUnlocked(string path, ConductorClaimRecord record)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(record, SerializerOptions) + "\n", new UTF8Encoding(false));
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
            }

            throw new ConductorClaimException($"Could not write the conductor claim at '{path}': {ex.Message}", ex);
        }
    }
}
