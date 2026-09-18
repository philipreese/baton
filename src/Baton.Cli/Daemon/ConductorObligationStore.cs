using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton;
using Baton.Status;

namespace Baton.Cli.Daemon;

/// <summary>The durable lifecycle projected for one conductor obligation.</summary>
public enum ConductorObligationStatus
{
    Pending,
    Submitted,
    TransportAcknowledged,
    ActionObserved,
    Blocked,
    Unsupported,
}

/// <summary>
/// The immutable payload of an actionable request. The idempotency key is the caller's stable
/// identity; the store assigns the obligation id once and refuses any later payload drift.
/// </summary>
public sealed record ConductorObligationRequest(
    string IdempotencyKey,
    string TargetProject,
    string? TargetRoom,
    string? TargetExecution,
    string? PullRequestHead,
    string RequestedAction,
    string Owner,
    DateTimeOffset CreatedAt,
    string Adapter,
    string AdapterCapability,
    bool AdapterSupported,
    string? UnsupportedReason = null);

/// <summary>A durable obligation projection; status is never inferred from transport acceptance.</summary>
public sealed record ConductorObligation(
    string ObligationId,
    string IdempotencyKey,
    string TargetProject,
    string? TargetRoom,
    string? TargetExecution,
    string? PullRequestHead,
    string RequestedAction,
    string Owner,
    DateTimeOffset CreatedAt,
    string Adapter,
    string AdapterCapability,
    bool AdapterSupported,
    ConductorObligationStatus Status,
    DateTimeOffset? SubmittedAt = null,
    DateTimeOffset? TransportAcknowledgedAt = null,
    DateTimeOffset? ActionObservedAt = null,
    string? TransportReceipt = null,
    string? Reason = null,
    string? ActionProof = null);

/// <summary>The result returned by a transport boundary. Accepted means receipt only.</summary>
public sealed record ConductorTransportResult(bool Accepted, string? Receipt = null);

/// <summary>A transport boundary supplied by a caller; this package does not choose a vendor.</summary>
public delegate Task<ConductorTransportResult> ConductorObligationTransport(
    ConductorObligation obligation,
    CancellationToken cancellationToken);

/// <summary>The same idempotency key was presented with a different authoritative payload.</summary>
public sealed class ConductorObligationConflictException : BatonFlowException
{
    public ConductorObligationConflictException(string idempotencyKey, string message)
        : base($"Conductor obligation idempotency key '{idempotencyKey}' conflicts with its retained payload: {message}")
    {
        IdempotencyKey = idempotencyKey;
    }

    public string IdempotencyKey { get; }
}

/// <summary>A retained obligation fact or projection could not be safely interpreted.</summary>
public sealed class ConductorObligationStoreException : BatonFlowException
{
    public ConductorObligationStoreException(string message)
        : base(message)
    {
    }

    public ConductorObligationStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Durable, transport-independent conductor-obligation state. Facts are appended to the existing
/// <see cref="FleetEventLog"/>; the JSON file is only a recoverable materialized projection, never a
/// second event log. A transport acknowledgement therefore cannot close an obligation.
/// </summary>
public sealed class ConductorObligationStore
{
    private const string LockNamePrefix = "baton-conductor-obligations";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly HashSet<FleetEventKind> ObligationKinds =
    [
        FleetEventKind.ConductorObligationPending,
        FleetEventKind.ConductorObligationSubmitted,
        FleetEventKind.ConductorObligationTransportAcknowledged,
        FleetEventKind.ConductorObligationActionObserved,
        FleetEventKind.ConductorObligationBlocked,
        FleetEventKind.ConductorObligationUnsupported,
    ];

    private readonly FleetEventLog _eventLog;
    private readonly string _snapshotPath;
    private readonly Func<DateTimeOffset> _now;

    public ConductorObligationStore(
        FleetEventLog eventLog,
        string? snapshotPath = null,
        Func<DateTimeOffset>? now = null)
    {
        _eventLog = eventLog ?? throw new ArgumentNullException(nameof(eventLog));
        _snapshotPath = snapshotPath ?? BatonPaths.ConductorObligationsFile;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        ArgumentException.ThrowIfNullOrEmpty(_snapshotPath);
    }

