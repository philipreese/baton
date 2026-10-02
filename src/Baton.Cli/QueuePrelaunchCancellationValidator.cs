using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.Domain;
using Baton.Queue;

namespace Baton.Cli;

/// <summary>The sole producer of proved prelaunch cancellation receipts; no absence-based launch judgment.</summary>
internal static class QueuePrelaunchCancellationValidator
{
    // Protected invariant: only an exact queued initial implementation whose entire positively
    // evidenced ancestry consists of modeled no-launch refusals may cancel and release WIP.
    // Missing, pending, real-start, contradictory or changed evidence is unknown, never authority.
    internal static QueuePrelaunchCancellationReceipt Validate(
        QueueItem item, QueueSnapshot snapshot, IReadOnlyList<FleetEvent> events, DateTimeOffset cancelledAt)
    {
        if (!QueuePrelaunchCancellationReceipt.IsEligibleSource(item))
        {
            throw Refusal(item, "the current row is not an untouched initial implementation");
        }
        if (events.Any(fact => fact.Id <= 0 || fact.At == default)
            || events.Select(fact => fact.Id).Distinct().Count() != events.Count)
        {
            throw Refusal(item, "retained fleet event identities or instants are ambiguous");
        }

        var ancestors = new List<QueuePrelaunchCancellationAncestor>();
        var attempts = new HashSet<FleetAttemptId>();
        var next = item.ParentAttemptId;
        while (next is { } attempt)
        {
            if (string.IsNullOrWhiteSpace(attempt.Value) || !attempts.Add(attempt))
            {
                throw Refusal(item, "the ancestry has an invalid identity or cycle");
            }
            var facts = events.Where(fact => fact.AttemptId == attempt).ToList();
            var admissions = facts.Where(fact => fact.Kind == FleetEventKind.AdmissionDecided).ToList();
            var refusals = facts.Where(fact => fact.Kind == FleetEventKind.AttemptRefused).ToList();
            if (facts.Count != 2 || admissions.Count != 1 || refusals.Count != 1)
            {
                throw Refusal(item, "every ancestor needs exactly one admission and one refusal, without execution facts");
            }
            var admission = admissions[0];
            var refusal = refusals[0];
            if (!IsPairMember(admission, item) || !IsPairMember(refusal, item)
                || admission.DedupeKey != $"admission:{attempt.Value}"
                || refusal.DedupeKey != $"attempt-refused:{attempt.Value}"
                || admission.Outcome is not null || refusal.Outcome is not ("runway-held" or "cleanup-claim")
                || admission.Id >= refusal.Id
                || FleetEventLog.Serialize(admission) != FleetEventLog.Serialize(refusal with
                {
                    Id = admission.Id,
                    At = admission.At,
                    Kind = admission.Kind,
                    DedupeKey = admission.DedupeKey,
                    Outcome = null,
                }))
            {
                throw Refusal(item, "an ancestor does not contain a matching producer-owned no-launch pair");
            }
            ancestors.Add(new(attempt, admission.ParentAttemptId, admission.Id, admission.At, refusal.Id, refusal.At));
            next = admission.ParentAttemptId;
        }
        ancestors.Reverse();

        // A disconnected history or a foreign work reusing an ancestor identity also destroys proof.
        foreach (var fact in events.Where(fact => fact.WorkId?.Value == item.Tag
            || fact.AttemptId is { } attempt && attempts.Contains(attempt)
            || fact.ParentAttemptId is { } parent && attempts.Contains(parent)))
        {
            if (fact.WorkId?.Value != item.Tag || fact.IssueId != item.Issue
                || fact.AttemptId is not { } attempt || !attempts.Contains(attempt)
                || fact.Kind is not (FleetEventKind.AdmissionDecided or FleetEventKind.AttemptRefused))
            {
                throw Refusal(item, "retained work history contains disconnected or contradictory evidence");
            }
        }

        ValidatePending(item, snapshot, attempts);
        var receipt = new QueuePrelaunchCancellationReceipt(
            1, item.Tag, item.ParentAttemptId!.Value, cancelledAt, QueueStore.ComputeRevision([item]), ancestors);
        if (!QueuePrelaunchCancellationReceipt.IsValidFor(item with
        {
            State = QueueItemState.Cancelled,
            CancelledAt = cancelledAt,
            PrelaunchCancellation = receipt,
        }))
        {
            throw Refusal(item, "the ancestry ordering or exact-row binding is invalid");
        }
        return receipt;
    }

