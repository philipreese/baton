using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Cli.Daemon;
using Baton.Queue;

namespace Baton.Cli;

/// <summary>Pure inspection of retained requests, not a delivery or recovery authority.</summary>
internal static class QueueRecoveryInspection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static string Fingerprint(QueueSnapshot snapshot, ConductorObligationInspection inspection) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Queue = snapshot,
            Obligations = inspection.Obligations.OrderBy(item => item.IdempotencyKey, StringComparer.Ordinal),
            Quarantined = inspection.Quarantined.OrderBy(pair => pair.Key, StringComparer.Ordinal),
        }, Json))).ToLowerInvariant();

    internal static IReadOnlyList<QueueRecoveryRow> Project(
        QueueSnapshot snapshot, ConductorObligationInspection inspection, bool history)
    {
        var retained = inspection.Obligations.ToDictionary(item => item.IdempotencyKey, StringComparer.Ordinal);
        return retained.Keys.Concat(inspection.Quarantined.Keys).Distinct(StringComparer.Ordinal)
            .OrderBy(key => retained.TryGetValue(key, out var item) ? item.CreatedAt : DateTimeOffset.MinValue)
            .ThenBy(key => key, StringComparer.Ordinal)
            .Where(key => history || inspection.Quarantined.ContainsKey(key)
                || retained[key].Status != ConductorObligationStatus.ActionObserved)
            .Select(key => Row(snapshot, key, retained.GetValueOrDefault(key), inspection.Quarantined.GetValueOrDefault(key)))
            .ToArray();
    }

    private static QueueRecoveryRow Row(
        QueueSnapshot snapshot, string key, ConductorObligation? obligation, string? error)
    {
        var evidence = new List<QueueRecoveryEvidence>();
        var canonical = obligation is not null
            && string.Equals(obligation.Owner, ConductorContinuation.Owner, StringComparison.Ordinal)
            && string.Equals(obligation.RequestedAction, ConductorContinuation.Action, StringComparison.Ordinal)
            && string.Equals(obligation.Adapter, ConductorContinuation.Adapter, StringComparison.Ordinal)
            && string.Equals(obligation.AdapterCapability, ConductorContinuation.AdapterCapability, StringComparison.Ordinal);
        if (canonical && ConductorContinuation.TryParseKey(key, out var tag, out var attempt, out var round)
            && string.Equals(key, ConductorContinuation.IdempotencyKey(tag, attempt, round), StringComparison.Ordinal))
        {
            foreach (var item in snapshot.Items.Where(item => string.Equals(item.Tag, tag, StringComparison.Ordinal)
                && string.Equals(item.Repository, obligation!.TargetProject, StringComparison.Ordinal)))
            {
                var source = item.AttemptId == attempt;
                var continuation = item.Stage == WorkStage.Continue && item.ParentAttemptId == attempt && item.Round == round;
                if (source || continuation)
                {
                    evidence.Add(new QueueRecoveryEvidence(item.Tag, source, continuation,
                        item.Stage is { } stage ? WorkStages.Token(stage) : null, item.Round,
                        item.State.ToString().ToLowerInvariant(), item.Halted, item.Retirement is not null,
                        item.RoomDirectory, item.AttemptId?.Value, item.ParentAttemptId?.Value, item.PullRequest));
                }
            }
        }
        else
        {
            canonical = false;
        }

        var heads = (snapshot.PullRequestObservations ?? [])
            .Where(observation => obligation is not null
                && string.Equals(observation.Repository, obligation.TargetProject, StringComparison.Ordinal)
                && evidence.Any(item => item.PullRequest == observation.PullRequest)).ToArray();
        // Receipts and saved heads never prove action or current approval. Inspection must not mutate
        // a lifecycle or guess a foreign owner's capabilities, even if its key resembles ours.
        var next = error is not null ? "owner intervention: repair quarantined retained evidence"
            : obligation is null ? "owner intervention: retained request is unavailable"
            : obligation.Status is ConductorObligationStatus.Blocked or ConductorObligationStatus.Unsupported
                ? $"owner intervention: {obligation.Owner}; no automatic retry"
            : obligation.Status == ConductorObligationStatus.ActionObserved
                ? $"recorded action proof only: {obligation.RequestedAction}; no further action inferred"
            : !string.Equals(obligation.Owner, ConductorContinuation.Owner, StringComparison.Ordinal)
                ? $"owner-controlled trigger: {obligation.Owner}; unknown to queue scheduler"
            : !canonical ? "owner intervention: queue continuation identity is noncanonical"
            : evidence.Count(item => item.Continuation) > 1 ? "owner intervention: ambiguous continuation evidence"
            : evidence.Any(item => item.Continuation) ? "queue scheduler reconciliation of retained continuation evidence"
            : evidence.Any(item => item.Source) ? "queue scheduler reconciliation of retained source; eligibility evaluated separately"
            : "owner intervention: source queue evidence is missing";
        return new QueueRecoveryRow(key, obligation, error, error is null, evidence, heads, next);
    }

    internal static async Task<int> WriteAsync(
        QueueOptions options, QueueSnapshot snapshot, TextWriter output, CancellationToken cancellationToken)
    {
        var inspection = await new ConductorObligationStore(FleetEventLog.OpenOperational())
            .InspectAsync(cancellationToken).ConfigureAwait(false);
        var rows = Project(snapshot, inspection, options.IncludeRetained);
        var observedAt = DateTimeOffset.UtcNow;
        const string consistency = "separate queue and retained obligation reads; saved heads are not fresh forge observations";
        if (options.ListFormat == QueueListOutputFormat.Json)
        {
            var size = options.PageSize ?? QueueInspectionProjection.DefaultPageSize;
            if (size is < 1 or > QueueInspectionProjection.MaxPageSize)
            {
                throw new CliArgumentException($"'--page-size' must be between 1 and {QueueInspectionProjection.MaxPageSize}.");
            }
            var fingerprint = Fingerprint(snapshot, inspection);
            var page = QueueInspectionProjection.PageItems(rows, fingerprint, !options.IncludeRetained,
                options.IncludeRetained, size, options.Cursor, recovery: true);
            await output.WriteLineAsync(JsonSerializer.Serialize(new QueueRecoveryDocument(1,
                options.IncludeRetained ? "recovery-history" : "recovery-unresolved", fingerprint, size,
                page.NextCursor is not null, page.NextCursor, observedAt, consistency, snapshot.Held, page.Items), Json))
                .ConfigureAwait(false);
        }
        else
        {
            output.WriteLine($"Recovery inspection observed {observedAt:O}: {consistency}.");
            output.WriteLine($"Queue held: {snapshot.Held}; retained requests shown: {rows.Count}.");
            foreach (var row in rows)
            {
                var item = row.Obligation;
                output.WriteLine($"{row.IdempotencyKey}  {item?.Status.ToString() ?? "unknown"}  owner: {item?.Owner ?? "unknown"}");
                if (item is not null)
                {
                    output.WriteLine($"  id: {item.ObligationId}; project: {item.TargetProject}; created: {item.CreatedAt:O}");
                    output.WriteLine($"  room: {item.TargetRoom ?? "unknown"}; execution: {item.TargetExecution ?? "unknown"}; retained head: {item.PullRequestHead ?? "unknown"}");
                    output.WriteLine($"  action: {item.RequestedAction}; adapter: {item.Adapter}; capability: {item.AdapterCapability}");
                    output.WriteLine($"  receipt: {item.TransportReceipt ?? "none"}; action proof: {item.ActionProof ?? "none"}; reason: {item.Reason ?? "none"}");
                }
                output.WriteLine($"  observation error: {row.ObservationError ?? "none"}; authoritative: {row.Authoritative}");
                foreach (var evidence in row.QueueEvidence)
                {
                    output.WriteLine($"  queue: {evidence.Tag}; source: {evidence.Source}; continuation: {evidence.Continuation}; stage: {evidence.Stage ?? "unknown"}; round: {evidence.Round}; state: {evidence.State}; halted: {evidence.Halted}; retained: {evidence.Retained}");
                }
                foreach (var head in row.SavedPullRequestObservations)
                {
                    output.WriteLine($"  saved PR #{head.PullRequest} head: {head.HeadSha ?? "unknown"}; observed: {head.ObservedAt:O}; error: {head.Error ?? "none"}");
                }
                output.WriteLine($"  next: {row.NextTrigger}");
            }
        }
        return 0;
    }
}

internal sealed record QueueRecoveryDocument(int SchemaVersion, string Selection, string SnapshotFingerprint,
    int PageSize, bool HasMore, string? NextCursor, DateTimeOffset ObservedAt, string ObservationConsistency,
    bool Held, IReadOnlyList<QueueRecoveryRow> Items);

internal sealed record QueueRecoveryRow(string IdempotencyKey, ConductorObligation? Obligation,
    string? ObservationError, bool Authoritative, IReadOnlyList<QueueRecoveryEvidence> QueueEvidence,
    IReadOnlyList<QueuePullRequestObservation> SavedPullRequestObservations, string NextTrigger);

internal sealed record QueueRecoveryEvidence(string Tag, bool Source, bool Continuation, string? Stage,
    int Round, string State, bool Halted, bool Retained, string? Room, string? AttemptId, string? ParentAttemptId,
    int? PullRequest);
