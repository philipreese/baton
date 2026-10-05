using System.Text.Json;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Outcomes;
using Baton.Tests.TestSupport;

namespace Baton.Tests.Outcomes;

public sealed class ExactRunningCompletionTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, false)]
    [InlineData(-1, 1, true)]
    [InlineData(-1, 1, false)]
    public void First_turn_outputs_cannot_settle_a_selected_execution_without_final_completion(
        int exitCode, int reason, bool terminalSuccess)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"exact-final-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "report.md"), "first turn completed everything");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("report.md", Schema: OutputSchema.NonEmptyText)], []);
            var result = JsonSerializer.Deserialize<CoreDispatchResult>(
                $$"""{"ExitCode":{{exitCode}},"Reason":{{reason}},"TerminalSuccessObserved":{{terminalSuccess.ToString().ToLowerInvariant()}},"TerminalResultObserved":true,"ExactRunningTransport":"agy-stream-v1"}""")!;
            var classified = OutcomeClassifier.Classify(result, contract, directory);
            Assert.Equal(OutcomeVerdict.Failed, classified.Verdict);
            Assert.Contains("final expected turn", classified.Reason);
        }
        finally { DirectoryCleanup.DeleteRecursively(directory); }
    }

    [Fact]
    public void Ordinary_one_shot_still_settles_valid_outputs()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"one-shot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "report.md"), "complete");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("report.md", Schema: OutputSchema.NonEmptyText)], []);
            Assert.Equal(OutcomeVerdict.Succeeded,
                OutcomeClassifier.Classify(new(0, CoreExitReason.Natural), contract, directory).Verdict);
        }
        finally { DirectoryCleanup.DeleteRecursively(directory); }
    }
}
