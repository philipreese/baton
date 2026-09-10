using Baton.Vendors;
using Baton.Cli.SmokeTests.TestSupport;
using Baton.Domain;

namespace Baton.Cli.SmokeTests;

/// <summary>
/// M12 Phase 4 (#98), the milestone's completion gate: a real <c>draft</c> (Claude) →
/// <c>review</c> (Gemini/<c>agy</c>) workflow — a composition case — run through
/// <see cref="RunCommand.ExecuteAsync"/>, pausing at <c>review</c>'s declared <see cref="PausePoint"/>,
/// then resumed to terminal success through <see cref="DecideCommand.ExecuteAsync"/> — the exact
/// calls <c>Program.cs</c> makes for <c>baton run</c>/<c>baton decide</c> — against the real headless
/// <c>claude</c> and <c>agy</c> CLIs via <see cref="WorkerAdapterRegistry.Default"/>, producing real
/// artifacts from both vendors on disk.
/// <para>
/// <b>Deliberately excluded from <c>Baton.slnx</c></b>, exactly like <see cref="LiveClaudeRunSmokeTest"/>:
/// this project never builds, restores, or runs as a side effect of <c>pixi run build</c>/<c>test</c>/
/// <c>lint</c>/<c>fmt-check</c>. It requires an authenticated <c>claude</c> CLI and an authenticated
/// <c>agy</c> CLI both on <c>PATH</c>, and outbound network access to both vendors' APIs. Invoke only
/// via <c>pixi run smoke-mixed-vendor</c>; see <c>docs/runbooks/live-mixed-vendor-smoke.md</c> for the
/// full runbook.
/// </para>
/// </summary>
public class LiveMixedVendorPausedRunSmokeTest
{
    [Fact]
    public async Task A_draft_review_workflow_pauses_on_review_and_baton_decide_Resume_settles_it_terminal_with_both_vendors_real_output()
    {
        var fixturesDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var workflowFilePath = Path.Combine(fixturesDirectory, "draft-review-paused-workflow.json");
        var bindingsFilePath = Path.Combine(fixturesDirectory, "draft-review-paused-bindings.json");

        var testRoot = Path.Combine(Path.GetTempPath(), $"mixed-vendor-smoke-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var pausedResult = await RunCommand.ExecuteAsync(runOptions, WorkerAdapterRegistry.Default, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Paused, pausedResult.State.Status);
            var draftState = pausedResult.State.Steps.Single(s => s.StepId.Value == "draft");
            var reviewState = pausedResult.State.Steps.Single(s => s.StepId.Value == "review");
            FlowAssert.Succeeded(draftState);
            Assert.Equal(StepStatus.Paused, reviewState.Status);
            Assert.Equal(StepStatus.Succeeded, reviewState.PausedOutcome);

            var pausedExecutionId = reviewState.LatestExecutionId!.Value;
            var decideOptions = new DecideOptions(
                roomDirectory, pausedExecutionId.Value, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);

            var finalResult = await DecideCommand.ExecuteAsync(decideOptions, WorkerAdapterRegistry.Default, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalResult.State.Status);
            Assert.All(finalResult.State.Steps, FlowAssert.Succeeded);

            var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
            var stepStateById = finalResult.State.Steps.ToDictionary(s => s.StepId);

            await AssertRealOutputAsync(artifactsRoot, stepStateById[new StepId("draft")], "draft");
            await AssertRealOutputAsync(artifactsRoot, stepStateById[new StepId("review")], "review");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// Only checks the output exists and is non-blank — a live worker's exact text is never
    /// asserted verbatim (the contract is "the file exists", not "the file says X"; the
    /// engine does not parse that text for routing). See
    /// <see href="../../../docs/agents/developing-baton.md"/>.
    /// </summary>
    private static async Task AssertRealOutputAsync(string artifactsRoot, StepState stepState, string outputName)
    {
        var outputPath = Path.Combine(artifactsRoot, $"execution_{stepState.LatestExecutionId}", outputName);
        Assert.True(File.Exists(outputPath), $"Expected worker output at '{outputPath}'.");
        Assert.False(string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(outputPath)));
    }
}
