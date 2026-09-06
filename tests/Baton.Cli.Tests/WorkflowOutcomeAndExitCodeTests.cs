using System.Reflection;
using Baton.Domain;
using Baton.Status;

namespace Baton.Cli.Tests;

/// <summary>
/// Pure unit coverage for <see cref="WorkflowOutcome"/> and <see cref="RunExitCodeResolver"/> (#1356)
/// against hand-built <see cref="FlowState"/>s — every terminal class the exit-code table promises,
/// without spinning up a real pump for each one. The wiring itself (that <c>Program</c> actually
/// returns these codes) is covered separately by the real-process tests in
/// <see cref="TerminalSentinelEndToEndTests"/>.
/// </summary>
public class WorkflowOutcomeAndExitCodeTests
{
    private static readonly WorkflowDefinitionSnapshotId SnapshotId = new(Guid.NewGuid().ToString("N"));

    [Fact]
    public void All_steps_succeeded_resolves_to_Succeeded_and_exit_0()
    {
        var state = TerminalState([Step("a", StepStatus.Succeeded), Step("b", StepStatus.Succeeded)]);

        Assert.Equal(WorkflowOutcome.Succeeded, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Succeeded, RunExitCodeResolver.Resolve(Result(state)));
    }

    /// <summary>
    /// #1945: the second succeeded-shaped word must exit 0, or the fix has only moved the bug — a
    /// lane whose work is committed and on the remote would still report failure to every caller
    /// branching on <c>$?</c>. The Succeeded case above is the control: same call, same shape, one
    /// flag apart.
    /// </summary>
    [Fact]
    public void A_room_that_finished_during_teardown_resolves_to_that_word_and_exit_0()
    {
        var state = TerminalState([
            Step("a", StepStatus.Succeeded),
            Step("b", StepStatus.Succeeded) with { FinishedDuringTeardown = true },
        ]);

        Assert.Equal(WorkflowOutcome.FinishedDuringTeardown, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Succeeded, RunExitCodeResolver.Resolve(Result(state)));
    }

