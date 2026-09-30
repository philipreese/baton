using Baton.Accounting;
using Baton.Conductor;
using Baton.Queue;
using Baton.Status;

namespace Baton.Cli.Daemon;

public sealed partial class QueueSchedulerService
{
    private async Task ValidateReplacementReviewLaunchAsync(
        QueueItem selected, QueueReplacementReviewAction action, CancellationToken cancellationToken)
    {
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        var current = snapshot.Items.FirstOrDefault(item => item.Tag == action.Tag);
        if (current is null || current.Retirement is not null || current.CancelledAt is not null
            || current.Stage != action.SourceStage || current.State != QueueItemState.Queued
            || current.ReplacementReviewAction != action
            || current.AttemptEnvelope?.AttemptId != selected.AttemptEnvelope?.AttemptId)
            throw new ConductorObligationStoreException("The authorized queue identity changed.");
        var obligation = await _conductorObligations.ReadAsync(action.ObligationKey, cancellationToken)
            .ConfigureAwait(false);
        if (obligation is null || obligation.Status != ConductorObligationStatus.TransportAcknowledged
            || obligation.Owner != action.Holder || obligation.TransportReceipt != action.AdviceDigest)
            throw new ConductorObligationStoreException("The retained obligation or advice digest changed.");
        var identity = RepositoryIdentity.From("https://" + action.Repository, null);
        var claim = identity is null ? null : await ConductorClaimStore.GetClaimAsync(identity,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (claim?.Holder != action.Holder)
            throw new ConductorObligationStoreException("Conductor ownership changed.");
        var sourceTerminal = await TerminalSentinelWriter.TryReadAsync(action.SourceRoomDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (sourceTerminal is null || sourceTerminal.State != current.StoppedWorkJudgment?.TerminalOutcome)
            throw new ConductorObligationStoreException("Source terminal evidence is unavailable.");
        await _advancer.ValidateReplacementReviewHeadAsync(current, action, cancellationToken)
            .ConfigureAwait(false);
    }

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
            if (action.CompletionProof is { Length: > 0 } proof)
            {
                var obligation = await _conductorObligations.ReadAsync(action.ObligationKey, cancellationToken)
                    .ConfigureAwait(false);
                if (obligation is not null
                    && obligation.Owner == action.Holder
                    && obligation.TransportReceipt == action.AdviceDigest
                    && obligation.Status is ConductorObligationStatus.TransportAcknowledged
                        or ConductorObligationStatus.ActionObserved)
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
