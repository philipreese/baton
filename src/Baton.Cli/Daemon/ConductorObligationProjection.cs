using System.Text.Json.Nodes;
using Baton.Conductor;
using Baton.Queue;
using Baton.Status;

namespace Baton.Cli.Daemon;

/// <summary>Public, bounded read-only view; never serialize the private obligation record.</summary>
internal static class ConductorObligationProjection
{
    internal const int RowLimit = 100;

    internal static async Task<JsonObject> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var store = new ConductorObligationStore(FleetEventLog.OpenOperational());
            var inspection = await store.InspectAsync(cancellationToken).ConfigureAwait(false);
            var stoppedSources = File.Exists(BatonPaths.QueueFile)
                ? (await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false)).Items
                : Array.Empty<QueueItem>();
            // Missing evidence is not proof that there are no obligations. Fact-only recovery is
            // valid, but absence of both snapshot and retained facts cannot establish an all-clear.
            if (!File.Exists(BatonPaths.ConductorObligationsFile)
                && inspection.Obligations.Count == 0 && inspection.Quarantined.Count == 0
                && !stoppedSources.Any(source => IsUnboundStoppedSource(source, out _)))
            {
                return Unavailable();
            }
            var advice = new Dictionary<string, StoppedWorkAdviceView>();
            foreach (var row in Eligible(inspection).Take(RowLimit))
            {
                var retained = await store.ReadStoppedWorkAdviceViewAsync(row, cancellationToken).ConfigureAwait(false);
                if (retained is not null) advice[row.IdempotencyKey] = retained;
            }
            return Project(inspection, advice, stoppedSources);
        }
        catch (Exception ex) when (ex is ConductorObligationStoreException or QueueStoreException
            or IOException or UnauthorizedAccessException)
        {
            // Exception messages and quarantine reasons may contain local paths or secrets.
            Console.Error.WriteLine("Conductor obligation projection: evidence unavailable; inspection failed.");
            return Unavailable();
        }
    }

    private static JsonObject Unavailable() => new()
    {
        ["available"] = false,
        ["reason"] = "evidence-unavailable",
    };

    private static IOrderedEnumerable<ConductorObligation> Eligible(ConductorObligationInspection inspection) =>
        inspection.Obligations.Where(row => !inspection.Quarantined.ContainsKey(row.IdempotencyKey))
            .OrderBy(row => row.Status == ConductorObligationStatus.ActionObserved)
            .ThenBy(row => row.CreatedAt).ThenBy(row => row.ObligationId, StringComparer.Ordinal);

    internal static JsonObject Project(ConductorObligationInspection inspection,
        IReadOnlyDictionary<string, StoppedWorkAdviceView>? advice = null,
        IReadOnlyList<QueueItem>? stoppedSources = null)
    {
        var eligible = Eligible(inspection).ToArray();
        var projectedRows = new List<(JsonObject Row, bool Completed, DateTimeOffset CreatedAt, string SortKey)>();
        foreach (var row in eligible)
        {
            var stopped = row.RequestedAction == StoppedWorkJudgmentKey.Action
                && row.AdapterCapability == StoppedWorkJudgmentKey.Capability;
            var projected = new JsonObject
            {
                ["requestedAction"] = row.RequestedAction switch
                {
                    "continue" => "Continue work",
                    "readiness-decision" => "Assess readiness",
                    "stopped-work-judgment" => "Assess stopped work",
                    _ => "Other action (details withheld)",
                },
                ["owner"] = row.Owner == ConductorContinuation.Owner
                    ? "queue-lifecycle" : stopped ? "Repository conductor" : "Other owner (details withheld)",
                ["status"] = row.Status.ToString(),
                ["createdAt"] = row.CreatedAt.ToString("O"),
                ["reason"] = row.Status switch
                {
                    ConductorObligationStatus.Pending => "Waiting for submission",
                    ConductorObligationStatus.Submitted => "Submitted; action not yet observed",
                    ConductorObligationStatus.TransportAcknowledged => "Receipt acknowledged; action not yet observed",
                    ConductorObligationStatus.ActionObserved => "Requested action independently observed",
                    ConductorObligationStatus.Blocked or ConductorObligationStatus.Unsupported => SafeCause(row.Reason),
                    _ => "Unknown state; inspect local evidence",
                },
            };
            if (stopped)
            {
                var view = advice?.GetValueOrDefault(row.IdempotencyKey);
                var source = stoppedSources?.FirstOrDefault(item =>
                    item.Halted && item.StoppedWorkJudgment?.Key == row.IdempotencyKey);
                projected["advice"] = ProjectAdvice(view, row.CreatedAt);
                if (view?.Issue is > 0) projected["issue"] = view.Issue;
                else if (source?.Issue is > 0) projected["issue"] = source.Issue;
                var stage = view?.Stage ?? source?.StoppedWorkJudgment?.Stage ?? source?.Stage;
                if (stage is { } stageValue && Enum.IsDefined(stageValue))
                    projected["stage"] = WorkStages.Token(stageValue);
            }
            projectedRows.Add((projected, row.Status == ConductorObligationStatus.ActionObserved,
                row.CreatedAt, row.ObligationId));
        }

        foreach (var source in stoppedSources ?? Array.Empty<QueueItem>())
        {
            if (!IsUnboundStoppedSource(source, out var intent)) continue;
            var createdAt = intent.ObservedAt;
            var projected = new JsonObject
            {
                ["requestedAction"] = "Assess stopped work",
                ["owner"] = "Repository conductor",
                ["status"] = ConductorObligationStatus.Blocked.ToString(),
                ["createdAt"] = createdAt.ToString("O"),
                ["reason"] = "Stopped-work evidence is incomplete; operator review required.",
                ["advice"] = ProjectAdvice(new StoppedWorkAdviceView(
                    StoppedWorkJudgmentState.Blocked, createdAt, source.Issue, intent.Stage), createdAt),
            };
            if (source.Issue is > 0) projected["issue"] = source.Issue;
            if (Enum.IsDefined(intent.Stage)) projected["stage"] = WorkStages.Token(intent.Stage);
            projectedRows.Add((projected, false, createdAt,
                "stopped-unbound:" + (source.Tag ?? string.Empty)));
        }

        var rows = new JsonArray();
        foreach (var projected in projectedRows
            .OrderBy(item => item.Completed)
            .ThenBy(item => item.CreatedAt)
            .ThenBy(item => item.SortKey, StringComparer.Ordinal)
            .Take(RowLimit))
        {
            rows.Add(projected.Row);
        }
        return new JsonObject
        {
            ["available"] = true,
            ["rows"] = rows,
            ["quarantinedCount"] = inspection.Quarantined.Count,
            ["omittedCount"] = Math.Max(0, projectedRows.Count - RowLimit),
            ["unresolvedCount"] = projectedRows.Count(item => !item.Completed),
            ["completedCount"] = projectedRows.Count(item => item.Completed),
        };
    }

    private static bool IsUnboundStoppedSource(QueueItem source, out StoppedWorkJudgment intent)
    {
        intent = source.StoppedWorkJudgment!;
        return source.Halted && source.StoppedWorkJudgment is { } captured
            && (string.IsNullOrWhiteSpace(captured.Key)
                || captured.AttemptId is null
                || string.IsNullOrWhiteSpace(captured.Holder));
    }

    private static JsonObject ProjectAdvice(StoppedWorkAdviceView? view, DateTimeOffset createdAt)
    {
        var result = new JsonObject
        {
            ["state"] = view is not null && Enum.IsDefined(view.State)
                ? view.State.ToString().ToLowerInvariant() : "unknown",
            ["observedAt"] = (view?.ObservedAt ?? createdAt).ToString("O"),
        };
        if (view?.State is StoppedWorkJudgmentState.Available or StoppedWorkJudgmentState.Stale
            && view.Response is { Decision: { } decision } response
            && Enum.IsDefined(decision.Choice) && !string.IsNullOrWhiteSpace(decision.Explanation)
            && decision.Explanation.Length <= 4096 && response.CompletedAt != default)
        {
            result["choice"] = decision.Choice == StoppedWorkAdviceChoice.Hold ? "hold" : "recommend";
            result["explanation"] = decision.Explanation;
            result["completedAt"] = response.CompletedAt.ToString("O");
        }
        return result;
    }

    // Exact matches only: never disclose free-text suffixes, paths, receipts or exception messages.
    private static string SafeCause(string? reason) => reason switch
    {
        "the open obligation is not a deterministic queue continuation" => "Invalid continuation identity",
        "durable queue evidence contains more than one continuation for the obligation" => "Conflicting continuation records",
        "the source attempt is no longer present and no continuation queue evidence exists" => "Source attempt missing; no continuation recorded",
        "the adapter does not support this obligation" => "Adapter capability unavailable",
        _ => "Cause withheld; inspect local evidence for details",
    };
}
