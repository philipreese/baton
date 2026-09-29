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

    internal async Task ReconcileStoppedWorkAdviceAsync(CancellationToken cancellationToken)
    {
        await _adviceReconciliation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
                var obligation = await _conductorObligations.EnqueueAsync(new(
                    intent.Key, intent.Repository, null, intent.Tag, intent.PullRequestHead,
                    StoppedWorkJudgmentKey.Action, intent.Holder, intent.ObservedAt,
                    StoppedWorkJudgmentKey.Adapter, StoppedWorkJudgmentKey.Capability, true,
                    TargetRevision: intent.PullRequestHead, ContextSha256: intent.ContextSha256), cancellationToken)
                    .ConfigureAwait(false);
                if (obligation.Status is ConductorObligationStatus.Blocked or ConductorObligationStatus.Unsupported
                    or ConductorObligationStatus.ActionObserved) continue;
                var directory = _conductorObligations.GetStoppedWorkAdviceEvidenceDirectory(intent.Key);
                var marked = File.Exists(Path.Combine(directory, "launch.json"));
                if (marked && !File.Exists(Path.Combine(directory, "response.json"))) continue;
                if (obligation.Status == ConductorObligationStatus.TransportAcknowledged
                    && (File.Exists(Path.Combine(directory, "source-checked"))
                        || File.Exists(Path.Combine(directory, "source-stale")))) continue;
                // Revocation prevents new spending, not recovery of an already retained answer.
                if (!marked && !StoppedWorkAdviceSettings.IsEnabled(intent.Repository)) continue;

                _adviceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var token = _adviceCancellation.Token;
                _stoppedWorkTaskKey = intent.Key;
                // Track the complete operation. Neither the queue mutex nor this tick waits on a model.
                _stoppedWorkTask = Task.Run(() => ProcessStoppedWorkAdviceAsync(item, obligation, token));
                return;
            }
        }
        finally
        {
            _adviceReconciliation.Release();
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
            await _conductorObligations.DecideStoppedWorkOnceAsync(intent.Key!, request, context,
                async (current, token) =>
                {
                    await ValidateStoppedWorkSourceAsync(source, current, token).ConfigureAwait(false);
                    CodexReadinessDecisionAdapter.ValidateStoppedWorkPrelaunch(request, context);
                    if (_stoppedWorkAdvicePreflight is not null)
                        await _stoppedWorkAdvicePreflight(current, token).ConfigureAwait(false);
                }, _stoppedWorkAdvice!, cancellationToken).ConfigureAwait(false);
            try
            {
                await ValidateStoppedWorkSourceAsync(source, obligation, cancellationToken, admission: false)
                    .ConfigureAwait(false);
                _conductorObligations.MarkStoppedWorkAdviceSourceChecked(intent.Key!);
            }
            catch (Exception)
            {
                // An answer is retained, but changed or unavailable source evidence cannot certify it as current.
                _conductorObligations.MarkStoppedWorkAdviceStale(intent.Key!);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The durable marker, if any, is retained; shutdown never grants a retry.
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
        if (admission && !StoppedWorkAdviceSettings.IsEnabled(intent.Repository)
            || intent.State != StoppedWorkJudgmentState.Pending)
            throw new ConductorObligationStoreException("Stopped-work advice is not enabled or eligible.");
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        var candidates = snapshot.Items.Where(item => item.Tag == source.Tag && item.Repository == source.Repository).ToArray();
        if (candidates.Length != 1 || !candidates[0].Halted || candidates[0].AttemptId != intent.AttemptId
            || candidates[0].Stage != intent.Stage || candidates[0].StoppedWorkJudgment != intent)
            throw new ConductorObligationStoreException("Stopped-work source evidence changed.");
        var identity = RepositoryIdentity.From("https://" + intent.Repository, null);
        var claim = identity is null ? null : await ConductorClaimStore.GetClaimAsync(identity,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (claim?.Holder != intent.Holder || claim?.Holder != obligation.Owner)
            throw new ConductorObligationStoreException("Stopped-work conductor ownership changed.");
        await _advancer.ValidateStoppedWorkHeadAsync(source, cancellationToken).ConfigureAwait(false);
    }

    internal async Task DrainStoppedWorkAdviceAsync()
    {
        _adviceCancellation?.Cancel();
        if (_stoppedWorkTask is not null)
        {
            try { await _stoppedWorkTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _adviceCancellation?.Dispose();
        _adviceCancellation = null;
        _stoppedWorkTask = null;
        _stoppedWorkTaskKey = null;
    }
}
