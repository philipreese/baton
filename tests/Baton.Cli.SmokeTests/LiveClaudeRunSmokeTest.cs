using Baton.Vendors;
using Baton.Cli.SmokeTests.TestSupport;
using Baton.Domain;

namespace Baton.Cli.SmokeTests;

/// <summary>
/// M11 Phase 4 (#87), the milestone's completion gate: a real draft → review workflow run through
/// <see cref="RunCommand.ExecuteAsync"/> — the exact call <c>Program.cs</c> makes — against the
/// real headless <c>claude</c> CLI via <see cref="WorkerAdapterRegistry.Default"/>'s
/// <see cref="ClaudeWorkerAdapter"/>, producing real artifacts on disk. Every prior end-to-end test
/// (<c>WorkflowEndToEndTests</c>, <c>RunCommandEndToEndTests</c>) dispatches through the real,
/// managed <c>BatonTask</c> engine but to a stub or shell-stub worker; this is the first time this
/// repo's own code ever reaches a live LLM.
/// <para>
/// <b>Deliberately excluded from <c>Baton.slnx</c>.</b> This project is not part of the solution
/// <c>pixi run test</c>/<c>lint</c>/<c>fmt-check</c> or the CI workflow build against, so it never
/// runs, builds, or restores as a side effect of any of those — matching Phase 4's plan ("cannot
/// live in default CI"). It requires an authenticated <c>claude</c> CLI on PATH (a logged-in
/// session or API key — the adapter itself owns no key-handling code to gate on) and outbound
/// network access. Invoke only via <c>pixi run smoke-claude</c>; see
/// <c>docs/runbooks/live-claude-smoke.md</c> for the full runbook.
/// </para>
/// </summary>
public class LiveClaudeRunSmokeTest
{
    [Fact]
    public async Task A_two_step_draft_review_workflow_runs_to_completion_against_the_real_claude_cli()
    {
        var fixturesDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var workflowFilePath = Path.Combine(fixturesDirectory, "draft-review-workflow.json");
        var bindingsFilePath = Path.Combine(fixturesDirectory, "draft-review-bindings.json");

        var testRoot = Path.Combine(Path.GetTempPath(), $"claude-smoke-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var finalState = (await RunCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, cancellationToken: TestContext.Current.CancellationToken)).State;

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(2, finalState.Steps.Count);
            Assert.All(finalState.Steps, FlowAssert.Succeeded);

            var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
            var stepStateById = finalState.Steps.ToDictionary(s => s.StepId);

            await AssertRealOutputAsync(artifactsRoot, stepStateById[new StepId("draft")], "draft");
            await AssertRealOutputAsync(artifactsRoot, stepStateById[new StepId("review")], "review");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// Only checks the output exists and is non-blank — unlike the shell-stub tests, a live
    /// worker's exact text is never asserted verbatim (the contract is "the file exists",
    /// not "the file says X"). The parsing boundary is in
    /// <see href="../../../docs/agents/developing-baton.md"/>.
    /// </summary>
    private static async Task AssertRealOutputAsync(string artifactsRoot, StepState stepState, string outputName)
    {
        var outputPath = Path.Combine(artifactsRoot, $"execution_{stepState.LatestExecutionId}", outputName);
        Assert.True(File.Exists(outputPath), $"Expected worker output at '{outputPath}'.");
        Assert.False(string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(outputPath)));
    }
}
