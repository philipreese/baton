using System.Diagnostics;
using Baton.Cli;
using Baton.Domain;
using Baton.Outcomes;
using static Baton.Cli.Tests.TestSupport.ProcessIdentityFixture;

namespace Baton.Cli.Tests;

public class EngineLivenessProbeTests
{
    private static readonly ExecutionId ExecutionId = new("exec-1");
    private static readonly WorkflowId WorkflowId = new("wf-1");
    private static readonly StepId StepId = new("step-1");

    [Fact]
    public void Probe_discrimination_with_real_live_process()
    {
        var livePid = Environment.ProcessId;
        var liveStartTime = new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUniversalTime();

        var probeResult = EngineLivenessProbe.Probe(livePid, liveStartTime);

        Assert.Equal(EngineLivenessStatus.Alive, probeResult.Status);
        Assert.Null(probeResult.Why);
    }

    [Fact]
    public void Probe_discrimination_with_real_dead_process()
    {
        var (deadPid, deadStartTime) = DeadProcessIdentity();

        var probeResult = EngineLivenessProbe.Probe(deadPid, deadStartTime);

        Assert.Equal(EngineLivenessStatus.Dead, probeResult.Status);
    }

    [Fact]
    public void Probe_failure_arm_returns_unknown_when_identity_is_missing_or_invalid()
    {
        var missingResult = EngineLivenessProbe.Probe(null, null);
        Assert.Equal(EngineLivenessStatus.Unknown, missingResult.Status);
        Assert.Contains("no process identity recorded", missingResult.Why);

        var invalidResult = EngineLivenessProbe.Probe(-1, DateTimeOffset.UtcNow);
        Assert.Equal(EngineLivenessStatus.Unknown, invalidResult.Status);
        Assert.Contains("invalid process identity", invalidResult.Why);
    }

    [Fact]
    public void FormatStepStatus_renders_both_polarities_and_probe_failure()
    {
        var livePid = Environment.ProcessId;
        var liveStartTime = new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUniversalTime();

        var (deadPid, deadStartTime) = DeadProcessIdentity();

        var liveAccepted = new FlowEvent.ExecutionRequestAccepted(
            MakeRequest("exec-live"), EnginePid: livePid, EngineStartTime: liveStartTime);

        var deadAccepted = new FlowEvent.ExecutionRequestAccepted(
            MakeRequest("exec-dead"), EnginePid: deadPid, EngineStartTime: deadStartTime);

        var legacyAccepted = new FlowEvent.ExecutionRequestAccepted(
            MakeRequest("exec-legacy"), EnginePid: null, EngineStartTime: null);

        var emptyUpstreams = new Dictionary<StepId, ExecutionId>();
        var runningStepLive = new StepState(StepId, StepStatus.Running, LatestExecutionId: new ExecutionId("exec-live"), emptyUpstreams);
        var runningStepDead = new StepState(StepId, StepStatus.Running, LatestExecutionId: new ExecutionId("exec-dead"), emptyUpstreams);
        var runningStepLegacy = new StepState(StepId, StepStatus.Running, LatestExecutionId: new ExecutionId("exec-legacy"), emptyUpstreams);
        var terminalStepDead = new StepState(StepId, StepStatus.Succeeded, LatestExecutionId: new ExecutionId("exec-dead"), emptyUpstreams);

        // Alive arm: renders Running (no annotation)
        var liveOutput = StatusCommand.FormatStepStatus(runningStepLive, [liveAccepted]);
        Assert.Equal("Running", liveOutput);

        // Dead arm: renders annotation
        var deadOutput = StatusCommand.FormatStepStatus(runningStepDead, [deadAccepted]);
        Assert.Equal("Running — engine not alive; crash recovery will classify on next pump", deadOutput);

        // Terminal step: does NOT render annotation even if probe is dead
        var terminalOutput = StatusCommand.FormatStepStatus(terminalStepDead, [deadAccepted]);
        Assert.Equal("Succeeded", terminalOutput);

        // Probe failure arm: renders liveness unknown (<why>)
        var failureOutput = StatusCommand.FormatStepStatus(runningStepLegacy, [legacyAccepted]);
        Assert.Equal("liveness unknown (no process identity recorded)", failureOutput);

        // Paused step: NO annotation even with a dead engine on record -- why is documented at
        // the positive Running gate in StatusCommand.FormatStepStatus.
        var pausedStepDead = new StepState(StepId, StepStatus.Paused, LatestExecutionId: new ExecutionId("exec-dead"), emptyUpstreams);
        var pausedOutput = StatusCommand.FormatStepStatus(pausedStepDead, [deadAccepted]);
        Assert.Equal("Paused", pausedOutput);

        // Pending step: no execution yet, no liveness claim -- never "liveness unknown".
        var pendingStep = new StepState(StepId, StepStatus.Pending, LatestExecutionId: null, emptyUpstreams);
        Assert.Equal("Pending", StatusCommand.FormatStepStatus(pendingStep, []));
    }

