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

    // Test-only file-operation seams keep failed replacement paths observable without touching an
    // operator's Baton root. Production always uses the platform file APIs below.
    internal static Func<string, string> ReadText { get; set; } = path => File.ReadAllText(path, Encoding.UTF8);
    internal static Action<string, string> WriteText { get; set; } =
        (path, text) => File.WriteAllText(path, text, new UTF8Encoding(false));
    internal static Action<string, string> MoveOverwriting { get; set; } =
        (source, destination) => File.Move(source, destination, overwrite: true);
    internal static Action<string> DeleteFile { get; set; } = File.Delete;

    internal static void ResetFileOperations()
    {
        ReadText = path => File.ReadAllText(path, Encoding.UTF8);
        WriteText = (path, text) => File.WriteAllText(path, text, new UTF8Encoding(false));
        MoveOverwriting = (source, destination) => File.Move(source, destination, overwrite: true);
        DeleteFile = File.Delete;
    }

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
            () => RunUnderClaimLock(path, () =>
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
            () => RunUnderClaimLock(path, () =>
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
            () => RunUnderClaimLock(path, () =>
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

        return Task.Run(
            () => RunUnderClaimLock(path, () => ReadUnlocked(path)),
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

        return Task.Run(
            () =>
            {
                var summaries = new List<ConductorClaimSummary>();

                foreach (var directory in EnumerateClaimDirectories(root))
                {
                    var path = Path.Combine(directory, BatonPaths.ConductorClaimFileName);

                    // The read itself classifies absence under lock, so an inaccessible claim can
                    // never be projected as absent between an existence probe and the read.
                    var record = RunUnderClaimLock(path, () => ReadUnlocked(path));

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
        InClaimStoreBoundary(
            () => Path.Combine(root, repositorySlug, BatonPaths.ConductorClaimFileName),
            $"Could not determine the conductor claim file location for repository '{repositorySlug}'");

    private static void EnsureParentDirectory(string path)
    {
        InClaimStoreBoundary(
            () =>
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            },
            $"Could not access the conductor claim directory for '{path}'");
    }

    internal static ConductorClaimRecord? ReadUnlocked(string path)
    {
        string text;
        try
        {
            text = ReadText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Only the actual open result may establish that a claim is absent.
            return null;
        }
        catch (Exception ex) when (IsClaimStoreBoundaryFailure(ex))
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

        ValidateRecord(path, record);

        return record;
    }

    private static IReadOnlyList<string> EnumerateClaimDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception ex) when (IsClaimStoreBoundaryFailure(ex))
        {
            throw new ConductorClaimException(
                $"Could not enumerate conductor claim files under '{root}': {ex.Message}. "
                + "Refusing to proceed — state cannot be verified.",
                ex);
        }
    }

    private static T RunUnderClaimLock<T>(string path, Func<T> operation)
    {
        try
        {
            return MutexGuardedFileLock.RunUnderLock(path, LockNamePrefix, LockTimeout, operation);
        }
        catch (ConductorClaimException)
        {
            throw;
        }
        catch (Exception ex) when (IsClaimStoreBoundaryFailure(ex))
        {
            throw new ConductorClaimException(
                $"Could not access the conductor claim file at '{path}': {ex.Message}. "
                + "Refusing to proceed — state cannot be verified.",
                ex);
        }
    }

    private static T InClaimStoreBoundary<T>(Func<T> operation, string message)
    {
        try
        {
            return operation();
        }
        catch (Exception ex) when (IsClaimStoreBoundaryFailure(ex))
        {
            throw new ConductorClaimException($"{message}: {ex.Message}. Refusing to proceed — state cannot be verified.", ex);
        }
    }

    private static void InClaimStoreBoundary(Action operation, string message) =>
        InClaimStoreBoundary(
            () =>
            {
                operation();
                return true;
            },
            message);

    private static bool IsClaimStoreBoundaryFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    internal static void WriteUnlocked(string path, ConductorClaimRecord record)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            WriteText(tempPath, JsonSerializer.Serialize(record, SerializerOptions) + "\n");
            MoveOverwriting(tempPath, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                DeleteFile(tempPath);
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
            }

            throw new ConductorClaimException($"Could not write the conductor claim at '{path}': {ex.Message}", ex);
        }
    }

    private static void ValidateRecord(string path, ConductorClaimRecord? record)
    {
        if (record is null || string.IsNullOrWhiteSpace(record.Repository) || string.IsNullOrWhiteSpace(record.RepositorySlug))
        {
            throw Corrupt(path, "it has an invalid or incomplete shape");
        }

        var canonical = RepositoryIdentity.TryCanonicalize(record.Repository);
        if (canonical is null || !string.Equals(canonical, record.Repository, StringComparison.Ordinal)
            || !string.Equals(RepositoryIdentity.FileSlugFor(canonical), record.RepositorySlug, StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), record.RepositorySlug, StringComparison.Ordinal))
        {
            throw Corrupt(path, "its repository identity or repository slug does not match its storage location");
        }

        if (record.Transitions is not { Count: > 0 })
        {
            throw Corrupt(path, "its audit transition history is missing");
        }

        string? holder = null;
        DateTime? acquiredAt = null;
        ConductorTakeoverProvenance? takeover = null;
        DateTime? previousTimestamp = null;

        foreach (var transition in record.Transitions)
        {
            if (transition is null || !Enum.IsDefined(transition.Kind) || !IsUtcTimestamp(transition.Timestamp)
                || (previousTimestamp.HasValue && transition.Timestamp < previousTimestamp.Value))
            {
                throw Corrupt(path, "its audit transition history is malformed or impossible");
            }

            switch (transition.Kind)
            {
                case ConductorClaimTransitionKind.Claim when holder is null
                    && !string.IsNullOrWhiteSpace(transition.Holder)
                    && transition.DisplacedHolder is null && transition.Reason is null:
                    holder = transition.Holder;
                    acquiredAt = transition.Timestamp;
                    takeover = null;
                    break;
                case ConductorClaimTransitionKind.Takeover when holder is not null
                    && !string.IsNullOrWhiteSpace(transition.Holder)
                    && !string.Equals(holder, transition.Holder, StringComparison.Ordinal)
                    && string.Equals(holder, transition.DisplacedHolder, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(transition.Reason):
                    holder = transition.Holder;
                    acquiredAt = transition.Timestamp;
                    takeover = new ConductorTakeoverProvenance(transition.DisplacedHolder!, transition.Reason!, transition.Timestamp);
                    break;
                case ConductorClaimTransitionKind.Release when holder is not null
                    && string.Equals(holder, transition.Holder, StringComparison.Ordinal)
                    && transition.DisplacedHolder is null && !string.IsNullOrWhiteSpace(transition.Reason):
                    holder = null;
                    acquiredAt = null;
                    takeover = null;
                    break;
                default:
                    throw Corrupt(path, "its audit transition history is malformed or impossible");
            }

            previousTimestamp = transition.Timestamp;
        }

        if (!string.Equals(holder, record.Holder, StringComparison.Ordinal)
            || acquiredAt != record.AcquiredAt || !Equals(takeover, record.Takeover))
        {
            throw Corrupt(path, "its current claim projection does not match its audit transition history");
        }
    }

    private static bool IsUtcTimestamp(DateTime timestamp) =>
        timestamp != default && timestamp.Kind == DateTimeKind.Utc;

    private static ConductorClaimException Corrupt(string path, string detail) =>
        new($"The conductor claim file at '{path}' is corrupt: {detail}. Refusing to proceed and preserving the existing file.");
}