    /// <summary>Creates one pending fact, or returns the existing identical obligation.</summary>
    public async Task<ConductorObligation> EnqueueAsync(
        ConductorObligationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var current = await ReadAsync(request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            RequireSamePayload(current, request);
            return current;
        }

        var obligation = NewObligation(request);
        await AppendAsync(FleetEventKind.ConductorObligationPending, obligation, cancellationToken)
            .ConfigureAwait(false);
        var retained = await ReadAsync(request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (retained is not null && !string.Equals(retained.ObligationId, obligation.ObligationId, StringComparison.Ordinal))
        {
            RequireSamePayload(retained, request);
            await SaveProjectionAsync(retained, cancellationToken).ConfigureAwait(false);
            return retained;
        }

        if (!request.AdapterSupported)
        {
            var reason = request.UnsupportedReason ?? "the adapter does not support this obligation";
            await AppendAsync(
                    FleetEventKind.ConductorObligationUnsupported,
                    obligation with { Status = ConductorObligationStatus.Unsupported, Reason = reason },
                    cancellationToken,
                    reason: reason)
                .ConfigureAwait(false);
            return (await ReadAsync(request.IdempotencyKey, cancellationToken).ConfigureAwait(false))!;
        }

        await SaveProjectionAsync(obligation, cancellationToken).ConfigureAwait(false);
        return obligation;
    }

    /// <summary>Reads one obligation by stable idempotency key after replaying retained facts.</summary>
    public async Task<ConductorObligation?> ReadAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        var projections = await ReadProjectedAsync(cancellationToken).ConfigureAwait(false);
        return projections.FirstOrDefault(item =>
            string.Equals(item.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
    }

    /// <summary>Returns obligations that can still receive work after a process restart.</summary>
    public async Task<IReadOnlyList<ConductorObligation>> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        var projections = await ReadProjectedAsync(cancellationToken).ConfigureAwait(false);
        return projections
            .Where(item => item.Status is ConductorObligationStatus.Pending
                or ConductorObligationStatus.Submitted
                or ConductorObligationStatus.TransportAcknowledged)
            .OrderBy(item => item.CreatedAt)
            .ToList();
    }

    /// <summary>
    /// Submits through the caller's boundary. A crash after the boundary returns is safe: a later
    /// call retries the same idempotency key until the local transport-ack fact is durable.
    /// </summary>
    public async Task<ConductorObligation> SubmitAsync(
        string idempotencyKey,
        ConductorObligationTransport transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ArgumentNullException.ThrowIfNull(transport);
        var obligation = await ReadAsync(idempotencyKey, cancellationToken).ConfigureAwait(false)
            ?? throw new ConductorObligationStoreException($"No conductor obligation exists for '{idempotencyKey}'.");

        if (obligation.Status is ConductorObligationStatus.ActionObserved
            or ConductorObligationStatus.Blocked
            or ConductorObligationStatus.Unsupported
            or ConductorObligationStatus.TransportAcknowledged)
        {
            return obligation;
        }

        if (!obligation.AdapterSupported)
        {
            // This is also the recovery path for a crash between the pending and unsupported facts.
            var reason = obligation.Reason ?? "the adapter does not support this obligation";
            await AppendAsync(
                    FleetEventKind.ConductorObligationUnsupported,
                    obligation with { Status = ConductorObligationStatus.Unsupported, Reason = reason },
                    cancellationToken,
                    reason: reason)
                .ConfigureAwait(false);
            return (await ReadAsync(idempotencyKey, cancellationToken).ConfigureAwait(false))!;
        }

        if (obligation.Status == ConductorObligationStatus.Pending)
        {
            obligation = obligation with
            {
                Status = ConductorObligationStatus.Submitted,
                SubmittedAt = _now().ToUniversalTime(),
            };
            await AppendAsync(FleetEventKind.ConductorObligationSubmitted, obligation, cancellationToken)
                .ConfigureAwait(false);
        }

        var response = await transport(obligation, cancellationToken).ConfigureAwait(false)
            ?? throw new ConductorObligationStoreException("The conductor transport returned no result.");
        if (!response.Accepted)
        {
            return (await ReadAsync(idempotencyKey, cancellationToken).ConfigureAwait(false))!;
        }

        var acknowledged = obligation with
        {
            Status = ConductorObligationStatus.TransportAcknowledged,
            TransportAcknowledgedAt = _now().ToUniversalTime(),
            TransportReceipt = response.Receipt,
        };
        await AppendAsync(
                FleetEventKind.ConductorObligationTransportAcknowledged,
                acknowledged,
                cancellationToken,
                receipt: response.Receipt)
            .ConfigureAwait(false);
        return (await ReadAsync(idempotencyKey, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Records independent evidence that the requested action actually occurred.</summary>
    public async Task<ConductorObligation> ObserveActionAsync(
        string idempotencyKey,
        string actionProof,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionProof);
        var obligation = await ReadAsync(idempotencyKey, cancellationToken).ConfigureAwait(false)
            ?? throw new ConductorObligationStoreException($"No conductor obligation exists for '{idempotencyKey}'.");
        if (obligation.Status == ConductorObligationStatus.ActionObserved)
        {
            if (!string.Equals(obligation.ActionProof, actionProof, StringComparison.Ordinal))
            {
                throw new ConductorObligationConflictException(
                    idempotencyKey, "the action-observed completion proof differs");
            }

            return obligation;
        }

        if (obligation.Status is not (ConductorObligationStatus.Submitted
            or ConductorObligationStatus.TransportAcknowledged))
        {
            throw new ConductorObligationStoreException(
                $"Conductor obligation '{idempotencyKey}' cannot be action-observed from status '{obligation.Status}'.");
        }

        var observed = obligation with
        {
            Status = ConductorObligationStatus.ActionObserved,
            ActionObservedAt = _now().ToUniversalTime(),
            ActionProof = actionProof,
        };
        await AppendAsync(
                FleetEventKind.ConductorObligationActionObserved,
                observed,
                cancellationToken,
                actionProof: actionProof)
            .ConfigureAwait(false);
        return (await ReadAsync(idempotencyKey, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Closes an open obligation explicitly without claiming the requested action occurred.</summary>
    public async Task<ConductorObligation> BlockAsync(
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var obligation = await ReadAsync(idempotencyKey, cancellationToken).ConfigureAwait(false)
            ?? throw new ConductorObligationStoreException($"No conductor obligation exists for '{idempotencyKey}'.");
        if (obligation.Status == ConductorObligationStatus.Blocked)
        {
            if (!string.Equals(obligation.Reason, reason, StringComparison.Ordinal))
            {
                throw new ConductorObligationConflictException(idempotencyKey, "the blocked reason differs");
            }

            return obligation;
        }

        if (obligation.Status is ConductorObligationStatus.ActionObserved or ConductorObligationStatus.Unsupported)
        {
            throw new ConductorObligationStoreException(
                $"Conductor obligation '{idempotencyKey}' has terminal status '{obligation.Status}'.");
        }

        var blocked = obligation with { Status = ConductorObligationStatus.Blocked, Reason = reason };
        await AppendAsync(FleetEventKind.ConductorObligationBlocked, blocked, cancellationToken, reason: reason)
            .ConfigureAwait(false);
        return (await ReadAsync(idempotencyKey, cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<FleetEvent?> AppendAsync(
        FleetEventKind kind,
        ConductorObligation obligation,
        CancellationToken cancellationToken,
        string? reason = null,
        string? receipt = null,
        string? actionProof = null)
    {
        var draft = new FleetEventDraft(
            kind,
            DedupeKey(kind, obligation.IdempotencyKey),
            _now().ToUniversalTime(),
            ObligationId: obligation.ObligationId,
            ObligationIdempotencyKey: obligation.IdempotencyKey,
            ObligationTargetProject: obligation.TargetProject,
            ObligationTargetRoom: obligation.TargetRoom,
            ObligationTargetExecution: obligation.TargetExecution,
            ObligationPullRequestHead: obligation.PullRequestHead,
            ObligationRequestedAction: obligation.RequestedAction,
            ObligationOwner: obligation.Owner,
            ObligationCreatedAt: obligation.CreatedAt,
            ObligationAdapter: obligation.Adapter,
            ObligationAdapterCapability: obligation.AdapterCapability,
            ObligationAdapterSupported: obligation.AdapterSupported,
            ObligationReason: reason ?? obligation.Reason,
            ObligationTransportReceipt: receipt ?? obligation.TransportReceipt,
            ObligationActionProof: actionProof ?? obligation.ActionProof);
        var appended = await _eventLog.Append(draft, cancellationToken).ConfigureAwait(false);
        if (appended is not null)
        {
            await SaveProjectionAsync(obligation, cancellationToken).ConfigureAwait(false);
        }

        return appended;
    }

    private async Task<IReadOnlyList<ConductorObligation>> ReadProjectedAsync(CancellationToken cancellationToken)
    {
        var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var rows = await _eventLog.ReadRetained(cancellationToken).ConfigureAwait(false);
        var eventGroups = rows
            .Where(row => ObligationKinds.Contains(row.Kind))
            .GroupBy(row => row.ObligationIdempotencyKey ?? string.Empty, StringComparer.Ordinal);
        var projected = snapshot.ToDictionary(item => item.IdempotencyKey, StringComparer.Ordinal);
        foreach (var group in eventGroups)
        {
            if (string.IsNullOrEmpty(group.Key))
            {
                throw new ConductorObligationStoreException("A conductor-obligation fact has no idempotency key.");
            }

            projected[group.Key] = Project(group.OrderBy(row => row.Id).ToList());
        }

        return projected.Values.ToList();
    }

    private static ConductorObligation Project(IReadOnlyList<FleetEvent> rows)
    {
        if (rows.Count == 0) throw new ArgumentException("At least one fact is required.", nameof(rows));
        var first = Payload(rows[0]);
        var current = first with { Status = StatusFor(rows[0].Kind) };
        if (rows[0].Kind == FleetEventKind.ConductorObligationPending)
        {
            current = current with { Status = ConductorObligationStatus.Pending };
        }

        var seen = new HashSet<FleetEventKind>();
        foreach (var row in rows)
        {
            var payload = Payload(row);
            RequireSamePayload(current, payload);
            if (!seen.Add(row.Kind))
            {
                throw new ConductorObligationStoreException(
                    $"Conductor obligation '{current.IdempotencyKey}' contains duplicate '{row.Kind}' facts.");
            }

            var next = StatusFor(row.Kind);
            if (!CanTransition(current.Status, next))
            {
                throw new ConductorObligationStoreException(
                    $"Conductor obligation '{current.IdempotencyKey}' contradicts status '{current.Status}' with '{next}'.");
            }

            current = current with
            {
                Status = next,
                SubmittedAt = row.Kind == FleetEventKind.ConductorObligationSubmitted
                    ? row.At : current.SubmittedAt,
                TransportAcknowledgedAt = row.Kind == FleetEventKind.ConductorObligationTransportAcknowledged
                    ? row.At : current.TransportAcknowledgedAt,
                ActionObservedAt = row.Kind == FleetEventKind.ConductorObligationActionObserved
                    ? row.At : current.ActionObservedAt,
                TransportReceipt = row.ObligationTransportReceipt ?? current.TransportReceipt,
                Reason = row.ObligationReason ?? current.Reason,
                ActionProof = row.ObligationActionProof ?? current.ActionProof,
            };
        }

        return current;
    }

    private static ConductorObligation Payload(FleetEvent row)
    {
        if (string.IsNullOrWhiteSpace(row.ObligationId)
            || string.IsNullOrWhiteSpace(row.ObligationIdempotencyKey)
            || string.IsNullOrWhiteSpace(row.ObligationTargetProject)
            || string.IsNullOrWhiteSpace(row.ObligationRequestedAction)
            || string.IsNullOrWhiteSpace(row.ObligationOwner)
            || row.ObligationCreatedAt is not { } createdAt
            || string.IsNullOrWhiteSpace(row.ObligationAdapter)
            || string.IsNullOrWhiteSpace(row.ObligationAdapterCapability)
            || row.ObligationAdapterSupported is not { } supported)
        {
            throw new ConductorObligationStoreException(
                $"Conductor obligation fact '{row.DedupeKey}' has an incomplete payload.");
        }

        return new(
            row.ObligationId,
            row.ObligationIdempotencyKey,
            row.ObligationTargetProject,
            row.ObligationTargetRoom,
            row.ObligationTargetExecution,
            row.ObligationPullRequestHead,
            row.ObligationRequestedAction,
            row.ObligationOwner,
            createdAt,
            row.ObligationAdapter,
            row.ObligationAdapterCapability,
            supported,
            ConductorObligationStatus.Pending);
    }

    private static ConductorObligationStatus StatusFor(FleetEventKind kind) => kind switch
    {
        FleetEventKind.ConductorObligationPending => ConductorObligationStatus.Pending,
        FleetEventKind.ConductorObligationSubmitted => ConductorObligationStatus.Submitted,
        FleetEventKind.ConductorObligationTransportAcknowledged => ConductorObligationStatus.TransportAcknowledged,
        FleetEventKind.ConductorObligationActionObserved => ConductorObligationStatus.ActionObserved,
        FleetEventKind.ConductorObligationBlocked => ConductorObligationStatus.Blocked,
        FleetEventKind.ConductorObligationUnsupported => ConductorObligationStatus.Unsupported,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a conductor-obligation fact."),
    };

    private static bool CanTransition(ConductorObligationStatus current, ConductorObligationStatus next) =>
        current == next
        || (current == ConductorObligationStatus.Pending
            && next is ConductorObligationStatus.Submitted
                or ConductorObligationStatus.Blocked
                or ConductorObligationStatus.Unsupported)
        || (current == ConductorObligationStatus.Submitted
            && next is ConductorObligationStatus.TransportAcknowledged
                or ConductorObligationStatus.ActionObserved
                or ConductorObligationStatus.Blocked)
        || (current == ConductorObligationStatus.TransportAcknowledged
            && next is ConductorObligationStatus.ActionObserved
                or ConductorObligationStatus.Blocked);

    private async Task SaveProjectionAsync(ConductorObligation obligation, CancellationToken cancellationToken)
    {
        await Task.Run(
                () => MutexGuardedFileLock.RunUnderLock(
                    _snapshotPath,
                    LockNamePrefix,
                    LockTimeout,
                    () =>
                    {
                        var current = ReadSnapshotUnlocked();
                        var updated = current
                            .Where(item => !string.Equals(item.IdempotencyKey, obligation.IdempotencyKey, StringComparison.Ordinal))
                            .Append(obligation)
                            .OrderBy(item => item.CreatedAt)
                            .ToList();
                        var parent = Path.GetDirectoryName(_snapshotPath);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        var temp = $"{_snapshotPath}.{Guid.NewGuid():N}.tmp";
                        try
                        {
                            File.WriteAllText(temp, JsonSerializer.Serialize(updated, Json) + "\n", new UTF8Encoding(false));
                            File.Move(temp, _snapshotPath, overwrite: true);
                        }
                        finally
                        {
                            File.Delete(temp);
                        }
                    }),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<IReadOnlyList<ConductorObligation>> ReadSnapshotAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () => MutexGuardedFileLock.RunUnderLock(
                _snapshotPath,
                LockNamePrefix,
                LockTimeout,
                () => (IReadOnlyList<ConductorObligation>)ReadSnapshotUnlocked()),
            cancellationToken);

    private List<ConductorObligation> ReadSnapshotUnlocked()
    {
        if (!File.Exists(_snapshotPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ConductorObligation>>(File.ReadAllText(_snapshotPath, Encoding.UTF8), Json)
                ?? throw new InvalidDataException("The conductor-obligation projection contains JSON null.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new ConductorObligationStoreException(
                $"Conductor-obligation projection '{_snapshotPath}' is malformed.", ex);
        }
    }

    private static ConductorObligation NewObligation(ConductorObligationRequest request) => new(
        Guid.NewGuid().ToString("N"),
        request.IdempotencyKey,
        request.TargetProject,
        request.TargetRoom,
        request.TargetExecution,
        request.PullRequestHead,
        request.RequestedAction,
        request.Owner,
        request.CreatedAt.ToUniversalTime(),
        request.Adapter,
        request.AdapterCapability,
        request.AdapterSupported,
        ConductorObligationStatus.Pending,
        Reason: request.UnsupportedReason);

    private static void ValidateRequest(ConductorObligationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetProject);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestedAction);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AdapterCapability);
        if (string.IsNullOrWhiteSpace(request.TargetRoom)
            && string.IsNullOrWhiteSpace(request.TargetExecution)
            && string.IsNullOrWhiteSpace(request.PullRequestHead))
        {
            throw new ArgumentException(
                "An obligation needs a room, execution, or pull-request head target.", nameof(request));
        }

        if (!request.AdapterSupported && string.IsNullOrWhiteSpace(request.UnsupportedReason))
        {
            throw new ArgumentException(
                "An unsupported adapter needs an explicit reason.", nameof(request));
        }
    }

    private static void RequireSamePayload(ConductorObligation current, ConductorObligationRequest request)
    {
        if (!SamePayload(current, request))
        {
            throw new ConductorObligationConflictException(
                request.IdempotencyKey, "target, action, owner, age, or adapter capability changed");
        }
    }

    private static void RequireSamePayload(ConductorObligation current, ConductorObligation other)
    {
        if (!SamePayload(current, other))
        {
            throw new ConductorObligationConflictException(
                current.IdempotencyKey, "a later fact changed the authoritative payload");
        }
    }

    private static bool SamePayload(ConductorObligation left, ConductorObligationRequest right) =>
        string.Equals(left.IdempotencyKey, right.IdempotencyKey, StringComparison.Ordinal)
        && string.Equals(left.TargetProject, right.TargetProject, StringComparison.Ordinal)
        && string.Equals(left.TargetRoom, right.TargetRoom, StringComparison.Ordinal)
        && string.Equals(left.TargetExecution, right.TargetExecution, StringComparison.Ordinal)
        && string.Equals(left.PullRequestHead, right.PullRequestHead, StringComparison.Ordinal)
        && string.Equals(left.RequestedAction, right.RequestedAction, StringComparison.Ordinal)
        && string.Equals(left.Owner, right.Owner, StringComparison.Ordinal)
        && left.CreatedAt == right.CreatedAt.ToUniversalTime()
        && string.Equals(left.Adapter, right.Adapter, StringComparison.Ordinal)
        && string.Equals(left.AdapterCapability, right.AdapterCapability, StringComparison.Ordinal)
        && left.AdapterSupported == right.AdapterSupported;

    private static bool SamePayload(ConductorObligation left, ConductorObligation right) =>
        left.ObligationId == right.ObligationId
        && SamePayload(left, new ConductorObligationRequest(
            right.IdempotencyKey, right.TargetProject, right.TargetRoom, right.TargetExecution,
            right.PullRequestHead, right.RequestedAction, right.Owner, right.CreatedAt, right.Adapter,
            right.AdapterCapability, right.AdapterSupported));

    private static string DedupeKey(FleetEventKind kind, string idempotencyKey) =>
        $"conductor-obligation:{kind.ToString().ToCamelCase()}:{idempotencyKey}";
}

internal static class ConductorObligationStringExtensions
{
    internal static string ToCamelCase(this string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
