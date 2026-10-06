using Baton.Accounting;
using Baton.Cli;
using Baton.Conductor;
using Baton.Queue;
using Baton.Status;

namespace Baton.Cli.Daemon;

public sealed partial class QueueSchedulerService
{
    private async Task<bool> ValidateReplacementReviewLaunchAsync(
        QueueItem selected, QueueReplacementReviewAction action, CancellationToken cancellationToken)
    {
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        var current = snapshot.Items.FirstOrDefault(item => item.Tag == action.Tag);
        if (current is null || current.Retirement is not null || current.CancelledAt is not null
            || current.Stage != action.SourceStage || current.State != QueueItemState.Queued
            || current.AttemptEnvelope?.AttemptId != selected.AttemptEnvelope?.AttemptId)
            throw new ConductorObligationStoreException("The authorized queue identity changed.");
        if (current.ReplacementReviewAction != action)
        {
            var promoted = action with
            {
                Origin = QueueReplacementReviewOrigin.Manual,
                PausedReason = null,
                NextTrigger = null,
            };
            if (action.Origin == QueueReplacementReviewOrigin.Automatic
                && current.ReplacementReviewAction == promoted)
                return false;
            if (action.Origin == QueueReplacementReviewOrigin.Automatic
                && current.ReplacementReviewAction is { PausedReason: not null }
                && !StoppedWorkAdviceSettings.IsAutomaticMissingVerdictReplacementReviewEnabled(action.Repository))
                return false;
            throw new ConductorObligationStoreException("The authorized queue identity changed.");
        }
        if (action.Origin == QueueReplacementReviewOrigin.Automatic
            && !StoppedWorkAdviceSettings.IsAutomaticMissingVerdictReplacementReviewEnabled(action.Repository))
        {
            await PauseReplacementReviewAsync(action, "automatic replacement review opt-in was revoked",
                "Re-enable the repository opt-in to resume this unlaunched action.").ConfigureAwait(false);
            return false;
        }
        var obligation = await _conductorObligations.ReadAsync(action.ObligationKey, cancellationToken)
            .ConfigureAwait(false);
        if (obligation is null || obligation.Owner != action.Holder)
            throw new ConductorObligationStoreException("The retained obligation or evidence owner changed.");
        var identity = RepositoryIdentity.From("https://" + action.Repository, null);
        var claim = identity is null ? null : await ConductorClaimStore.GetClaimAsync(identity,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (claim?.Holder != action.Holder)
            throw new ConductorObligationStoreException("Conductor ownership changed.");
        if (snapshot.Held)
            throw new ConductorObligationStoreException("The queue is held.");
        if (action.Origin == QueueReplacementReviewOrigin.Automatic)
            ReplacementReviewConductorCommand.ValidateAutomaticAuthority(current, action.Repository);
        if (ReplacementReviewEvidenceProvenance.For(action)
            == ReplacementReviewEvidenceProvenance.CompletedFollow)
            ReplacementReviewEvidenceValidator.ValidateCompletedFollowAction(
                obligation, current, action, ConductorClaimStore.GetClaimGeneration(claim));
        else if (obligation.Status != ConductorObligationStatus.TransportAcknowledged
            || obligation.TransportReceipt != action.AdviceDigest)
            throw new ConductorObligationStoreException("The retained legacy advice digest changed.");
        var sourceTerminal = await TerminalSentinelWriter.TryReadAsync(action.SourceRoomDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (sourceTerminal is null || sourceTerminal.State != current.StoppedWorkJudgment?.TerminalOutcome)
            throw new ConductorObligationStoreException("Source terminal evidence is unavailable.");
        await _advancer.ValidateReplacementReviewHeadAsync(current, action, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static Task PauseReplacementReviewAsync(
        QueueReplacementReviewAction action, string reason, string trigger) =>
        QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
        {
            Items = snapshot.Items.Select(item =>
                item.Tag == action.Tag && item.ReplacementReviewAction == action
                    && item.State == QueueItemState.Queued && action.ReplacementAttemptId is null
                    ? item with
                    {
                        ReplacementReviewAction = action with
                        {
                            PausedReason = reason,
                            NextTrigger = trigger,
                        },
                    }
                    : item).ToList(),
        }, CancellationToken.None);

    private static Task BlockReplacementReviewAsync(string tag, string key, string reason, string trigger) =>
        QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
        {
            Items = snapshot.Items.Select(item =>
                item.Tag == tag && item.ReplacementReviewAction is { } action
                    && action.ObligationKey == key && action.ReplacementAttemptId is null
                    && item.State == QueueItemState.Queued
                    ? item with
                    {
                        State = QueueItemState.Failed,
                        Halted = true,
                        Error = reason,
                        ReplacementReviewAction = action with { BlockedReason = reason, NextTrigger = trigger },
                    }
                    : item).ToList(),
        }, CancellationToken.None);

    internal async Task ReconcileReplacementReviewActionsAsync(CancellationToken cancellationToken)
    {
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        foreach (var item in snapshot.Items.Where(item => item.ReplacementReviewAction is not null))
        {
            var action = item.ReplacementReviewAction!;
            string provenance;
            try { provenance = ReplacementReviewEvidenceProvenance.For(action); }
            catch (ConductorObligationStoreException) { continue; }
            if (action.Origin == QueueReplacementReviewOrigin.Automatic
                && action.ReplacementAttemptId is null && item.State == QueueItemState.Queued)
            {
                var enabled = StoppedWorkAdviceSettings.IsAutomaticMissingVerdictReplacementReviewEnabled(
                    action.Repository);
                if (enabled == (action.PausedReason is not null))
                {
                    await QueueStore.MutateAsync(BatonPaths.QueueFile, current => current with
                    {
                        Items = current.Items.Select(candidate =>
                            candidate.Tag == item.Tag && candidate.ReplacementReviewAction == action
                                ? candidate with
                                {
                                    ReplacementReviewAction = action with
                                    {
                                        PausedReason = enabled ? null : "automatic replacement review opt-in was revoked",
                                        NextTrigger = enabled ? null : "Re-enable the repository opt-in to resume this unlaunched action.",
                                    },
                                }
                                : candidate).ToList(),
                    }, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
            if (action.CompletionProof is { Length: > 0 } proof)
            {
                var obligation = await _conductorObligations.ReadAsync(action.ObligationKey, cancellationToken)
                    .ConfigureAwait(false);
                var authentic = false;
                if (obligation is not null && obligation.Owner == action.Holder)
                {
                    try
                    {
                        if (snapshot.Held) continue;
                        if (action.Origin == QueueReplacementReviewOrigin.Automatic)
                            ReplacementReviewConductorCommand.ValidateAutomaticAuthority(item, action.Repository);
                        var identity = RepositoryIdentity.From("https://" + action.Repository, null);
                        var claim = identity is null ? null : await ConductorClaimStore.GetClaimAsync(identity,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        if (claim?.Holder != action.Holder) continue;
                        await _advancer.ValidateReplacementReviewCompletionAsync(item, action, cancellationToken)
                            .ConfigureAwait(false);
                        if (provenance == ReplacementReviewEvidenceProvenance.CompletedFollow)
                        {
                            ReplacementReviewEvidenceValidator.ValidateCompletedFollowAction(
                                obligation, item, action, ConductorClaimStore.GetClaimGeneration(claim));
                            authentic = obligation.Status is ConductorObligationStatus.Pending
                                or ConductorObligationStatus.Submitted
                                or ConductorObligationStatus.ActionObserved;
                        }
                        else
                        {
                            authentic = obligation.TransportReceipt == action.AdviceDigest
                                && obligation.Status is ConductorObligationStatus.TransportAcknowledged
                                    or ConductorObligationStatus.ActionObserved;
                        }
                    }
                    catch (Exception ex) when (ex is ConductorObligationStoreException or IOException
                        or UnauthorizedAccessException)
                    {
                        authentic = false;
                    }
                }
                if (authentic)
                    await _conductorObligations.ObserveActionAsync(action.ObligationKey, proof, cancellationToken)
                        .ConfigureAwait(false);
                continue;
            }

            if (action.BlockedReason is not null)
                continue;
            if (action.ReplacementAttemptId is null
                && item.State is not (QueueItemState.Failed or QueueItemState.Cancelled)
                && item.Retirement is null)
                continue;
            var targetIsCurrent = item.AttemptId == action.ReplacementAttemptId
                && action.ReplacementAttemptId is not null
                && item.RoomDirectory == action.ReplacementRoomDirectory
                && item.Stage == action.SourceStage;
            if (targetIsCurrent && !(item.Halted && item.State == QueueItemState.Failed))
                continue;
            var reason = action.ReplacementAttemptId is null
                ? "Replacement review admission failed before worker launch."
                : targetIsCurrent
                ? "Replacement review halted without independently verified exact-head verdict."
                : "Replacement review advanced or changed without independently verified exact-head verdict.";
            await QueueStore.MutateAsync(BatonPaths.QueueFile, currentSnapshot => currentSnapshot with
            {
                Items = currentSnapshot.Items.Select(current =>
                    current.Tag == item.Tag && current.ReplacementReviewAction == action
                        ? current with
                        {
                            ReplacementReviewAction = action with
                            {
                                BlockedReason = reason,
                                NextTrigger = "Inspect the retained replacement room and exact PR head; reconcile manually.",
                            },
                        }
                        : current).ToList(),
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
