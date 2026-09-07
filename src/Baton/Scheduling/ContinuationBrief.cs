using Baton.Domain;
using Baton.Status;

namespace Baton.Scheduling;

/// <summary>
/// #1373: the preamble a retry-after-timeout carries into the vendor's prompt, so attempt N+1 knows
/// attempt N existed at all. The ruling is spec/baton.md §3's second #1373 paragraph.
/// <para>
/// A retried worker is standing in its predecessor's tree, and before this nothing told it so: the
/// only route to that fact was <c>git log</c>/<c>git status</c> archaeology it had no reason to
/// perform, so it restarted. This is the whole of the fix for the unmutated case; a mutated workspace
/// does not reach a retry at all.
/// </para>
/// <para>
/// One template, one place. Architecture Rule 1 is untouched: this text is composed from structured
/// journal facts (attempt counts, the configured timeout, the recorded failure reason) and never from
/// anything the worker said.
/// </para>
/// </summary>
public static class ContinuationBrief
{
    /// <summary>
    /// The brief for <paramref name="stepState"/>'s next attempt, or <see langword="null"/> when its
    /// previous attempt was not killed by the dispatch timeout — an ordinary failure retries with the
    /// unchanged brief exactly as before this issue.
    /// </summary>
    /// <param name="stepState">
    /// The step as projected <b>before</b> this dispatch's own <c>ExecutionRequestAccepted</c> is
    /// appended, so <see cref="StepState.LatestFailureReason"/> is still the predecessor's.
    /// </param>
    /// <param name="maxAttempts">
    /// <see cref="RetryPolicy.MaxAttempts"/> — the total attempts allowed, which is what makes
    /// "attempt 2 of 3" a budget the worker can act on rather than a bare ordinal.
    /// </param>
    /// <param name="timeout">
    /// <c>WorkerBinding.Process.Timeout</c>. A killed attempt ran essentially its whole budget by
    /// definition, so the configured value IS the predecessor's duration — no per-execution timing
    /// needs recording to say it. Since #2019 that is a FLOOR rather than an equality: a predecessor
    /// whose build-lock queueing was credited back ran up to
    /// <see cref="Dispatch.BuildLockWaitCredit.MaxBudgetMultiplier"/>× this. The brief still quotes the
    /// configured value, which stays exactly right for the sentence that matters to the next attempt —
    /// its own budget is this, and the credit it may earn is not a plan it can spend.
    /// </param>
    public static string? ForRetryAfterTimeout(StepState stepState, int maxAttempts, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(stepState);

        // The one predicate for "this step's latest attempt was a timeout", shared with every surface
        // that already tells timeouts apart (record-once).
        if (!WorkflowOutcome.IsTimeoutFailure(stepState))
        {
            return null;
        }

        var attempt = stepState.ConsecutiveFailureCount + 1;

        // "of M" only while M is still true. RetryEngine.MayRetry lets an ExhaustedUntil classification
        // bypass the MaxAttempts check entirely (quota pacing is not a retry budget), so a
        // quota-throttled lane can genuinely reach an attempt past its policy -- and "attempt 7 of 2"
        // in a brief whose whole job is to be believed is worse than no denominator.
        var attemptClause = attempt <= maxAttempts
            ? $"This is attempt {attempt} of {maxAttempts}."
            : $"This is attempt {attempt} (past the policy's {maxAttempts}, which a vendor quota pause extends).";

        return $"""
            [baton] CONTINUATION BRIEF -- read this before the brief below.

            {attemptClause} Attempt {attempt - 1} ran its full {DescribeDuration(timeout)} timeout budget and was killed by baton. It did not crash, it was not refused, and it did not decide to stop -- it ran out of clock, mid-work.

            You are in the SAME workspace it left behind. Its work is still on disk. Before you write anything, read what is already there -- `git status`, `git log`, and the files themselves -- and then FINISH what attempt {attempt - 1} started. Do not restart it from the beginning, and do not undo it. Your budget is the same {DescribeDuration(timeout)}, so spend it on what is left rather than on what is done.

            The original brief follows, unchanged.

            ----------------------------------------------------------------------

            """;
    }

    /// <summary>
    /// A duration in the units a person dispatched it in — <c>--timeout 60m</c> reads back as
    /// <c>60m</c>, not <c>01:00:00</c>. Whole units only; the worker needs a magnitude, not a stopwatch.
    /// </summary>
    internal static string DescribeDuration(TimeSpan duration)
    {
        // Floored, not rounded: 45s must read as "45s", and rounding it up to "1m" would overstate a
        // budget by a third.
        var totalMinutes = (long)duration.TotalMinutes;
        if (totalMinutes < 1)
        {
            return $"{Math.Max(1, (long)Math.Round(duration.TotalSeconds))}s";
        }

        if (totalMinutes < 60)
        {
            return $"{totalMinutes}m";
        }

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return minutes == 0 ? $"{hours}h" : $"{hours}h {minutes}m";
    }
}
