using Baton.Queue;

namespace Baton.Cli;

public sealed record TaskReceiptBinding(
    string Status,
    IReadOnlyList<string> Mismatches,
    IReadOnlyList<string> Missing);

public sealed record TaskDecisionView(
    string State,
    string NextTrigger,
    string? Responsibility,
    string NextActor,
    TaskReceiptBinding? ReceiptBinding,
    bool HeadChanged,
    bool ReadinessRegressed);

public static class TaskDecisionProjection
{
    public static TaskDecisionView Decide(
        QueueItem item,
        QueuePullRequestObservation? pullRequestObservation,
        bool isPreparationOwnerAlive)
    {
        var owner = item.OwnedTask;
        var readiness = owner?.Ready;
        var receiptMismatches = new List<string>();
        var receiptMissing = new List<string>();
        if (readiness is not null && owner is not null)
        {
            if (string.IsNullOrWhiteSpace(readiness.Id)) receiptMissing.Add("id");
            if (readiness.ReadyObservedAt == default) receiptMissing.Add("observedAt");
            if (string.IsNullOrWhiteSpace(readiness.TaskId)) receiptMissing.Add("taskId");
            else if (!string.Equals(readiness.TaskId, owner.Id, StringComparison.Ordinal)) receiptMismatches.Add("taskId");
            if (string.IsNullOrWhiteSpace(readiness.Repository)) receiptMissing.Add("repository");
            else if (!string.Equals(readiness.Repository, owner.Repository, StringComparison.Ordinal)) receiptMismatches.Add("repository");
            if (readiness.Issue <= 0) receiptMissing.Add("issue");
            else if (readiness.Issue != owner.Issue) receiptMismatches.Add("issue");
            if (item.PullRequest is null || item.PullRequest <= 0) receiptMissing.Add("rowPullRequest");
            if (readiness.PullRequest <= 0) receiptMissing.Add("pullRequest");
            else if (item.PullRequest is { } rowPullRequest && readiness.PullRequest != rowPullRequest)
                receiptMismatches.Add("pullRequest");
            if (string.IsNullOrWhiteSpace(readiness.HeadSha)) receiptMissing.Add("headSha");
        }

        var receiptBinding = readiness is null ? null : new TaskReceiptBinding(
            receiptMismatches.Count > 0 ? "mismatched"
                : receiptMissing.Count > 0 ? "incomplete" : "complete",
            receiptMismatches,
            receiptMissing);

        var headChanged = readiness is not null && pullRequestObservation?.HeadSha is { } observedHead
            && !string.Equals(observedHead, readiness.HeadSha, StringComparison.Ordinal);

        // Checks is the aggregate display word, not required-check policy. An optional failure
        // can coexist with passing required checks; the advancer's retained reconciliation error
        // and required-check wait are the authority for a current readiness regression.
        var readinessRegressed = readiness is not null
            && (item.Error is not null || item.RequiredCheckEvidenceWait is not null);

        var abandonedPreparation = item.IssuePreparation is { State: TaskPreparationState.Preparing }
            && !isPreparationOwnerAlive;

        // Precedence: retired, cancelled, preparation, blocked/halted/failed, launched, receipt readiness, queued.
        var state = item.Retirement is not null ? "retired"
            : item.State == QueueItemState.Cancelled ? "cancelled"
            : item.IssuePreparation?.State == TaskPreparationState.Preparing
                ? (abandonedPreparation ? "blocked" : "preparing")
            : item.IssuePreparation?.State == TaskPreparationState.Blocked || item.Halted
                || item.State == QueueItemState.Failed ? "blocked"
            : item.State == QueueItemState.Launched ? "running"
            : readiness is not null ? (item.Stage == WorkStage.Ready && !headChanged && !readinessRegressed
                && receiptBinding?.Status == "complete"
                ? "ready-as-of" : "stale")
            : "queued";

        var nextTrigger = state switch
        {
            "preparing" => "preparation-completion",
            "queued" or "running" => "daemon-tick",
            "blocked" => "conductor-judgment",
            "stale" => "conductor-reassessment",
            "ready-as-of" => "conductor-handoff",
            _ => "none",
        };

        var responsibility = state switch
        {
            "ready-as-of" => "reconcile-review-and-fresh-forge-gates-then-merge-under-existing-authority",
            "stale" => "reassess-current-readiness",
            "blocked" => "judge-retained-blocker",
            _ => null,
        };

        var nextActor = nextTrigger switch
        {
            "preparation-completion" => "preparation completion",
            "daemon-tick" => "scheduler reconciliation",
            "conductor-judgment" => "conductor judgment",
            "conductor-reassessment" => "conductor readiness reassessment",
            "conductor-handoff" => "conductor review/merge handoff under existing authority",
            _ => "no next action",
        };

        return new TaskDecisionView(
            state,
            nextTrigger,
            responsibility,
            nextActor,
            receiptBinding,
            headChanged,
            readinessRegressed);
    }
}
