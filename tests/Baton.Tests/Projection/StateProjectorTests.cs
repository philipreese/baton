using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Domain;
using Baton.Projection;
using Baton.Store;
using Baton.Tests.Shared;

namespace Baton.Tests.Projection;

public class StateProjectorTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");

    private static WorkflowDefinitionSnapshot TwoStepSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("architect-critic"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
            new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
        ]);

    private static ExecutionRequest MakeRequest(ExecutionId executionId, StepId stepId)
        => new(
            executionId,
            new WorkflowId("wf-1"),
            stepId,
            "worker",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    private static StepState StepFor(FlowState state, StepId stepId) => Assert.Single(state.Steps, s => s.StepId == stepId);

    private static ExecutionRequest MakeStepLessRequest(ExecutionId executionId, string worker = "human")
        => new(
            executionId,
            new WorkflowId("wf-1"),
            StepId: null,
            worker,
            Inputs: [],
            Outputs: [],
            Timeout: null,
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    [Fact]
    public void A_step_with_no_events_at_all_projects_as_Pending()
    {
        var state = StateProjector.Project([], TwoStepSnapshot());

        Assert.All(state.Steps, s =>
        {
            Assert.Equal(StepStatus.Pending, s.Status);
            Assert.Null(s.LatestExecutionId);
        });
    }

    [Fact]
    public void An_accepted_request_with_no_terminal_event_yet_projects_as_Running()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Running, architect.Status);
        Assert.Equal(executionId, architect.LatestExecutionId);
    }

    [Fact]
    public void A_succeeded_execution_projects_as_Succeeded()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Succeeded, StepFor(state, Architect).Status);
    }

    /// <summary>
    /// #1622 (b)/#1390: the seam between <c>Outcomes.OutcomeClassifier</c> (which computes
    /// WorkspaceChanged/Hollow/HollowReason onto the classification) and
    /// <c>Status.WorkflowStatusProjector</c> (which reads them off <c>StepState</c> for
    /// <c>status --json</c>) is <see cref="FlowEvent.ExecutionSucceeded"/> and this projector — this
    /// is the wiring test for that seam, independent of a real dispatch.
    /// </summary>
    [Fact]
    public void A_succeeded_execution_carries_workspaceChanged_and_hollow_through_to_StepState()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId, WorkspaceChanged: false, Hollow: true, HollowReason: "no diff, no outputs"),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Succeeded, architect.Status);
        Assert.False(architect.WorkspaceChanged);
        Assert.True(architect.Hollow);
        Assert.Equal("no diff, no outputs", architect.HollowReason);
    }

    /// <summary>
    /// The field-absence control: a plain <see cref="FlowEvent.ExecutionSucceeded"/> (the shape every
    /// non-tree-changing role's settle, and every pre-#1622 ledger line, carries) must leave all three
    /// fields null, not defaulted to false — the "field absent for a review role" contract.
    /// </summary>
    [Fact]
    public void A_succeeded_execution_with_no_work_product_fields_leaves_them_null()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Null(architect.WorkspaceChanged);
        Assert.Null(architect.Hollow);
        Assert.Null(architect.HollowReason);
    }

    [Fact]
    public void A_failed_execution_projects_as_Failed()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Failed, StepFor(state, Architect).Status);
    }

    [Fact]
    public void A_cancelled_execution_projects_as_Cancelled()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.CancellationRequested(executionId),
            new FlowEvent.ExecutionCancelled(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Cancelled, StepFor(state, Architect).Status);
    }

    [Fact]
    public void Only_the_most_recently_accepted_attempt_determines_a_steps_status()
    {
        var firstAttempt = new ExecutionId("exec-1");
        var secondAttempt = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(firstAttempt, Architect)),
            new FlowEvent.ExecutionFailed(firstAttempt, FailureClassification.Retryable),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(secondAttempt, Architect)),
            new FlowEvent.ExecutionSucceeded(secondAttempt),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Succeeded, architect.Status);
        Assert.Equal(secondAttempt, architect.LatestExecutionId);
    }

    [Fact]
    public void A_paused_execution_projects_as_Paused_even_though_it_already_reached_a_terminal_outcome()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Critic),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Paused, StepFor(state, Critic).Status);
    }

    [Fact]
    public void Resuming_a_paused_execution_reverts_it_to_its_underlying_terminal_status()
    {
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.Resume, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Succeeded, StepFor(state, Critic).Status);
    }

    [Fact]
    public void Rejecting_a_paused_execution_that_had_succeeded_projects_it_as_Rejected()
    {
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.Reject, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Rejected, StepFor(state, Critic).Status);
    }

    [Fact]
    public void Rejecting_a_paused_execution_that_had_failed_projects_it_as_Rejected_not_Failed()
    {
        // Equivalent in effect to exhausting RetryPolicy, but externally triggered:
        // Rejected is a distinct terminal status from Failed so the Retry Engine never reconsiders it.
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable),
            new FlowEvent.WorkflowPaused(executionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.Reject, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Rejected, StepFor(state, Critic).Status);
    }

    [Fact]
    public void A_paused_execution_reports_its_underlying_outcome_as_PausedOutcome()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Critic),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Succeeded, StepFor(state, Critic).PausedOutcome);
    }

    [Fact]
    public void A_step_that_is_not_currently_paused_reports_a_null_PausedOutcome()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Null(StepFor(state, Critic).PausedOutcome);
    }

    [Fact]
    public void A_step_with_no_WorkflowPaused_ever_recorded_projects_PauseRecordedForLatestExecution_false()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.False(StepFor(state, Critic).PauseRecordedForLatestExecution);
    }

    [Fact]
    public void PauseRecordedForLatestExecution_stays_true_after_resume_even_though_Status_reverts()
    {
        // "One resolving decision per pause": Resume clears the transient Paused status,
        // but the fact that this exact ExecutionId was once paused must survive so the Pause Engine
        // never re-pauses it.
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.Resume, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var critic = StepFor(state, Critic);
        Assert.Equal(StepStatus.Succeeded, critic.Status);
        Assert.True(critic.PauseRecordedForLatestExecution);
    }

    [Fact]
    public void A_new_attempt_after_resume_starts_with_PauseRecordedForLatestExecution_false()
    {
        // A fresh ExecutionId (e.g. via RetryWithRevision/Supersede, landing in later
        // phases) has never itself been paused, regardless of the step's history.
        var firstAttempt = new ExecutionId("exec-1");
        var secondAttempt = new ExecutionId("exec-2");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(firstAttempt, Critic)),
            new FlowEvent.ExecutionSucceeded(firstAttempt),
            new FlowEvent.WorkflowPaused(firstAttempt, Critic),
            new FlowEvent.ExternalDecisionRecorded(decisionId, firstAttempt, DecisionType.Resume, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(secondAttempt, Critic)),
            new FlowEvent.ExecutionSucceeded(secondAttempt),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.False(StepFor(state, Critic).PauseRecordedForLatestExecution);
    }

    [Fact]
    public void An_all_pending_workflow_projects_WorkflowStatus_Running()
    {
        // Flipped by #810 (the pin carried no rationale): an empty journal on a started run means
        // "accepted, first dispatch imminent" — or crashed before it, which already reads as
        // in-flight. Terminal's contract is "nothing further to dispatch", and a root step with
        // satisfiable dependencies is exactly further-to-dispatch.
        var state = StateProjector.Project([], TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Running, state.Status);
    }

    [Fact]
    public void A_workflow_with_a_running_step_projects_WorkflowStatus_Running()
    {
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("exec-1"), Architect)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Running, state.Status);
    }

    [Fact]
    public void A_workflow_with_a_paused_step_and_nothing_running_projects_WorkflowStatus_Paused()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Architect),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Paused, state.Status);
    }

    [Fact]
    public void Running_takes_priority_over_Paused_when_both_are_present()
    {
        var architectExecutionId = new ExecutionId("exec-1");
        var criticExecutionId = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
            new FlowEvent.ExecutionSucceeded(architectExecutionId),
            new FlowEvent.WorkflowPaused(architectExecutionId, Architect),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Running, state.Status);
    }

    [Fact]
    public void A_fully_succeeded_workflow_projects_WorkflowStatus_Terminal()
    {
        var architectExecutionId = new ExecutionId("exec-1");
        var criticExecutionId = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
            new FlowEvent.ExecutionSucceeded(architectExecutionId),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
            new FlowEvent.ExecutionSucceeded(criticExecutionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Terminal, state.Status);
    }

    [Fact]
    public void A_rejected_request_never_having_been_accepted_leaves_the_step_Pending()
    {
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestRejected(new ExecutionId("exec-1"), "concurrency cap reached"),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Pending, architect.Status);
        Assert.Null(architect.LatestExecutionId);
    }

    /// <summary>
    /// #1549: the discriminating instrument for "new events are no-ops for state" — a plain
    /// round-trip through <see cref="StateProjector"/> would pass even if one of these events
    /// accidentally mutated <see cref="ProjectionCheckpointState"/>, as long as nothing later
    /// asserted on the mutated field. Comparing the full projected <see cref="FlowState"/> WITH each
    /// event interleaved against the same sequence WITHOUT it is what actually proves "no state
    /// consequence" rather than merely "nothing this test happened to check changed."
    /// </summary>
    [Fact]
    public void The_1549_heartbeat_and_cancellation_delivery_events_never_change_projected_state()
    {
        var executionId = new ExecutionId("exec-1");
        var baseline = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };
        // FlowState's own record equality is reference-equal on its List<T> members (records don't
        // get structural collection equality for free), so two independently-projected FlowStates
        // that are genuinely identical still fail Assert.Equal directly -- serializing both to JSON
        // is what actually compares the full shape.
        var baselineJson = JsonSerializer.Serialize(StateProjector.Project(baseline, TwoStepSnapshot()));

        FlowEvent[] Interleaved(FlowEvent noOpEvent) =>
        [
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            noOpEvent,
            new FlowEvent.ExecutionSucceeded(executionId),
        ];

        foreach (var noOpEvent in new FlowEvent[]
                 {
                     new FlowEvent.ExecutionProgress(executionId),
                     new FlowEvent.CancellationDelivered(executionId),
                     new FlowEvent.CancellationRejected(executionId),
                 })
        {
            var json = JsonSerializer.Serialize(StateProjector.Project(Interleaved(noOpEvent), TwoStepSnapshot()));
            Assert.Equal(baselineJson, json);
        }
    }

    /// <summary>
    /// #734: the same discriminating instrument as
    /// <see cref="The_1549_heartbeat_and_cancellation_delivery_events_never_change_projected_state"/>
    /// above, applied to the four newer cases <c>StateProjector</c>'s own no-op group cites this test
    /// by name for.
    /// </summary>
    [Fact]
    public void The_734_delivery_events_never_change_projected_state()
    {
        var executionId = new ExecutionId("exec-1");
        var baseline = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };
        var baselineJson = JsonSerializer.Serialize(StateProjector.Project(baseline, TwoStepSnapshot()));

        FlowEvent[] Interleaved(FlowEvent noOpEvent) =>
        [
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            noOpEvent,
            new FlowEvent.ExecutionSucceeded(executionId),
        ];

        foreach (var noOpEvent in new FlowEvent[]
                 {
                     new FlowEvent.DeliveryPrOpened(123, "734-lane"),
                     new FlowEvent.DeliveryChecksGreen(123),
                     new FlowEvent.DeliveryChecksRed(123),
                     new FlowEvent.DeliveryMerged(123),
                     new FlowEvent.DeliveryMerged(123, Merged: false),
                 })
        {
            var json = JsonSerializer.Serialize(StateProjector.Project(Interleaved(noOpEvent), TwoStepSnapshot()));
            Assert.Equal(baselineJson, json);
        }
    }

    [Fact]
    public void A_fail_fail_succeed_sequence_resets_the_consecutive_failure_count_to_zero()
    {
        var first = new ExecutionId("exec-1");
        var second = new ExecutionId("exec-2");
        var third = new ExecutionId("exec-3");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(first, Architect)),
            new FlowEvent.ExecutionFailed(first, FailureClassification.Retryable),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(second, Architect)),
            new FlowEvent.ExecutionFailed(second, FailureClassification.Retryable),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(third, Architect)),
            new FlowEvent.ExecutionSucceeded(third),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(0, architect.ConsecutiveFailureCount);
        Assert.Null(architect.LatestFailureClassification);
    }

    [Fact]
    public void A_fail_fail_sequence_leaves_the_consecutive_failure_count_at_two()
    {
        var first = new ExecutionId("exec-1");
        var second = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(first, Architect)),
            new FlowEvent.ExecutionFailed(first, FailureClassification.Retryable),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(second, Architect)),
            new FlowEvent.ExecutionFailed(second, FailureClassification.Permanent),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(2, architect.ConsecutiveFailureCount);
        Assert.Equal(FailureClassification.Permanent, architect.LatestFailureClassification);
    }

    [Fact]
    public void A_step_with_no_events_projects_a_zero_consecutive_failure_count_and_null_classification()
    {
        var state = StateProjector.Project([], TwoStepSnapshot());

        Assert.All(state.Steps, s =>
        {
            Assert.Equal(0, s.ConsecutiveFailureCount);
            Assert.Null(s.LatestFailureClassification);
        });
    }

    [Fact]
    public void Causal_linking_for_failure_history_is_by_ExecutionId_not_line_position()
    {
        // Architect and Critic attempts interleave in the log; each step's failure count must
        // track only its own ExecutionIds, never be confused by append order across steps.
        var architectFirst = new ExecutionId("a-1");
        var criticFirst = new ExecutionId("c-1");
        var architectSecond = new ExecutionId("a-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectFirst, Architect)),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticFirst, Critic)),
            new FlowEvent.ExecutionFailed(architectFirst, FailureClassification.Retryable),
            new FlowEvent.ExecutionSucceeded(criticFirst),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectSecond, Architect)),
            new FlowEvent.ExecutionFailed(architectSecond, FailureClassification.Retryable),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(2, StepFor(state, Architect).ConsecutiveFailureCount);
        Assert.Equal(0, StepFor(state, Critic).ConsecutiveFailureCount);
        Assert.Null(StepFor(state, Critic).LatestFailureClassification);
    }

    [Fact]
    public void RetryWithRevision_resets_the_consecutive_failure_count_for_a_fresh_retry_round()
    {
        // The externally-triggered counterpart to a success resetting the budget (M8 Phase 1):
        // an exhausted step reopened via RetryWithRevision must not still read as exhausted.
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable),
            new FlowEvent.WorkflowPaused(executionId, Architect),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.RetryWithRevision, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Failed, architect.Status);
        Assert.Equal(0, architect.ConsecutiveFailureCount);
    }

    [Fact]
    public void An_ExhaustedUntil_failure_does_not_increment_the_consecutive_failure_count()
    {
        // Both directions of the projector's ExhaustedUntil counting rule (the why lives on the
        // ExecutionFailed case in StateProjector.cs): the quota hit leaves the count alone, the
        // ordinary failure after it still counts.
        var first = new ExecutionId("exec-1");
        var second = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(first, Architect)),
            new FlowEvent.ExecutionFailed(first, FailureClassification.ExhaustedUntil, "quota", DateTimeOffset.UnixEpoch),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(second, Architect)),
            new FlowEvent.ExecutionFailed(second, FailureClassification.Retryable),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(1, architect.ConsecutiveFailureCount);
        Assert.Equal(FailureClassification.Retryable, architect.LatestFailureClassification);
    }

    [Fact]
    public void RetryWithRevision_clears_the_latest_failure_classification_like_a_success_does()
    {
        // The reopen IS a fresh round (the count reset above already says so). Leaving the stale
        // classification behind let a reopened quota-failed step re-pace itself to the old vendor
        // reset moment instead of honoring the operator's explicit retry-now. The log replayed
        // here is also what a pre-#594 engine could legitimately have written (a quota step that
        // auto-paused), so this doubles as replay compatibility for that history.
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota", DateTimeOffset.UnixEpoch),
            new FlowEvent.WorkflowPaused(executionId, Architect),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.RetryWithRevision, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Null(architect.LatestFailureClassification);
        Assert.Null(architect.LatestFailureReason);
        Assert.Null(architect.LatestExecutionFailedRetryNotBefore);
    }

    /// <summary>
    /// #815: the same clearing as above, but reached via the widened validator path — a quota-
    /// parked step that was never paused (no <see cref="FlowEvent.WorkflowPaused"/> in this log at
    /// all), which a lane workflow's missing <see cref="WorkflowStepDefinition.PausePoint"/> makes
    /// the ordinary case. <see cref="MutationInterface"/> always appends
    /// <see cref="FlowEvent.WorkflowResumed"/> after recording a decision regardless of whether a
    /// pause occurred, and this projector case already keys off the decision's
    /// <see cref="FlowEvent.ExternalDecisionRecorded.ReferencedExecutionId"/> rather than Paused
    /// status — so no projector change was needed for #815, only proven here.
    /// </summary>
    [Fact]
    public void RetryWithRevision_clears_the_classification_for_a_quota_parked_step_with_no_prior_pause()
    {
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var retryNotBefore = DateTimeOffset.UnixEpoch.AddHours(1);
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota", retryNotBefore),
            new FlowEvent.StepRetryScheduled(Architect, executionId, retryNotBefore, RetryDelayMs: 60_000),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.RetryWithRevision, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Failed, architect.Status);
        Assert.Equal(0, architect.ConsecutiveFailureCount);
        Assert.Null(architect.LatestFailureClassification);
        Assert.Null(architect.LatestFailureReason);
        Assert.Null(architect.LatestExecutionFailedRetryNotBefore);
        Assert.Null(architect.RetryNotBefore);
        Assert.Null(architect.RetryDelayMs);
    }

    // #1586 S1: FlowEvent.StepRetryForeclosed -- the missing primitive the state-truth design's own
    // proposal on #1586 names. No producer appends this event in this slice; every fixture below
    // fabricates it directly, exactly as the slice's own scope note permits.

    [Fact]
    public void StepRetryForeclosed_clears_the_pending_retry_fields_and_records_the_flag()
    {
        var executionId = new ExecutionId("exec-1");
        var retryNotBefore = DateTimeOffset.UnixEpoch.AddHours(4);
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota", retryNotBefore),
            new FlowEvent.StepRetryScheduled(Architect, executionId, retryNotBefore, RetryDelayMs: 60_000),
            new FlowEvent.StepRetryForeclosed(Architect, executionId, "dead pump, unfireable park", ForeclosedBy: "settle"),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.True(architect.RetryForeclosed);
        Assert.Null(architect.RetryNotBefore);
        Assert.Null(architect.RetryDelayMs);
        Assert.Null(architect.RetryScheduledForExecutionId);
    }

    [Fact]
    public void StepRetryForeclosed_is_a_noop_when_ForExecutionId_does_not_match_the_currently_scheduled_retry()
    {
        // #1586 S1: the all-or-nothing guard (mirroring ExecutionCancelled's own retry-field clear,
        // #1605) -- a foreclosure naming a STALE execution id must not touch a retry re-scheduled
        // since, for either half of the change: the flag must stay unset AND the fields must stay
        // populated. A half-applied foreclosure (flag set, fields intact, or the reverse) would make
        // DeriveWorkflowStatus's two independent deliverability disjuncts (RetryNotBefore is not null
        // / MayRetry) disagree with each other.
        var staleExecutionId = new ExecutionId("exec-1");
        var currentExecutionId = new ExecutionId("exec-2");
        var retryNotBefore = DateTimeOffset.UnixEpoch.AddHours(4);
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(currentExecutionId, Architect)),
            new FlowEvent.ExecutionFailed(currentExecutionId, FailureClassification.ExhaustedUntil, "quota", retryNotBefore),
            new FlowEvent.StepRetryScheduled(Architect, currentExecutionId, retryNotBefore, RetryDelayMs: 60_000),
            // Names the OLDER execution -- the retry now scheduled belongs to currentExecutionId.
            new FlowEvent.StepRetryForeclosed(Architect, staleExecutionId, "stale foreclosure"),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.False(architect.RetryForeclosed);
        Assert.Equal(retryNotBefore, architect.RetryNotBefore);
        Assert.Equal(60_000, architect.RetryDelayMs);
        Assert.Equal(currentExecutionId, architect.RetryScheduledForExecutionId);
    }

    [Fact]
    public void A_fresh_ExecutionRequestAccepted_reopens_a_foreclosed_step()
    {
        var executionId = new ExecutionId("exec-1");
        var retryNotBefore = DateTimeOffset.UnixEpoch.AddHours(4);
        var redriveExecutionId = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota", retryNotBefore),
            new FlowEvent.StepRetryScheduled(Architect, executionId, retryNotBefore, RetryDelayMs: 60_000),
            new FlowEvent.StepRetryForeclosed(Architect, executionId, "dead pump"),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(redriveExecutionId, Architect)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.False(StepFor(state, Architect).RetryForeclosed);
    }

    [Fact]
    public void A_fresh_ExecutionRequestAccepted_reopens_an_indeterminate_step()
    {
        // #1623 / F5: a fresh dispatch clears the Indeterminate flag and its reason text alongside
        // RetryForeclosedStepIds. Both halves asserted, not just the text: the flag is what would
        // otherwise leave WorkflowOutcome pinned to Indeterminate and MayRetry permanently false
        // underneath a legitimate new attempt -- which is the whole failure this clear exists to
        // prevent, and it is invisible to an assertion that only reads the diagnostic string.
        var executionId = new ExecutionId("exec-1");
        var redriveExecutionId = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.VerifyFailed(executionId, ["fmt-check"], "GATES: FAIL 1 of 25 -- fmt-check"),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(redriveExecutionId, Architect)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.False(architect.IndeterminateAwaitingResolution);
        Assert.Null(architect.IndeterminateReason);
        Assert.False(architect.RetryForeclosed);
    }

    [Fact]
    public void RetryWithRevision_reopens_a_foreclosed_step()
    {
        var executionId = new ExecutionId("exec-1");
        var retryNotBefore = DateTimeOffset.UnixEpoch.AddHours(4);
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota", retryNotBefore),
            new FlowEvent.StepRetryScheduled(Architect, executionId, retryNotBefore, RetryDelayMs: 60_000),
            new FlowEvent.StepRetryForeclosed(Architect, executionId, "dead pump"),
            new FlowEvent.WorkflowPaused(executionId, Architect),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.RetryWithRevision, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.False(StepFor(state, Architect).RetryForeclosed);
    }

    [Fact]
    public void StepRetryForeclosed_survives_an_incremental_checkpoint_resume()
    {
        // The DeepCopy landmine: ProjectionCheckpointState.DeepCopy constructs a new instance
        // POSITIONALLY, so a trailing member relying only on its `?? new()` init default (as
        // RetryForeclosedStepIds does, for replay-safety against an older checkpoint's serialized
        // JSON) is silently dropped here if DeepCopy's own constructor call forgets to pass it along
        // -- exactly the shape #1606 hit first with LatestCapturedResponseFileByStepId /
        // LatestUnsatisfiedOutputNamesByStepId. A plain ApplyEvent unit test cannot catch this: it
        // never DeepCopies. Resuming from a checkpoint over the SAME events (zero new events to
        // replay) isolates the checkpoint's own carried state as the only source of truth.
        var executionId = new ExecutionId("exec-1");
        var retryNotBefore = DateTimeOffset.UnixEpoch.AddHours(4);
        var events = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota", retryNotBefore),
            new FlowEvent.StepRetryScheduled(Architect, executionId, retryNotBefore, RetryDelayMs: 60_000),
            new FlowEvent.StepRetryForeclosed(Architect, executionId, "dead pump"),
        };

        var (freshState, checkpoint) = StateProjector.ProjectAndCheckpoint(events, TwoStepSnapshot());
        Assert.True(StepFor(freshState, Architect).RetryForeclosed);

        // Same full event list, plus the prior checkpoint: StateProjector.Project's `logByteOffset: 0`
        // default takes the "full event list supplied" branch (skipCount = checkpoint.EventOffset ==
        // events.Count), so DeepCopy is exercised and NOTHING is replayed on top of it.
        var resumedState = StateProjector.Project(events, TwoStepSnapshot(), checkpoint);

        Assert.True(StepFor(resumedState, Architect).RetryForeclosed);
    }

    // The state-truth design's own named red test on #1586 ("assert both polarities"): a parked
    // room's projection must go Terminal WITH the foreclosure event and stay Running WITHOUT it. Same
    // event log, one event apart.

    [Fact]
    public void Foreclosing_an_unfireable_ExhaustedUntil_park_lets_the_workflow_reach_Terminal()
    {
        var executionId = new ExecutionId("exec-1");
        var retryNotBefore = DateTimeOffset.UnixEpoch.AddHours(4);
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota", retryNotBefore),
            new FlowEvent.StepRetryScheduled(Architect, executionId, retryNotBefore, RetryDelayMs: 60_000),
            new FlowEvent.StepRetryForeclosed(Architect, executionId, "dead pump, unfireable park"),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Terminal, state.Status);
    }

    [Fact]
    public void The_same_park_without_foreclosure_keeps_the_workflow_Running()
    {
        // Polarity partner: identical log, minus the StepRetryForeclosed event -- proves the Terminal
        // reading above is caused by the foreclosure, not incidentally by the ExhaustedUntil shape
        // itself (which #1513's own design deliberately keeps Running/"Stalled", never Terminal, absent
        // a settle).
        var executionId = new ExecutionId("exec-1");
        var retryNotBefore = DateTimeOffset.UnixEpoch.AddHours(4);
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota", retryNotBefore),
            new FlowEvent.StepRetryScheduled(Architect, executionId, retryNotBefore, RetryDelayMs: 60_000),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Running, state.Status);
    }

    // #1877: the second apply arm -- an administrative foreclosure naming the step's LATEST execution
    // when NOTHING is scheduled for it. Both fixtures below deliberately carry no StepRetryScheduled,
    // which is what makes them discriminate: every fixture above reaches the pre-existing
    // "matches the scheduled retry" arm instead, so reverting the new arm leaves all of them green.

    [Fact]
    public void StepRetryForeclosed_applies_with_no_scheduled_retry_when_it_names_the_steps_latest_execution()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Permanent, "rejected by conductor"),
            // No StepRetryScheduled anywhere: the only arm that can apply this is the #1877 one.
            new FlowEvent.StepRetryForeclosed(Architect, executionId, "already rejected", ForeclosedBy: "resolve --close"),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.True(architect.RetryForeclosed);
        Assert.Null(architect.RetryNotBefore);
        Assert.Null(architect.RetryDelayMs);
        Assert.Null(architect.RetryScheduledForExecutionId);
    }

    [Fact]
    public void StepRetryForeclosed_with_no_scheduled_retry_is_a_noop_when_it_names_a_stale_execution()
    {
        // Polarity partner: identical no-schedule shape, one execution newer. The step's latest
        // execution has moved past the name this event carries, so neither arm may apply it --
        // otherwise a foreclosure could reach back over a re-drive that already reopened the step.
        var staleExecutionId = new ExecutionId("exec-1");
        var currentExecutionId = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(staleExecutionId, Architect)),
            new FlowEvent.ExecutionFailed(staleExecutionId, FailureClassification.Permanent, "rejected by conductor"),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(currentExecutionId, Architect)),
            new FlowEvent.StepRetryForeclosed(Architect, staleExecutionId, "stale foreclosure", ForeclosedBy: "resolve --close"),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.False(StepFor(state, Architect).RetryForeclosed);
    }

    // #1608: FlowEvent.ExecutionIndeterminate / FlowEvent.CaptureResolved -- the two-predicate
    // model's disagreement case and its own resolution verb's room fact.

    [Fact]
    public void ExecutionIndeterminate_projects_Failed_status_with_the_awaiting_resolution_flag_set()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionIndeterminate(
                executionId, "captured, awaiting conductor resolution", ".captured-response.md", ["plan.md"]),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        // See StateProjector's ExecutionIndeterminate case comment for the single-added-enum-value
        // ruling this pins. WorkflowOutcome.DescribeTerminal is what reads the flag below at the
        // room level; pinned separately by WorkflowOutcomeAndExitCodeTests.
        Assert.Equal(StepStatus.Failed, architect.Status);
        Assert.True(architect.IndeterminateAwaitingResolution);
        Assert.Equal(".captured-response.md", architect.LatestCapturedResponseFile);
        Assert.Equal(["plan.md"], architect.LatestUnsatisfiedOutputNames);
        Assert.Null(architect.LatestFailureClassification);
        Assert.Null(architect.LatestExecutionFailedRetryNotBefore);
    }

    [Fact]
    public void CaptureResolved_accepted_settles_the_step_Succeeded_and_clears_the_capture_fields()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionIndeterminate(executionId, "captured", ".captured-response.md", ["plan"]),
            new FlowEvent.CaptureResolved(Architect, executionId, Accepted: true, ResolvedOutputNames: ["plan"]),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Succeeded, architect.Status);
        Assert.False(architect.IndeterminateAwaitingResolution);
        Assert.Null(architect.LatestCapturedResponseFile);
        Assert.Null(architect.LatestUnsatisfiedOutputNames);
    }

    [Fact]
    public void CaptureResolved_rejected_leaves_the_step_Failed_but_clears_the_awaiting_resolution_flag()
    {
        // Polarity partner of the accepted test above -- one field apart (Accepted: false), otherwise
        // identical, proving the Succeeded settle above is about Accepted, not incidentally about
        // CaptureResolved being projected at all. The captured-response audit trail (file name,
        // unsatisfied names) stays recorded -- only the resolution state changes.
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionIndeterminate(executionId, "captured", ".captured-response.md", ["plan"]),
            new FlowEvent.CaptureResolved(Architect, executionId, Accepted: false, Reason: "not an honest plan.md"),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Failed, architect.Status);
        Assert.False(architect.IndeterminateAwaitingResolution);
        Assert.Equal(".captured-response.md", architect.LatestCapturedResponseFile);
        Assert.Equal(["plan"], architect.LatestUnsatisfiedOutputNames);
    }

    /// <summary>
    /// F8 (#1720 review): a non-accepting resolution of a step with NO recorded producer — the legacy
    /// pre-#1593 shape `--close` admits — must foreclose retry, or the engine re-dispatches a step the
    /// conductor just closed. Unreachable through today's writers (every one records a producer),
    /// which is exactly why an unforeclosed arm would be invisible if it became reachable.
    /// </summary>
    [Fact]
    public void CaptureResolved_on_a_step_with_no_recorded_producer_forecloses_retry()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable, "worker exited 1"),
            new FlowEvent.CaptureResolved(Architect, executionId, Accepted: false, Reason: "closed by the conductor"),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Failed, architect.Status);
        Assert.True(architect.RetryForeclosed);
        // F11's other half: a null producer means `--close`, not `--reject` -- the admission
        // predicates never let `--reject` reach one -- so the room reads resolved, not rejected.
        Assert.True(architect.ResolvedByConductor);
        Assert.False(architect.ConductorRejected);
    }

    [Fact]
    public void CaptureResolved_is_a_noop_when_StepId_does_not_match_the_execution_owning_step()
    {
        // The same stale-target guard StepRetryForeclosed's own ForExecutionId check already follows
        // -- a resolution naming the wrong step for this execution id must not misapply.
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionIndeterminate(executionId, "captured", ".captured-response.md", ["plan"]),
            new FlowEvent.CaptureResolved(Critic, executionId, Accepted: true, ResolvedOutputNames: ["plan"]),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Failed, architect.Status);
        Assert.True(architect.IndeterminateAwaitingResolution);
    }

    [Fact]
    public void A_fresh_ExecutionRequestAccepted_clears_an_unresolved_indeterminate_capture()
    {
        // Review finding on this PR: mirrors A_fresh_ExecutionRequestAccepted_reopens_a_foreclosed_step
        // above -- defensive symmetry, not a currently-reachable path today. See StateProjector's own
        // #1608 review comment on this same clear (ExecutionRequestAccepted arm) for why it's pinned
        // anyway.
        var executionId = new ExecutionId("exec-1");
        var redriveExecutionId = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionIndeterminate(executionId, "captured", ".captured-response.md", ["plan"]),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(redriveExecutionId, Architect)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.False(StepFor(state, Architect).IndeterminateAwaitingResolution);
    }

    [Fact]
    public void A_step_declaring_PausePoint_that_settles_Indeterminate_projects_Paused_with_the_flag_still_set()
    {
        // #1608 review finding 3: the shape spec/baton.md §3's "Unless the step declares a
        // PausePoint" section describes, pinned at the projection layer -- FlowState.cs's doc for
        // IndeterminateAwaitingResolution previously claimed it could not occur. This fixture appends
        // the same two events the pump would append for that pause obligation, so the projected shape
        // is pinned independent of the scheduling layer that decides to append them.
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snapshot-pause"),
            new WorkflowTemplateId("architect-only-paused"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(
                    Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1),
                    PausePoint: new PausePoint([])),
            ]);

        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionIndeterminate(
                executionId, "captured, awaiting conductor resolution", ".captured-response.md", ["plan"]),
            new FlowEvent.WorkflowPaused(executionId, Architect),
        };

        var state = StateProjector.Project(events, snapshot);

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Paused, architect.Status);
        Assert.True(architect.IndeterminateAwaitingResolution);
        Assert.Equal(WorkflowStatus.Paused, state.Status);
    }

    [Fact]
    public void ExecutionIndeterminate_survives_an_incremental_checkpoint_resume()
    {
        // The same DeepCopy landmine StepRetryForeclosed_survives_an_incremental_checkpoint_resume
        // above pins for RetryForeclosedStepIds -- IndeterminateAwaitingResolutionStepIds is a second,
        // independent trailing member relying on ProjectionCheckpointState.DeepCopy's positional
        // constructor call actually carrying it forward.
        var executionId = new ExecutionId("exec-1");
        var events = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionIndeterminate(executionId, "captured", ".captured-response.md", ["plan"]),
        };

        var (freshState, checkpoint) = StateProjector.ProjectAndCheckpoint(events, TwoStepSnapshot());
        Assert.True(StepFor(freshState, Architect).IndeterminateAwaitingResolution);

        var resumedState = StateProjector.Project(events, TwoStepSnapshot(), checkpoint);

        Assert.True(StepFor(resumedState, Architect).IndeterminateAwaitingResolution);
    }

    [Fact]
    public void RetryEngine_MayRetry_refuses_an_unresolved_indeterminate_step_projected_from_real_events()
    {
        // Closes the loop between StateProjector's own projection and RetryEngineTests' hand-built
        // fixtures: this proves the flag RetryEngine.MayRetry gates on is the same one
        // ExecutionIndeterminate actually sets, not merely a field the two happen to share a name for.
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionIndeterminate(executionId, "captured", ".captured-response.md", ["plan"]),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.False(Baton.Scheduling.RetryEngine.MayRetry(StepFor(state, Architect), new RetryPolicy(MaxAttempts: 5)));
    }

    [Fact]
    public void RetryWithRevision_with_a_SupplementaryExecutionId_projects_it_as_pending_for_the_referenced_step()
    {
        var executionId = new ExecutionId("exec-1");
        var supplementaryExecutionId = new ExecutionId("supplement-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable),
            new FlowEvent.WorkflowPaused(executionId, Architect),
            new FlowEvent.ExternalDecisionRecorded(
                decisionId, executionId, DecisionType.RetryWithRevision, null, supplementaryExecutionId),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(supplementaryExecutionId, StepFor(state, Architect).PendingSupplementaryExecutionId);
    }

    [Fact]
    public void A_newer_ExecutionRequestAccepted_clears_the_pending_supplementary_fact_for_that_step()
    {
        var firstAttempt = new ExecutionId("exec-1");
        var secondAttempt = new ExecutionId("exec-2");
        var supplementaryExecutionId = new ExecutionId("supplement-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(firstAttempt, Architect)),
            new FlowEvent.ExecutionFailed(firstAttempt, FailureClassification.Retryable),
            new FlowEvent.WorkflowPaused(firstAttempt, Architect),
            new FlowEvent.ExternalDecisionRecorded(
                decisionId, firstAttempt, DecisionType.RetryWithRevision, null, supplementaryExecutionId),
            new FlowEvent.WorkflowResumed(decisionId),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(secondAttempt, Architect)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Null(StepFor(state, Architect).PendingSupplementaryExecutionId);
    }

    [Fact]
    public void Supersede_marks_its_TargetStepId_as_a_pending_Supersede_target_with_a_pending_supplement()
    {
        var criticExecutionId = new ExecutionId("c-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
            new FlowEvent.ExecutionSucceeded(new ExecutionId("a-1")),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
            new FlowEvent.ExecutionSucceeded(criticExecutionId),
            new FlowEvent.WorkflowPaused(criticExecutionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(
                decisionId, criticExecutionId, DecisionType.Supersede, Architect, criticExecutionId),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.True(architect.IsPendingSupersedeTarget);
        Assert.Equal(criticExecutionId, architect.PendingSupplementaryExecutionId);

        // Critic itself carries no pending fact — it is the decision's referent, not its target.
        Assert.False(StepFor(state, Critic).IsPendingSupersedeTarget);
    }

    [Fact]
    public void A_newer_ExecutionRequestAccepted_for_the_target_clears_IsPendingSupersedeTarget()
    {
        var architectFirstAttempt = new ExecutionId("a-1");
        var architectSecondAttempt = new ExecutionId("a-2");
        var criticExecutionId = new ExecutionId("c-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectFirstAttempt, Architect)),
            new FlowEvent.ExecutionSucceeded(architectFirstAttempt),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
            new FlowEvent.ExecutionSucceeded(criticExecutionId),
            new FlowEvent.WorkflowPaused(criticExecutionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(
                decisionId, criticExecutionId, DecisionType.Supersede, Architect, criticExecutionId),
            new FlowEvent.WorkflowResumed(decisionId),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectSecondAttempt, Architect)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.False(architect.IsPendingSupersedeTarget);
        Assert.Null(architect.PendingSupplementaryExecutionId);
    }

    [Fact]
    public void Projecting_the_same_events_twice_produces_an_identical_result()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };
        var snapshot = TwoStepSnapshot();

        var first = StateProjector.Project(events, snapshot);
        var second = StateProjector.Project(events, snapshot);

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    [Fact]
    public void A_step_less_ExecutionRequestAccepted_with_no_terminal_event_is_a_pending_StepLessExecution()
    {
        var executionId = new ExecutionId("supplement-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeStepLessRequest(executionId)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var stepLess = Assert.Single(state.StepLessExecutions);
        Assert.Equal(executionId, stepLess.ExecutionId);
        Assert.Equal("human", stepLess.Worker);

        // Never perturbs any step's own projection — a step-less execution belongs to no StepId.
        Assert.Equal(StepStatus.Pending, StepFor(state, Architect).Status);
        Assert.Equal(StepStatus.Pending, StepFor(state, Critic).Status);
    }

    [Fact]
    public void A_settled_step_less_execution_is_no_longer_a_pending_StepLessExecution()
    {
        var executionId = new ExecutionId("supplement-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeStepLessRequest(executionId)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Empty(state.StepLessExecutions);
    }

    [Fact]
    public void Multiple_pending_step_less_executions_are_tracked_independently_in_append_order()
    {
        var first = new ExecutionId("supplement-1");
        var second = new ExecutionId("supplement-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeStepLessRequest(first)),
            new FlowEvent.ExecutionRequestAccepted(MakeStepLessRequest(second)),
            new FlowEvent.ExecutionSucceeded(first),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var pending = Assert.Single(state.StepLessExecutions);
        Assert.Equal(second, pending.ExecutionId);
    }

    [Fact]
    public void A_CancellationRequested_with_no_terminal_event_yet_is_unfulfilled()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.CancellationRequested(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal([executionId], state.CancellationRequestedExecutionIds);
        // Mid-execution, not an outcome — the step itself stays Running until a terminal event.
        Assert.Equal(StepStatus.Running, StepFor(state, Architect).Status);
    }

    [Fact]
    public void A_CancellationRequested_against_an_already_terminal_execution_is_never_unfulfilled()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.CancellationRequested(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        // The too-late request: recorded, but never surfaced as still owed.
        Assert.Empty(state.CancellationRequestedExecutionIds);
        Assert.Equal(StepStatus.Succeeded, StepFor(state, Architect).Status);
    }

    [Fact]
    public void A_CancellationRequested_is_no_longer_unfulfilled_once_a_terminal_event_lands()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.CancellationRequested(executionId),
            new FlowEvent.ExecutionCancelled(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Empty(state.CancellationRequestedExecutionIds);
        Assert.Equal(StepStatus.Cancelled, StepFor(state, Architect).Status);
    }

    // #810: these pin the phantom-Terminal windows (the why and the live capture live on
    // StateProjector.DeriveWorkflowStatus's doc) plus the polarities that keep genuinely-dead
    // workflows Terminal.

    [Fact]
    public void A_workflow_between_steps_is_Running_not_Terminal()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Pending, StepFor(state, Critic).Status);
        Assert.Equal(WorkflowStatus.Running, state.Status);
    }

    [Fact]
    public void A_failed_step_with_retry_budget_keeps_the_workflow_Running_before_its_retry_is_scheduled()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable),
        };

        var state = StateProjector.Project(events, ThreeAttemptSnapshot());

        Assert.Null(StepFor(state, Architect).RetryNotBefore);
        Assert.Equal(WorkflowStatus.Running, state.Status);
    }

    [Fact]
    public void An_ExhaustedUntil_step_keeps_the_workflow_Running_never_Terminal()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota", DateTimeOffset.UnixEpoch),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Running, state.Status);
    }

    [Fact]
    public void A_retry_eligible_step_below_a_dead_chain_is_dead_too_and_the_workflow_Terminal()
    {
        // The #810 review's high finding, reconstructed: Critic fails with budget left (MayRetry
        // true), then a Supersede reopens Architect and its fresh attempt fails permanently.
        // Critic is locally retry-eligible forever, but DependencyResolver will never dispatch it
        // (its dependency is not Succeeded) — so local eligibility without the dependency cascade
        // read this workflow as Running for eternity. It is Terminal: nothing can ever dispatch.
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
            new FlowEvent.ExecutionSucceeded(new ExecutionId("a-1")),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("c-1"), Critic)),
            new FlowEvent.ExecutionFailed(new ExecutionId("c-1"), FailureClassification.Retryable),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("o-1"), Observer)),
            new FlowEvent.ExecutionSucceeded(new ExecutionId("o-1")),
            new FlowEvent.WorkflowPaused(new ExecutionId("o-1"), Observer),
            new FlowEvent.ExternalDecisionRecorded(
                new DecisionId("decision-1"), new ExecutionId("o-1"), DecisionType.Supersede, Architect, new ExecutionId("o-1")),
            new FlowEvent.WorkflowResumed(new DecisionId("decision-1")),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-2"), Architect)),
            new FlowEvent.ExecutionFailed(new ExecutionId("a-2"), FailureClassification.Permanent),
        };

        var state = StateProjector.Project(events, ThreeStepPausingSnapshot());

        var critic = StepFor(state, Critic);
        Assert.Equal(StepStatus.Failed, critic.Status);
        Assert.Equal(WorkflowStatus.Terminal, state.Status);
    }

    [Fact]
    public void A_pending_step_below_a_budget_exhausted_failure_is_dead_and_the_workflow_Terminal()
    {
        // The polarity that keeps this fix honest: Pending alone must NOT mean Running. Architect
        // fails with no budget left (MaxAttempts 1) and no retry eligibility; Critic can never
        // receive its inputs, so the workflow genuinely has nothing further to dispatch.
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Pending, StepFor(state, Critic).Status);
        Assert.Equal(WorkflowStatus.Terminal, state.Status);
    }

    [Fact]
    public void A_pending_step_below_a_cancelled_step_is_dead_and_the_workflow_Terminal()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionCancelled(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Terminal, state.Status);
    }

    private static readonly StepId Observer = new("observer");

    // Critic carries retry budget (the local-eligibility arm under test); Observer is an
    // independent step whose pause hosts the Supersede decision.
    private static WorkflowDefinitionSnapshot ThreeStepPausingSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-3p"),
        new WorkflowTemplateId("architect-critic-observer"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
            new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(3)),
            new WorkflowStepDefinition(Observer, "observer", ["goal"], ["notes"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
        ]);

    private static WorkflowDefinitionSnapshot ThreeAttemptSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-3a"),
        new WorkflowTemplateId("architect-critic-retrying"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(3)),
            new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(3)),
        ]);

    [Fact]
    public void ExecutionCount_increments_on_every_ExecutionRequestAccepted_and_survives_RetryWithRevision_and_Success()
    {
        var exec1 = new ExecutionId("exec-1");
        var exec2 = new ExecutionId("exec-2");
        var exec3 = new ExecutionId("exec-3");
        var decisionId = new DecisionId("decision-1");
        var snapshot = ThreeAttemptSnapshot();

        // 1. First execution accepted and failed
        var events = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Architect)),
            new FlowEvent.ExecutionFailed(exec1, FailureClassification.Retryable),
        };
        var state1 = StateProjector.Project(events, snapshot);
        var arch1 = StepFor(state1, Architect);
        Assert.Equal(1, arch1.ExecutionCount);
        Assert.Equal(1, arch1.ConsecutiveFailureCount);

        // 2. Second execution accepted and failed with ExhaustedUntil (which doesn't increment ConsecutiveFailureCount)
        events.Add(new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec2, Architect)));
        events.Add(new FlowEvent.ExecutionFailed(exec2, FailureClassification.ExhaustedUntil));
        var state2 = StateProjector.Project(events, snapshot);
        var arch2 = StepFor(state2, Architect);
        Assert.Equal(2, arch2.ExecutionCount);
        Assert.Equal(1, arch2.ConsecutiveFailureCount); // ExhaustedUntil skipped ConsecutiveFailureCount increment

        // 3. Paused, RetryWithRevision, Resumed
        events.Add(new FlowEvent.WorkflowPaused(exec2, Architect));
        events.Add(new FlowEvent.ExternalDecisionRecorded(decisionId, exec2, DecisionType.RetryWithRevision, null, null));
        events.Add(new FlowEvent.WorkflowResumed(decisionId));
        var statePausedResumed = StateProjector.Project(events, snapshot);
        var archPausedResumed = StepFor(statePausedResumed, Architect);
        Assert.Equal(2, archPausedResumed.ExecutionCount);
        Assert.Equal(0, archPausedResumed.ConsecutiveFailureCount); // RetryWithRevision cleared ConsecutiveFailureCount

        // 4. Third execution accepted (post-RetryWithRevision) and succeeded
        events.Add(new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec3, Architect)));
        var state3Running = StateProjector.Project(events, snapshot);
        var arch3Running = StepFor(state3Running, Architect);
        Assert.Equal(3, arch3Running.ExecutionCount);

        events.Add(new FlowEvent.ExecutionSucceeded(exec3));
        var state3Succeeded = StateProjector.Project(events, snapshot);
        var arch3Succeeded = StepFor(state3Succeeded, Architect);
        Assert.Equal(3, arch3Succeeded.ExecutionCount);
        Assert.Equal(0, arch3Succeeded.ConsecutiveFailureCount);
        Assert.Equal(StepStatus.Succeeded, arch3Succeeded.Status);
    }

    [Fact]
    public void StaleCheckpoint_MissingExecutionCountByStepId_IsRejectedRatherThanUndercounted()
    {
        // #1522 review finding 2: the old determinism test called the same pure function twice with
        // identical arguments -- it could not fail. This is the arm that actually discriminates: a
        // checkpoint.json shaped exactly like one a pre-#1522 binary would have written (Version: 2,
        // no ExecutionCountByStepId key at all) must be REJECTED by ProjectionCheckpointStore.Load
        // and force a full replay, not be trusted with the missing counter defaulting to empty.
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_stale_checkpoint_execcount_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var snapshot = TwoStepSnapshot();
            var exec1 = new ExecutionId("exec-1");
            var exec2 = new ExecutionId("exec-2");

            var midwayEvents = new List<FlowEvent>
            {
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Architect)),
                new FlowEvent.ExecutionFailed(exec1, FailureClassification.Retryable),
            };
            var (_, checkpointMidway) = StateProjector.ProjectAndCheckpoint(midwayEvents, snapshot);

            var allEvents = new List<FlowEvent>(midwayEvents)
            {
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec2, Architect)),
                new FlowEvent.ExecutionSucceeded(exec2),
            };

            // Simulate a checkpoint.json written by the pre-#1522 binary: Version 2, and the
            // ExecutionCountByStepId key absent entirely (not present-but-null -- absent, because
            // that binary never knew the key existed).
            var json = JsonSerializer.Serialize(checkpointMidway, FlowEventLogJson.Options);
            var node = JsonNode.Parse(json)!.AsObject();
            node["Version"] = 2;
            node["State"]!.AsObject().Remove("ExecutionCountByStepId");

            var checkpointFilePath = ProjectionCheckpointStore.GetCheckpointFilePath(tempDir);
            Directory.CreateDirectory(Path.GetDirectoryName(checkpointFilePath)!);
            File.WriteAllText(checkpointFilePath, node.ToJsonString());

            var loadedCheckpoint = ProjectionCheckpointStore.Load(tempDir);

            var tailState = StateProjector.Project(allEvents, snapshot, loadedCheckpoint);
            var fullReplayState = StateProjector.Project(allEvents, snapshot, checkpoint: null);

            // With the guard tightened to `Version < 4`, the stale Version-2 checkpoint is rejected
            // (Load returns null) and the room falls back to a full replay -- so the two ordinals
            // agree. Before that fix, Load accepted the Version-2 checkpoint, the missing key
            // defaulted to an empty dictionary, and the tail-only projection undercounted.
            Assert.Null(loadedCheckpoint);
            Assert.Equal(StepFor(fullReplayState, Architect).ExecutionCount, StepFor(tailState, Architect).ExecutionCount);
            Assert.Equal(2, StepFor(fullReplayState, Architect).ExecutionCount);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    // #1623 (contract: spec/baton.md §3). Both of that issue's
    // producers settle a step Indeterminate via the same ApplyIndeterminate helper -- these fixtures
    // pin that shape, mirroring StepRetryForeclosed's own test block above.

    [Fact]
    public void VerifyFailed_settles_the_step_Failed_and_records_an_IndeterminateReason()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.VerifyFailed(executionId, ["fmt-check", "lint"], "GATES: FAIL 2 of 25 -- fmt-check, lint"),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Failed, architect.Status);
        // #1623/#1644 merge: the FLAG is what WorkflowOutcome and RetryEngine read for all three
        // Indeterminate producers -- asserting only the reason text would leave the one line that
        // unifies this producer with #1608's (ApplyIndeterminate's Add to
        // IndeterminateAwaitingResolutionStepIds) deletable with every test still green.
        Assert.True(architect.IndeterminateAwaitingResolution);
        Assert.NotNull(architect.IndeterminateReason);
        Assert.Contains("fmt-check", architect.IndeterminateReason);
        Assert.True(architect.RetryForeclosed);
        Assert.Equal(FailureClassification.Permanent, architect.LatestFailureClassification);
    }

    [Fact]
    public void VerifyFailed_records_the_failing_members_own_output_as_IndeterminateVerifyTail()
    {
        // #1701: proves the ApplyIndeterminate->StepState wiring this new field depends on.
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.VerifyFailed(executionId, ["tool-refresh-selftest"], "FAILED: could not write current pointer"),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal("FAILED: could not write current pointer", architect.IndeterminateVerifyTail);
    }

    [Fact]
    public void ExecutionArrested_leaves_IndeterminateVerifyTail_null_never_fabricated()
    {
        // #1701: an arrest's IndeterminateReason is already the full diagnostic (DescribeArrest) --
        // nothing truncated to recover, so this must stay null rather than echoing the reason text.
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionArrested(executionId, new WorkerUsage(TokensIn: 500_000, TokensOut: 120_000), ["manage_task", "manage_task"]),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.NotNull(architect.IndeterminateReason);
        Assert.Null(architect.IndeterminateVerifyTail);
    }

    [Fact]
    public void A_fresh_ExecutionRequestAccepted_clears_IndeterminateVerifyTail_alongside_IndeterminateReason()
    {
        // The reopen path (a future producer minting a fresh execution for a still-Indeterminate
        // step) -- CaptureResolved and the Arrested-stays-null path each have their own test; this is
        // the third clearing site (StateProjector.cs, ExecutionRequestAccepted's reopen arm).
        var executionId = new ExecutionId("exec-1");
        var redriveExecutionId = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.VerifyFailed(executionId, ["fmt-check"], "GATES: FAIL 1 of 25 -- fmt-check"),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(redriveExecutionId, Architect)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Null(architect.IndeterminateVerifyTail);
    }

    [Fact]
    public void CaptureResolved_clears_IndeterminateVerifyTail_alongside_IndeterminateReason()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.VerifyFailed(executionId, ["fmt-check"], "GATES: FAIL 1 of 25 -- fmt-check"),
            new FlowEvent.CaptureResolved(Architect, executionId, Accepted: false),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Null(architect.IndeterminateVerifyTail);
    }

    [Fact]
    public void ExecutionIndeterminate_overwriting_the_producer_clears_the_stale_IndeterminateVerifyTail()
    {
        // #1711 review F3: a VerifyFailed tail must not survive the producer being overwritten by
        // ExecutionIndeterminate's CapturedResponse/ContractFailure arms -- same clearing discipline
        // as the other three sites (:127, :378).
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.VerifyFailed(executionId, ["fmt-check"], "GATES: FAIL 1 of 25 -- fmt-check"),
            new FlowEvent.ExecutionIndeterminate(executionId, "captured", ".captured-response.md", ["plan"]),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(IndeterminateProducer.CapturedResponse, architect.IndeterminateProducer);
        Assert.Null(architect.IndeterminateVerifyTail);
    }

    [Fact]
    public void ExecutionArrested_settles_the_step_Failed_and_records_an_IndeterminateReason()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionArrested(executionId, new WorkerUsage(TokensIn: 500_000, TokensOut: 120_000), ["manage_task", "manage_task"]),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Failed, architect.Status);
        Assert.True(architect.IndeterminateAwaitingResolution);
        Assert.NotNull(architect.IndeterminateReason);
        Assert.Contains("620000", architect.IndeterminateReason);
        Assert.True(architect.RetryForeclosed);
    }

    [Fact]
    public void ExecutionArrested_DescribeArrest_pins_the_token_budget_text_off_BilledTokens()
    {
        // Must 6e: BilledTokens is what the arrest text now reports, not ContextLevelTokens + TokensOut.
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionArrested(
                executionId,
                new WorkerUsage(TokensIn: 500_000, TokensOut: 120_000, ContextLevelTokens: 500_000, BilledTokens: 1_234_567),
                Reason: ArrestReason.TokenBudget),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(
            "Execution arrested: token budget exceeded (1234567 billed tokens measured) — awaiting conductor resolution.",
            architect.IndeterminateReason);
    }

    /// <summary>
    /// #1745: a role's token budget can now default per adapter, so the arrest text names WHICH
    /// adapter's figure applied -- without it, "token budget exceeded" no longer says enough to tell a
    /// conductor whether the claude or the agy figure fired.
    /// </summary>
    [Fact]
    public void ExecutionArrested_DescribeArrest_names_the_adapter_the_budget_applied_to()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionArrested(
                executionId,
                new WorkerUsage(TokensIn: 500_000, TokensOut: 120_000, BilledTokens: 1_234_567),
                Reason: ArrestReason.TokenBudget,
                Adapter: "agy"),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(
            "Execution arrested: token budget exceeded (1234567 billed tokens measured) on adapter 'agy' — awaiting conductor resolution.",
            architect.IndeterminateReason);
    }

    /// <summary>A ledger line written before #1745 carries no Adapter and reads exactly as it did before.</summary>
    [Fact]
    public void ExecutionArrested_DescribeArrest_omits_the_adapter_clause_when_none_was_recorded()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionArrested(
                executionId,
                new WorkerUsage(TokensIn: 500_000, TokensOut: 120_000, BilledTokens: 1_234_567),
                Reason: ArrestReason.TokenBudget),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(
            "Execution arrested: token budget exceeded (1234567 billed tokens measured) — awaiting conductor resolution.",
            architect.IndeterminateReason);
    }

    [Fact]
    public void ExecutionArrested_DescribeArrest_pins_the_tool_step_cap_text()
    {
        // Must 6e: the SECOND, independent producer's own pinned text.
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionArrested(
                executionId,
                Usage: null,
                LastToolNames: ["run_command", "run_command"],
                Reason: ArrestReason.ToolStepCap,
                ToolStepCount: 81),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(
            "Execution arrested: tool-step cap exceeded (81 tool steps measured) — awaiting conductor resolution.",
            architect.IndeterminateReason);
    }

    /// <summary>
    /// #2002 rule 3, over a fixture ledger. Both arms in one test because the clause is only worth
    /// anything if it is CONDITIONAL: a build that appended it unconditionally would pass an assertion
    /// that a dominant shape is named, and would then be naming a shape on every long-but-honest lane.
    /// The measured room this is drawn from read 54 % on `Get-Process -Id &lt;n&gt;`.
    /// </summary>
    [Fact]
    public void ExecutionArrested_DescribeArrest_names_the_dominant_command_shape_only_when_one_dominates()
    {
        var dominated = new ExecutionId("exec-1");
        var mixed = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(dominated, Architect)),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(mixed, Critic)),
            new FlowEvent.ExecutionArrested(
                dominated,
                Reason: ArrestReason.ToolStepCap,
                ToolStepCount: 272,
                DominantCommandShape: "Get-Process -Id <n> -ErrorAction SilentlyContinue",
                DominantCommandSharePercent: 54),
            new FlowEvent.ExecutionArrested(
                mixed,
                Reason: ArrestReason.ToolStepCap,
                ToolStepCount: 272),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(
            "Execution arrested: tool-step cap exceeded (272 tool steps measured) — awaiting conductor "
            + "resolution. 54 % of steps were `Get-Process -Id <n> -ErrorAction SilentlyContinue`.",
            StepFor(state, Architect).IndeterminateReason);
        Assert.Equal(
            "Execution arrested: tool-step cap exceeded (272 tool steps measured) — awaiting conductor resolution.",
            StepFor(state, Critic).IndeterminateReason);
    }

    [Fact]
    public void ExecutionArrested_DescribeArrest_pins_the_billed_rate_text_naming_window_observed_and_limit()
    {
        // #1691: the THIRD producer's own pinned text -- window width, observed rate, armed limit, all
        // three named. StateProjector.DescribeBilledRateArrest carries why each one is there.
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionArrested(
                executionId,
                new WorkerUsage(TokensIn: 96_546, TokensOut: 3_679, BilledTokens: 278_565),
                LastToolNames: ["run_command"],
                Reason: ArrestReason.BilledRate,
                ToolStepCount: 26,
                PeakBilledInWindow: 278_565,
                BilledRateLimit: 250_000),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(
            "Execution arrested: billed-token rate limit exceeded (278565 billed tokens in a 5 min window, limit 250000) — awaiting conductor resolution.",
            architect.IndeterminateReason);
    }

    [Fact]
    public void ExecutionArrested_DescribeArrest_degrades_the_billed_rate_text_when_the_figures_are_absent()
    {
        // #1691, the polarity arm: a BilledRate line with neither figure still names the mechanism and
        // the window rather than fabricating a zero-token claim. Only reachable from a writer older
        // than the fields, which cannot exist today -- pinned so it stays a graceful degrade rather
        // than becoming "(0 billed tokens in a 5 min window, limit 0)" under a later edit.
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionArrested(executionId, Usage: null, Reason: ArrestReason.BilledRate),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(
            "Execution arrested: billed-token rate limit exceeded (over a 5 min window) — awaiting conductor resolution.",
            architect.IndeterminateReason);
    }

    /// <summary>
    /// #1691 state-enumeration sweep: <c>DescribeArrest</c>'s switch has a THROWING default arm, so
    /// before this test a member added to <see cref="ArrestReason"/> without an arm failed at runtime,
    /// in production, on the one code path that runs when something has already gone wrong. Driven off
    /// <see cref="Enum.GetValues{T}"/> so a new member fails here instead, the moment it is declared.
    /// </summary>
    [Fact]
    public void ExecutionArrested_DescribeArrest_covers_every_ArrestReason_member()
    {
        foreach (var reason in Enum.GetValues<ArrestReason>())
        {
            var executionId = new ExecutionId("exec-1");
            var events = new FlowEvent[]
            {
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
                new FlowEvent.ExecutionArrested(executionId, Usage: null, Reason: reason),
            };

            var state = StateProjector.Project(events, TwoStepSnapshot());

            var architect = StepFor(state, Architect);
            Assert.False(string.IsNullOrWhiteSpace(architect.IndeterminateReason),
                $"ArrestReason.{reason} produced no arrest description — DescribeArrest is missing an arm for it.");
            Assert.StartsWith("Execution arrested:", architect.IndeterminateReason, StringComparison.Ordinal);
            Assert.True(architect.RetryForeclosed,
                $"ArrestReason.{reason} must foreclose retry like every other arrest — a new trigger silently allowing a blind retry is the failure this sweep exists to catch.");
        }
    }

    [Fact]
    public void ExecutionArrested_DescribeArrest_treats_a_null_reason_as_the_legacy_token_budget_case()
    {
        // A ledger line written before #1682 -- Reason/ToolStepCount/BilledTokens all absent.
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionArrested(executionId, new WorkerUsage(TokensIn: 500_000, TokensOut: 120_000)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        // #1686 review F8: this figure is the OLD level-based (ContextLevelTokens/TokensIn + TokensOut)
        // reading, computed because a pre-#1682 line never set BilledTokens at all -- it must not claim
        // to be "billed tokens", the quantity this PR exists to stop treating as authoritative.
        Assert.Equal(
            "Execution arrested: token budget exceeded (620000 tokens measured) — awaiting conductor resolution.",
            architect.IndeterminateReason);
    }

    [Fact]
    public void VerifyFailed_is_never_retried_even_within_MaxAttempts()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.VerifyFailed(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());
        var architect = StepFor(state, Architect);

        Assert.False(Baton.Scheduling.RetryEngine.MayRetry(architect, new RetryPolicy(MaxAttempts: 5)));
    }

    [Theory]
    [InlineData(VerifyFailedKind.EngineRestart, "Verify did not complete across an engine restart — awaiting conductor resolution.")]
    [InlineData(VerifyFailedKind.TimedOut, "Verify timed out — awaiting conductor resolution.")]
    [InlineData(VerifyFailedKind.Cancelled, "Verify cancelled — awaiting conductor resolution.")]
    public void VerifyFailed_with_non_gate_kind_records_corresponding_IndeterminateReason(
        VerifyFailedKind kind, string expectedReason)
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.VerifyFailed(executionId, null, "tail", kind),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());
        var architect = StepFor(state, Architect);

        Assert.Equal(StepStatus.Failed, architect.Status);
        Assert.Equal(expectedReason, architect.IndeterminateReason);
    }

    [Fact]
    public void VerifyNotRun_with_BuildLockBusy_settles_Indeterminate_with_the_BuildLockBusy_producer()
    {
        // #1796: unlike the pre-flight (BuildLockBusy: false) shape, this one DID start a real
        // verify run -- VerifyStarted fires first -- and must settle Indeterminate rather than
        // staying diagnostic-only.
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.VerifyStarted(executionId),
            new FlowEvent.VerifyNotRun(executionId, "build lock busy for 1800s (holder: PID 1234 (dotnet build) since 2026-09-04 01:00:00)", BuildLockBusy: true),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());
        var architect = StepFor(state, Architect);

        Assert.Equal(StepStatus.Failed, architect.Status);
        Assert.True(architect.IndeterminateAwaitingResolution);
        Assert.Equal(IndeterminateProducer.BuildLockBusy, architect.IndeterminateProducer);
        Assert.Equal(
            "build lock busy for 1800s (holder: PID 1234 (dotnet build) since 2026-09-04 01:00:00) — awaiting conductor resolution.",
            architect.IndeterminateReason);
        Assert.False(Baton.Scheduling.RetryEngine.MayRetry(architect, new RetryPolicy(MaxAttempts: 5)));
    }

    [Fact]
    public void VerifyNotRun_with_BuildLockBusy_clears_UnmatchedVerifyExecutionIds()
    {
        // Mirrors VerifyFailed's own clear -- VerifyStarted DID fire for this shape, unlike the
        // pre-flight VerifyNotRun, so the crash-recovery bookkeeping must not treat it as orphaned.
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.VerifyStarted(executionId),
            new FlowEvent.VerifyNotRun(executionId, "build lock busy", BuildLockBusy: true),
        };

        var (_, checkpoint) = StateProjector.ProjectAndCheckpoint(events, TwoStepSnapshot(), null, 0);

        Assert.DoesNotContain(executionId, checkpoint.State.UnmatchedVerifyExecutionIds);
    }

    [Fact]
    public void VerifyNotRun_without_BuildLockBusy_stays_diagnostic_only_not_Indeterminate()
    {
        // Polarity partner: the pre-#1796 pre-flight shape (default BuildLockBusy: false) must be
        // unaffected by the new branch -- still no Indeterminate consequence.
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.VerifyNotRun(executionId, "no pixi project: gates-quiet"),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());
        var architect = StepFor(state, Architect);

        Assert.Equal(StepStatus.Succeeded, architect.Status);
        Assert.False(architect.IndeterminateAwaitingResolution);
        Assert.Null(architect.IndeterminateReason);
        Assert.Equal("no pixi project: gates-quiet", architect.VerifyNotRunReason);
    }

    [Fact]
    public void VerifyPassed_and_VerifyStarted_are_diagnostic_only()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.VerifyStarted(executionId),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.VerifyPassed(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());
        var architect = StepFor(state, Architect);

        Assert.Equal(StepStatus.Succeeded, architect.Status);
        Assert.Null(architect.IndeterminateReason);
    }

    // Polarity partner for both producers above: identical log minus the one event stays an ordinary
    // Succeeded/Terminal room, not Indeterminate.

    [Fact]
    public void The_same_execution_without_VerifyFailed_settles_Succeeded_not_Indeterminate()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());
        var architect = StepFor(state, Architect);

        Assert.Null(architect.IndeterminateReason);
        Assert.Equal(StepStatus.Succeeded, architect.Status);
    }

    [Fact]
    public void StepRebound_is_projected_without_perturbing_step_state()
    {
        // #1583 / S6 (spec/baton.md §3, #802 section 3.3): StepRebound is a diagnostic ledger event consumed by ExecutionUsageProjector;
        // it does not alter step lifecycle status or retry counters.
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.StepRebound(Architect, executionId, "agy", "gemini-3-pro", "claude", "sonnet", "Failover"),
            new FlowEvent.ExecutionSucceeded(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());
        var architect = StepFor(state, Architect);

        Assert.Equal(StepStatus.Succeeded, architect.Status);
        Assert.Equal(executionId, architect.LatestExecutionId);
        Assert.Equal(0, architect.ConsecutiveFailureCount);
    }

    [Fact]
    public void An_Indeterminate_step_survives_an_incremental_checkpoint_resume()
    {
        // The same #1606 DeepCopy landmine StepRetryForeclosed_survives_an_incremental_checkpoint_resume
        // pins for RetryForeclosedStepIds -- IndeterminateReasonByStepId is a second trailing dictionary
        // relying on its own `?? new()` init default, and DeepCopy constructs positionally.
        var executionId = new ExecutionId("exec-1");
        var events = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.VerifyFailed(executionId, ["lint"], "tail"),
        };

        var (freshState, checkpoint) = StateProjector.ProjectAndCheckpoint(events, TwoStepSnapshot());
        Assert.NotNull(StepFor(freshState, Architect).IndeterminateReason);
        // #1701: IndeterminateVerifyTailByStepId is a third trailing dictionary added the same way --
        // same DeepCopy landmine, so it gets the same fresh/resumed round-trip proof.
        Assert.Equal("tail", StepFor(freshState, Architect).IndeterminateVerifyTail);

        var resumedState = StateProjector.Project(events, TwoStepSnapshot(), checkpoint);
        Assert.NotNull(StepFor(resumedState, Architect).IndeterminateReason);
        Assert.Equal("tail", StepFor(resumedState, Architect).IndeterminateVerifyTail);
    }

    [Fact]
    public void A_FinishedDuringTeardown_step_survives_an_incremental_checkpoint_resume()
    {
        // #1945 review MEDIUM 5: FinishedDuringTeardownStepIds is a fourth trailing member added the
        // same way as RetryForeclosedStepIds / IndeterminateReasonByStepId / IndeterminateVerifyTail
        // above -- relying on its own `?? new()` init default, with DeepCopy constructing
        // positionally. Dropped there, the room silently downgrades to plain Succeeded on the next
        // incremental resume: a wrong answer that looks exactly like a right one, which is why this
        // member gets the same fresh-vs-resumed proof every prior one did.
        var executionId = new ExecutionId("exec-1");
        var events = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId, FinishedDuringTeardown: true),
        };

        var (freshState, checkpoint) = StateProjector.ProjectAndCheckpoint(events, TwoStepSnapshot());
        Assert.True(StepFor(freshState, Architect).FinishedDuringTeardown);

        var resumedState = StateProjector.Project(events, TwoStepSnapshot(), checkpoint);
        Assert.True(StepFor(resumedState, Architect).FinishedDuringTeardown);
    }

    [Fact]
    public void A_fresh_dispatch_clears_a_prior_attempts_FinishedDuringTeardown()
    {
        // The accept-side clear (StateProjector's #1945 line among the other fresh-dispatch clears):
        // asserted while the new attempt is still in flight, so nothing but that clear can be what
        // moved the flag -- the new execution has not settled either way yet.
        var events = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("exec-1"), Architect)),
            new FlowEvent.ExecutionSucceeded(new ExecutionId("exec-1"), FinishedDuringTeardown: true),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("exec-2"), Architect)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.False(StepFor(state, Architect).FinishedDuringTeardown);
    }

    [Fact]
    public void A_retry_that_succeeds_normally_leaves_no_FinishedDuringTeardown_behind()
    {
        // Set-or-clear, never set-only: the latest execution speaks. Without this the room would keep
        // reporting the old word after an attempt that finished ordinarily.
        var events = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("exec-1"), Architect)),
            new FlowEvent.ExecutionSucceeded(new ExecutionId("exec-1"), FinishedDuringTeardown: true),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("exec-2"), Architect)),
            new FlowEvent.ExecutionSucceeded(new ExecutionId("exec-2")),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Succeeded, StepFor(state, Architect).Status);
        Assert.False(StepFor(state, Architect).FinishedDuringTeardown);
    }

    [Fact]
    public void A_retry_that_itself_finishes_during_teardown_still_carries_the_word()
    {
        // The discriminating control for the two arms above: the identical event shape with the
        // SECOND attempt flagged. Without it, a projector that simply never set the flag on a retried
        // step would pass both of them.
        var events = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("exec-1"), Architect)),
            new FlowEvent.ExecutionSucceeded(new ExecutionId("exec-1")),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("exec-2"), Architect)),
            new FlowEvent.ExecutionSucceeded(new ExecutionId("exec-2"), FinishedDuringTeardown: true),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.True(StepFor(state, Architect).FinishedDuringTeardown);
    }

    [Fact]
    public void An_Indeterminate_step_reaches_workflow_Terminal()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.VerifyFailed(executionId),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("exec-2"), Critic)),
            new FlowEvent.ExecutionSucceeded(new ExecutionId("exec-2")),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Terminal, state.Status);
    }

    [Fact]
    public void StepRebound_overrides_the_accepted_requests_Adapter_and_Model_and_survives_a_full_replay()
    {
        // #1583 HIGH: StepRebound must be projected as an override on AcceptedRequestByExecutionId,
        // not merely journaled — a crash before the checkpoint save (the path this event exists for)
        // recovers the rebind only if a full replay from scratch reproduces it.
        var executionId = new ExecutionId("exec-1");
        var acceptedRequest = MakeRequest(executionId, Architect) with { Adapter = "claude", Model = "sonnet" };
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(acceptedRequest),
            new FlowEvent.StepRebound(Architect, executionId, PreviousAdapter: "claude", PreviousModel: "sonnet", NewAdapter: "agy", NewModel: "gemini-3-pro"),
        };

        var (_, checkpoint) = StateProjector.ProjectAndCheckpoint(events, TwoStepSnapshot());

        var reboundRequest = Assert.Single(checkpoint.State.AcceptedRequestByExecutionId.Values);
        Assert.Equal("agy", reboundRequest.Adapter);
        Assert.Equal("gemini-3-pro", reboundRequest.Model);
    }
}
