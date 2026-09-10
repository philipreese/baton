using Baton.Mutation;
using Baton.Tests.Projection;
using Xunit;

namespace Baton.Tests.Mutation;

/// <summary>
/// #1971 review (MEDIUM): the raw-output write is a DIAGNOSTIC write on the settle path. Its caller,
/// <c>MutationInterface</c>, has already appended <c>FlowEvent.VerifyStarted</c> when it awaits
/// <see cref="VerifyRunner.RunProcessAsync"/>, and the enclosing <c>try</c> there catches only
/// <c>PromptPreambleException</c>, <c>CommandLineTooLongException</c> and <c>BatonException</c> — so
/// anything else escaping this write leaves the room stuck at <c>VerifyStarted</c> with no terminal
/// event, the exact failure those catch arms exist to prevent. This class pins the degrade: the
/// outcome still settles, with its tail, and the loss is reported on stderr rather than swallowed.
/// <para>
/// Its own class because it swaps the process-global <see cref="Console.Error"/> to read that warning,
/// which <c>Baton.Architecture.Tests.ConsoleSwapTests</c> requires be enrolled in a
/// <c>DisableParallelization</c> collection — enrolling <c>VerifyRunnerTests</c> itself would
/// serialize a dozen process-spawning tests for one arm's benefit.
/// </para>
/// <para>
/// Scope of what this measures, stated rather than implied: the arm below reaches the catch through
/// <see cref="IOException"/>, which the pre-review filter already caught. The widening to
/// <c>ArgumentException</c>/<c>NotSupportedException</c> and the inner guard around the warning write
/// are reasoned from <c>OutputMaterializer.TryCaptureFinalResponse</c>'s identical posture, not
/// measured here — an unwritable process-global stderr is not reproducible from inside a test that
/// reads that same stream.
/// </para>
/// </summary>
[Collection(ConsoleErrorCaptureCollection.Name)]
public sealed class VerifyRawOutputWriteFailureTests
{
    [Fact]
    public async Task An_unwritable_raw_output_directory_still_settles_with_a_tail_and_reports_the_loss()
    {
        var fixtureDirectory = Path.Combine(Path.GetTempPath(), $"baton-1971-{Guid.NewGuid():N}");
        var originalError = Console.Error;
        var captured = new StringWriter();
        try
        {
            Directory.CreateDirectory(fixtureDirectory);
            // The raw-output path is occupied by a FILE, so Directory.CreateDirectory cannot make a
            // directory there and the write fails before it starts.
            var blockedPath = Path.Combine(fixtureDirectory, "artifacts");
            await File.WriteAllTextAsync(blockedPath, "not a directory", TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(
                Path.Combine(fixtureDirectory, "stream.txt"),
                string.Join("\n",
                    "   Assert.Equal() Failure: the projection was not rebuilt",
                    "  FAIL  test-no-build  (exit 1)",
                    "GATES: FAIL 1 of 25 -- test-no-build",
                    string.Empty),
                TestContext.Current.CancellationToken);

            Console.SetError(captured);
            var outcome = await VerifyRunner.RunProcessAsync(
                "cmd", ["/c", "type stream.txt & exit 1"], fixtureDirectory, TestContext.Current.CancellationToken,
                rawOutputDirectory: blockedPath);

            // Settles exactly as it would with no directory at all: the verdict, the members and the
            // filtered tail are all still there. Nothing about the diagnostic write reaches the caller.
            Assert.False(outcome.Passed);
            Assert.Equal(Baton.Domain.VerifyFailedKind.GatesFailed, outcome.Kind);
            Assert.Equal(["test-no-build"], outcome.FailingMembers);
            Assert.NotNull(outcome.Tail);
            Assert.Contains("Assert.Equal() Failure: the projection was not rebuilt", outcome.Tail!, StringComparison.Ordinal);
            // No file was written, so no pointer line is fabricated for a file that is not there.
            Assert.DoesNotContain(VerifyRunner.RawOutputFileName, outcome.Tail, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(blockedPath, VerifyRunner.RawOutputFileName)));

            // Logged, never silently swallowed ([development guide](../../../../docs/agents/developing-baton.md)).
            Assert.Contains(VerifyRunner.RawOutputFileName, captured.ToString(), StringComparison.Ordinal);
            Assert.Contains("The filtered tail is still recorded.", captured.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            Baton.Tests.Shared.DirectoryCleanup.DeleteRecursively(fixtureDirectory);
        }
    }
}
