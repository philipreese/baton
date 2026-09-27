using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baton.Domain;
using Baton.Queue;

namespace Baton.Cli;

/// <summary>
/// The typed row projection shared by queue list's text and JSON renderers. It deliberately accepts
/// the already-read queue snapshot and ledger facts; it never refreshes mutable queue state itself.
/// </summary>
internal static class QueueInspectionProjection
{
    internal const int DefaultPageSize = 50;
    internal const int MaxPageSize = 200;
    internal const string MalformedCursorMessage =
        "Queue list cursor is malformed; restart the inspection without --cursor.";
    internal const string ChangedSnapshotMessage =
        "Queue list cursor is no longer valid because the queue snapshot changed; restart the inspection without --cursor.";
    internal const string ChangedSelectionMessage =
        "Queue list cursor is no longer valid because the queue selection changed; restart the inspection without --cursor.";

    private static readonly JsonSerializerOptions FingerprintJsonOptions = new()
    {
        WriteIndented = false,
    };

    internal static string Fingerprint(QueueSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, FingerprintJsonOptions));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static QueueInspectionCursor DecodeCursor(string value)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Base64UrlDecode(value));
            var cursor = JsonSerializer.Deserialize<QueueInspectionCursor>(json);
            if (cursor is null
                || cursor.Version != 1
                || string.IsNullOrWhiteSpace(cursor.Fingerprint)
                || cursor.Offset < 0
                || cursor.Offset > 2_000_000)
            {
                throw new JsonException();
            }

            return cursor;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
        {
            throw new CliArgumentException(MalformedCursorMessage);
        }
    }

    internal static string EncodeCursor(QueueInspectionCursor cursor)
    {
        var json = JsonSerializer.Serialize(cursor);
        return Base64UrlEncode(Encoding.UTF8.GetBytes(json));
    }

    internal static async Task<IReadOnlyList<QueueInspectionRow>> ProjectAsync(
        IReadOnlyList<QueueItem> items,
        IReadOnlyList<QueuePullRequestObservation>? observations,
        IReadOnlyList<QueueDecisionEntry> decisions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(decisions);

        var latestObservations = (observations ?? [])
            .GroupBy(observation => $"{observation.Repository}\0{observation.PullRequest}", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(observation => observation.AttemptedAt).First(), StringComparer.Ordinal);
        var decisionsByTag = decisions
            .Where(decision => decision.Tag is { Length: > 0 })
            .GroupBy(decision => decision.Tag!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<QueueDecisionEntry>)group.ToList(), StringComparer.Ordinal);

        var rows = new List<QueueInspectionRow>(items.Count);
        foreach (var item in items)
        {
            latestObservations.TryGetValue(
                $"{item.Repository}\0{item.PullRequest}",
                out var pullRequestObservation);
            decisionsByTag.TryGetValue(item.Tag, out var itemDecisions);
            var settlement = await QueueRoomSettlementProjection.ReadAsync(item, cancellationToken).ConfigureAwait(false);

            rows.Add(new QueueInspectionRow(
                item.Tag,
                new QueueInspectionLifecycle(
                    item.Stage is { } stage ? WorkStages.Token(stage) : null,
                    item.Round,
                    item.State.ToString().ToLowerInvariant(),
                    item.Halted,
                    item.Retirement is not null),
                new QueueInspectionRoomEvidence(item.RoomDirectory, settlement),
                item.PullRequest is { } pullRequest
                    ? new QueueInspectionPullRequestEvidence(
                        item.Repository,
                        pullRequest,
                        item.Branch,
                        PullRequestState(pullRequestObservation),
                        pullRequestObservation?.HeadSha,
                        pullRequestObservation?.ObservedAt,
                        pullRequestObservation?.AttemptedAt,
                        pullRequestObservation?.Error)
                    : null,
                new QueueInspectionRouting(
                    item.Role,
                    item.Adapter,
                    item.Model,
                    item.Effort,
                    item.DeclaredTaskSize,
                    item.LifecycleReason,
                    item.LifecyclePin,
                    item.WorkerAssignment),
                new QueueInspectionRequirements(item.Requirements),
                item.LastAdmission,
                QueueInspectionDecisionTimestamps.From(item, itemDecisions ?? [])));
        }

        return rows;
    }

    internal static QueueInspectionWindow PageItems(
        IReadOnlyList<QueueItem> items,
        string fingerprint,
        bool active,
        bool includeRetained,
        int pageSize,
        string? cursor)
    {
        var start = 0;
        if (cursor is not null)
        {
            var decoded = DecodeCursor(cursor);
            if (!string.Equals(decoded.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new CliArgumentException(ChangedSnapshotMessage);
            }

            if (decoded.Active != active || decoded.IncludeRetained != includeRetained)
            {
                throw new CliArgumentException(ChangedSelectionMessage);
            }

            start = decoded.Offset;
        }

        var page = items.Skip(start).Take(pageSize).ToArray();
        var nextOffset = start + page.Length;
        var nextCursor = nextOffset < items.Count
            ? EncodeCursor(new QueueInspectionCursor(1, fingerprint, nextOffset, active, includeRetained))
            : null;
        return new QueueInspectionWindow(page, nextCursor);
    }

    private static string? PullRequestState(QueuePullRequestObservation? observation) =>
        observation is { State: { } state } && PullRequestObservationStates.IsKnown(state)
            ? state
            : null;

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException();
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}