    /// <summary>
    /// The polarity control for the arm above: the flag alone decides, and it only decides when every
    /// step succeeded. A failed sibling keeps the room Failed — a lane that finished ONE step during
    /// teardown and broke on another has not finished.
    /// </summary>
    [Fact]
    public void The_teardown_word_never_overrides_a_failed_sibling_step()
    {
        var state = TerminalState([
            Step("a", StepStatus.Succeeded) with { FinishedDuringTeardown = true },
            Step("b", StepStatus.Failed, reason: "Worker exited with non-zero code 1."),
        ]);

        Assert.Equal(WorkflowOutcome.Failed, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Failed, RunExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_zero_step_terminal_workflow_resolves_to_Succeeded_vacuously_matching_pre_1356_behaviour()
    {
        var state = TerminalState([]);

        Assert.Equal(WorkflowOutcome.Succeeded, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Succeeded, RunExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_step_that_ran_and_failed_for_an_ordinary_reason_resolves_to_Failed_and_exit_1()
    {
        var state = TerminalState([
            Step("a", StepStatus.Succeeded),
            Step("b", StepStatus.Failed, reason: "Worker exited with non-zero code 1."),
        ]);

        Assert.Equal(WorkflowOutcome.Failed, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Failed, RunExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_rejected_step_resolves_to_Failed_and_exit_1()
    {
        var state = TerminalState([Step("a", StepStatus.Rejected)]);

        Assert.Equal(WorkflowOutcome.Failed, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Failed, RunExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_step_whose_only_failure_was_a_dispatch_timeout_resolves_to_exit_3_not_the_generic_Failed_bucket()
    {
        // The exact sentence OutcomeClassifier.Classify writes for CoreExitReason.TimedOut -- the
        // only signal this distinction has (there is no structural Timeout classification).
        var state = TerminalState([Step("a", StepStatus.Failed, reason: "Execution timed out. stderr: …")]);

        // The JSON/human-facing outcome word stays the coarse "Failed" -- #1356 point 1's shape
        // doesn't ask for a sixth top-level state, only the exit-code table (point 2) asks for a
        // distinct class.
        Assert.Equal(WorkflowOutcome.Failed, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Timeout, RunExitCodeResolver.Resolve(Result(state)));
    }

    /// <summary>
    /// #1373: the narrowing of exit 3, pinned rather than left to the prose in `spec/baton.md` §3 and
    /// `docs/agents/invoking-baton.md`'s exit-code table. A dispatch timeout whose workspace carried
    /// work settles Indeterminate, so it leaves the Timeout bucket for the generic Failed one — a
    /// harness branching on exit 3 to auto-retry a lane would otherwise silently keep doing so for
    /// exactly the rooms the ruling exists to stop it retrying.
    /// </summary>
    [Fact]
    public void A_dispatch_timeout_that_settled_Indeterminate_leaves_the_Timeout_bucket_for_exit_1()
    {
        var timedOutAndMutated = Step(
            "a",
            StepStatus.Failed,
            reason: "Execution timed out. Workspace carries 2 new commit(s) and 14 changed/untracked "
                + "path(s) — resolve it … awaiting conductor resolution.")
            with
        { IndeterminateAwaitingResolution = true, IndeterminateProducer = IndeterminateProducer.ContractFailure };

        var state = TerminalState([timedOutAndMutated]);

        Assert.Equal(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Failed, RunExitCodeResolver.Resolve(Result(state)));

        // The discriminating control: the SAME reason without the flag is the unmutated timeout, which
        // still exits 3. So this pins the Indeterminate settle as the cause, not the reason text.
        Assert.Equal(
            RunExitCode.Timeout,
            RunExitCodeResolver.Resolve(Result(TerminalState([timedOutAndMutated with
            {
                IndeterminateAwaitingResolution = false,
                IndeterminateProducer = null,
            }]))));

        // And the step is still recognisably a timeout to every surface that tells them apart — the
        // exit code narrowed, the classification did not disappear.
        Assert.True(WorkflowOutcome.IsTimeoutFailure(timedOutAndMutated));
    }

    [Fact]
    public void A_timeout_alongside_a_genuine_hard_failure_stays_in_the_Failed_bucket_not_Timeout()
    {
        // Mixed outcome: one step timed out, another failed outright. The hard failure is the more
        // actionable signal, so it wins rather than the two averaging out to a misleadingly narrow
        // "just a timeout" code.
        var state = TerminalState([
            Step("a", StepStatus.Failed, reason: "Execution timed out."),
            Step("b", StepStatus.Failed, reason: "Worker exited with non-zero code 1."),
        ]);

        Assert.Equal(RunExitCode.Failed, RunExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_cancelled_step_with_nothing_else_failed_resolves_to_Cancelled_and_exit_4()
    {
        var state = TerminalState([Step("a", StepStatus.Succeeded), Step("b", StepStatus.Cancelled)]);

        Assert.Equal(WorkflowOutcome.Cancelled, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Cancelled, RunExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_paused_workflow_without_wait_is_not_terminal_and_resolves_to_the_general_Failed_bucket()
    {
        var state = new FlowState(SnapshotId, [Step("a", StepStatus.Paused)], WorkflowStatus.Paused);

        Assert.Equal(WorkflowOutcome.Paused, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Failed, RunExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_still_running_workflow_resolves_to_the_general_Failed_bucket()
    {
        var state = new FlowState(SnapshotId, [Step("a", StepStatus.Running)], WorkflowStatus.Running);

        Assert.Equal(WorkflowOutcome.Running, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Failed, RunExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_wait_timeout_expiry_on_a_still_paused_workflow_resolves_to_exit_3_not_the_generic_Failed_bucket()
    {
        // #1378: WaitTimedOut is set by RunCommand's --wait poll loop, never by anything the ledger
        // itself records -- the room is genuinely still Paused, distinct from the dispatch-timeout
        // arm above (which IS a Terminal, Failed room). Checked ahead of WorkflowOutcome entirely.
        var state = new FlowState(SnapshotId, [Step("a", StepStatus.Paused)], WorkflowStatus.Paused);

        Assert.Equal(WorkflowOutcome.Paused, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Timeout, RunExitCodeResolver.Resolve(Result(state, waitTimedOut: true)));
    }

    [Fact]
    public void A_wait_timeout_flag_on_a_room_that_actually_reached_Terminal_defers_to_the_real_outcome()
    {
        // #1478 review, F1 (the race itself is explained at RunCommand.WaitForTerminalAsync's
        // timedOut computation): RunCommand refuses to pair WaitTimedOut with a Terminal state;
        // this arm pins the resolver's own guard so that even a future producer of the pairing
        // cannot make exit 3 contradict a written terminal sentinel.
        var state = TerminalState([Step("a", StepStatus.Succeeded)]);

        Assert.Equal(RunExitCode.Succeeded, RunExitCodeResolver.Resolve(Result(state, waitTimedOut: true)));
    }

    // #1608 review: was "S1 did NOT wire this swap" -- now inverted, since this PR IS that swap. What
    // still matters about this exact fixture: a journal line written before #1608 shipped recorded
    // FlowEvent.ExecutionFailed (Permanent) with the capture fields attached, never
    // FlowEvent.ExecutionIndeterminate, so replaying it never sets IndeterminateAwaitingResolution.
    // That backward-compat reading is what this pins now -- the capture *fields* being present is not
    // by itself what makes a room read Indeterminate; the flag is.
    [Fact]
    public void A_pre_1608_captured_response_Failed_step_without_the_new_flag_still_describes_as_Failed()
    {
        var step = new StepState(
            new StepId("a"), StepStatus.Failed, new ExecutionId("exec-1"), new Dictionary<StepId, ExecutionId>(),
            LatestFailureClassification: FailureClassification.Permanent,
            LatestFailureReason: "Contract not satisfied: 'advice.md' is missing. Response captured to '.captured-response.md'; awaiting conductor resolution.",
            LatestCapturedResponseFile: ".captured-response.md",
            LatestUnsatisfiedOutputNames: ["advice.md"],
            IndeterminateAwaitingResolution: false);
        var state = TerminalState([step]);

        Assert.Equal(WorkflowOutcome.Failed, WorkflowOutcome.Describe(state));
        Assert.NotEqual(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(state));
    }

    // #1608: the actual producer this issue adds -- an unresolved ExecutionIndeterminate projects
    // IndeterminateAwaitingResolution true, and DescribeTerminal must read the room Indeterminate for
    // it even though the step's own Status stays Failed (the "single added enum value" ruling).
    [Fact]
    public void An_unresolved_indeterminate_capture_describes_the_room_as_Indeterminate_not_Failed()
    {
        var step = new StepState(
            new StepId("a"), StepStatus.Failed, new ExecutionId("exec-1"), new Dictionary<StepId, ExecutionId>(),
            LatestFailureClassification: null,
            LatestFailureReason: "Contract not satisfied: 'advice.md' is missing. Response captured to '.captured-response.md'; awaiting conductor resolution.",
            LatestCapturedResponseFile: ".captured-response.md",
            LatestUnsatisfiedOutputNames: ["advice.md"],
            IndeterminateAwaitingResolution: true);
        var state = TerminalState([step]);

        Assert.Equal(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Failed, RunExitCodeResolver.Resolve(Result(state)));
    }

    // #1593: an uncaptured exit-0 contract failure has LatestCapturedResponseFile null,
    // IndeterminateAwaitingResolution true, and describes the room as Indeterminate.
    [Fact]
    public void An_uncaptured_exit_0_contract_failure_describes_the_room_as_Indeterminate_not_Failed()
    {
        var step = new StepState(
            new StepId("a"), StepStatus.Failed, new ExecutionId("exec-1"), new Dictionary<StepId, ExecutionId>(),
            LatestFailureClassification: null,
            LatestFailureReason: "Contract not satisfied: 'advice.md' is missing — worker exited 0 with work possibly on disk; awaiting conductor resolution.",
            LatestCapturedResponseFile: null,
            LatestUnsatisfiedOutputNames: ["advice.md"],
            IndeterminateAwaitingResolution: true);
        var state = TerminalState([step]);

        Assert.Equal(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Failed, RunExitCodeResolver.Resolve(Result(state)));
    }

    // Polarity partner: a resolved (rejected) capture clears the flag but leaves the step Failed --
    // this is the shape 'baton resolve --reject' produces, and it must read as an ordinary Failed room
    // again, not stay stuck reading Indeterminate forever.
    [Fact]
    public void A_resolved_rejected_capture_describes_the_room_as_Failed_again()
    {
        var step = new StepState(
            new StepId("a"), StepStatus.Failed, new ExecutionId("exec-1"), new Dictionary<StepId, ExecutionId>(),
            LatestFailureClassification: null,
            LatestCapturedResponseFile: ".captured-response.md",
            LatestUnsatisfiedOutputNames: ["advice.md"],
            IndeterminateAwaitingResolution: false);
        var state = TerminalState([step]);

        Assert.Equal(WorkflowOutcome.Failed, WorkflowOutcome.Describe(state));
    }

    // #1623 (contract: spec/baton.md §3), merged onto #1608's flag. VerifyFailed and ExecutionArrested
    // describe identically here -- StateProjectorTests pins the two events separately, so this file
    // only needs the shared StepState shape both leave behind. Note what is asserted and what is not:
    // IndeterminateAwaitingResolution is the flag DescribeTerminal reads for ALL THREE producers, and
    // IndeterminateReason is diagnostic text carried alongside it, never a second gate -- the
    // polarity partner directly below is what discriminates those two claims.
    [Fact]
    public void A_verify_failed_step_resolves_to_Indeterminate_not_Failed()
    {
        var step = new StepState(
            new StepId("a"), StepStatus.Failed, new ExecutionId("exec-1"), new Dictionary<StepId, ExecutionId>(),
            LatestFailureClassification: FailureClassification.Permanent,
            LatestFailureReason: "Verify failed (fmt-check) — awaiting conductor resolution.",
            IndeterminateAwaitingResolution: true,
            IndeterminateReason: "Verify failed (fmt-check) — awaiting conductor resolution.");
        var state = TerminalState([step]);

        Assert.Equal(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Failed, RunExitCodeResolver.Resolve(Result(state)));
    }

    // The discriminating control for the test above: the same reason text with the flag down reads
    // Failed. Without this arm that test would pass equally against a DescribeTerminal reading
    // IndeterminateReason -- exactly the second, parallel mechanism the #1644 merge removed.
    [Fact]
    public void An_IndeterminateReason_without_the_flag_describes_as_Failed_not_Indeterminate()
    {
        var step = new StepState(
            new StepId("a"), StepStatus.Failed, new ExecutionId("exec-1"), new Dictionary<StepId, ExecutionId>(),
            LatestFailureClassification: FailureClassification.Permanent,
            IndeterminateAwaitingResolution: false,
            IndeterminateReason: "Verify failed (fmt-check) — awaiting conductor resolution.");
        var state = TerminalState([step]);

        Assert.Equal(WorkflowOutcome.Failed, WorkflowOutcome.Describe(state));
    }

    [Fact]
    public void An_Indeterminate_step_alongside_an_ordinary_success_still_resolves_to_Indeterminate()
    {
        var state = TerminalState([
            Step("a", StepStatus.Succeeded),
            new StepState(
                new StepId("b"), StepStatus.Failed, new ExecutionId("exec-2"), new Dictionary<StepId, ExecutionId>(),
                IndeterminateAwaitingResolution: true,
                IndeterminateReason: "Execution arrested: token budget exceeded — awaiting conductor resolution."),
        ]);

        Assert.Equal(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(state));
    }

    // #1586 S1 review F1: the operator's amendment 1 called this a "tripwire pattern" that sweeps
    // every predicate that must learn a new WorkflowOutcome member -- a mechanism the repo did not
    // actually have (no reflection over the constant set anywhere, no vocabulary checker under
    // tools/). This test IS that mechanism: the failure message doubles as the sweep list, so
    // whoever adds a seventh member reads it here rather than discovering the gap via
    // RunExitCodeResolver's silent wildcard (the concrete failure this closes).
    [Fact]
    public void The_WorkflowOutcome_vocabulary_is_pinned_so_a_new_member_forces_the_consumer_sweep()
    {
        var members = typeof(WorkflowOutcome)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                // #1945 added FinishedDuringTeardown; the sweep below was walked for it.
                "Cancelled", "Failed", "FinishedDuringTeardown", "Indeterminate", "Paused", "Running",
                "Succeeded",
            ],
            members);
        // Adding a member? Sweep: RunExitCodeResolver.Resolve, RedispatchCommand's parent gate,
        // StatusCommand, FleetStatusTool, QueueSchedulerService.ClassifyTerminal (#1934 — it decides a
        // queue item's fate from this word, and fails the item closed on one it does not know),
        // glass.html chipsHtml + render buckets (that last one is no longer only prose -- the arm
        // below reads the file), spec/baton.md §3's table. #1608 review finding 10: this is a WorkflowOutcome sweep only -- adding a new
        // FlowEvent is a DIFFERENT, unlisted population with its own two display sites (glass.html's
        // EVENT_NAMES map, RoomDetailTool.FlowEventStepId) that this list does not cover and no test
        // enumerates; check both by hand when a FlowEvent variant is added.
    }

    // #1945 review HIGH 2: the sweep list in the test above is a COMMENT, and "glass.html's render
    // buckets" is the half of it that was read and skipped -- a FinishedDuringTeardown room matched no
    // bucket, is truthy so it missed the `other` catch-all, and rendered in no column at all. That is
    // the third time the same hole was measured (Stalled #1582, Indeterminate #1586 S1, Cancelled
    // #1698, "29 of 72 live rooms were Cancelled and none rendered"), so the list stops being prose
    // for this member: this arm reads the real file and fails if a word the vocabulary carries is
    // absent from render()'s bucketing block. Substring-level on purpose -- it pins that the word was
    // CONSIDERED there, not which bucket it landed in, which is a judgment (Cancelled rides Failed,
    // FinishedDuringTeardown rides Succeeded) no assertion should freeze.
    [Fact]
    public void Every_room_facing_WorkflowOutcome_member_is_named_in_glass_htmls_render_buckets()
    {
        var glassPath = Path.Combine(RepoRoot(), "tools", "fleet-glass", "glass.html");
        Assert.True(File.Exists(glassPath), $"glass.html must exist at {glassPath}");

        var html = File.ReadAllText(glassPath);
        var bucketsStart = html.IndexOf("function render(", StringComparison.Ordinal);
        Assert.True(bucketsStart >= 0, "glass.html must still define a render() function to bucket rooms in.");
        var bucketsEnd = html.IndexOf("const other", bucketsStart, StringComparison.Ordinal);
        Assert.True(bucketsEnd > bucketsStart, "render()'s bucketing block must still end at the `other` catch-all.");
        var buckets = html[bucketsStart..bucketsEnd];

        // "Paused" is excluded, and only it: glass has no Paused bucket today and never had one --
        // pre-existing, its own population, not this pin's to invent. An eighth member is NOT
        // excluded by default; it lands in the required set and forces a decision here.
        var unswept = typeof(WorkflowOutcome)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(word => word != WorkflowOutcome.Paused)
            .Where(word => !buckets.Contains($"\"{word}\"", StringComparison.Ordinal))
            .OrderBy(word => word, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unswept.Length == 0,
            "tools/fleet-glass/glass.html's render() buckets name no arm for: " + string.Join(", ", unswept) +
            ". A room carrying such a state renders in NO column (it matches no exact-match bucket and " +
            "is truthy, so it misses `other` too). Give it a bucket, then also sweep primaryStateChip, " +
            "roomDetailHtml's `terminal` predicate, and the dismissall section map.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Baton.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate the repo root (Baton.slnx) by walking up from " + AppContext.BaseDirectory);
    }

    private static FlowState TerminalState(IReadOnlyList<StepState> steps) =>
        new(SnapshotId, steps, WorkflowStatus.Terminal);

    private static StepState Step(string stepId, StepStatus status, string? reason = null) =>
        new(new StepId(stepId), status, new ExecutionId(Guid.NewGuid().ToString("N")),
            new Dictionary<StepId, ExecutionId>(), LatestFailureReason: reason);

    private static CommandResult Result(FlowState state, bool waitTimedOut = false) => new(
        state,
        new WorkflowDefinitionSnapshot(SnapshotId, new WorkflowTemplateId("t"), 1, []),
        WaitTimedOut: waitTimedOut);
}