    internal static void ValidatePending(
        QueueItem item, QueueSnapshot snapshot, IReadOnlySet<FleetAttemptId> ancestors)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in snapshot.PendingFleetEvents ?? [])
        {
            FleetEventDraft draft;
            try
            {
                draft = FleetEventLog.DeserializeDraft(raw, strictSchema: true);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
            {
                throw Refusal(item, "a pending fleet fact cannot be classified");
            }
            var prefix = draft.Kind switch
            {
                FleetEventKind.AdmissionDecided => "admission",
                FleetEventKind.AttemptStarted => "attempt-started",
                FleetEventKind.AttemptRefused => "attempt-refused",
                FleetEventKind.AttemptSettled => "attempt-settled",
                _ => null,
            };
            if (prefix is null || draft.AttemptId is not { Value.Length: > 0 } attempt
                || draft.WorkId is not { Value.Length: > 0 } || !QueueTag.IsValid(draft.WorkId.Value.Value)
                || string.IsNullOrWhiteSpace(attempt.Value)
                || draft.OccurredAt == default || string.IsNullOrWhiteSpace(draft.DeclaredRole)
                || draft.AdmissionDecision is not (TaskRequirementAdmission.Admitted or TaskRequirementAdmission.Unknown or TaskRequirementAdmission.Refused)
                || !WellFormed(draft.EffectiveGrant, required: true)
                || !WellFormed(draft.RequestedRequirements) || !WellFormed(draft.MissingCapabilities)
                || !AdmissionMetadata(draft.AdmissionDecision, draft.EffectiveGrant, draft.RequestedRequirements, draft.MissingCapabilities)
                || draft.DedupeKey != $"{prefix}:{attempt.Value}" || !keys.Add(draft.DedupeKey)
                || draft.Kind == FleetEventKind.AttemptStarted && draft.RoomId is not { Value.Length: > 0 }
                || draft.ParentAttemptId is { } malformedParent && string.IsNullOrWhiteSpace(malformedParent.Value))
            {
                throw Refusal(item, "a pending fleet fact is malformed or ambiguous");
            }
            if (draft.WorkId.Value.Value == item.Tag || ancestors.Contains(attempt)
                || draft.ParentAttemptId is { } parent && ancestors.Contains(parent))
            {
                throw Refusal(item, "a pending fleet fact overlaps this work or its ancestry");
            }
        }
    }

    private static bool IsPairMember(FleetEvent fact, QueueItem item) =>
        fact.WorkId?.Value == item.Tag && fact.IssueId == item.Issue && fact.Stage == "implement"
        && fact.DeclaredRole == "implement" && fact.PullRequestId is null
        && fact.RoomId is null && fact.ExecutionId is null && fact.RevisionId is null && fact.RevisionKind is null
        && fact.ReviewRoundId is null && fact.ReviewVerdict is null && fact.CheckRunId is null
        && fact.CheckName is null && fact.CheckStatus is null && fact.CheckConclusion is null
        && fact.CheckStartedAt is null && fact.CheckCompletedAt is null && fact.ArtifactReferences is null
        && fact.Usage is null && fact.ElapsedMilliseconds is null && fact.LastMeaningfulProgressAt is null
        && fact.OutcomeDetail is null && fact.ObligationId is null && fact.ObligationActionProof is null
        && fact.AdmissionDecision is TaskRequirementAdmission.Admitted or TaskRequirementAdmission.Unknown
        && WellFormed(fact.EffectiveGrant, required: true)
        && WellFormed(fact.RequestedRequirements) && WellFormed(fact.MissingCapabilities)
        && AdmissionMetadata(fact.AdmissionDecision, fact.EffectiveGrant, fact.RequestedRequirements, fact.MissingCapabilities)
        && OptionalText(fact.Vendor) && OptionalText(fact.Model) && OptionalText(fact.Effort)
        && OptionalText(fact.AttemptBaseRevision)
        // Match only the actual queue no-launch producer fields; even matching extra evidence on
        // both sides of a pair cannot turn an execution/revision/obligation artifact into authority.
        && FleetEventLog.Serialize(fact) == FleetEventLog.Serialize(new FleetEvent(
            fact.Id, fact.At, fact.Kind, fact.DedupeKey,
            AttemptId: fact.AttemptId, ParentAttemptId: fact.ParentAttemptId, WorkId: fact.WorkId,
            IssueId: fact.IssueId, Vendor: fact.Vendor, Model: fact.Model, Effort: fact.Effort,
            DeclaredRole: fact.DeclaredRole, EffectiveGrant: fact.EffectiveGrant,
            RequestedRequirements: fact.RequestedRequirements, MissingCapabilities: fact.MissingCapabilities,
            AdmissionDecision: fact.AdmissionDecision, Outcome: fact.Outcome,
            Stage: fact.Stage, AttemptBaseRevision: fact.AttemptBaseRevision));

    private static bool OptionalText(string? value) => value is null || !string.IsNullOrWhiteSpace(value);

    private static bool AdmissionMetadata(
        string? decision, IReadOnlyList<string>? grant, IReadOnlyList<string>? requested, IReadOnlyList<string>? missing) =>
        grant is not null && grant.All(TaskRequirements.IsKnown)
        && (requested is null || requested.All(TaskRequirements.IsKnown))
        && (decision switch
        {
            TaskRequirementAdmission.Unknown => requested is null && missing is null,
            TaskRequirementAdmission.Admitted => requested is not null && missing is null
                && requested.All(requirement => grant.Contains(requirement, StringComparer.Ordinal)),
            TaskRequirementAdmission.Refused => missing is { Count: > 0 },
            _ => false,
        });

    private static bool WellFormed(IReadOnlyList<string>? values, bool required = false) =>
        values is null ? !required : values.All(value => !string.IsNullOrWhiteSpace(value))
            && values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    internal static CliArgumentException Refusal(QueueItem item, string reason) => new(
        $"Queue item '{item.Tag}' cannot be cancelled as a proved pre-launch request: {reason}.",
        "retain the lifecycle until its exact launch evidence can be reconciled; a live room uses 'baton cancel <room-dir>'.");
}