internal sealed record QueueInspectionCursor(
    int Version,
    string Fingerprint,
    int Offset,
    bool Active,
    bool IncludeRetained);

internal sealed record QueueInspectionWindow(IReadOnlyList<QueueItem> Items, string? NextCursor);

internal sealed record QueueInspectionDocument(
    int SchemaVersion,
    string Selection,
    string SnapshotFingerprint,
    int PageSize,
    bool HasMore,
    string? NextCursor,
    string ObservationConsistency,
    bool Held,
    IReadOnlyList<QueueInspectionRow> Items);

internal sealed record QueueInspectionRow(
    string Tag,
    QueueInspectionLifecycle Lifecycle,
    QueueInspectionRoomEvidence Room,
    QueueInspectionPullRequestEvidence? PullRequest,
    QueueInspectionRouting Routing,
    QueueInspectionRequirements Requirements,
    TaskRequirementAdmission? Admission,
    QueueInspectionDecisionTimestamps DecisionTimestamps);

internal sealed record QueueInspectionLifecycle(
    string? Stage,
    int Round,
    string State,
    bool Halted,
    bool Retained);

internal sealed record QueueInspectionRoomEvidence(
    string? Directory,
    QueueRoomSettlementProjection.Observation? Settlement);

internal sealed record QueueInspectionPullRequestEvidence(
    string? Repository,
    int Number,
    string? Branch,
    string? State,
    string? HeadSha,
    DateTimeOffset? ObservedAt,
    DateTimeOffset? AttemptedAt,
    string? Error);

internal sealed record QueueInspectionRouting(
    string Role,
    string? Adapter,
    string? Model,
    string? Effort,
    TaskSizeDeclaration DeclaredTaskSize,
    string? LifecycleReason,
    bool LifecyclePin,
    FrozenWorkerAssignment? WorkerAssignment);

internal sealed record QueueInspectionRequirements(IReadOnlyList<string>? Declared);

internal sealed record QueueInspectionDecisionTimestamps(
    DateTimeOffset? AddedAt,
    DateTimeOffset? LastDecisionAt,
    DateTimeOffset? AdmissionAt,
    DateTimeOffset? LaunchedAt,
    DateTimeOffset? AdvancedAt,
    DateTimeOffset? FailedAt,
    DateTimeOffset? CancelledAt,
    DateTimeOffset? RetiredAt,
    DateTimeOffset? RestoredAt)
{
    internal static QueueInspectionDecisionTimestamps From(
        QueueItem item,
        IReadOnlyList<QueueDecisionEntry> decisions)
    {
        DateTimeOffset? At(string decision) => decisions
            .Where(entry => string.Equals(entry.Decision, decision, StringComparison.Ordinal))
            .Select(entry => (DateTimeOffset?)entry.At)
            .OrderByDescending(value => value)
            .FirstOrDefault();

        return new(
            item.AddedAt,
            decisions.Select(entry => (DateTimeOffset?)entry.At).OrderByDescending(value => value).FirstOrDefault(),
            decisions.Where(entry => entry.Admission is not null)
                .Select(entry => (DateTimeOffset?)entry.At).OrderByDescending(value => value).FirstOrDefault(),
            item.LaunchedAt ?? At(QueueDecisionEntry.Launched),
            At(QueueDecisionEntry.Advanced),
            At(QueueDecisionEntry.Failed),
            item.CancelledAt ?? At(QueueDecisionEntry.Cancelled),
            At(QueueDecisionEntry.Retired) ?? item.Retirement?.At,
            At(QueueDecisionEntry.Restored));
    }
}
