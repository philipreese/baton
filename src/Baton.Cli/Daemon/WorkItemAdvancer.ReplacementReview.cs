using System.Security.Cryptography;
using System.Text;
using Baton.Cli;
using Baton.Accounting;
using Baton.Conductor;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Daemon;

public sealed partial class WorkItemAdvancer
{
    internal Action? ReplacementReviewAfterProofPersisted { get; set; }

    internal async Task ValidateReplacementReviewSourceAsync(
        QueueItem source, QueueReplacementReviewAction action, CancellationToken cancellationToken)
    {
        var intent = source.StoppedWorkJudgment;
        if (intent is null || intent.Key != action.ObligationKey
            || intent.HaltCause != StoppedWorkHaltCause.MissingVerdict
            || intent.Stage is not (WorkStage.Review or WorkStage.ReReview)
            || source.Stage != intent.Stage || source.AttemptId != intent.AttemptId
            || source.RoomDirectory != action.SourceRoomDirectory
            || source.State != QueueItemState.Failed || !source.Halted
            || source.Retirement is not null || source.CancelledAt is not null
            || source.Round <= 0 || source.Round >= WorkStages.MaxRounds
            || source.AutomaticFixUsed is null || intent.AttemptId is null
            || intent.AttemptId != action.SourceAttemptId
            || intent.PullRequest != action.PullRequest
            || intent.PullRequestHead != action.HeadSha
            || source.Repository != action.Repository || source.Tag != action.Tag
            || source.Workspace != action.Workspace || source.Branch != action.Branch
            || source.PullRequest != action.PullRequest || source.Round != action.SourceRound)
            throw new ConductorObligationStoreException("Replacement review source or round history is ineligible.");

        if (action.Origin == QueueReplacementReviewOrigin.Automatic
            && !intent.AutomaticMissingVerdictReplacementReviewEligible)
            throw new ConductorObligationStoreException(
                "Automatic replacement review source was not opted in when it halted.");

        if (intent.TerminalEvidenceAvailable != true || string.IsNullOrWhiteSpace(intent.TerminalOutcome)
            || source.AttemptEnvelope is { } envelope && envelope.AttemptId != intent.AttemptId)
            throw new ConductorObligationStoreException("Replacement review source terminal identity is unavailable.");

        var sentinel = await TerminalSentinelWriter.TryReadAsync(action.SourceRoomDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (sentinel is null || sentinel.State != intent.TerminalOutcome)
            throw new ConductorObligationStoreException("Replacement review source has no readable terminal evidence.");
        var verdictPath = FindVerdict(sentinel);
        var verdict = verdictPath is null ? null : TryReadVerdict(verdictPath);
        if (verdict is { Completion: ReviewCompletion.Complete, Decision: ReviewDecision.Block }
            || verdict is { Completion: ReviewCompletion.Complete, Decision: ReviewDecision.Approve }
                && IsExactHeadVerdict(verdict, action.HeadSha))
            throw new ConductorObligationStoreException("A usable lifecycle verdict already exists for the source review.");

        await ValidateReplacementReviewHeadAsync(source, action, cancellationToken).ConfigureAwait(false);
    }

    internal async Task ValidateReplacementReviewHeadAsync(
        QueueItem source, QueueReplacementReviewAction action, CancellationToken cancellationToken)
    {
        if (source.Repository != action.Repository || source.Workspace != action.Workspace
            || source.Branch != action.Branch || source.PullRequest != action.PullRequest
            || source.Stage != action.SourceStage || source.Retirement is not null
            || source.CancelledAt is not null)
            throw new ConductorObligationStoreException("Replacement review queue or workspace identity changed.");
        if (!Directory.Exists(source.Workspace))
            throw new ConductorObligationStoreException("Replacement review workspace is unavailable.");
        var persisted = RepositoryIdentity.From("https://" + action.Repository, null);
        var currentIdentity = _repositoryIdentity is null
            ? persisted
            : await _repositoryIdentity(source.Workspace, cancellationToken).ConfigureAwait(false);
        if (persisted?.RemoteValue != action.Repository || currentIdentity?.RemoteValue != action.Repository)
            throw new ConductorObligationStoreException("Replacement review repository context changed.");
        var observed = await ReadPullRequestSnapshotAsync(source, cancellationToken).ConfigureAwait(false);
        var workspaceHead = await _workspaceHead(source.Workspace, cancellationToken).ConfigureAwait(false);
        if (!observed.Succeeded || observed.IsOpen != true || observed.Number != action.PullRequest
            || !string.Equals(observed.HeadSha, action.HeadSha, StringComparison.Ordinal)
            || !string.Equals(workspaceHead, action.HeadSha, StringComparison.Ordinal))
            throw new ConductorObligationStoreException("Replacement review open PR or workspace head changed or is unavailable.");
    }

    internal static bool IsExactHeadVerdict(ReviewVerdict verdict, string head) =>
        verdict.Completion == ReviewCompletion.Complete
        && verdict.Decision is ReviewDecision.Approve or ReviewDecision.Block
        && verdict.ReviewedRef is { Length: 40 } reviewed
        && reviewed.All(Uri.IsHexDigit)
        && string.Equals(reviewed, head, StringComparison.OrdinalIgnoreCase);

    internal static string RenderReplacementReviewBrief(QueueItem item, QueueReplacementReviewAction action)
    {
        var findingsVerdict = ReadLastVerdict(item);
        return QueueBriefTemplates.Compose(action.SourceStage, item, new QueueBriefTemplates.BriefContext(
            Title: $"Implement #{item.Issue}",
            Do: item.Instructions ?? string.Empty,
            PullRequest: action.PullRequest,
            HeadSha: action.HeadSha,
            Round: item.Round + 1,
            Findings: findingsVerdict is null ? null : QueueBriefTemplates.RenderFindings(findingsVerdict)));
    }

    private async Task<QueueItem?> TryPersistReplacementReviewProofAsync(
        QueueItem item, WorkflowStatusView? sentinel, string? verdictPath,
        CancellationToken cancellationToken)
    {
        var action = item.ReplacementReviewAction;
        if (action is null || action.CompletionProof is not null
            || action.ReplacementAttemptId is null || action.ReplacementAttemptId != item.AttemptId
            || action.ReplacementRoomDirectory != item.RoomDirectory
            || item.Stage != action.SourceStage || item.State is not (QueueItemState.Done or QueueItemState.Failed)
            || sentinel is null || verdictPath is null)
            return item;

        byte[] verdictBytes;
        ReviewVerdict? verdict;
        try
        {
            verdictBytes = File.ReadAllBytes(verdictPath);
            verdict = ReviewVerdictSchema.TryParse(verdictBytes, out var parsed, out _) ? parsed : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return item;
        }
        if (verdict is null || !IsExactHeadVerdict(verdict, action.HeadSha)) return item;
        try
        {
            await ValidateReplacementReviewHeadAsync(item, action, cancellationToken).ConfigureAwait(false);
        }
        catch (ConductorObligationStoreException)
        {
            return item;
        }

        var verdictDigest = Convert.ToHexString(SHA256.HashData(verdictBytes)).ToLowerInvariant();
        var evidenceDigest = action.EvidenceDigest ?? action.AdviceDigest ?? string.Empty;
        var proofPayload = string.Join('|', ReplacementReviewEvidenceProvenance.For(action),
            action.ObligationKey, evidenceDigest,
            action.ReplacementAttemptId.Value.Value, action.ReplacementRoomDirectory,
            action.HeadSha, verdictDigest, verdict.Decision);
        var proof = "replacement-review-sha256:"
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proofPayload))).ToLowerInvariant();
        var updatedAction = action with { CompletionProof = proof };
        var changed = await TryMarkAsync(item, current => current with
        {
            ReplacementReviewAction = updatedAction,
        }).ConfigureAwait(false);
        if (changed) ReplacementReviewAfterProofPersisted?.Invoke();
        return changed ? item with { ReplacementReviewAction = updatedAction } : null;
    }
}
