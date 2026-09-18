using Baton.Dispatch;
using Baton.Domain;
using Baton.Outcomes;
using Baton.Scheduling;

namespace Baton.Tests.Outcomes;

public sealed class OutstandingToolRecoveryTests
{
    [Fact]
    public void Terminal_success_with_an_outstanding_tool_is_retryable_once_and_structured()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"recovery-outcome-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        try
        {
            var result = new CoreDispatchResult(
                0,
                CoreExitReason.Natural,
                TerminalSuccessObserved: true,
                OutstandingToolAtTerminalSuccess: new OutstandingToolAtTerminalSuccess(
                    "run_command", "dotnet build -warnaserror"));
            var contract = new WorkerContract("implement", [], [], []);

            var first = OutcomeClassifier.Classify(result, contract, outputDirectory);
            Assert.Equal(OutcomeVerdict.Failed, first.Verdict);
            Assert.Equal(FailureClassification.Retryable, first.FailureClassification);
            Assert.Equal(
                "The vendor reported terminal success while the 'run_command' tool step was still active; "
                + "no completion was observed for command 'dotnet build -warnaserror'.",
                first.Reason);
            Assert.Equal(
                new RecoveryCause(
                    RecoveryCauseKind.OutstandingToolAtTerminalSuccess,
                    "run_command",
                    "dotnet build -warnaserror"),
                first.RecoveryCause);

            var repeated = OutcomeClassifier.Classify(
                result,
                contract,
                outputDirectory,
                priorRecoveryCause: first.RecoveryCause,
                priorRecoveryOccurrence: 1);
            Assert.Equal(FailureClassification.Permanent, repeated.FailureClassification);
            Assert.Equal(
                "The vendor again reported terminal success while the 'run_command' tool step was still active "
                + "after the one automatic recovery; conductor rerouting is required.",
                repeated.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public void The_first_recovery_continuation_brief_is_pinned()
    {
        var cause = new RecoveryCause(
            RecoveryCauseKind.OutstandingToolAtTerminalSuccess,
            "run_command",
            "dotnet build -warnaserror");
        var step = new StepState(
            new StepId("implement"),
            StepStatus.Failed,
            new ExecutionId("exec-1"),
            new Dictionary<StepId, ExecutionId>(),
            ConsecutiveFailureCount: 1,
            LatestFailureClassification: FailureClassification.Retryable,
            LatestRecoveryCause: cause,
            RecoveryOccurrence: 1);

        var brief = ContinuationBrief.ForRetry(step, 3, TimeSpan.FromMinutes(1));

        Assert.Equal(
            ("[baton] CONTINUATION BRIEF -- read this before the brief below.\n\n"
            + "This is the same workspace. Inspect and preserve the existing work; do not remap or restart the task. "
            + "The named tool `run_command` was not observed completing. The command was `dotnet build -warnaserror`.\n\n"
            + "Run required commands synchronously and wait for them to finish. Finish the original acceptance and provide the remaining success evidence.\n\n"
            + "The original brief follows, unchanged.\n\n"
            + "----------------------------------------------------------------------\n").Replace("\n", Environment.NewLine),
            brief);
    }
}
