using System.Text.Json;
using Baton.Accounting;
using Baton.Conductor;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Cli.Daemon;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>Explicit, one-slot replacement of a stopped review with a missing usable verdict.</summary>
internal static class ReplacementReviewConductorCommand
{
    internal static async Task<int> ExecuteAsync(
        ConductorOptions options,
        TextWriter output,
        string batonRoot,
        WorkItemAdvancer? advancer = null,
        ConductorObligationStore? obligations = null,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? beforeAdviceRead = null,
        bool automatic = false)
    {
        var key = options.ObligationKey!;
        var holder = options.Holder!;
        var expectedHead = options.ExpectedHead!;
        if (options.Action != "replace-review" || string.IsNullOrWhiteSpace(key)
            || string.IsNullOrWhiteSpace(holder)
            || expectedHead is not { Length: 40 } || !expectedHead.All(Uri.IsHexDigit))
            throw new CliArgumentException("A replacement review requires a typed action, holder, obligation and full head SHA.");
        if (!string.Equals(Path.GetFullPath(batonRoot), Path.GetFullPath(BatonPaths.Root),
            StringComparison.OrdinalIgnoreCase))
            throw new ConductorObligationStoreException("Replacement review requires one canonical Baton store root.");
        advancer ??= new WorkItemAdvancer();
        obligations ??= new ConductorObligationStore(FleetEventLog.OpenOperational());
        var obligation = await obligations.ReadAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new ConductorObligationStoreException($"No stopped-work obligation exists for '{key}'.");
        if (obligation.RequestedAction != StoppedWorkJudgmentKey.Action
            || obligation.AdapterCapability != StoppedWorkJudgmentKey.Capability
            || !StoppedWorkJudgmentKey.TryParse(key, out var repository, out var tag, out var sourceAttempt, out var stage)
            || stage is not (WorkStage.Review or WorkStage.ReReview)
            || obligation.TargetProject != repository || obligation.TargetExecution != tag)
            throw new ConductorObligationStoreException("The obligation is not a typed stopped-work review source.");

        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        var candidates = snapshot.Items.Where(item => item.StoppedWorkJudgment?.Key == key).ToArray();
        if (candidates.Length != 1)
            throw new ConductorObligationStoreException("The stopped-work source is missing or ambiguous.");
        var source = candidates[0];
        if (source.ReplacementReviewAction is { } retained)
        {
            RequireSameRequest(retained, obligation, holder, expectedHead);
            if (!automatic && retained.Origin == QueueReplacementReviewOrigin.Automatic)
            {
                await QueueStore.MutateAsync(BatonPaths.QueueFile, current => current with
                {
                    Items = current.Items.Select(item =>
                    {
                        if (item.Tag != source.Tag || item.ReplacementReviewAction is not { } currentAction)
                            return item;
                        RequireSameRequest(currentAction, obligation, holder, expectedHead);
                        retained = currentAction with
                        {
                            Origin = QueueReplacementReviewOrigin.Manual,
                            PausedReason = null,
                            NextTrigger = null,
                        };
                        return item with { ReplacementReviewAction = retained };
                    }).ToList(),
                }, cancellationToken).ConfigureAwait(false);
            }
            output.WriteLine($"Replacement review for '{key}' is retained; "
                + (retained.CompletionProof is not null ? "completion verified." :
                    retained.BlockedReason is not null ? "blocked." :
                    retained.ReplacementAttemptId is not null ? "attempt bound." : "authorized."));
            return 0;
        }
        if (snapshot.Held)
            throw new ConductorObligationStoreException("The queue is held; replacement review admission is refused.");
        if (obligation.Status != ConductorObligationStatus.TransportAcknowledged
            || obligation.Owner != holder || obligation.TransportReceipt is not { Length: > 0 } digest
            || !digest.StartsWith("stopped-work-advice-sha256:", StringComparison.Ordinal)
            || obligation.PullRequestHead != expectedHead
            || obligation.TargetRevision != expectedHead
            || source.StoppedWorkJudgment is not { } intent
            || intent.AttemptId != sourceAttempt || intent.Stage != stage
            || intent.ContextSha256 != obligation.ContextSha256
            || intent.Holder != holder || intent.PullRequestHead != expectedHead
            || intent.PullRequest is not > 0 || source.RoomDirectory is not { Length: > 0 }
            || source.Branch is not { Length: > 0 } || source.Repository != repository)
            throw new ConductorObligationStoreException("Stopped-work advice, source, holder, or PR head is ineligible.");

        if (beforeAdviceRead is not null)
            await beforeAdviceRead(cancellationToken).ConfigureAwait(false);
        var view = await obligations.ReadStoppedWorkAdviceViewAsync(obligation, cancellationToken)
            .ConfigureAwait(false);
        if (view is not { State: StoppedWorkJudgmentState.Available, Response: not null })
        {
            // Another identical caller may have admitted the slot after this caller loaded the
            // halted source. That admission itself makes the advice projection stale.
            var latest = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
            var sources = latest.Items.Where(item => item.StoppedWorkJudgment?.Key == key).ToArray();
            if (sources.Length == 1 && sources[0].ReplacementReviewAction is { } raced)
            {
                RequireSameRequest(raced, obligation, holder, expectedHead);
                RequireSameSource(raced, source);
                if (!automatic && raced.Origin == QueueReplacementReviewOrigin.Automatic)
                {
                    // A racing manual request must retain manual authority on this exact slot;
                    // promoting it under the queue lock must never consume another review round.
                    await QueueStore.MutateAsync(BatonPaths.QueueFile, currentSnapshot =>
                    {
                        var current = currentSnapshot.Items.FirstOrDefault(item => item.Tag == source.Tag)
                            ?? throw new ConductorObligationStoreException("The retained source disappeared.");
                        var currentAction = current.ReplacementReviewAction
                            ?? throw new ConductorObligationStoreException("The retained action slot disappeared.");
                        RequireSameRequest(currentAction, obligation, holder, expectedHead);
                        RequireSameSource(currentAction, source);
                        if (currentAction.Origin != QueueReplacementReviewOrigin.Automatic)
                            return currentSnapshot;
                        return currentSnapshot with
                        {
                            Items = currentSnapshot.Items.Select(item => ReferenceEquals(item, current)
                                ? item with
                                {
                                    ReplacementReviewAction = currentAction with
                                    {
                                        Origin = QueueReplacementReviewOrigin.Manual,
                                        PausedReason = null,
                                        NextTrigger = null,
                                    },
                                }
                                : item).ToList(),
                        };
                    }, cancellationToken).ConfigureAwait(false);
                }
                output.WriteLine($"Replacement review for '{key}' is already authorized.");
                return 0;
            }
            throw new ConductorObligationStoreException("No complete current stopped-work advice is retained.");
        }

        // Automatic admission is the one narrow exception to advice-only routing: a retained
        // typed MissingVerdict source plus Recommend may consume this existing action slot, but
        // neither explanation text nor any other halt or choice is authority.
        if (automatic
            && (intent.HaltCause != StoppedWorkHaltCause.MissingVerdict
                || intent.Stage is not (WorkStage.Review or WorkStage.ReReview)
                || !intent.AutomaticMissingVerdictReplacementReviewEligible
                || !StoppedWorkAdviceSettings.IsAutomaticMissingVerdictReplacementReviewEnabled(repository)
                || view.Response.Decision.Choice != StoppedWorkAdviceChoice.Recommend))
            throw new ConductorObligationStoreException(
                "Automatic replacement review requires an opted-in MissingVerdict source and a retained Recommend advice choice.");

        var identity = RepositoryIdentity.From("https://" + repository, null)
            ?? throw new ConductorObligationStoreException("Repository identity is invalid.");
        var claim = await ConductorClaimStore.GetClaimAsync(identity, batonRoot, cancellationToken)
            .ConfigureAwait(false);
        if (claim?.Holder != holder)
            throw new ConductorObligationStoreException("Conductor ownership changed.");

        var action = new QueueReplacementReviewAction(
            key, holder, digest, repository, tag, sourceAttempt, source.RoomDirectory,
            stage, source.Round, intent.PullRequest.Value, expectedHead, source.Workspace,
            source.Branch, DateTimeOffset.UtcNow,
            Origin: automatic ? QueueReplacementReviewOrigin.Automatic : QueueReplacementReviewOrigin.Manual);
        await advancer.ValidateReplacementReviewSourceAsync(source, action, cancellationToken)
            .ConfigureAwait(false);
        var destinationRole = WorkStages.RoleFor(stage);
        var destinationSelection = QueueTierTable.SelectionForStage(source, stage).Selection;
        var requirements = TaskRequirementPreflight.RequirementsFor(
            WorkerRoleCatalog.For(destinationRole), destinationSelection?.Requirements);

        var sourceJson = JsonSerializer.Serialize(source);
        var admitted = false;
        var replayed = false;
        await QueueStore.MutateAsync(BatonPaths.QueueFile, currentSnapshot =>
        {
            var current = currentSnapshot.Items.FirstOrDefault(item => item.Tag == tag);
            if (current?.ReplacementReviewAction is { } existing)
            {
                RequireSameRequest(existing, obligation, holder, expectedHead);
                replayed = true;
                if (!automatic && existing.Origin == QueueReplacementReviewOrigin.Automatic)
                {
                    var promoted = existing with
                    {
                        Origin = QueueReplacementReviewOrigin.Manual,
                        PausedReason = null,
                        NextTrigger = null,
                    };
                    return currentSnapshot with
                    {
                        Items = currentSnapshot.Items.Select(item =>
                            ReferenceEquals(item, current)
                                ? item with { ReplacementReviewAction = promoted }
                                : item).ToList(),
                    };
                }
                return currentSnapshot;
            }
            if (currentSnapshot.Held || current is null
                || !string.Equals(JsonSerializer.Serialize(current), sourceJson, StringComparison.Ordinal))
                throw new ConductorObligationStoreException("Replacement review source or queue hold changed before admission.");
            // Revocation observed after asynchronous source validation must refuse new automatic
            // admission before writing a brief, retaining a slot or consuming the next round.
            if (automatic
                && !StoppedWorkAdviceSettings.IsAutomaticMissingVerdictReplacementReviewEnabled(repository))
                throw new ConductorObligationStoreException(
                    "Automatic replacement review opt-in was revoked before admission.");
            var brief = WorkItemAdvancer.RenderReplacementReviewBrief(current, action);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            QueueCommand.WriteSpecFileAtomically(current.SpecFile, brief);
            var updated = current with
            {
                ReplacementReviewAction = action,
                Stage = stage,
                Role = destinationRole,
                Requirements = requirements,
                LastAdmission = null,
                WorkerAssignment = null,
                MemoryAddGrant = null,
                Round = current.Round + 1,
                State = QueueItemState.Queued,
                Halted = false,
                Error = null,
                ReconciliationKind = null,
                ParentAttemptId = sourceAttempt,
                RoomDirectory = null,
                LaunchedAt = null,
                AttemptId = null,
                AttemptBaseRevision = null,
                AttemptEnvelope = null,
                LaunchMayHaveBegunAt = null,
                AttemptAdmissionFactDurable = false,
                AttemptStartedFactDurable = false,
                AttemptRefusedFactDurable = false,
                AttemptSettledFactDurable = false,
                LaunchRecoveryKind = null,
                OriginatingPullRequestRecoveryClaim = null,
                OriginatingPullRequestRecoveryProofDigest = null,
            };
            admitted = true;
            return currentSnapshot with
            {
                Items = currentSnapshot.Items.Select(item => ReferenceEquals(item, current) ? updated : item).ToList(),
            };
        }, cancellationToken).ConfigureAwait(false);
        output.WriteLine(replayed
            ? $"Replacement review for '{key}' is already authorized."
            : admitted ? $"Authorized replacement review for '{key}' at round {source.Round + 1}."
                : throw new ConductorObligationStoreException("Replacement review admission was not retained."));
        return 0;
    }

    internal static void RequireSameRequest(
        QueueReplacementReviewAction action, ConductorObligation obligation, string holder, string head)
    {
        if (action.ObligationKey != obligation.IdempotencyKey || action.Holder != holder
            || action.AdviceDigest != obligation.TransportReceipt
            || action.HeadSha != head || action.Repository != obligation.TargetProject
            || action.Tag != obligation.TargetExecution)
            throw new ConductorObligationConflictException(obligation.IdempotencyKey,
                "replacement review request differs from the retained action slot");
    }

    private static void RequireSameSource(QueueReplacementReviewAction action, QueueItem source)
    {
        if (action.SourceAttemptId != source.AttemptId
            || action.SourceRoomDirectory != source.RoomDirectory
            || action.SourceStage != source.Stage || action.SourceRound != source.Round
            || action.PullRequest != source.PullRequest || action.Workspace != source.Workspace
            || action.Branch != source.Branch || action.Repository != source.Repository
            || action.Tag != source.Tag || action.ObligationKey != source.StoppedWorkJudgment?.Key)
            throw new ConductorObligationConflictException(action.ObligationKey,
                "replacement review source differs from the retained action slot");
    }
}
