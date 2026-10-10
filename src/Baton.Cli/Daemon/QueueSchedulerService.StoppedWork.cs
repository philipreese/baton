using System.Text.Json;
using Baton.Cli;
using Baton.Conductor;
using Baton.Accounting;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Daemon;

public sealed partial class QueueSchedulerService
{
    private readonly SemaphoreSlim _adviceReconciliation = new(1, 1);
    private CancellationTokenSource? _adviceCancellation;
    private bool _adviceStopping;
    private sealed record HostedOperation(Task Task, CancellationTokenSource Cancellation)
    {
        internal bool Delivering { get; set; } = true;
    }
    private readonly Dictionary<string, HostedOperation> _hostedOperations = new(StringComparer.Ordinal);
    internal ConductorFollowBroker? FollowBroker { get; set; }
    internal Func<string, CancellationToken, Task<RepositoryIdentity?>>? FollowRepositoryResolver { get; set; }

    private async Task ScheduleOwnedHaltAsync(QueueItem source, CancellationToken token)
    {
        if (source.StoppedWorkJudgment?.FollowAttachmentId is null)
        {
            await NotifyOwnedHaltAsync(source, token).ConfigureAwait(false);
            return;
        }
        // The committed queue intent survives an enqueue/notification gap. Selection owns and
        // tracks the actual operation; the scheduler callback never waits on a conversation.
        if (source.Repository is { } repository && RepositoryIdentity.From("https://" + repository, null) is { } identity)
        {
            var claim = await ConductorClaimStore.GetClaimAsync(identity, BatonPaths.Root, token, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            if (claim is { Held: true } && source.StoppedWorkJudgment?.Key is { } key)
                await RetainFollowWaitAsync(key, "Hosted acquisition is Held.",
                    "Unhold this acquisition; next scheduler reconciliation rechecks the retained event.", token).ConfigureAwait(false);
        }
        await ReconcileStoppedWorkAdviceAsync(token, allowLaunch: true, continueHosted: true).ConfigureAwait(false);
    }

    internal async Task<ConductorFollowResult?> NotifyOwnedHaltAsync(QueueItem source, CancellationToken token)
    {
        if (!source.Halted || source.StoppedWorkJudgment is not
            { Key: { } key, AttemptId: not null, Holder: { } holder } intent) return null;
        ConductorFollowResult? result = null;
        var existing = await _conductorObligations.ReadAsync(key, token).ConfigureAwait(false);
        var obligation = await _conductorObligations.EnqueueAsync(new(key, intent.Repository, null, intent.Tag, intent.PullRequestHead,
            StoppedWorkJudgmentKey.Action, holder, intent.ObservedAt,
            existing?.Adapter ?? StoppedWorkJudgmentKey.ProviderRoute, StoppedWorkJudgmentKey.Capability, true,
            TargetRevision: intent.PullRequestHead, ContextSha256: intent.ContextSha256), token).ConfigureAwait(false);
        // Queue commit and obligation enqueue are both complete. No queue lock crosses this boundary.
        try
        {
            if (intent.FollowAttachmentId is not null)
            {
                var retained = await ConductorFollowSession.NotifyAttachedAsync(source, token, FollowBroker,
                    FollowRepositoryResolver, _advancer.ValidateStoppedWorkHeadAsync).ConfigureAwait(false);
                result = retained;
                if (retained?.DecisionEvidence is { Decision: "ReplaceReview" } decision)
                {
                    await ReplacementReviewConductorCommand.ExecuteAsync(
                        new ConductorOptions(
                            ConductorVerb.Act,
                            Holder: holder,
                            ObligationKey: key,
                            Action: "replace-review",
                            ExpectedHead: decision.SourceHeadSha),
                        TextWriter.Null,
                        BatonPaths.Root,
                        _advancer,
                        _conductorObligations,
                        token,
                        automatic: true,
                        evidence: new ReplacementReviewEvidenceInput(
                            ReplacementReviewEvidenceProvenance.CompletedFollow,
                            decision.ResponseSha256,
                            retained.EvidenceLocation,
                            decision)).ConfigureAwait(false);
                }
                if (retained?.Status is "delivered" or "replayed")
                    await QueueStore.MutateAsync(BatonPaths.QueueFile, current => current with
                    {
                        Items = current.Items.Select(item => item.StoppedWorkJudgment is { } pending
                            && pending.Key == key && pending.FollowAttachmentId == intent.FollowAttachmentId
                            && pending.FollowContinuationPending
                            ? item with
                            {
                                StoppedWorkJudgment = pending with
                                {
                                    FollowContinuationPending = false,
                                    FollowContinuationWait = null,
                                    FollowContinuationTrigger = null
                                }
                            }
                            : item).ToList(),
                    }, token).ConfigureAwait(false);
                else if (retained?.Status == "held-pending")
                    await RetainFollowWaitAsync(key, "Hosted acquisition is Held.",
                        "Unhold this acquisition; next scheduler reconciliation rechecks the retained event.", token).ConfigureAwait(false);
                else if (retained?.Status is "refused" or "uncertain")
                    await RetainFollowWaitAsync(key, retained.Diagnostic,
                        "Owner must inspect retained evidence and current eligibility; uncertain launches have no retry path.", token).ConfigureAwait(false);
                else if (retained?.Status == "admission-pending")
                    await RetainFollowWaitAsync(key, retained.Diagnostic,
                        "Next scheduler reconciliation rechecks session, queue and runway admission.", token).ConfigureAwait(false);
            }
            else if (source.OwnedTask is null || !ConductorFollowSession.HasRegistrationFor(source))
            {
                await _conductorObligations.RecordLegacyHaltNotificationAsync(obligation, token).ConfigureAwait(false);
                await ReconcileStoppedWorkAdviceAsync(token, allowLaunch: true).ConfigureAwait(false);
            }
        }
        catch (HostedConductorHeldException)
        {
            result = new("held-pending", key, obligation.ObligationId, string.Empty, "Hosted action admission is Held.");
            await RetainFollowWaitAsync(key, "Complete response retained; hosted action admission is Held.",
                "Unhold this acquisition; next scheduler reconciliation admits the saved decision without another model call.", token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CliArgumentException
            or ConductorClaimException or ConductorObligationStoreException or JsonException)
        {
            Console.Error.WriteLine("Owned-halt delivery refused; durable source and obligation retained.");
            await RetainFollowWaitAsync(key, "Hosted continuation unresolved: current source, head, grants, opt-in or registration could not be verified.",
                "Owner must inspect the retained source and response; next scheduler reconciliation rechecks current eligibility without retrying uncertain launches.", token).ConfigureAwait(false);
        }
        return result;
    }

    private static Task RetainFollowWaitAsync(string key, string reason, string trigger, CancellationToken token) =>
        QueueStore.MutateAsync(BatonPaths.QueueFile, current => current with
        {
            Items = current.Items.Select(item => item.StoppedWorkJudgment is { FollowContinuationPending: true } pending
                && pending.Key == key && (pending.FollowContinuationWait != reason || pending.FollowContinuationTrigger != trigger)
                ? item with { StoppedWorkJudgment = pending with { FollowContinuationWait = reason, FollowContinuationTrigger = trigger } }
                : item).ToList(),
        }, token);

    internal async Task RecoverAttachedFollowAsync(CancellationToken token, bool waitForCompletion = true)
    {
        // Production startup selects tracked operations without blocking the first scheduler tick.
        // Tests may join the exact selected pass to observe complete retained proof causally.
        await ReconcileStoppedWorkAdviceAsync(token, allowLaunch: true, continueHosted: true).ConfigureAwait(false);
        if (waitForCompletion) await Task.WhenAll(await SnapshotHostedTasksAsync()).ConfigureAwait(false);
    }

    internal async Task ReconcileStoppedWorkAdviceAsync(CancellationToken cancellationToken,
        bool allowLaunch = false, bool continueHosted = false)
    {
        await _adviceReconciliation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReconcileStoppedWorkAdviceUnderLockAsync(cancellationToken, allowLaunch, continueHosted).ConfigureAwait(false);
        }
        finally
        {
            _adviceReconciliation.Release();
        }
    }

    private async Task ReconcileStoppedWorkAdviceUnderLockAsync(CancellationToken cancellationToken, bool allowLaunch,
        bool continueHosted = false)
    {
        if (_adviceStopping || cancellationToken.IsCancellationRequested) return;
        if (continueHosted) await SelectHostedOperationsAsync(cancellationToken).ConfigureAwait(false);
        if (_stoppedWorkTask is { IsCompleted: false }) return;
        if (_stoppedWorkTask is not null)
        {
            await _stoppedWorkTask.ConfigureAwait(false);
            _stoppedWorkTask = null;
            _stoppedWorkTaskKey = null;
            _adviceCancellation?.Dispose();
            _adviceCancellation = null;
        }

        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        foreach (var item in snapshot.Items.Where(item => item.StoppedWorkJudgment is not null))
        {
            var intent = item.StoppedWorkJudgment!;
            if (intent.Key is null || intent.AttemptId is null || string.IsNullOrWhiteSpace(intent.Holder)) continue;
            var existing = await _conductorObligations.ReadAsync(intent.Key, cancellationToken).ConfigureAwait(false);
            var obligation = await _conductorObligations.EnqueueAsync(new(
                intent.Key, intent.Repository, null, intent.Tag, intent.PullRequestHead,
                StoppedWorkJudgmentKey.Action, intent.Holder, intent.ObservedAt,
                existing?.Adapter ?? StoppedWorkJudgmentKey.ProviderRoute, StoppedWorkJudgmentKey.Capability, true,
                TargetRevision: intent.PullRequestHead, ContextSha256: intent.ContextSha256), cancellationToken)
                .ConfigureAwait(false);
            if (obligation.Status is ConductorObligationStatus.Blocked or ConductorObligationStatus.Unsupported
                or ConductorObligationStatus.ActionObserved) continue;
            var directory = _conductorObligations.GetStoppedWorkAdviceEvidenceDirectory(intent.Key);
            var marked = File.Exists(Path.Combine(directory, "launch.json"));
            if (!marked && _hostedOperations.Values.Any(operation => operation.Delivering)) continue;
            if (!marked && (!allowLaunch
                    || !await _conductorObligations.HasPendingLegacyHaltNotificationAsync(obligation, cancellationToken)
                        .ConfigureAwait(false))
                || intent.FollowAttachmentId is not null) continue;
            if (marked && !File.Exists(Path.Combine(directory, "response.json"))) continue;
            if (obligation.Status == ConductorObligationStatus.TransportAcknowledged)
            {
                if (File.Exists(Path.Combine(directory, "source-stale"))) continue;
                if (File.Exists(Path.Combine(directory, "source-checked")))
                {
                    var automaticCandidate = intent.AutomaticMissingVerdictReplacementReviewEligible
                        && intent.HaltCause == StoppedWorkHaltCause.MissingVerdict
                        && item.ReplacementReviewAction is null;
                    if (!automaticCandidate
                        || File.Exists(Path.Combine(directory, "automatic-admission-refused"))
                        || !StoppedWorkAdviceSettings.IsAutomaticMissingVerdictReplacementReviewEnabled(
                            intent.Repository)
                        || snapshot.Held)
                        continue;
                    var retained = await _conductorObligations.ReadStoppedWorkAdviceViewAsync(
                        obligation, cancellationToken).ConfigureAwait(false);
                    if (retained?.Response?.Decision.Choice != StoppedWorkAdviceChoice.Recommend)
                        continue;
                }
            }
            // Revocation prevents new spending, not recovery of an already retained answer.
            if (!marked && !StoppedWorkAdviceSettings.IsEnabled(intent.Repository)) continue;
            if (!marked && item.OwnedTask is not null && ConductorFollowSession.HasRegistrationFor(item)) continue;

            _adviceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _adviceCancellation.Token;
            _stoppedWorkTaskKey = intent.Key;
            // Track the complete operation. Neither the queue mutex nor this tick waits on a model.
            _stoppedWorkTask = Task.Run(() => ProcessAndDrainStoppedWorkAdviceAsync(item, obligation,
                token, cancellationToken));
            return;
        }
    }

    private async Task SelectHostedOperationsAsync(CancellationToken token)
    {
        foreach (var entry in _hostedOperations.Where(pair => pair.Value.Task.IsCompleted).ToArray())
        {
            await entry.Value.Task.ConfigureAwait(false);
            entry.Value.Cancellation.Dispose();
            _hostedOperations.Remove(entry.Key);
        }
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, token).ConfigureAwait(false);
        var pending = ConductorFollowSession.OrderHostedCandidates(snapshot.Items.Where(item => item.Halted && item.OwnedTask is not null
            && item.ReplacementReviewAction is null
            && item.StoppedWorkJudgment is { FollowAttachmentId: not null, FollowContinuationPending: true })).ToArray();
        // Materialize source obligations before starting concurrent hosted processing. The original
        // store remains the sole owner; enqueue races must not manufacture competing payloads.
        foreach (var row in pending)
        {
            var intent = row.StoppedWorkJudgment!;
            if (intent.Key is null || intent.AttemptId is null || string.IsNullOrWhiteSpace(intent.Holder)) continue;
            var existing = await _conductorObligations.ReadAsync(intent.Key, token).ConfigureAwait(false);
            await _conductorObligations.EnqueueAsync(new(intent.Key, intent.Repository, null, intent.Tag, intent.PullRequestHead,
                StoppedWorkJudgmentKey.Action, intent.Holder, intent.ObservedAt,
                existing?.Adapter ?? StoppedWorkJudgmentKey.ProviderRoute, StoppedWorkJudgmentKey.Capability, true,
                TargetRevision: intent.PullRequestHead, ContextSha256: intent.ContextSha256), token).ConfigureAwait(false);
        }
        var claims = await ConductorClaimStore.ListRetainedClaimsAsync(BatonPaths.Root, token, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        foreach (var repository in pending.Select(item => item.Repository).Concat(claims.Select(claim => claim.Repository)).Distinct(StringComparer.Ordinal))
        {
            if (repository is null) continue;
            var correction = await ConductorFollowSession.CorrectionTargetAsync(BatonPaths.Root, repository, token).ConfigureAwait(false);
            var rows = pending.Where(item => item.Repository == repository).ToArray();
            var target = correction ?? rows.Select(row => ConductorFollowSession.HostedTargetFor(row, BatonPaths.Root)).FirstOrDefault(candidate => candidate is not null);
            if (target is null) continue;
            rows = rows.Where(row => row.StoppedWorkJudgment?.FollowAttachmentId == target.AttachmentId).ToArray();
            var key = target.Repository + "\n" + target.ClaimGeneration;
            if (_hostedOperations.ContainsKey(key)) continue;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var task = Task.Run(() => ContinueAndDrainHostedWorkAsync(target, rows, cancellation.Token, token));
            _hostedOperations.Add(key, new(task, cancellation));
        }
    }

    private async Task ContinueAndDrainHostedWorkAsync(HostedFollowTarget target, IReadOnlyList<QueueItem> pending,
        CancellationToken token, CancellationToken schedulerToken)
    {
        try
        {
            // One attempt per retained identity in this snapshot; a refusal cannot monopolize the slot.
            foreach (var source in pending)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (await ConductorFollowSession.CanContinueAttachedAsync(source, token).ConfigureAwait(false))
                    {
                        var result = await NotifyOwnedHaltAsync(source, token).ConfigureAwait(false);
                        await ConductorFollowSession.ResolveCorrectionPredecessorAsync(target, BatonPaths.Root,
                            source.StoppedWorkJudgment!.Key!, result, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConductorClaimException
                    or ConductorObligationStoreException or CliArgumentException or JsonException)
                {
                    Console.Error.WriteLine("Hosted continuation could not persist its disposition; durable source remains pending for reconciliation.");
                }
            }
            if (await ConductorFollowSession.CorrectionTargetAsync(BatonPaths.Root, target.Repository, token).ConfigureAwait(false) is { } correction)
                await ConductorFollowSession.NotifyCorrectionAsync(correction, BatonPaths.Root, token, FollowBroker,
                    FollowRepositoryResolver).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConductorClaimException
            or ConductorObligationStoreException or CliArgumentException or JsonException)
        {
            Console.Error.WriteLine("Hosted correction reconciliation refused; accepted input remains retained.");
        }
        finally
        {
            await _adviceReconciliation.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_hostedOperations.TryGetValue(target.Repository + "\n" + target.ClaimGeneration, out var operation))
                    operation.Delivering = false;
            }
            finally { _adviceReconciliation.Release(); }
            await DrainStoppedWorkNotificationsAsync(token, schedulerToken, clearLegacy: false).ConfigureAwait(false);
        }
    }

    private async Task ProcessAndDrainStoppedWorkAdviceAsync(QueueItem source, ConductorObligation obligation,
        CancellationToken token, CancellationToken schedulerToken)
    {
        await ProcessStoppedWorkAdviceAsync(source, obligation, token).ConfigureAwait(false);
        await DrainStoppedWorkNotificationsAsync(token, schedulerToken, obligation).ConfigureAwait(false);
    }

    private async Task DrainStoppedWorkNotificationsAsync(CancellationToken token, CancellationToken schedulerToken,
        ConductorObligation? completed = null, bool clearLegacy = true)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (completed is not null)
                await _conductorObligations.CompleteLegacyHaltNotificationAsync(completed, token).ConfigureAwait(false);
            await _adviceReconciliation.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                // Clear and select under one lock, so a notification cannot fall into an idle gap.
                if (clearLegacy)
                {
                    _stoppedWorkTask = null;
                    _stoppedWorkTaskKey = null;
                    _adviceCancellation?.Dispose();
                    _adviceCancellation = null;
                }
                await ReconcileStoppedWorkAdviceUnderLockAsync(schedulerToken, allowLaunch: true).ConfigureAwait(false);
            }
            finally { _adviceReconciliation.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            Console.Error.WriteLine("Stopped-work notification drain failed; durable evidence retained for startup recovery.");
        }
    }

    private async Task ProcessStoppedWorkAdviceAsync(QueueItem source, ConductorObligation obligation,
        CancellationToken cancellationToken)
    {
        var intent = source.StoppedWorkJudgment!;
        var request = new StoppedWorkAdviceRequest(obligation.ObligationId, intent.Repository, intent.Tag,
            intent.AttemptId!.Value, intent.Stage, intent.ContextSha256, intent.ObservedAt,
            intent.PullRequestHead, intent.AttemptBaseRevision, intent.Holder!, intent.HaltCause,
            intent.RepairAllowance, intent.VerdictAvailable, intent.RequiredChecks, intent.State);
        var context = new StoppedWorkAdviceContext(intent.Repository, intent.Tag, intent.AttemptId.Value,
            intent.Stage, intent.ObservedAt, intent.PullRequestHead, intent.AttemptBaseRevision,
            intent.TerminalOutcome, intent.TerminalEvidenceAvailable, intent.Checks, intent.ChecksObservedAt,
            intent.HaltCause, intent.RepairAllowance, intent.VerdictAvailable, intent.RequiredChecks, intent.State);
        try
        {
            var adviceResult = await _conductorObligations.DecideStoppedWorkOnceAsync(intent.Key!, request, context,
                async (current, token) =>
                {
                    await ValidateStoppedWorkSourceAsync(source, current, token).ConfigureAwait(false);
                    CodexReadinessDecisionAdapter.ValidateStoppedWorkPrelaunch(request, context);
                    var provider = StoppedWorkAdviceSettings.SelectProvider(intent.Repository);
                    if (_stoppedWorkAdvice is null && provider == StoppedWorkAdviceProviderDescriptor.Claude)
                        await new ClaudeStoppedWorkAdviceAdapter().PreflightAsync(token).ConfigureAwait(false);
                    if (_stoppedWorkAdvicePreflight is not null)
                        await _stoppedWorkAdvicePreflight(current, token).ConfigureAwait(false);
                    // Free CLI/auth checks may take time; source/owner revocation during them
                    // must still refuse before the irreversible charged marker.
                    await ValidateStoppedWorkSourceAsync(source, current, token).ConfigureAwait(false);
                    return provider;
                }, (provider, current, input, evidence, directory, token) =>
                    _stoppedWorkAdvice is not null
                        ? _stoppedWorkAdvice(current, input, evidence, directory, token)
                        : provider == StoppedWorkAdviceProviderDescriptor.Claude
                            ? new ClaudeStoppedWorkAdviceAdapter().DecideAsync(input, evidence, directory, token)
                            : new CodexReadinessDecisionAdapter().DecideStoppedWorkAsync(input, evidence, directory, token),
                cancellationToken).ConfigureAwait(false);
            var evidenceDirectory = _conductorObligations.GetStoppedWorkAdviceEvidenceDirectory(intent.Key!);
            // Saved advice is as-of evidence, never permission to act. Replay keeps that evidence;
            // ExecuteAsync separately revalidates the current source, owner, workspace and PR head.
            if (!File.Exists(Path.Combine(evidenceDirectory, "source-checked")))
            {
                try
                {
                    await ValidateStoppedWorkSourceAsync(source, obligation, cancellationToken, admission: false)
                        .ConfigureAwait(false);
                    _conductorObligations.MarkStoppedWorkAdviceSourceChecked(intent.Key!);
                }
                catch (Exception)
                {
                    _conductorObligations.MarkStoppedWorkAdviceStale(intent.Key!);
                    return;
                }
            }

            var automaticRecommendation = intent.AutomaticMissingVerdictReplacementReviewEligible
                && intent.HaltCause == StoppedWorkHaltCause.MissingVerdict
                && adviceResult.Response.Decision.Choice == StoppedWorkAdviceChoice.Recommend;
            if (!automaticRecommendation) return;
            var admissionSnapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken)
                .ConfigureAwait(false);
            if (admissionSnapshot.Held
                || !StoppedWorkAdviceSettings.IsAutomaticMissingVerdictReplacementReviewEnabled(intent.Repository))
                return;
            try
            {
                await ReplacementReviewConductorCommand.ExecuteAsync(
                    new ConductorOptions(
                        ConductorVerb.Act,
                        Holder: intent.Holder,
                        ObligationKey: intent.Key,
                        Action: "replace-review",
                        ExpectedHead: intent.PullRequestHead),
                    TextWriter.Null,
                    BatonPaths.Root,
                    _advancer,
                    _conductorObligations,
                    cancellationToken,
                    automatic: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                var current = await QueueStore.LoadAsync(BatonPaths.QueueFile, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!current.Held
                    && StoppedWorkAdviceSettings.IsAutomaticMissingVerdictReplacementReviewEnabled(intent.Repository))
                    _conductorObligations.MarkStoppedWorkAutomaticAdmissionRefused(intent.Key!);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The durable marker, if any, is retained; shutdown never grants a retry.
        }
        catch (StoppedWorkDeliverySuppressedException)
        {
            // Another durable transport owns delivery. The original obligation remains replayable.
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Stopped-work advice did not complete; retained evidence requires inspection.");
            var directory = _conductorObligations.GetStoppedWorkAdviceEvidenceDirectory(intent.Key!);
            if (!File.Exists(Path.Combine(directory, "launch.json")))
            {
                try
                {
                    await _conductorObligations.BlockAsync(intent.Key!, "stopped-work admission refused",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The source stays halted. Failure to save the refusal must not stop unrelated scheduling.
                    Console.Error.WriteLine("Stopped-work admission refusal could not be retained; source remains halted.");
                }
            }
        }
    }

    private async Task ValidateStoppedWorkSourceAsync(QueueItem source, ConductorObligation obligation,
        CancellationToken cancellationToken, bool admission = true)
    {
        var intent = source.StoppedWorkJudgment!;
        if (admission && source.OwnedTask is not null && ConductorFollowSession.HasRegistrationFor(source))
            throw new StoppedWorkDeliverySuppressedException();
        // Protected invariant: only a halt stamped eligible while the exact repository opt-in was
        // true may cross NEW paid-advice admission. Owned halted obligations are still enqueued and
        // remain visible when advice is off; this check protects only an unmarked provider launch.
        if (admission && (!intent.AdviceEligibleAtHalt
                || !StoppedWorkAdviceSettings.IsEnabled(intent.Repository))
            || intent.State != StoppedWorkJudgmentState.Pending)
            throw new ConductorObligationStoreException("Stopped-work advice is not enabled or eligible.");
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        var candidates = snapshot.Items.Where(item => item.Tag == source.Tag && item.Repository == source.Repository).ToArray();
        if (candidates.Length != 1 || !candidates[0].Halted || candidates[0].Retirement is not null
            || candidates[0].CancelledAt is not null || candidates[0].State == QueueItemState.Cancelled
            || candidates[0].AttemptId != intent.AttemptId || candidates[0].Stage != intent.Stage
            || candidates[0].StoppedWorkJudgment != intent)
            throw new ConductorObligationStoreException("Stopped-work source evidence changed.");
        var identity = RepositoryIdentity.From("https://" + intent.Repository, null);
        var claim = identity is null ? null : await ConductorClaimStore.GetClaimAsync(identity,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (claim?.Holder != intent.Holder || claim?.Holder != obligation.Owner)
            throw new ConductorObligationStoreException("Stopped-work conductor ownership changed.");
        await _advancer.ValidateStoppedWorkHeadAsync(source, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<Task[]> SnapshotHostedTasksAsync()
    {
        await _adviceReconciliation.WaitAsync().ConfigureAwait(false);
        try { return _hostedOperations.Values.Select(operation => operation.Task).ToArray(); }
        finally { _adviceReconciliation.Release(); }
    }

    internal async Task DrainStoppedWorkAdviceAsync()
    {
        Task? pending;
        HostedOperation[] hosted;
        await _adviceReconciliation.WaitAsync().ConfigureAwait(false);
        try
        {
            _adviceStopping = true;
            _adviceCancellation?.Cancel();
            pending = _stoppedWorkTask;
            hosted = _hostedOperations.Values.ToArray();
            foreach (var operation in hosted) operation.Cancellation.Cancel();
        }
        finally { _adviceReconciliation.Release(); }
        if (pending is not null)
        {
            try { await pending.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        foreach (var operation in hosted)
        {
            try { await operation.Task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            operation.Cancellation.Dispose();
        }
        _hostedOperations.Clear();
        _adviceCancellation?.Dispose();
        _adviceCancellation = null;
        _stoppedWorkTask = null;
        _stoppedWorkTaskKey = null;
    }
}
