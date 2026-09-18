using Baton.Domain;

namespace Baton.Scheduling;

/// <summary>
/// Capability 10: decides whether a step's latest failed attempt is eligible for a
/// brand-new <see cref="ExecutionRequest"/>. A pure predicate over <see cref="StepState"/> and
/// <see cref="RetryPolicy"/> — no I/O, no dispatch — consulted by the <see cref="DependencyResolver"/>.
/// </summary>
public static class RetryEngine
{
    /// <summary>
    /// True exactly when <paramref name="stepState"/>'s latest attempt is <see cref="StepStatus.Failed"/>,
    /// its <see cref="StepState.LatestFailureClassification"/> is not <see cref="FailureClassification.Permanent"/>
    /// (absent or unrecognized defaults to <see cref="FailureClassification.Retryable"/>), and
    /// <see cref="StepState.ConsecutiveFailureCount"/> has not yet reached <paramref name="retryPolicy"/>'s
    /// <see cref="RetryPolicy.MaxAttempts"/> — the total number of attempts allowed per round.
    /// <see cref="FailureClassification.ExhaustedUntil"/> bypasses the attempts check entirely:
    /// 0026 obliges the engine to never spend retry attempts against an exhausted quota, because
    /// retrying is not what is wrong — the retry is paced to the vendor's reset instant instead
    /// (MutationInterface.GetRetryObligations), never abandoned for lack of budget.
    /// <see cref="StepStatus.Cancelled"/> is never retried regardless of policy: cancellation is a
    /// decision to stop, not a failure to route around, and this predicate only ever returns true for a
    /// step whose latest attempt is <see cref="StepStatus.Failed"/>.
    /// <see cref="StepState.RetryForeclosed"/> true bypasses everything below straight to
    /// <c>false</c> (#1586 S1): a <see cref="Domain.FlowEvent.StepRetryForeclosed"/> voided this
    /// step's scheduled retry deliberately — unconditionally, regardless of remaining
    /// <see cref="RetryPolicy.MaxAttempts"/> budget or <see cref="FailureClassification.ExhaustedUntil"/>'s
    /// own bypass of it, the same way <see cref="StepState.LinkedFromExecutionId"/> below already
    /// short-circuits for an unrelated reason.
    /// <see cref="StepState.LinkedFromExecutionId"/> not null bypasses everything above straight to
    /// <c>false</c> (issue #1359 F4): a failed resume is never auto-retried by the settling pump.
    /// <c>baton resume</c>'s own contract is "one message per resume invocation" — a MaxAttempts-driven
    /// retry would silently dispatch further, unattended vendor invocations against a budget the
    /// operator was never asked about, and <c>PrepareExecutionAsync</c>'s retry-minted
    /// <see cref="ExecutionRequest"/> carries no <see cref="ExecutionRequest.LinkedFromExecutionId"/>
    /// of its own, so a retry would also silently clear the very link this verb exists to record.
    /// <see cref="StepState.IndeterminateAwaitingResolution"/> true bypasses everything else straight
    /// to <c>false</c> (#1608, #1623) — one arm covering all three of its producers, never one check
    /// per producer. An unresolved <see cref="Domain.FlowEvent.ExecutionIndeterminate"/>
    /// carries no <see cref="FailureClassification"/> to gate on (<see cref="FailureClassification.Permanent"/>
    /// is a different verdict's vocabulary), so this is its own explicit arm rather than a value the
    /// predicate below would happen to allow through — a null <see cref="StepState.LatestFailureClassification"/>
    /// is ordinarily retryable, and Indeterminate's <c>Reason</c>/<c>CapturedResponseFile</c> shape
    /// leaves that field null too. For the #1623 producers
    /// (<see cref="Domain.FlowEvent.VerifyFailed"/>, <see cref="Domain.FlowEvent.ExecutionArrested"/>)
    /// the same arm is what makes "never a blind retry" explicit rather than an accident of the
    /// <see cref="FailureClassification.Permanent"/> those two also record. For a captured-response
    /// settle, only a recorded <c>baton resolve</c> clears the flag; once cleared by an ACCEPT the
    /// predicate below applies exactly as it would to any other Failed step. #1877: a REJECT no longer
    /// re-arms it — <see cref="Projection.StateProjector"/>'s <see cref="Domain.FlowEvent.CaptureResolved"/>
    /// arm forecloses retry for every producer, so <see cref="StepState.RetryForeclosed"/> above is
    /// what refuses a rejected step here, and the room settles Terminal instead of reading Running
    /// with nothing alive to drive it. A verify-failed or arrested step is not a
    /// <c>--accept-capture</c>/<c>--reject</c> target at all (it has no captured response to accept or
    /// reject) — it is closed by <c>baton resolve --close</c>, or reopens through a fresh dispatch.
    /// </summary>
    public static bool MayRetry(StepState stepState, RetryPolicy retryPolicy)
    {
        ArgumentNullException.ThrowIfNull(stepState);
        ArgumentNullException.ThrowIfNull(retryPolicy);

        if (stepState.RetryForeclosed)
        {
            return false;
        }

        if (stepState.LinkedFromExecutionId is not null)
        {
            return false;
        }

        // #1608 / #1623: retry-ineligible by an explicit arm, not merely a side effect of
        // RetryForeclosed above (the ruling's own wording). ONE arm for every producer in
        // spec/baton.md §3's producer table, because they raise one flag rather than each carrying
        // its own field to check here.
        if (stepState.IndeterminateAwaitingResolution)
        {
            return false;
        }

        // #2002: one automatic continuation is the whole recovery budget for this structured cause.
        // A repeated mismatch remains Failed for conductor rerouting instead of looping.
        if (stepState.LatestRecoveryCause?.Kind == RecoveryCauseKind.OutstandingToolAtTerminalSuccess
            && stepState.RecoveryOccurrence >= 2)
        {
            return false;
        }

        return stepState.Status == StepStatus.Failed
            && stepState.LatestFailureClassification != FailureClassification.Permanent
            && stepState.LatestFailureClassification != FailureClassification.ToolDenied
            && (stepState.LatestFailureClassification == FailureClassification.ExhaustedUntil
                || stepState.ConsecutiveFailureCount < retryPolicy.MaxAttempts);
    }
}
