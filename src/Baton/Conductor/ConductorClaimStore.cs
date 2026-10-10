using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
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
                        if (existing.Stopped)
                            throw new ConductorClaimException("This hosted acquisition is stopped; explicitly release or take over before a new claim.");
                        // Idempotent claim by the same holder: do not invent a second acquisition.
                        return existing;
                    }

                    var takeoverCommand = $"baton conductor takeover {normalizedHolder} --reason <text>";
                    throw new ConductorClaimException(
                        $"Repository '{identity.Value}' is currently claimed by conductor '{existing.Holder}'. "
                        + $"Use '{takeoverCommand}' to replace the current holder.",
                        takeoverCommand);
                }

                var generation = Guid.NewGuid().ToString("N");
                var transitions = new List<ConductorClaimTransition>(existing?.Transitions ?? []);
                transitions.Add(new ConductorClaimTransition(
                    Kind: ConductorClaimTransitionKind.Claim,
                    Holder: normalizedHolder,
                    DisplacedHolder: null,
                    Reason: null,
                    Timestamp: timestamp, Generation: generation));

                var record = new ConductorClaimRecord(
                    Repository: identity.Value,
                    RepositorySlug: identity.FileSlug,
                    Holder: normalizedHolder,
                    AcquiredAt: timestamp,
                    Takeover: null,
                    Transitions: transitions,
                    ClaimGeneration: generation);

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

                var generation = Guid.NewGuid().ToString("N");
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
                    Timestamp: timestamp, Generation: generation));

                var record = new ConductorClaimRecord(
                    Repository: identity.Value,
                    RepositorySlug: identity.FileSlug,
                    Holder: normalizedHolder,
                    AcquiredAt: timestamp,
                    Takeover: takeoverProvenance,
                    Transitions: transitions,
                    ClaimGeneration: generation);

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
                    Transitions: transitions,
                    ClaimGeneration: existing.ClaimGeneration);

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
        CancellationToken cancellationToken = default,
        TimeSpan? lockTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var root = batonRoot ?? BatonPaths.Root;
        var path = GetClaimFilePath(root, identity.FileSlug);

        return Task.Run(
            () => RunUnderClaimLock(path, () => ReadUnlocked(path), lockTimeout),
            cancellationToken);
    }

    /// <summary>
    /// Returns the immutable generation of the current acquisition. Older claim records predate the
    /// field, so their complete retained history is deterministically fingerprinted instead of being
    /// treated as interchangeable with a later acquisition.
    /// </summary>
    public static string GetClaimGeneration(ConductorClaimRecord claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!string.IsNullOrWhiteSpace(claim.ClaimGeneration))
            return claim.ClaimGeneration;

        var history = JsonSerializer.Serialize(claim.Transitions ?? [], SerializerOptions);
        return "legacy-" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{claim.Repository}\n{history}"))).ToLowerInvariant();
    }

    /// <summary>Runs a short synchronous operation while the current claim cannot be replaced.</summary>
    public static T WithCurrentClaim<T>(RepositoryIdentity identity, string batonRoot,
        Func<ConductorClaimRecord?, T> operation, TimeSpan? lockTimeout = null) =>
        RunUnderClaimLock(GetClaimFilePath(batonRoot, identity.FileSlug),
            () => operation(ReadUnlocked(GetClaimFilePath(batonRoot, identity.FileSlug))), lockTimeout);

    /// <summary>Current permission to consume a hosted acquisition; retained evidence is separate.</summary>
    public static bool IsCurrentHostedAuthority(ConductorClaimRecord? claim, string holder, string generation) =>
        IsCurrentHostedAcquisition(claim, holder, generation) && !claim!.Held;

    public static bool IsCurrentHostedAcquisition(ConductorClaimRecord? claim, string holder, string generation) =>
        claim is { Stopped: false } && claim.Holder == holder && GetClaimGeneration(claim) == generation;

    /// <summary>Caller owns queue admission. Replay precedes stale-target validation, under one claim lock.</summary>
    public static ConductorHostedControlReceipt ApplyHostedControl(
        RepositoryIdentity identity, string batonRoot, ConductorHostedControlRequest request,
        string issuer, bool takeover, Action<ConductorClaimRecord> validateTarget,
        TimeSpan? lockTimeout = null) => ApplyHostedControl(identity, batonRoot, request, issuer,
            takeover ? "takeover" : "stop", validateTarget, lockTimeout);

    public static ConductorHostedControlReceipt ApplyHostedControl(
        RepositoryIdentity identity, string batonRoot, ConductorHostedControlRequest request,
        string issuer, string operation, Action<ConductorClaimRecord> validateTarget,
        TimeSpan? lockTimeout = null) => RunUnderClaimLock(GetClaimFilePath(batonRoot, identity.FileSlug), () =>
    {
        var takeover = operation == "takeover";
        var reversible = operation is "hold" or "unhold";
        if (operation is not ("stop" or "takeover" or "hold" or "unhold")
            || reversible && request.ExpectedControlRevision is null)
            throw new ConductorClaimException("Exact control revision is required for Hold or Unhold.");
        ValidateControlInput(request, issuer, takeover, identity.Value);
        var path = GetClaimFilePath(batonRoot, identity.FileSlug);
        var claim = ReadUnlocked(path) ?? throw new ConductorClaimException("Conductor claim is absent.");
        var previous = claim.Transitions!.Select(t => t.Control).FirstOrDefault(c =>
            c is not null && c.Issuer == issuer && c.Request.RequestId == request.RequestId);
        if (previous is not null)
        {
            if (previous.Request != request || previous.Operation != operation)
                throw new ConductorClaimException("Control request ID was already used with different input.");
            return previous;
        }
        var generation = GetClaimGeneration(claim);
        if (claim.Holder != request.Holder || generation != request.ClaimGeneration
            || !takeover && claim.Stopped
            || request.ExpectedControlRevision is { } expected && expected != claim.ControlRevision
            || reversible && claim.Held != (operation == "unhold"))
            throw new ConductorClaimException("Conductor identity changed; refresh before controlling it.");
        validateTarget(claim);
        // Pin the legacy acquisition before appending history: its derived identity must not move.
        var transitions = claim.Transitions!.ToList();
        var acquisition = transitions.FindLastIndex(t => t.Kind is ConductorClaimTransitionKind.Claim or ConductorClaimTransitionKind.Takeover);
        if (transitions[acquisition].Generation is null)
            transitions[acquisition] = transitions[acquisition] with { Generation = generation };
        var timestamp = DateTime.UtcNow;
        var resultGeneration = takeover ? Guid.NewGuid().ToString("N") : generation;
        var resultHolder = takeover ? request.DestinationHolder! : request.Holder;
        var receipt = new ConductorHostedControlReceipt(request, issuer, operation, resultHolder, resultGeneration, timestamp);
        var kind = operation switch
        {
            "takeover" => ConductorClaimTransitionKind.Takeover,
            "hold" => ConductorClaimTransitionKind.Hold,
            "unhold" => ConductorClaimTransitionKind.Unhold,
            _ => ConductorClaimTransitionKind.Stop,
        };
        transitions.Add(new(kind,
            resultHolder, takeover ? claim.Holder : null, request.Reason, timestamp,
            takeover ? resultGeneration : null, receipt));
        WriteUnlocked(path, claim with
        {
            Holder = resultHolder,
            ClaimGeneration = resultGeneration,
            Stopped = operation == "stop",
            Held = operation == "hold",
            AcquiredAt = takeover ? timestamp : claim.AcquiredAt,
            Takeover = takeover ? new(claim.Holder!, request.Reason, timestamp) : claim.Takeover,
            DestinationAddress = takeover ? request.DestinationAddress : claim.DestinationAddress,
            Transitions = transitions,
        });
        return receipt;
    }, lockTimeout);

    private static void ValidateControlInput(ConductorHostedControlRequest request, string issuer,
        bool takeover, string repository)
    {
        static bool Label(string? value, int bound) => !string.IsNullOrWhiteSpace(value)
            && value.Length <= bound && !value.Any(char.IsControl) && value == value.Trim();
        if (request.Repository != repository || !Label(request.Holder, 256)
            || !Label(request.ClaimGeneration, 256) || !Label(request.AttachmentId, 32)
            || request.AttachmentId.Length != 32 || !request.AttachmentId.All(Uri.IsHexDigit)
            || !Label(request.RequestId, 128) || !Label(request.Reason, 1024) || !Label(issuer, 256)
            || request.ExpectedControlRevision is < 0
            || takeover && (!Label(request.DestinationHolder, 256) || !IsSafeHostedHolderLabel(request.DestinationHolder!)
                || !Label(request.DestinationAddress, 512)
                || request.DestinationHolder == request.Holder)
            || !takeover && (request.DestinationHolder is not null || request.DestinationAddress is not null))
            throw new ConductorClaimException("A bounded exact control identity, reason and explicit takeover destination are required.");
    }

    /// <summary>Holder display data admitted by hosted Take Over must remain readable in Glass.</summary>
    public static bool IsSafeHostedHolderLabel(string value) => value.Length <= 256 && !value.Any(char.IsControl)
        && !value.Contains('\\') && !value.Contains(":/", StringComparison.Ordinal) && !value.StartsWith('/');

    /// <summary>
    /// Lists all currently held repository claims under <paramref name="batonRoot"/> (spec/baton.md §14).
    /// </summary>
    public static Task<IReadOnlyList<ConductorClaimSummary>> ListHeldClaimsAsync(
        string? batonRoot = null,
        CancellationToken cancellationToken = default,
        TimeSpan? lockTimeout = null)
    {
        var root = batonRoot ?? BatonPaths.Root;

        return Task.Run(
            () =>
            {
                var summaries = new List<ConductorClaimSummary>();

                foreach (var directory in EnumerateClaimDirectories(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = Path.Combine(directory, BatonPaths.ConductorClaimFileName);

                    // The read itself classifies absence under lock, so an inaccessible claim can
                    // never be projected as absent between an existence probe and the read.
                    var record = RunUnderClaimLock(path, () => ReadUnlocked(path), lockTimeout);

                    if (record is not null && !string.IsNullOrWhiteSpace(record.Holder) && record.AcquiredAt.HasValue)
                    {
                        summaries.Add(new ConductorClaimSummary(
                            Repository: record.Repository,
                            RepositorySlug: record.RepositorySlug,
                            Holder: record.Holder,
                            AcquiredAt: record.AcquiredAt.Value,
                            Takeover: record.Takeover, Stopped: record.Stopped, DestinationAddress: record.DestinationAddress, Held: record.Held));
                    }
                }

                return (IReadOnlyList<ConductorClaimSummary>)summaries
                    .OrderBy(s => s.Repository, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            },
            cancellationToken);
    }

    /// <summary>Retained authority and receipts, including released claims, for bounded operator reconciliation.</summary>
    public static Task<IReadOnlyList<ConductorClaimRecord>> ListRetainedClaimsAsync(string root,
        CancellationToken token = default, TimeSpan? lockTimeout = null) => Task.Run(() =>
    {
        var records = new List<ConductorClaimRecord>();
        foreach (var directory in EnumerateClaimDirectories(root))
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, BatonPaths.ConductorClaimFileName);
            var record = RunUnderClaimLock(path, () => ReadUnlocked(path), lockTimeout);
            if (record is not null) records.Add(record);
        }
        return (IReadOnlyList<ConductorClaimRecord>)records.OrderBy(c => c.Repository, StringComparer.OrdinalIgnoreCase).ToArray();
    }, token);

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

    private static T RunUnderClaimLock<T>(string path, Func<T> operation, TimeSpan? lockTimeout = null)
    {
        try
        {
            return MutexGuardedFileLock.RunUnderLock(path, LockNamePrefix, lockTimeout ?? LockTimeout, operation);
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
        var stopped = false;
        var held = false;
        long controlRevision = 0;
        string? generation = null;
        string? destinationAddress = null;
        var requests = new HashSet<(string, string)>();
        var generations = new HashSet<string>(StringComparer.Ordinal);

        foreach (var transition in record.Transitions)
        {
            if (transition is null || !Enum.IsDefined(transition.Kind) || !IsUtcTimestamp(transition.Timestamp)
                || (previousTimestamp.HasValue && transition.Timestamp < previousTimestamp.Value))
            {
                throw Corrupt(path, "its audit transition history is malformed or impossible");
            }

            var previousGeneration = generation;
            switch (transition.Kind)
            {
                case ConductorClaimTransitionKind.Claim when holder is null
                    && !string.IsNullOrWhiteSpace(transition.Holder)
                    && transition.DisplacedHolder is null && transition.Reason is null:
                    holder = transition.Holder;
                    acquiredAt = transition.Timestamp;
                    takeover = null;
                    stopped = false;
                    held = false;
                    generation = transition.Generation;
                    destinationAddress = null;
                    break;
                case ConductorClaimTransitionKind.Takeover when holder is not null
                    && !string.IsNullOrWhiteSpace(transition.Holder)
                    && !string.Equals(holder, transition.Holder, StringComparison.Ordinal)
                    && string.Equals(holder, transition.DisplacedHolder, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(transition.Reason):
                    holder = transition.Holder;
                    acquiredAt = transition.Timestamp;
                    takeover = new ConductorTakeoverProvenance(transition.DisplacedHolder!, transition.Reason!, transition.Timestamp);
                    stopped = false;
                    held = false;
                    generation = transition.Generation;
                    destinationAddress = transition.Control?.Request?.DestinationAddress;
                    break;
                case ConductorClaimTransitionKind.Release when holder is not null
                    && string.Equals(holder, transition.Holder, StringComparison.Ordinal)
                    && transition.DisplacedHolder is null && !string.IsNullOrWhiteSpace(transition.Reason):
                    holder = null;
                    acquiredAt = null;
                    takeover = null;
                    stopped = false;
                    held = false;
                    destinationAddress = null;
                    break;
                case ConductorClaimTransitionKind.Stop when holder is not null && !stopped
                    && transition.Holder == holder && transition.DisplacedHolder is null
                    && !string.IsNullOrWhiteSpace(transition.Reason) && transition.Control is not null
                    && transition.Generation is null:
                    stopped = true;
                    held = false;
                    break;
                case ConductorClaimTransitionKind.Hold or ConductorClaimTransitionKind.Unhold
                    when holder is not null && !stopped && transition.Holder == holder
                    && transition.DisplacedHolder is null && !string.IsNullOrWhiteSpace(transition.Reason)
                    && transition.Control is not null && transition.Generation is null
                    && held == (transition.Kind == ConductorClaimTransitionKind.Unhold):
                    held = transition.Kind == ConductorClaimTransitionKind.Hold;
                    break;
                default:
                    throw Corrupt(path, "its audit transition history is malformed or impossible");
            }

            if (transition.Control is { } control)
            {
                if (control.Request is null) throw Corrupt(path, "its control request is missing");
                try
                {
                    ValidateControlInput(control.Request, control.Issuer,
                        transition.Kind == ConductorClaimTransitionKind.Takeover, record.Repository);
                }
                catch (ConductorClaimException) { throw Corrupt(path, "its control receipt is malformed"); }
                if (transition.Kind is not (ConductorClaimTransitionKind.Stop or ConductorClaimTransitionKind.Takeover
                        or ConductorClaimTransitionKind.Hold or ConductorClaimTransitionKind.Unhold)
                    || control.Operation != transition.Kind.ToString().ToLowerInvariant()
                    || control.Request.ExpectedControlRevision is { } revision && revision != controlRevision
                    || transition.Kind is ConductorClaimTransitionKind.Hold or ConductorClaimTransitionKind.Unhold
                        && control.Request.ExpectedControlRevision is null
                    || control.AppliedAt != transition.Timestamp || control.ResultHolder != holder
                    || control.ResultGeneration != generation || control.Request.Reason != transition.Reason
                    || previousGeneration is null || control.Request.ClaimGeneration != previousGeneration
                    || transition.Kind != ConductorClaimTransitionKind.Takeover && control.Request.Holder != holder
                    || transition.Kind == ConductorClaimTransitionKind.Takeover
                        && (control.Request.Holder != transition.DisplacedHolder || control.Request.DestinationHolder != holder)
                    || !requests.Add((control.Issuer, control.Request.RequestId)))
                    throw Corrupt(path, "its control receipt conflicts with authority history");
                controlRevision++;
            }
            if (transition.Generation is not null && (transition.Generation.Length > 256
                || string.IsNullOrWhiteSpace(transition.Generation) || transition.Generation.Any(char.IsControl)
                || transition.Kind is not (ConductorClaimTransitionKind.Claim or ConductorClaimTransitionKind.Takeover)
                || !generations.Add(transition.Generation)))
                throw Corrupt(path, "its acquisition generation is malformed");
            previousTimestamp = transition.Timestamp;
        }

        if (!string.Equals(holder, record.Holder, StringComparison.Ordinal)
            || acquiredAt != record.AcquiredAt || !Equals(takeover, record.Takeover)
            || stopped != record.Stopped || held != record.Held || destinationAddress != record.DestinationAddress
            || generation is not null && generation != record.ClaimGeneration)
        {
            throw Corrupt(path, "its current claim projection does not match its audit transition history");
        }
    }

    private static bool IsUtcTimestamp(DateTime timestamp) =>
        timestamp != default && timestamp.Kind == DateTimeKind.Utc;

    private static ConductorClaimException Corrupt(string path, string detail) =>
        new($"The conductor claim file at '{path}' is corrupt: {detail}. Refusing to proceed and preserving the existing file.");
}