    /// <summary>
    /// #1622 (b)/#1390: a hollow success renders visibly different from an ordinary one -- the room
    /// word stays "Succeeded" (never reclassified; spec/baton.md §3), but the operator reading
    /// `baton status` must not see it as indistinguishable from a real one.
    /// </summary>
    [Fact]
    public void FormatStepStatus_renders_hollow_reason_for_a_hollow_Succeeded_step()
    {
        var emptyUpstreams = new Dictionary<StepId, ExecutionId>();
        var hollowStep = new StepState(
            StepId, StepStatus.Succeeded, LatestExecutionId: new ExecutionId("exec-hollow"), emptyUpstreams,
            WorkspaceChanged: false, Hollow: true, HollowReason: "no diff, no outputs");

        var output = StatusCommand.FormatStepStatus(hollowStep, []);

        Assert.Equal("Succeeded — hollow: no diff, no outputs", output);
    }

    /// <summary>
    /// The polarity control: a genuinely non-hollow Succeeded step (WorkspaceChanged true, or Hollow
    /// simply absent for a non-tree-changing role) renders exactly the plain "Succeeded" it always
    /// has -- <see cref="FormatStepStatus_renders_both_polarities_and_probe_failure"/> already pins
    /// the field-absent case; this pins the WorkspaceChanged: true case specifically.
    /// </summary>
    [Fact]
    public void FormatStepStatus_renders_plain_Succeeded_when_the_workspace_actually_changed()
    {
        var emptyUpstreams = new Dictionary<StepId, ExecutionId>();
        var changedStep = new StepState(
            StepId, StepStatus.Succeeded, LatestExecutionId: new ExecutionId("exec-changed"), emptyUpstreams,
            WorkspaceChanged: true, Hollow: false);

        var output = StatusCommand.FormatStepStatus(changedStep, []);

        Assert.Equal("Succeeded", output);
    }

    [Fact]
    public void FormatStepStatus_does_not_render_an_unfireable_park_for_a_foreclosed_step()
    {
        // #1586 S1 (second-reader finding): a FlowEvent.StepRetryForeclosed clears RetryNotBefore
        // the same way an unobligated ExhaustedUntil park does (MutationInterface.GetRetryObligations
        // leaves no obligation for either), but the two mean opposite things -- one is still waiting
        // on an unknown vendor reset, the other is settled and will never dispatch again. Without the
        // RetryForeclosed guard, this would render "parked (vendor quota) — reset unknown", the exact
        // misreport #1513/#1582 were paid to fix, for a room that is in fact done.
        var emptyUpstreams = new Dictionary<StepId, ExecutionId>();
        var foreclosedStep = new StepState(
            StepId, StepStatus.Failed, LatestExecutionId: ExecutionId, emptyUpstreams,
            LatestFailureClassification: FailureClassification.ExhaustedUntil,
            RetryNotBefore: null,
            RetryForeclosed: true);

        Assert.Equal("Failed", StatusCommand.FormatStepStatus(foreclosedStep, []));
    }

    [Fact]
    public void FormatStepStatus_names_the_journal_foreclosure_instead_of_the_park_it_replaced()
    {
        // #2072 (second-reader finding): StateProjector stores no reason for a StepRetryForeclosed,
        // so the step still carries the park's "quota" -- rendered as-is, a dead-pump arrest reads as
        // a vendor failure. The polarity partner is the test above: same step, no journal event,
        // plain "Failed".
        var emptyUpstreams = new Dictionary<StepId, ExecutionId>();
        var foreclosedStep = new StepState(
            StepId, StepStatus.Failed, LatestExecutionId: ExecutionId, emptyUpstreams,
            LatestFailureClassification: FailureClassification.ExhaustedUntil,
            LatestFailureReason: "quota",
            RetryNotBefore: null,
            RetryForeclosed: true);
        var foreclosure = new FlowEvent.StepRetryForeclosed(
            StepId, ExecutionId, "Arrested: pump dead — worker pid 4242", Baton.Cli.Daemon.DeadPumpProbe.DiagnosticName);

        var rendered = StatusCommand.FormatStepStatus(foreclosedStep, [foreclosure]);

        Assert.Equal("Failed — retry foreclosed by dead-pump probe: Arrested: pump dead — worker pid 4242", rendered);
        Assert.DoesNotContain("quota", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatStepStatus_still_renders_the_unfireable_park_when_not_foreclosed()
    {
        // Polarity partner: identical fixture, RetryForeclosed false -- proves the guard above is
        // about foreclosure specifically, not incidentally about the ExhaustedUntil/null-RetryNotBefore
        // shape itself, which StatusCommandEndToEndTests's own
        // Status_of_an_unknown_instant_exhausted_step_renders_parked_vendor_quota_reset_unknown pins
        // end to end through the CLI.
        var emptyUpstreams = new Dictionary<StepId, ExecutionId>();
        var unforeclosedStep = new StepState(
            StepId, StepStatus.Failed, LatestExecutionId: ExecutionId, emptyUpstreams,
            LatestFailureClassification: FailureClassification.ExhaustedUntil,
            RetryNotBefore: null,
            RetryForeclosed: false);

        // #802: the park still renders, and since #802 it also names the decision the operator owes
        // (no FallbackOnExhaustion declared → the redispatch verb). The prefix is what this polarity
        // test pins; the suffix's exact wording belongs to #802's own status tests.
        var rendered = StatusCommand.FormatStepStatus(unforeclosedStep, []);
        Assert.StartsWith("parked (vendor quota) — reset unknown", rendered, StringComparison.Ordinal);
        Assert.Contains("no fallback declared", rendered, StringComparison.Ordinal);
    }

    private static ExecutionRequest MakeRequest(string execId) =>
        new(new ExecutionId(execId), WorkflowId, StepId, "worker", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>());
}
