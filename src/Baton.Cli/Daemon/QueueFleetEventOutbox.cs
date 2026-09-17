using System.Text.Json;
using Baton;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;

namespace Baton.Cli.Daemon;

/// <summary>
/// The queue's durable delivery state for the four attempt facts the queue owns. A queue mutation
/// places a serialized draft here; this pump appends it to the fleet log and removes it only in a
/// later queue mutation. The fleet log's dedupe key makes a crash between those two operations safe.
/// </summary>
internal sealed class QueueFleetEventOutbox
{
    internal const int MaxPendingFacts = 256;

    private readonly Func<FleetEventDraft, CancellationToken, Task<FleetEvent?>> _append;

    internal QueueFleetEventOutbox(
        Func<FleetEventDraft, CancellationToken, Task<FleetEvent?>> append)
    {
        _append = append ?? throw new ArgumentNullException(nameof(append));
    }

    /// <summary>Publishes every pending draft in queue order, acknowledging each one separately.</summary>
    internal async Task PumpAsync(string queuePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(queuePath);

        var snapshot = await QueueStore.LoadAsync(queuePath, cancellationToken).ConfigureAwait(false);
        var pending = snapshot.PendingFleetEvents ?? [];
        ValidateNoDuplicateKeys(pending);

        foreach (var raw in pending)
        {
            var draft = ReadAndValidate(raw, snapshot);
            var appended = await _append(draft, cancellationToken).ConfigureAwait(false);
            if (appended is { } eventRow
                && !string.Equals(eventRow.DedupeKey, draft.DedupeKey, StringComparison.Ordinal))
            {
                throw Fault(
                    draft,
                    $"the fleet log returned dedupe key '{eventRow.DedupeKey}' for draft '{draft.DedupeKey}'");
            }

            snapshot = await AcknowledgeAsync(queuePath, raw, draft, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Places one complete draft in the queue snapshot without doing any external I/O.</summary>
    internal static QueueSnapshot Enqueue(QueueSnapshot snapshot, FleetEventDraft draft)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(draft);

        var pending = snapshot.PendingFleetEvents ?? [];
        ValidateBasic(draft);
        ValidateNoDuplicateKeys(pending);

        var serialized = FleetEventLog.SerializeDraft(draft);
        using var document = JsonDocument.Parse(serialized);
        var element = document.RootElement.Clone();
        foreach (var existing in pending)
        {
            if (!string.Equals(GetString(existing, "dedupeKey"), draft.DedupeKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(existing.GetRawText(), element.GetRawText(), StringComparison.Ordinal))
            {
                return snapshot;
            }

            throw Fault(
                draft,
                $"a different pending payload already uses dedupe key '{draft.DedupeKey}'");
        }

        if (pending.Count >= MaxPendingFacts)
        {
            throw Fault(
                draft,
                $"the pending fleet-fact outbox is full at {MaxPendingFacts} entries");
        }

        return snapshot with { PendingFleetEvents = [.. pending, element] };
    }

    /// <summary>Whether a serialized pending entry belongs to one current attempt.</summary>
    internal static bool HasPendingFor(QueueSnapshot snapshot, FleetAttemptId attemptId) =>
        snapshot.PendingFleetEvents is { Count: > 0 } pending
        && pending.Any(raw => string.Equals(
            GetString(raw, "attemptId"), attemptId.Value, StringComparison.Ordinal));

    private static async Task<QueueSnapshot> AcknowledgeAsync(
        string queuePath,
        JsonElement expectedRaw,
        FleetEventDraft draft,
        CancellationToken cancellationToken)
    {
        QueueSnapshot? acknowledged = null;
        await QueueStore.MutateAsync(
            queuePath,
            current =>
            {
                var pending = current.PendingFleetEvents ?? [];
                var matches = pending
                    .Where(raw => string.Equals(
                        GetString(raw, "dedupeKey"), draft.DedupeKey, StringComparison.Ordinal))
                    .ToList();
                if (matches.Count == 0)
                {
                    // A concurrent pump may have acknowledged this exact key. Its append was
                    // still durable, so retaining the current image is safe and idempotent.
                    acknowledged = current;
                    return current;
                }

                if (matches.Count != 1
                    || !string.Equals(matches[0].GetRawText(), expectedRaw.GetRawText(), StringComparison.Ordinal))
                {
                    throw Fault(draft, "the pending payload changed or was duplicated before acknowledgement");
                }

                var matchIndex = pending.ToList().FindIndex(raw =>
                    string.Equals(GetString(raw, "dedupeKey"), draft.DedupeKey, StringComparison.Ordinal)
                    && string.Equals(raw.GetRawText(), expectedRaw.GetRawText(), StringComparison.Ordinal));
                if (matchIndex < 0)
                {
                    throw Fault(draft, "the pending payload could not be located for acknowledgement");
                }

                var item = current.Items.FirstOrDefault(candidate =>
                    candidate.AttemptEnvelope?.AttemptId == draft.AttemptId);
                if (item is null)
                {
                    throw Fault(draft, "its queue item disappeared before acknowledgement");
                }

                var updatedItem = MarkDurable(item, draft.Kind);
                acknowledged = current with
                {
                    Items = current.Items
                        .Select(candidate => ReferenceEquals(candidate, item) ? updatedItem : candidate)
                        .ToList(),
                    PendingFleetEvents = pending.Count == 1
                        ? null
                        : pending.Where((_, index) => index != matchIndex).ToList(),
                };
                return acknowledged;
            },
            cancellationToken).ConfigureAwait(false);

        return acknowledged ?? throw new InvalidOperationException("Queue fleet-fact acknowledgement produced no queue snapshot.");
    }

    private static QueueItem MarkDurable(QueueItem item, FleetEventKind kind) => kind switch
    {
        FleetEventKind.AdmissionDecided => item with { AttemptAdmissionFactDurable = true },
        FleetEventKind.AttemptStarted => item with { AttemptStartedFactDurable = true },
        FleetEventKind.AttemptRefused => item with { AttemptRefusedFactDurable = true },
        FleetEventKind.AttemptSettled => item with { AttemptSettledFactDurable = true },
        _ => throw Fault(
            new FleetEventDraft(kind, "acknowledgement", DateTimeOffset.UtcNow),
            "only attempt facts may be acknowledged by the queue outbox"),
    };

    private static FleetEventDraft ReadAndValidate(JsonElement raw, QueueSnapshot snapshot)
    {
        var attemptId = GetString(raw, "attemptId");
        var workTag = GetString(raw, "workId");
        FleetEventDraft draft;
        try
        {
            if (raw.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("the pending value is not an object");
            }

            draft = FleetEventLog.DeserializeDraft(raw);
            ValidateBasic(draft);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new QueueFleetEventOutboxException(
                $"Pending fleet fact for attempt '{attemptId ?? "<unknown>"}' and work tag "
                + $"'{workTag ?? "<unknown>"}' is malformed: {ex.Message}",
                attemptId,
                workTag,
                ex);
        }

        var item = snapshot.Items.FirstOrDefault(candidate =>
            candidate.AttemptEnvelope?.AttemptId == draft.AttemptId);
        if (item is null)
        {
            throw Fault(draft, "no queue item retains the matching attempt envelope");
        }

        ValidateAgainstEnvelope(draft, item, item.AttemptEnvelope!);
        return draft;
    }

    private static void ValidateBasic(FleetEventDraft draft)
    {
        if (!FleetEventKinds.All.Contains(draft.Kind)
            || draft.Kind is not (FleetEventKind.AdmissionDecided
                or FleetEventKind.AttemptStarted
                or FleetEventKind.AttemptRefused
                or FleetEventKind.AttemptSettled))
        {
            throw new JsonException($"fleet kind '{draft.Kind}' is not a queue attempt fact");
        }

        if (string.IsNullOrWhiteSpace(draft.DedupeKey))
        {
            throw new JsonException("the draft has no dedupe key");
        }

        if (draft.AttemptId is not { Value.Length: > 0 }
            || draft.WorkId is not { Value.Length: > 0 }
            || string.IsNullOrWhiteSpace(draft.DeclaredRole)
            || string.IsNullOrWhiteSpace(draft.AdmissionDecision)
            || draft.EffectiveGrant is null)
        {
            throw new JsonException("the draft is missing its attempt, work, role, admission, or grant identity");
        }

        if (draft.Kind == FleetEventKind.AttemptStarted && draft.RoomId is not { Value.Length: > 0 })
        {
            throw new JsonException("an attemptStarted draft has no room identity");
        }
    }

    private static void ValidateAgainstEnvelope(
        FleetEventDraft draft, QueueItem item, QueueAttemptEnvelope envelope)
    {
        var expectedKey = draft.Kind switch
        {
            FleetEventKind.AdmissionDecided => $"admission:{envelope.AttemptId.Value}",
            FleetEventKind.AttemptStarted => $"attempt-started:{envelope.AttemptId.Value}",
            FleetEventKind.AttemptRefused => $"attempt-refused:{envelope.AttemptId.Value}",
            FleetEventKind.AttemptSettled => $"attempt-settled:{envelope.AttemptId.Value}",
            _ => throw Fault(draft, "unsupported queue attempt fact"),
        };
        if (!string.Equals(draft.DedupeKey, expectedKey, StringComparison.Ordinal))
        {
            throw Fault(draft, $"expected dedupe key '{expectedKey}'");
        }

        if (!string.Equals(draft.WorkId!.Value.Value, envelope.WorkId, StringComparison.Ordinal)
            || draft.ParentAttemptId != envelope.ParentAttemptId
            || draft.IssueId != envelope.Issue
            || draft.PullRequestId != envelope.PullRequest
            || !string.Equals(draft.DeclaredRole, envelope.DeclaredRole, StringComparison.Ordinal)
            || !string.Equals(draft.Vendor, envelope.Adapter, StringComparison.Ordinal)
            || !string.Equals(draft.Model, envelope.Model, StringComparison.Ordinal)
            || !string.Equals(draft.Effort, envelope.Effort, StringComparison.Ordinal)
            || !string.Equals(draft.Stage, StageToken(envelope.Stage), StringComparison.Ordinal)
            || !Same(draft.EffectiveGrant, envelope.EffectiveGrant)
            || !Same(draft.RequestedRequirements, envelope.RequestedRequirements)
            || !Same(draft.MissingCapabilities, envelope.MissingCapabilities)
            || !string.Equals(draft.AdmissionDecision, envelope.AdmissionDecision, StringComparison.Ordinal))
        {
            throw Fault(draft, "its payload contradicts the retained attempt envelope");
        }

        var expectedRoomId = envelope.RoomId is { Length: > 0 } roomId
            ? roomId
            : null;
        if (draft.Kind is FleetEventKind.AttemptStarted or FleetEventKind.AttemptSettled
            && !string.Equals(draft.RoomId?.Value, expectedRoomId, StringComparison.Ordinal))
        {
            throw Fault(draft, "its room identity contradicts the retained attempt envelope");
        }

        var invalidOrder = draft.Kind switch
        {
            FleetEventKind.AdmissionDecided =>
                item.AttemptAdmissionFactDurable
                || item.AttemptStartedFactDurable
                || item.AttemptRefusedFactDurable
                || item.AttemptSettledFactDurable,
            FleetEventKind.AttemptStarted =>
                !item.AttemptAdmissionFactDurable
                || item.AttemptRefusedFactDurable
                || item.AttemptSettledFactDurable,
            FleetEventKind.AttemptRefused =>
                !item.AttemptAdmissionFactDurable
                || item.AttemptStartedFactDurable
                || item.AttemptSettledFactDurable,
            FleetEventKind.AttemptSettled =>
                !item.AttemptAdmissionFactDurable
                || !item.AttemptStartedFactDurable
                || item.AttemptRefusedFactDurable,
            _ => true,
        };
        if (invalidOrder)
        {
            throw Fault(draft, "its fact order contradicts the queue's durable attempt facts");
        }
    }

    private static string? StageToken(WorkStage? stage) => stage is { } value ? WorkStages.Token(value) : null;

    private static bool Same(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        left is null || right is null
            ? left is null && right is null
            : left.SequenceEqual(right, StringComparer.Ordinal);

    private static void ValidateNoDuplicateKeys(IReadOnlyList<JsonElement> pending)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in pending)
        {
            var key = GetString(raw, "dedupeKey");
            if (key is null || !seen.Add(key))
            {
                var attemptId = GetString(raw, "attemptId") ?? "<unknown>";
                var workTag = GetString(raw, "workId") ?? "<unknown>";
                throw new QueueFleetEventOutboxException(
                    $"Pending fleet fact for attempt '{attemptId}' and work tag '{workTag}' is duplicated or has no dedupe key.",
                    attemptId,
                    workTag);
            }
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static QueueFleetEventOutboxException Fault(FleetEventDraft draft, string reason) =>
        new(
            $"Pending fleet fact for attempt '{draft.AttemptId?.Value ?? "<unknown>"}' and work tag "
            + $"'{draft.WorkId?.Value ?? "<unknown>"}' is invalid: {reason}.",
            draft.AttemptId?.Value,
            draft.WorkId?.Value);
}

/// <summary>A pending queue fleet fact could not be safely interpreted or acknowledged.</summary>
internal sealed class QueueFleetEventOutboxException : BatonFlowException
{
    internal QueueFleetEventOutboxException(
        string message, string? attemptId = null, string? workTag = null, Exception? innerException = null)
        : base(message, innerException ?? new InvalidOperationException(message))
    {
        AttemptId = attemptId;
        WorkTag = workTag;
    }

    internal string? AttemptId { get; }

    internal string? WorkTag { get; }
}
