using Baton.Tests.Projection;
using Baton.Tests.TestSupport;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Outcomes;
using Baton.Status;
using Baton.Workspaces;

namespace Baton.Tests.Outcomes;

/// <summary>
/// #1607 flake fix (found while gating, unrelated to #1607's own change): several tests here drive
/// <see cref="OutcomeClassifier.Classify"/> with a <see cref="FakeResponseParser"/> that returns a
/// non-null response, which reaches <c>OutputMaterializer.TryCaptureFinalResponse</c>'s success arm and
/// writes a real, unguarded <c>Console.Error.WriteLine("CAPTURED (#1594): ...")</c> — the same
/// process-global stream <see cref="ConsoleErrorCaptureCollection"/> exists to serialize access to.
/// Before this fix, this class ran in xUnit's normal parallel pool and could interleave with any test
/// in that collection (observed: <c>RoomTurnThrottleTests.A_partial_throttle_file_overrides_only_the_field_it_names</c>,
/// which asserts stderr is empty after temporarily swapping it via <c>Console.SetError</c>, occasionally
/// captured this class's stray write instead). Joining the same collection is the fix: the collection
/// docstring's own scope ("every test class that captures loud-fallback stderr") reads as
/// SetError-swapping only, but the actual invariant it protects is "nothing else may write to the real
/// stream while a member holds it swapped" — a class that writes it unguarded needs the same
/// serialization as a class that swaps it.
/// </summary>
[Collection(ConsoleErrorCaptureCollection.Name)]
public class OutcomeClassifierTests
{
    [Fact]
    public void Classify_returns_Succeeded_for_a_clean_exit_with_all_outputs_present()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "plan"), "content");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
            Assert.Null(classification.FailureClassification);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_returns_Indeterminate_when_exit_code_is_zero_but_a_required_output_is_missing()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.Null(classification.FailureClassification);
            Assert.Null(classification.CapturedResponseFile);
            Assert.Equal(["plan"], classification.UnsatisfiedOutputNames);
            Assert.Contains("work possibly on disk", classification.Reason);
            Assert.Contains("awaiting conductor resolution", classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    // #1594/#1608 -- see OutcomeClassifier.Classify's own remarks on the captured-response arm for
    // why this settles Indeterminate rather than Succeeded/Failed(Permanent), and
    // OutputMaterializer's class remarks for the capture ruling itself.

    [Fact]
    public void Classify_captures_a_missing_outputs_response_and_settles_Indeterminate_leaving_the_declared_output_unwritten()
    {
        var directory = CreateTempDirectory();
        try
        {
            WriteStdoutLog(directory, """{"event":"result","result":{"status":"SUCCESS","response":"the worker's real answer"}}""");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory,
                responseParser: new FakeResponseParser("the worker's real answer"));

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.Null(classification.FailureClassification);
            Assert.Equal(OutputMaterializer.CapturedResponseFileName, classification.CapturedResponseFile);
            Assert.Equal(["advice.md"], classification.UnsatisfiedOutputNames);
            Assert.Contains(OutputMaterializer.CapturedResponseFileName, classification.Reason);
            Assert.Contains("awaiting conductor resolution", classification.Reason);

            // The declared output directory is untouched -- its emptiness IS the honest state.
            Assert.False(File.Exists(Path.Combine(directory, "advice.md")));

            var captured = File.ReadAllText(Path.Combine(directory, OutputMaterializer.CapturedResponseFileName));
            Assert.StartsWith(OutputMaterializer.CapturedResponseHeader, captured);
            Assert.Contains("the worker's real answer", captured);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_leaves_a_missing_output_indeterminate_when_there_is_no_stdout_log_at_all()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);

            // No .stdout.log at all -- no capture possible, settles Indeterminate (#1593)
            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory,
                responseParser: new FakeResponseParser(response: null));

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.Null(classification.CapturedResponseFile);
            Assert.Equal(["advice.md"], classification.UnsatisfiedOutputNames);
            Assert.False(File.Exists(Path.Combine(directory, "advice.md")));
            Assert.False(File.Exists(Path.Combine(directory, OutputMaterializer.CapturedResponseFileName)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_leaves_a_missing_output_indeterminate_when_the_parser_declines_the_stdout_lines_last_line()
    {
        // The polarity arm the previous test's "no .stdout.log" case can't reach: a real stream log
        // exists, but the adapter's parser looks at its last line and says "not a usable response"
        // (e.g. a non-SUCCESS terminal envelope) -- the FakeResponseParser is consulted here, not
        // short-circuited before it runs.
        var directory = CreateTempDirectory();
        try
        {
            WriteStdoutLog(directory, """{"event":"result","result":{"status":"ERROR","response":""}}""");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory,
                responseParser: new FakeResponseParser(response: null));

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.Null(classification.CapturedResponseFile);
            Assert.Equal(["advice.md"], classification.UnsatisfiedOutputNames);
            Assert.False(File.Exists(Path.Combine(directory, "advice.md")));
            Assert.False(File.Exists(Path.Combine(directory, OutputMaterializer.CapturedResponseFileName)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_never_captures_a_mixed_population_of_missing_and_present_but_wrong_outputs()
    {
        // Genuinely mixed (second-reader review, #1594): one output entirely absent (Missing), a
        // second one present but not JSON (NotJson) -- distinct from the single-output NotJson test
        // below, which never exercises the "some Missing, some not" branch at all.
        var directory = CreateTempDirectory();
        try
        {
            WriteStdoutLog(directory, """{"event":"result","result":{"status":"SUCCESS","response":"the worker's real answer"}}""");
            File.WriteAllText(Path.Combine(directory, "verdict.json"), "not json");
            var contract = new WorkerContract(
                "worker", [],
                [
                    new ProducedOutput("advice.md"),
                    new ProducedOutput("verdict.json", new OutputCondition("/ok", new JsonScalar.Boolean(true))),
                ],
                []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory,
                responseParser: new FakeResponseParser("the worker's real answer"));

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.Null(classification.CapturedResponseFile);
            Assert.Equal(["advice.md", "verdict.json"], classification.UnsatisfiedOutputNames);
            Assert.False(File.Exists(Path.Combine(directory, "advice.md")));
            Assert.False(File.Exists(Path.Combine(directory, OutputMaterializer.CapturedResponseFileName)));
            Assert.Equal("not json", File.ReadAllText(Path.Combine(directory, "verdict.json")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_never_captures_over_a_present_output_that_failed_for_a_different_reason()
    {
        var directory = CreateTempDirectory();
        try
        {
            WriteStdoutLog(directory, """{"event":"result","result":{"status":"SUCCESS","response":"the worker's real answer"}}""");
            // A present, non-JSON file declaring a condition: ConditionFailed via NotJson, not Missing.
            File.WriteAllText(Path.Combine(directory, "verdict.json"), "not json");
            var contract = new WorkerContract(
                "worker", [], [new ProducedOutput("verdict.json", new OutputCondition("/ok", new JsonScalar.Boolean(true)))], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory,
                responseParser: new FakeResponseParser("the worker's real answer"));

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.Null(classification.CapturedResponseFile);
            Assert.Equal(["verdict.json"], classification.UnsatisfiedOutputNames);
            // Untouched: the real file a worker actually wrote must never be clobbered by the envelope.
            Assert.Equal("not json", File.ReadAllText(Path.Combine(directory, "verdict.json")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_never_captures_a_missing_output_that_declares_a_schema()
    {
        // Second-reader finding (#1594): a multi-output role like `review` declares report.md AND
        // verdict.json. If agy writes neither, both are Missing -- the naive "all Missing" gate would
        // capture a response that can never resolve verdict.json (OutputSchema.ReviewVerdict), a
        // capture that can only ever satisfy half the contract. Nothing must be captured at all.
        var directory = CreateTempDirectory();
        try
        {
            WriteStdoutLog(directory, """{"event":"result","result":{"status":"SUCCESS","response":"free-form prose, not a verdict"}}""");
            var contract = new WorkerContract(
                "worker", [],
                [new ProducedOutput("report.md"), new ProducedOutput("verdict.json", Schema: OutputSchema.ReviewVerdict)],
                []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory,
                responseParser: new FakeResponseParser("free-form prose, not a verdict"));

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.Null(classification.CapturedResponseFile);
            Assert.Equal(["report.md", "verdict.json"], classification.UnsatisfiedOutputNames);
            Assert.False(File.Exists(Path.Combine(directory, "report.md")));
            Assert.False(File.Exists(Path.Combine(directory, "verdict.json")));
            Assert.False(File.Exists(Path.Combine(directory, OutputMaterializer.CapturedResponseFileName)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_never_captures_a_missing_json_output_with_no_declared_schema()
    {
        // Second-reader finding (#1594): OutputSchema/OutputCondition is not the only signal that an
        // output can't honestly resolve from prose. `orchestrate`'s turn-actions.json (WorkerRoles.json)
        // declares Schema: None yet is structurally JSON a downstream reader will try to parse as
        // such -- Missing-only + no-schema must still refuse a bare .json name.
        var directory = CreateTempDirectory();
        try
        {
            WriteStdoutLog(directory, """{"event":"result","result":{"status":"SUCCESS","response":"free-form prose"}}""");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("turn-actions.json")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory,
                responseParser: new FakeResponseParser("free-form prose"));

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.Null(classification.CapturedResponseFile);
            Assert.Equal(["turn-actions.json"], classification.UnsatisfiedOutputNames);
            Assert.False(File.Exists(Path.Combine(directory, "turn-actions.json")));
            Assert.False(File.Exists(Path.Combine(directory, OutputMaterializer.CapturedResponseFileName)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_never_captures_a_missing_diff_output_alongside_a_missing_report_even_though_both_are_Missing()
    {
        // Same finding as the schema/json arms above, the `janitor` shape: janitor.md (prose-safe) +
        // branch.diff (not), BOTH Missing (not a mixed-reason population -- this exercises the
        // prose-unsafe check on its own, within an all-Missing set). A capture that can only ever
        // resolve janitor.md and never branch.diff must not be recorded at all.
        var directory = CreateTempDirectory();
        try
        {
            WriteStdoutLog(directory, """{"event":"result","result":{"status":"SUCCESS","response":"ran the checkers, all green"}}""");
            var contract = new WorkerContract(
                "worker", [], [new ProducedOutput("janitor.md"), new ProducedOutput("branch.diff")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory,
                responseParser: new FakeResponseParser("ran the checkers, all green"));

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.Null(classification.CapturedResponseFile);
            Assert.Equal(["janitor.md", "branch.diff"], classification.UnsatisfiedOutputNames);
            Assert.False(File.Exists(Path.Combine(directory, "janitor.md")));
            Assert.False(File.Exists(Path.Combine(directory, "branch.diff")));
            Assert.False(File.Exists(Path.Combine(directory, OutputMaterializer.CapturedResponseFileName)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_captures_when_only_one_of_a_multioutput_contracts_declared_outputs_is_missing()
    {
        // Review F9: janitor.md missing, branch.diff present and valid -- the response is captured
        // (janitor.md is the sole unsatisfied output, and it's prose-safe), branch.diff is
        // byte-unchanged, and the declared output directory stays otherwise untouched.
        var directory = CreateTempDirectory();
        try
        {
            WriteStdoutLog(directory, """{"event":"result","result":{"status":"SUCCESS","response":"ran the checkers, all green"}}""");
            File.WriteAllText(Path.Combine(directory, "branch.diff"), "--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+b\n");
            var contract = new WorkerContract(
                "worker", [], [new ProducedOutput("janitor.md"), new ProducedOutput("branch.diff")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory,
                responseParser: new FakeResponseParser("ran the checkers, all green"));

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.Null(classification.FailureClassification);
            Assert.Equal(OutputMaterializer.CapturedResponseFileName, classification.CapturedResponseFile);
            Assert.Equal(["janitor.md"], classification.UnsatisfiedOutputNames);
            Assert.False(File.Exists(Path.Combine(directory, "janitor.md")));
            Assert.Equal("--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+b\n", File.ReadAllText(Path.Combine(directory, "branch.diff")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_does_not_capture_when_no_response_parser_is_supplied()
    {
        var directory = CreateTempDirectory();
        try
        {
            WriteStdoutLog(directory, """{"event":"result","result":{"status":"SUCCESS","response":"the worker's real answer"}}""");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.Null(classification.CapturedResponseFile);
            Assert.Equal(["advice.md"], classification.UnsatisfiedOutputNames);
            Assert.False(File.Exists(Path.Combine(directory, "advice.md")));
            Assert.False(File.Exists(Path.Combine(directory, OutputMaterializer.CapturedResponseFileName)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_scans_before_a_terminal_usage_line_for_the_last_recognized_response()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, ExecutionStreamLogger.StdoutLogFileName),
                "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"answer\"}}\n" +
                "{\"type\":\"turn.completed\",\"usage\":{\"output_tokens\":7}}\n");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory,
                responseParser: new MatchingResponseParser("item.completed", "answer"));

            Assert.Equal(OutputMaterializer.CapturedResponseFileName, classification.CapturedResponseFile);
            Assert.Contains(
                "answer",
                File.ReadAllText(Path.Combine(directory, OutputMaterializer.CapturedResponseFileName)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    private static void WriteStdoutLog(string outputDirectory, string lastLine) =>
        File.WriteAllText(Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName), lastLine + "\n");

    private sealed class FakeResponseParser(string? response) : IWorkerResponseParser
    {
        public bool TryParseFinalResponse(string rawLine, out string? response2)
        {
            response2 = response;
            return response is not null;
        }
    }

    private sealed class MatchingResponseParser(string marker, string parsed) : IWorkerResponseParser
    {
        public bool TryParseFinalResponse(string rawLine, out string? response)
        {
            response = rawLine.Contains(marker, StringComparison.Ordinal) ? parsed : null;
            return response is not null;
        }

        public bool IsPostResponseTerminalLine(string rawLine) =>
            rawLine.Contains("turn.completed", StringComparison.Ordinal);
    }

    private sealed class FakeUsageParser(WorkerUsage? usage) : IWorkerUsageParser
    {
        public bool TryParseFinalUsage(string rawLine, out WorkerUsage? usageOut)
        {
            usageOut = usage;
            return usage is not null;
        }
    }

    // #1586 S1 (the #1594 ruling's tripwire): SubstantialWorkNoOutputsEvidence, scoped to the exact
    // "worker exited 0, contract unsatisfied" shape above -- never the non-zero-exit or timeout paths.

    [Fact]
    public void Classify_records_substantial_work_evidence_alongside_a_successful_capture()
    {
        var directory = CreateTempDirectory();
        try
        {
            WriteStdoutLog(directory, """{"event":"result","result":{"status":"SUCCESS","response":"the worker's real answer"}}""");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory,
                responseParser: new FakeResponseParser("the worker's real answer"),
                usageParser: new FakeUsageParser(new WorkerUsage(TokensOut: 500, Turns: 4)));

            // Both facts land on the same classification (OutcomeClassifier.Classify's own remarks
            // explain why the evidence is computed independent of capture outcome) -- this fixture is
            // the capture-succeeded half.
            Assert.Equal(OutputMaterializer.CapturedResponseFileName, classification.CapturedResponseFile);
            Assert.NotNull(classification.SubstantialWorkNoOutputsEvidence);
            Assert.Contains("4 turn", classification.SubstantialWorkNoOutputsEvidence);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_records_substantial_work_evidence_even_when_nothing_was_captured()
    {
        // The "not captured" half: no responseParser at all, so OutputMaterializer never fires -- the
        // evidence must still attach to the plain contract-failure fallback return.
        var directory = CreateTempDirectory();
        try
        {
            WriteStdoutLog(directory, """{"event":"result","result":{"status":"SUCCESS","response":"irrelevant to the usage parser"}}""");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory,
                usageParser: new FakeUsageParser(new WorkerUsage(TokensOut: 500, Turns: 4)));

            Assert.Null(classification.CapturedResponseFile);
            Assert.NotNull(classification.SubstantialWorkNoOutputsEvidence);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_leaves_evidence_null_when_no_usage_parser_is_supplied()
    {
        var directory = CreateTempDirectory();
        try
        {
            WriteStdoutLog(directory, """{"event":"result","result":{"status":"SUCCESS","response":"x"}}""");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory);

            Assert.Null(classification.SubstantialWorkNoOutputsEvidence);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_leaves_evidence_null_when_the_worker_reported_no_turns_or_tokens()
    {
        var directory = CreateTempDirectory();
        try
        {
            WriteStdoutLog(directory, """{"event":"result","result":{"status":"SUCCESS","response":"x"}}""");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory,
                usageParser: new FakeUsageParser(new WorkerUsage(TokensOut: null, Turns: null)));

            Assert.Null(classification.SubstantialWorkNoOutputsEvidence);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_leaves_evidence_null_when_only_some_declared_outputs_are_missing()
    {
        // Partial contracts (one output present, one missing) are a different, narrower failure than
        // "wrote nothing" -- the tripwire is scoped to ALL declared outputs missing, per
        // AllDeclaredOutputsMissing's own doc. Same fixture as
        // Classify_captures_when_only_one_of_a_multioutput_contracts_declared_outputs_is_missing, plus
        // a usage parser that WOULD report real work if consulted.
        var directory = CreateTempDirectory();
        try
        {
            WriteStdoutLog(directory, """{"event":"result","result":{"status":"SUCCESS","response":"ran the checkers, all green"}}""");
            File.WriteAllText(Path.Combine(directory, "branch.diff"), "--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+b\n");
            var contract = new WorkerContract(
                "worker", [], [new ProducedOutput("janitor.md"), new ProducedOutput("branch.diff")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory,
                responseParser: new FakeResponseParser("ran the checkers, all green"),
                usageParser: new FakeUsageParser(new WorkerUsage(TokensOut: 500, Turns: 4)));

            Assert.Null(classification.SubstantialWorkNoOutputsEvidence);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_leaves_evidence_null_for_a_non_zero_exit_code_even_with_real_usage()
    {
        // Deliberately out of scope: a crash-exit failure already has an obvious explanation, and this
        // tripwire targets the #1594 shape specifically (natural exit 0, contract unsatisfied).
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural), contract, directory,
                usageParser: new FakeUsageParser(new WorkerUsage(TokensOut: 500, Turns: 4)));

            Assert.Null(classification.SubstantialWorkNoOutputsEvidence);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_returns_Failed_for_a_non_zero_exit_code()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    // #1089 flips this from the old "a timeout always fails regardless of outputs" into a guarded
    // exception. The three arms below pin the guard from every direction: outputs alone are not enough
    // (no marker -> Failed), the marker plus outputs succeeds, and the marker alone is not enough
    // (no outputs -> Failed).

    [Fact]
    public void Classify_fails_a_timeout_with_satisfied_outputs_when_no_terminal_success_was_observed()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "plan"), "content");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            // Outputs present, but the worker streamed no terminal success marker -- this could be a
            // mid-write kill, not a finished-then-hung run, so the default holds: TimedOut -> Failed.
            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.TimedOut, TerminalSuccessObserved: false), contract, directory);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_succeeds_a_timeout_when_terminal_success_was_observed_and_outputs_are_satisfied()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "plan"), "content");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            // The worker declared success (terminal marker on stdout) AND every declared output exists --
            // it finished, then hung at teardown (#1089). A from-scratch retry would rebuild existing work.
            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.TimedOut, TerminalSuccessObserved: true), contract, directory);

            Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_fails_a_timeout_with_terminal_success_but_missing_outputs()
    {
        var directory = CreateTempDirectory();
        try
        {
            // No output file written: the marker alone is not enough, the contract is unsatisfied.
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.TimedOut, TerminalSuccessObserved: true), contract, directory);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_returns_Cancelled_for_a_cancel_requested_exit_even_with_a_non_zero_code()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(137, CoreExitReason.CancelRequested), contract, directory);

            Assert.Equal(OutcomeVerdict.Cancelled, classification.Verdict);
            Assert.Null(classification.FailureClassification);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_reads_a_self_reported_Permanent_FailureClassification_from_OptionalMetadata()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "outcome.json"), """{"FailureClassification": "Permanent"}""");
            var contract = new WorkerContract("worker", [], [], OptionalMetadata: ["outcome.json"]);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Equal(FailureClassification.Permanent, classification.FailureClassification);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_treats_a_missing_or_unrecognized_FailureClassification_as_null()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], OptionalMetadata: ["outcome.json"]);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Null(classification.FailureClassification);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_includes_exit_code_in_Reason_for_non_zero_exit()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);

            var class1 = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural), contract, directory);
            var class42 = OutcomeClassifier.Classify(
                new CoreDispatchResult(42, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Failed, class1.Verdict);
            Assert.NotNull(class1.Reason);
            Assert.Contains("1", class1.Reason);

            Assert.Equal(OutcomeVerdict.Failed, class42.Verdict);
            Assert.NotNull(class42.Reason);
            Assert.Contains("42", class42.Reason);

            // Polarity: distinct exit codes produce distinct reasons
            Assert.NotEqual(class1.Reason, class42.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_includes_timeout_diagnostic_in_Reason_for_timed_out_execution()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "plan"), "content");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classTimeout = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.TimedOut), contract, directory);
            var classExitCode = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Failed, classTimeout.Verdict);
            Assert.NotNull(classTimeout.Reason);

            // Polarity: timeout reason differs from exit code failure reason
            Assert.NotEqual(classTimeout.Reason, classExitCode.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_lists_all_unsatisfied_outputs_in_Reason_when_multiple_fail()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contractBothMissing = new WorkerContract(
                "worker", [], [new ProducedOutput("alpha.txt"), new ProducedOutput("beta.json")], []);
            var contractSingleMissing = new WorkerContract(
                "worker", [], [new ProducedOutput("alpha.txt")], []);

            var classBoth = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contractBothMissing, directory);
            var classSingle = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contractSingleMissing, directory);

            Assert.Equal(OutcomeVerdict.Indeterminate, classBoth.Verdict);
            Assert.NotNull(classBoth.Reason);
            Assert.Contains("alpha.txt", classBoth.Reason);
            Assert.Contains("beta.json", classBoth.Reason);

            Assert.Equal(OutcomeVerdict.Indeterminate, classSingle.Verdict);
            Assert.NotNull(classSingle.Reason);
            Assert.Contains("alpha.txt", classSingle.Reason);
            Assert.DoesNotContain("beta.json", classSingle.Reason);

            // Polarity: missing two outputs produces a different reason string than missing one output
            Assert.NotEqual(classBoth.Reason, classSingle.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// The 500-character cap cuts at a fixed index, and a non-BMP character occupies two UTF-16
    /// chars, so a cut can land between them and leave a lone high surrogate — malformed UTF-16
    /// written into an append-only journal. Reachable rather than theoretical: a contract-failure
    /// reason renders values from the worker's own JSON output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The offsets place a single emoji at, on either side of, and across the cut, so exactly one
    /// row lands mid-pair and the neighbours are its controls. An earlier version of this test built
    /// the overlong reason from 35 emoji-laden names and <b>passed with the fix removed</b> — the
    /// per-name padding shifted the cut by a multiple of itself rather than by one char, so no row
    /// ever straddled a pair. Computing the boundary rather than hoping to hit it is the difference
    /// between this test and that one.
    /// </para>
    /// <para>
    /// Asserted as a UTF-8 round trip rather than by inspecting chars, because that is the actual
    /// harm: encoding a lone surrogate substitutes U+FFFD, so a reason that survives the round trip
    /// unchanged is exactly one that reaches <c>flow.jsonl</c> intact.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(469)]
    [InlineData(470)]
    [InlineData(471)]
    [InlineData(472)]
    public void Classify_never_truncates_Reason_through_the_middle_of_a_surrogate_pair(int emojiOffset)
    {
        var directory = CreateTempDirectory();
        try
        {
            // "Contract not satisfied: '" is 25 chars, so name index k sits at reason index 25 + k,
            // and the cut is at 500 - "...".Length = 497. Offset 471 therefore puts the pair's high
            // surrogate at 496 and its low surrogate at 497 — straddling the cut exactly.
            var name = new string('a', emojiOffset) + "\U0001F600" + new string('a', 100);
            var contract = new WorkerContract("worker", [], [new ProducedOutput(name)], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory);

            Assert.NotNull(classification.Reason);
            Assert.True(classification.Reason.Length <= 500);

            var roundTripped = System.Text.Encoding.UTF8.GetString(
                System.Text.Encoding.UTF8.GetBytes(classification.Reason));

            Assert.Equal(classification.Reason, roundTripped);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_truncates_Reason_to_500_characters_with_ellipsis_when_pathological()
    {
        var directory = CreateTempDirectory();
        try
        {
            // Exactly at the listing cap, so nothing overflows and the ellipsis is what ends the
            // string. Above the cap the reason ends with "(+N more)" instead — that path is the
            // overflow test's, and conflating the two is what made this fixture fail when the count
            // cap landed: it was asserting an ellipsis on a reason that had legitimately stopped
            // ending with one.
            var outputs = Enumerable.Range(1, 8)
                .Select(i => new ProducedOutput($"pathological_long_output_filename_entry_number_{i:D2}_forcing_truncation_of_the_assembled_reason.json"))
                .ToList();
            var contract = new WorkerContract("worker", [], outputs, []);

            var classTruncated = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Indeterminate, classTruncated.Verdict);
            Assert.NotNull(classTruncated.Reason);
            Assert.True(classTruncated.Reason.Length <= 500, $"Reason length {classTruncated.Reason.Length} exceeded 500 characters cap.");
            // F13 (#1593 review): the suffix now follows the cut, so a bare EndsWith("...") no longer
            // holds — but a bare Contains("...") would pass equally on a literal "..." anywhere in an
            // output name, which is not what this assertion means to discriminate. "... —" pins the cut
            // immediately followed by the suffix's own leading text, keeping the discrimination the
            // pre-suffix EndsWith form had.
            Assert.Contains("... —", classTruncated.Reason);
            Assert.Contains("awaiting conductor resolution", classTruncated.Reason);

            // Polarity arm: non-pathological short reason is not truncated and does not end with ellipsis
            var shortContract = new WorkerContract("worker", [], [new ProducedOutput("short.txt")], []);
            var classShort = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), shortContract, directory);

            Assert.NotNull(classShort.Reason);
            Assert.True(classShort.Reason.Length < 500);
            Assert.False(classShort.Reason.EndsWith("..."));
            Assert.False(classShort.Reason.EndsWith("…"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"outcome-classifier-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// When more unsatisfied outputs exist than the reason lists, the "(+N more)" marker must
    /// survive truncation — it is the only signal that anything was omitted.
    /// </summary>
    /// <remarks>
    /// The first version appended the marker and then truncated the whole string, which made the
    /// marker the first thing cut. That reinstated, at the count layer, the same silent dropping the
    /// per-value cap had just been added to prevent: a signal that vanishes exactly when it becomes
    /// true. Found by a second reader after the review, in code written to fix the review.
    /// </remarks>
    [Fact]
    public void Classify_keeps_the_overflow_marker_even_when_the_reason_is_truncated()
    {
        var directory = CreateTempDirectory();
        try
        {
            // Long names so the listed outputs alone blow the 500-char budget, forcing the collision
            // between truncation and the marker.
            var outputs = Enumerable.Range(1, 40)
                .Select(i => new ProducedOutput($"a_deliberately_long_output_filename_number_{i:D2}_forcing_truncation.json"))
                .ToList();
            var contract = new WorkerContract("worker", [], outputs, []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory);

            Assert.NotNull(classification.Reason);
            Assert.True(
                classification.Reason.Length <= 500,
                $"Reason length {classification.Reason.Length} exceeded the 500-character cap.");
            Assert.Contains("(+32 more)", classification.Reason);
            Assert.Contains("awaiting conductor resolution", classification.Reason);

            // Polarity: at or under the listing cap there is no marker to preserve.
            var fewOutputs = Enumerable.Range(1, 3).Select(i => new ProducedOutput($"out{i}.json")).ToList();
            var fewClassification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural),
                new WorkerContract("worker", [], fewOutputs, []),
                directory);

            Assert.NotNull(fewClassification.Reason);
            Assert.DoesNotContain("more)", fewClassification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    // #563: what the worker wrote to stderr reaches Reason, which #597 already carries to the CLI,
    // both desktop surfaces and the daemon's wire payload.

    /// <summary>
    /// The exact case measured on this host: a real <c>agy</c> dispatch failed with
    /// <c>Error: invalid model selection (--model "gemini-3-pro" --effort "high"): --effort is not
    /// supported for model "gemini-3-pro"</c> on stderr, and AER reported only
    /// <c>Worker exited with non-zero code 1.</c>
    /// </summary>
    [Fact]
    public void Classify_carries_the_workers_stderr_into_the_reason_for_a_non_zero_exit()
    {
        var directory = CreateTempDirectory();
        try
        {
            const string stderr =
                "Error: invalid model selection (--model \"gemini-3-pro\" --effort \"high\"): "
                + "--effort is not supported for model \"gemini-3-pro\"";

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural, stderr),
                new WorkerContract("worker", [], [], []),
                directory);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.NotNull(classification.Reason);
            Assert.Contains("Worker exited with non-zero code 1.", classification.Reason);
            Assert.Contains("--effort is not supported for model", classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// The polarity control for every stderr test here, and the regression guard for the three
    /// reason strings that shipped in #597: a worker that wrote nothing must produce the
    /// byte-for-byte pre-#563 reason. An implementation that appended an empty <c>stderr:</c> label
    /// would pass every <c>Contains</c> assertion above while telling an operator the worker had
    /// spoken and said nothing.
    /// </summary>
    [Fact]
    public void Classify_leaves_the_reason_byte_for_byte_unchanged_when_the_worker_wrote_no_stderr()
    {
        var directory = CreateTempDirectory();
        try
        {
            var emptyContract = new WorkerContract("worker", [], [], []);

            Assert.Equal(
                "Worker exited with non-zero code 1.",
                OutcomeClassifier.Classify(
                    new CoreDispatchResult(1, CoreExitReason.Natural, StderrTail: null), emptyContract, directory).Reason);

            Assert.Equal(
                "Execution timed out.",
                OutcomeClassifier.Classify(
                    new CoreDispatchResult(0, CoreExitReason.TimedOut, StderrTail: null), emptyContract, directory).Reason);

            Assert.Equal(
                "Contract not satisfied: 'plan' is missing — worker exited 0 with work possibly on disk; awaiting conductor resolution.",
                OutcomeClassifier.Classify(
                    new CoreDispatchResult(0, CoreExitReason.Natural, StderrTail: null),
                    new WorkerContract("worker", [], [new ProducedOutput("plan")], []),
                    directory).Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// Whitespace-only stderr takes the same path as none at all. A worker that writes a stray
    /// newline has not diagnosed anything, and a dangling <c>stderr:</c> label would imply it had.
    /// </summary>
    [Fact]
    public void Classify_treats_whitespace_only_stderr_as_no_stderr()
    {
        var directory = CreateTempDirectory();
        try
        {
            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural, "  \r\n\t  "),
                new WorkerContract("worker", [], [], []),
                directory);

            Assert.Equal("Worker exited with non-zero code 1.", classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// Every consumer of <c>Reason</c> is line-oriented — <c>FlowStateReporter</c> writes one
    /// <c>"  {StepId}: {Status} — {Reason}"</c> line per step — so an embedded newline from a
    /// multi-line vendor error would split one step's row into rows that no longer parse as steps.
    /// </summary>
    [Fact]
    public void Classify_flattens_multi_line_stderr_to_a_single_line()
    {
        var directory = CreateTempDirectory();
        try
        {
            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural, "first line\nsecond line\r\n\r\nthird line\n"),
                new WorkerContract("worker", [], [], []),
                directory);

            Assert.NotNull(classification.Reason);
            Assert.DoesNotContain('\n', classification.Reason);
            Assert.DoesNotContain('\r', classification.Reason);

            // Collapsed, not deleted: the words must survive, separated by single spaces, and the
            // trailing newline must not leave the reason ending in a space.
            Assert.Contains("first line second line third line", classification.Reason);
            Assert.Equal(classification.Reason.TrimEnd(), classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// Bounded, keeping the end, and marked when anything was dropped — the three properties that
    /// are each one mistake apart from a diagnostic that lies about its own completeness.
    /// </summary>
    [Fact]
    public void Classify_bounds_a_long_stderr_tail_keeps_its_end_and_marks_the_truncation()
    {
        var directory = CreateTempDirectory();
        try
        {
            var stderr = "OPENING-BANNER " + new string('x', 5000) + " CLOSING-DIAGNOSTIC";

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural, stderr),
                new WorkerContract("worker", [], [], []),
                directory);

            Assert.NotNull(classification.Reason);
            Assert.Contains("CLOSING-DIAGNOSTIC", classification.Reason);
            Assert.DoesNotContain("OPENING-BANNER", classification.Reason);

            // The ellipsis sits at the front of the rendered tail, because the front is where the
            // cut was made — marking the other end would claim the opposite about what is missing.
            Assert.Contains("stderr: …", classification.Reason);

            // Polarity: a tail short enough to survive intact carries no marker at all.
            var shortClassification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural, "brief failure"),
                new WorkerContract("worker", [], [], []),
                directory);

            Assert.Equal("Worker exited with non-zero code 1. stderr: brief failure", shortClassification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// Two caps sit in series and only the classifier's emits a marker, so the classifier's must be
    /// the tighter one — otherwise stderr long enough to hit the dispatcher's silent cap could be
    /// rendered whole, and an operator would see a tail that had content dropped with nothing
    /// saying so. Asserted rather than left to the comment on <c>MaxStderrTailInReason</c>.
    /// </summary>
    [Fact]
    public void The_marked_display_cap_is_tighter_than_the_silent_retention_cap()
    {
        Assert.True(
            OutcomeClassifier.MaxStderrTailInReason < CoreDispatcher.MaxRetainedStderrLength,
            $"display cap {OutcomeClassifier.MaxStderrTailInReason} must stay below retention cap "
            + $"{CoreDispatcher.MaxRetainedStderrLength}, or truncation becomes invisible.");
    }

    /// <summary>
    /// The contract-failure path gets stderr too. This is #597's exit-0-no-output case, which has
    /// the least other evidence available — a worker that decided it had nothing to write very often
    /// says why on its way out.
    /// </summary>
    [Fact]
    public void Classify_carries_stderr_into_the_reason_for_a_contract_failure_after_a_clean_exit()
    {
        var directory = CreateTempDirectory();
        try
        {
            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural, "warning: no changes were required"),
                new WorkerContract("worker", [], [new ProducedOutput("plan")], []),
                directory);

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.Equal(
                "Contract not satisfied: 'plan' is missing — worker exited 0 with work possibly on disk; awaiting conductor resolution. stderr: warning: no changes were required",
                classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_delegates_to_IFailureClassifier_and_carries_ExhaustedUntil_and_RetryNotBefore()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);
            var now = new DateTimeOffset(2026, 7, 30, 15, 0, 0, TimeSpan.Zero);
            var testTime = new TestTimeProvider(now);
            var specimenStderr = "Error: Individual quota reached. Please upgrade your subscription to increase your limits. Resets in 28m40s.";
            var mockClassifier = new TestQuotaClassifier(specimenStderr, FailureClassification.ExhaustedUntil, now.AddMinutes(28).AddSeconds(40));

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural, specimenStderr),
                contract,
                directory,
                mockClassifier,
                testTime);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Equal(FailureClassification.ExhaustedUntil, classification.FailureClassification);
            Assert.Equal(now.AddMinutes(28).AddSeconds(40), classification.RetryNotBefore);
            Assert.Contains("Worker exited with non-zero code 1. stderr: Error: Individual quota reached.", classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_preserves_Reason_intact_when_quota_like_stderr_has_no_parseable_duration()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);
            var unparseableStderr = "Error: Individual quota reached. Resets in unknown.";
            var mockClassifier = new TestQuotaClassifier("dummy", null, null);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural, unparseableStderr),
                contract,
                directory,
                mockClassifier);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Null(classification.FailureClassification);
            Assert.Equal("Worker exited with non-zero code 1. stderr: Error: Individual quota reached. Resets in unknown.", classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_vetoes_satisfied_exit_0_run_when_failure_classifier_detects_auto_denied_tool()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);
            var autoDeniedStderr = "a required tool was auto-denied permission";
            var mockClassifier = new TestQuotaClassifier(autoDeniedStderr, FailureClassification.ToolDenied, null);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural, autoDeniedStderr),
                contract,
                directory,
                mockClassifier);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Equal(FailureClassification.ToolDenied, classification.FailureClassification);
            Assert.Contains("Execution failed: a required tool was auto-denied.", classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_returns_Succeeded_for_satisfied_exit_0_run_when_failure_classifier_returns_false()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);
            var mockClassifier = new TestQuotaClassifier("dummy", null, null);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural, "all good"),
                contract,
                directory,
                mockClassifier);

            Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
            Assert.Null(classification.FailureClassification);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_vetoes_a_satisfied_exit_0_run_when_the_stream_carries_a_quota_exhaustion_signal()
    {
        // #1622: a worker hitting quota mid-lane can still exit 0 against a trivially-satisfied
        // (zero-output) contract; this must not read Succeeded. See OutcomeClassifier.Classify's own
        // remarks for why -- pinned here as the gating unit test, mock classifier and all.
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);
            var quotaStderr = "Individual quota reached. Resets in 1h";
            var resetAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var mockClassifier = new TestQuotaClassifier(
                quotaStderr,
                FailureClassification.ExhaustedUntil,
                resetAt);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural, quotaStderr),
                contract,
                directory,
                mockClassifier);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Equal(FailureClassification.ExhaustedUntil, classification.FailureClassification);
            Assert.Equal(resetAt, classification.RetryNotBefore);
            Assert.Contains("vendor's quota-exhaustion signal was present in the stream", classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    // #1622's real captured-stream fixture (a genuine parse, not pass-through against a canned
    // classifier double) lives in Baton.Vendors.Tests.ClaudeWorkerAdapterTests
    // (Classify_vetoes_a_satisfied_exit_0_run_when_the_real_stream_json_stdout_tail_carries_credits_required):
    // OutcomeClassifier lives in Baton, which cannot reference Baton.Vendors (Architecture Rule 2), so
    // the arm exercising a real IFailureClassifier implementation against OutcomeClassifier.Classify has
    // to live in the project that can see both.

    [Theory]
    [InlineData(FailureClassification.Retryable)]
    [InlineData(FailureClassification.Permanent)]
    public void Classify_does_not_veto_a_satisfied_exit_0_run_for_a_non_ToolDenied_non_ExhaustedUntil_classification(
        FailureClassification classification)
    {
        // #914 scope gate, widened by #1622 (a): ONLY ToolDenied/ExhaustedUntil veto an otherwise-
        // satisfied exit-0 run. This is the polarity control for both vetoing tests above -- reds if a
        // future change widens the veto to every non-null FailureClassification instead of exactly
        // these two.
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);
            var stderr = "some other failure signature";
            var mockClassifier = new TestQuotaClassifier(stderr, classification, null);

            var result = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural, stderr),
                contract,
                directory,
                mockClassifier);

            Assert.Equal(OutcomeVerdict.Succeeded, result.Verdict);
            Assert.Null(result.FailureClassification);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_delegates_to_IFailureClassifier_with_StdoutTail_and_carries_ExhaustedUntil()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);
            var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);
            var stdoutEnvelope = """{"type":"result","is_error":true,"errorCode":"credits_required","result":"Subscription quota exhausted."}""";
            var mockClassifier = new TestQuotaTwoTailClassifier(null, stdoutEnvelope, FailureClassification.ExhaustedUntil, null);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural, StderrTail: null, StdoutTail: stdoutEnvelope),
                contract,
                directory,
                mockClassifier,
                testTime);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Equal(FailureClassification.ExhaustedUntil, classification.FailureClassification);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_passes_multi_line_stream_json_stdout_tail_through_to_classifier_unaltered()
    {
        // #1561 finding 7: this pins pass-through, not parsing -- TestQuotaTwoTailClassifier only
        // matches the tail against its own canned fixture (equality, not JSON parsing), so it would
        // pass identically with a one-line tail or with "garbage" in both slots. The actual
        // multi-line stream-json *parse* is exercised in
        // ClaudeWorkerAdapterTests.CreditsRequired_InRealisticStreamJsonStdoutTail_ClassifiesExhaustedUntil.
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);
            var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);
            var streamJsonTail = """
                {"type":"system","subtype":"init","session_id":"s-123"}
                {"type":"assistant","message":{"content":[{"type":"text","text":"Attempting run..."}]}}
                {"type":"result","is_error":true,"errorCode":"credits_required","result":"Subscription quota exhausted."}
                """;
            var mockClassifier = new TestQuotaTwoTailClassifier(null, streamJsonTail, FailureClassification.ExhaustedUntil, null);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural, StderrTail: null, StdoutTail: streamJsonTail),
                contract,
                directory,
                mockClassifier,
                testTime);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Equal(FailureClassification.ExhaustedUntil, classification.FailureClassification);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_with_ordinary_error_on_StdoutTail_stays_unclassified()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);
            var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);
            var stdoutEnvelope = """{"type":"result","is_error":true,"errorCode":"other_error","result":"Failed"}""";
            var mockClassifier = new TestQuotaTwoTailClassifier(null, stdoutEnvelope, null, null);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural, StderrTail: null, StdoutTail: stdoutEnvelope),
                contract,
                directory,
                mockClassifier,
                testTime);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Null(classification.FailureClassification);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    private sealed class TestQuotaTwoTailClassifier(
        string? matchStderr,
        string? matchStdout,
        FailureClassification? classificationToEmit,
        DateTimeOffset? notBeforeToEmit) : IFailureClassifier
    {
        public bool TryClassifyFailure(
            string? stderrTail,
            string? stdoutTail,
            TimeProvider timeProvider,
            out FailureClassification? classification,
            out DateTimeOffset? retryNotBefore)
        {
            if (stderrTail == matchStderr && stdoutTail == matchStdout && classificationToEmit is not null)
            {
                classification = classificationToEmit;
                retryNotBefore = notBeforeToEmit;
                return true;
            }

            classification = null;
            retryNotBefore = null;
            return false;
        }
    }


    private sealed class TestQuotaClassifier(string matchStderr, FailureClassification? classificationToEmit, DateTimeOffset? notBeforeToEmit) : IFailureClassifier
    {
        public bool TryClassifyFailure(string? stderrTail, TimeProvider timeProvider, out FailureClassification? classification, out DateTimeOffset? retryNotBefore)
        {
            if (stderrTail == matchStderr && classificationToEmit is not null)
            {
                classification = classificationToEmit;
                retryNotBefore = notBeforeToEmit;
                return true;
            }

            classification = null;
            retryNotBefore = null;
            return false;
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    /// <summary>
    /// The round-trip drift guard for #617's failed-step banner: SplitReasonAndStderr must undo
    /// exactly what the classifier's own stderr-appending write produced, through the real classify
    /// path rather than a hand-built string — so a change to the separator's spelling reds here
    /// instead of silently stranding every banner at "no excerpt".
    /// </summary>
    [Fact]
    public void SplitReasonAndStderr_round_trips_the_classifiers_own_writing()
    {
        var directory = CreateTempDirectory();
        try
        {
            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural, StderrTail: "connect ECONNREFUSED 127.0.0.1:5432"),
                new WorkerContract("worker-a", [], [], []),
                directory);

            Assert.NotNull(classification.Reason);
            var (sentence, excerpt) = OutcomeClassifier.SplitReasonAndStderr(classification.Reason);
            Assert.Equal("connect ECONNREFUSED 127.0.0.1:5432", excerpt);
            Assert.DoesNotContain("stderr:", sentence);
            Assert.False(string.IsNullOrWhiteSpace(sentence));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void SplitReasonAndStderr_returns_the_whole_reason_when_no_stderr_half_exists()
    {
        // Polarity: a silent worker's reason has no separator and must come back intact — a split
        // that manufactures an excerpt out of nothing would show phantom worker speech.
        var (sentence, excerpt) = OutcomeClassifier.SplitReasonAndStderr("Worker exited with code 1.");
        Assert.Equal("Worker exited with code 1.", sentence);
        Assert.Null(excerpt);

        var (fallback, none) = OutcomeClassifier.SplitReasonAndStderr(null);
        Assert.Equal("Step failed.", fallback);
        Assert.Null(none);
    }

    /// <summary>
    /// A truncated tail's leading <c>…</c> must survive the split. WithStderr's whole contract is
    /// that a cut tail is never shown unmarked (see <c>MaxStderrTailInReason</c>'s remarks); a
    /// splitter that strips the mark re-creates invisible truncation on the one surface that
    /// renders the excerpt as a standalone block.
    /// </summary>
    [Fact]
    public void SplitReasonAndStderr_keeps_the_truncation_ellipsis_on_a_cut_tail()
    {
        var directory = CreateTempDirectory();
        try
        {
            // Comfortably past MaxStderrTailInReason (350) after whitespace collapse.
            var longStderr = string.Join(" ", Enumerable.Range(1, 100).Select(i => $"line{i:D3}"));

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural, StderrTail: longStderr),
                new WorkerContract("worker-a", [], [], []),
                directory);

            Assert.NotNull(classification.Reason);
            var (_, excerpt) = OutcomeClassifier.SplitReasonAndStderr(classification.Reason);
            Assert.NotNull(excerpt);
            Assert.StartsWith("…", excerpt);

            // And the polarity is the untruncated round-trip test above: a short tail carries no
            // ellipsis, so the mark appears exactly when content was dropped.
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// The contract-failure path (exit 0, outputs unsatisfied) appends stderr too — #597's
    /// commonest case — and the banner splits it the same way. Untested before this: both prior
    /// round-trip fixtures took the non-zero-exit path only.
    /// </summary>
    [Fact]
    public void SplitReasonAndStderr_round_trips_a_contract_failure_reason()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker-a", [], [new ProducedOutput("result.json")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural, StderrTail: "wrote nothing: out of quota"),
                contract,
                directory);

            Assert.NotNull(classification.Reason);
            var (sentence, excerpt) = OutcomeClassifier.SplitReasonAndStderr(classification.Reason);
            Assert.StartsWith("Contract not satisfied:", sentence);
            Assert.Contains("'result.json' is missing", sentence);
            Assert.Equal("wrote nothing: out of quota", excerpt);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Audited_execution_with_clean_worktree_yields_Succeeded()
    {
        var outboxDir = CreateTempDirectory();
        var worktreeDir = CreateTempDirectory();
        try
        {
            InitGitRepository(worktreeDir);
            File.WriteAllText(Path.Combine(outboxDir, "plan"), "content");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural),
                contract,
                outboxDir,
                grantAuditMode: GrantAuditMode.AuditedNotEnforced,
                worktreePath: worktreeDir);

            Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outboxDir);
            DirectoryCleanup.DeleteRecursively(worktreeDir);
        }
    }

    [Fact]
    public void Audited_execution_with_dirty_worktree_yields_Failed_naming_stray_file()
    {
        var outboxDir = CreateTempDirectory();
        var worktreeDir = CreateTempDirectory();
        try
        {
            InitGitRepository(worktreeDir);
            File.WriteAllText(Path.Combine(outboxDir, "plan"), "content");
            File.WriteAllText(Path.Combine(worktreeDir, "stray_file.txt"), "dirt");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural),
                contract,
                outboxDir,
                grantAuditMode: GrantAuditMode.AuditedNotEnforced,
                worktreePath: worktreeDir);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Equal(FailureClassification.Permanent, classification.FailureClassification);
            Assert.NotNull(classification.Reason);
            Assert.Contains("Grant audit failed", classification.Reason);
            Assert.Contains("stray_file.txt", classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outboxDir);
            DirectoryCleanup.DeleteRecursively(worktreeDir);
        }
    }

    [Fact]
    public void Enforced_execution_with_dirty_worktree_yields_Succeeded_without_auditing()
    {
        var outboxDir = CreateTempDirectory();
        var worktreeDir = CreateTempDirectory();
        try
        {
            InitGitRepository(worktreeDir);
            File.WriteAllText(Path.Combine(outboxDir, "plan"), "content");
            File.WriteAllText(Path.Combine(worktreeDir, "stray_file.txt"), "dirt");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural),
                contract,
                outboxDir,
                grantAuditMode: GrantAuditMode.Enforced,
                worktreePath: worktreeDir);

            Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outboxDir);
            DirectoryCleanup.DeleteRecursively(worktreeDir);
        }
    }

    [Fact]
    public void Audited_execution_fails_closed_when_worktree_is_invalid_or_git_fails()
    {
        var outboxDir = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(outboxDir, "plan"), "content");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural),
                contract,
                outboxDir,
                grantAuditMode: GrantAuditMode.AuditedNotEnforced,
                worktreePath: Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}"));

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Equal(FailureClassification.Permanent, classification.FailureClassification);
            Assert.NotNull(classification.Reason);
            Assert.Contains("Grant audit failed", classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outboxDir);
        }
    }

    /// <summary>
    /// F4 (#1593 review): production only ever hands <c>IsWorkspaceUntouched</c> an ACTUALLY-provisioned
    /// worktree path (<see cref="WorkerBinding.Process.IsWorktree"/>) plus the ref it was provisioned
    /// from — never a plain <c>git init</c> directory standing in for one, which is not a worktree at
    /// all (<see cref="WorktreeProvisioner.IsWorktree"/> reads false for it) and so exercised the WRONG
    /// branch of <see cref="WorktreeProvisioner.IsWorkspaceUntouched"/> before this fix. Rewritten to use
    /// a real provisioned worktree and its real base ref, so this pins the branch production actually
    /// takes rather than the non-worktree <c>@{upstream}</c> arm.
    /// </summary>
    /// <summary>
    /// N2 (#1664 re-review): passes the REAL resolved base SHA
    /// (<see cref="WorktreeProvisioner.ResolveBaseCommit"/>), not the literal symbolic
    /// <c>"HEAD"</c> the pre-fix version of this test passed. A literal <c>"HEAD"</c>, read back out
    /// of the worktree itself, is <c>HEAD..HEAD ≡ 0</c> — degenerate, and unable to discriminate a
    /// commit from no commit, which is exactly why production's own use of the same value could not
    /// have caught N2. This is the control: same clean workspace, but now compared against a base
    /// that is genuinely capable of being ahead of.
    /// </summary>
    [Fact]
    public void Dead_worker_without_terminal_result_on_untouched_workspace_retains_Failed_verdict()
    {
        var outboxDir = CreateTempDirectory();
        var sourceRepo = CreateTempDirectory();
        var worktreeParent = CreateTempDirectory();
        var worktreeDir = Path.Combine(worktreeParent, "workspace");
        try
        {
            InitGitRepository(sourceRepo);
            var baseSha = WorktreeProvisioner.ResolveBaseCommit(sourceRepo, "HEAD");
            Assert.NotNull(baseSha);
            WorktreeProvisioner.Provision(worktreeDir, sourceRepo, "HEAD");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);

            // Streamed worker, but no terminal success record observed, and workspace is completely clean.
            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural, TerminalSuccessObserved: false),
                contract,
                outboxDir,
                responseParser: new FakeResponseParser(response: null),
                worktreePath: worktreeDir,
                worktreeBaseRef: baseSha);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Null(classification.CapturedResponseFile);
            Assert.Contains("Contract not satisfied: 'advice.md' is missing", classification.Reason);
            Assert.DoesNotContain("work possibly on disk", classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outboxDir);
            DirectoryCleanup.DeleteRecursively(sourceRepo);
            DirectoryCleanup.DeleteRecursively(worktreeParent);
        }
    }

    /// <summary>
    /// N2 (#1664 re-review): the commit polarity the rewritten "untouched" test above cannot exercise
    /// — a worker that commits inside its worktree, compared against the REAL resolved base SHA, must
    /// NOT read as untouched. Before this fix, production never populated a real base SHA at all
    /// (<c>WorktreeWorkspaces</c> nulled <c>Worktree</c> in the same expression that stamped
    /// <c>IsWorktree: true</c>), so this arm of <see cref="WorktreeProvisioner.IsWorkspaceUntouched"/>
    /// could never fire and this test is what makes that reachable.
    /// </summary>
    [Fact]
    public void Dead_worker_without_terminal_result_who_committed_over_the_real_base_sha_settles_Indeterminate()
    {
        var outboxDir = CreateTempDirectory();
        var sourceRepo = CreateTempDirectory();
        var worktreeParent = CreateTempDirectory();
        var worktreeDir = Path.Combine(worktreeParent, "workspace");
        try
        {
            InitGitRepository(sourceRepo);
            var baseSha = WorktreeProvisioner.ResolveBaseCommit(sourceRepo, "HEAD");
            Assert.NotNull(baseSha);
            WorktreeProvisioner.Provision(worktreeDir, sourceRepo, "HEAD");

            File.WriteAllText(Path.Combine(worktreeDir, "committed.txt"), "the worker's own commit");
            RunGitProcess(worktreeDir, "add", ".");
            RunGitProcess(worktreeDir, "commit", "-m", "worker commit");

            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural, TerminalSuccessObserved: false),
                contract,
                outboxDir,
                responseParser: new FakeResponseParser(response: null),
                worktreePath: worktreeDir,
                worktreeBaseRef: baseSha);

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outboxDir);
            DirectoryCleanup.DeleteRecursively(sourceRepo);
            DirectoryCleanup.DeleteRecursively(worktreeParent);
        }
    }

    /// <summary>
    /// N5/F6 (#1664 re-review): the single behaviour F6 asked for, previously unmeasured — a worker
    /// that emitted a terminal `result` record reporting FAILURE (not SUCCESS) on an otherwise
    /// UNTOUCHED workspace must settle Indeterminate, not take the dead-worker Failed/retry path.
    /// <see cref="CoreDispatchResult.TerminalResultObserved"/>, not
    /// <see cref="CoreDispatchResult.TerminalSuccessObserved"/>, is what tells
    /// <c>isDeadWorkerWithoutResult</c> a result actually arrived — every other test at this workspace
    /// shape passes <c>TerminalResultObserved: false</c> (the default) and would pass identically
    /// under the old, retired predicate.
    /// </summary>
    [Fact]
    public void A_self_reported_failure_result_on_an_untouched_workspace_settles_Indeterminate_not_Failed()
    {
        var outboxDir = CreateTempDirectory();
        var sourceRepo = CreateTempDirectory();
        var worktreeParent = CreateTempDirectory();
        var worktreeDir = Path.Combine(worktreeParent, "workspace");
        try
        {
            InitGitRepository(sourceRepo);
            var baseSha = WorktreeProvisioner.ResolveBaseCommit(sourceRepo, "HEAD");
            Assert.NotNull(baseSha);
            WorktreeProvisioner.Provision(worktreeDir, sourceRepo, "HEAD");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural, TerminalSuccessObserved: false, TerminalResultObserved: true),
                contract,
                outboxDir,
                responseParser: new FakeResponseParser(response: null),
                worktreePath: worktreeDir,
                worktreeBaseRef: baseSha);

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outboxDir);
            DirectoryCleanup.DeleteRecursively(sourceRepo);
            DirectoryCleanup.DeleteRecursively(worktreeParent);
        }
    }

    /// <summary>
    /// F4 (#1593 review): pins the no-worktree case explicitly, rather than leaving it inferred — a
    /// room with no provisioned worktree passes <c>worktreePath: null</c>
    /// (<c>MutationInterface</c>'s own <c>binding.IsWorktree ? ... : null</c> gate), which
    /// <c>IsWorkspaceUntouched</c> fails closed on immediately, so the untouched-workspace retry carve-out
    /// can never fire for a non-worktree room — it always settles Indeterminate instead, the same as a
    /// mutated workspace would.
    /// </summary>
    [Fact]
    public void Dead_worker_without_terminal_result_on_a_null_worktree_path_settles_Indeterminate()
    {
        var outboxDir = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural, TerminalSuccessObserved: false),
                contract,
                outboxDir,
                responseParser: new FakeResponseParser(response: null),
                worktreePath: null);

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outboxDir);
        }
    }

    [Fact]
    public void Dead_worker_without_terminal_result_on_mutated_workspace_settles_Indeterminate()
    {
        var outboxDir = CreateTempDirectory();
        var worktreeDir = CreateTempDirectory();
        try
        {
            InitGitRepository(worktreeDir);
            // Worker modified a file on disk before dying
            File.WriteAllText(Path.Combine(worktreeDir, "modified.txt"), "stray work");

            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural, TerminalSuccessObserved: false),
                contract,
                outboxDir,
                responseParser: new FakeResponseParser(response: null),
                worktreePath: worktreeDir);

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.Null(classification.CapturedResponseFile);
            Assert.Equal(["advice.md"], classification.UnsatisfiedOutputNames);
            Assert.Contains("work possibly on disk", classification.Reason);
            Assert.Contains("awaiting conductor resolution", classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outboxDir);
            DirectoryCleanup.DeleteRecursively(worktreeDir);
        }
    }

    /// <summary>
    /// P1 (#1664 third re-review): the prior version of this test used a plain <c>git init</c>
    /// directory (not a provisioned worktree — <see cref="WorktreeProvisioner.IsWorktree"/> reads false
    /// for one), and left its ten files entirely untracked under one new directory, which
    /// <c>git status --porcelain</c> collapses to a single <c>?? src/</c> line rather than ten. Both
    /// gaps together made the fixture pass unchanged on the pre-fix tree — see the doc comment N1 left
    /// on this test for what it wrongly claimed. Rewritten to provision a REAL worktree (so the
    /// commits-over-base half of the evidence is in play too) and to COMMIT the ten files first, then
    /// modify them, so <c>git status --porcelain</c> reports ten distinct <c> M …</c> lines — the shape
    /// that actually blows the suffix budget.
    /// </summary>
    [Fact]
    public void Classify_bounds_ten_long_stray_paths_on_a_dirty_worktree_instead_of_throwing()
    {
        var outboxDir = CreateTempDirectory();
        var sourceRepo = CreateTempDirectory();
        var worktreeParent = CreateTempDirectory();
        var worktreeDir = Path.Combine(worktreeParent, "workspace");
        try
        {
            InitGitRepository(sourceRepo);
            var baseSha = WorktreeProvisioner.ResolveBaseCommit(sourceRepo, "HEAD");
            Assert.NotNull(baseSha);
            WorktreeProvisioner.Provision(worktreeDir, sourceRepo, "HEAD");

            var nestedDir = Path.Combine(worktreeDir, "src", "Baton", "Outcomes");
            Directory.CreateDirectory(nestedDir);
            for (var i = 0; i < 10; i++)
            {
                // Each repo-relative path (from worktreeDir) is well past 40 characters.
                File.WriteAllText(Path.Combine(nestedDir, $"OutcomeClassifierScenarioNumber{i:D2}.cs"), "stray work");
            }
            RunGitProcess(worktreeDir, "add", ".");
            RunGitProcess(worktreeDir, "commit", "-m", "add ten scenario files");
            // Now modify every committed file so porcelain reports ten " M …" lines instead of one
            // collapsed "?? src/" directory entry.
            for (var i = 0; i < 10; i++)
            {
                File.AppendAllText(Path.Combine(nestedDir, $"OutcomeClassifierScenarioNumber{i:D2}.cs"), " modified");
            }

            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural, TerminalSuccessObserved: false),
                contract,
                outboxDir,
                responseParser: new FakeResponseParser(response: null),
                worktreePath: worktreeDir,
                worktreeBaseRef: baseSha);

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.NotNull(classification.Reason);
            Assert.True(
                classification.Reason.Length <= 500,
                $"Reason length {classification.Reason.Length} exceeded the 500-character cap.");
            Assert.Contains("OutcomeClassifierScenarioNumber", classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outboxDir);
            DirectoryCleanup.DeleteRecursively(sourceRepo);
            DirectoryCleanup.DeleteRecursively(worktreeParent);
        }
    }

    [Fact]
    public void Nonzero_exit_with_missing_contract_retains_Failed_verdict()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Null(classification.CapturedResponseFile);
            Assert.Null(classification.UnsatisfiedOutputNames);
            Assert.Contains("Worker exited with non-zero code 1.", classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    private static void InitGitRepository(string path)
    {
        RunGitProcess(path, "init");
        RunGitProcess(path, "config", "user.name", "Test");
        RunGitProcess(path, "config", "user.email", "test@test.com");
        File.WriteAllText(Path.Combine(path, "README.md"), "init");
        RunGitProcess(path, "add", ".");
        RunGitProcess(path, "commit", "-m", "initial");
    }

    private static void RunGitProcess(string cwd, params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        using var proc = System.Diagnostics.Process.Start(startInfo);
        proc?.WaitForExit();
    }

    // ---- #1680: the first-verdict canary ----

    [Fact]
    public void Tool_calls_with_zero_hook_verdicts_settle_Indeterminate_never_Succeeded()
    {
        var directory = CreateTempDirectory();
        try
        {
            // A fabricated stream: an otherwise clean, contract-satisfied exit -- would be Succeeded
            // on every other predicate this classifier checks -- reporting 3 tool calls happened while
            // the hook's own ledger recorded zero verdicts for any of them.
            File.WriteAllText(Path.Combine(directory, "plan"), "content");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory,
                toolCallCount: 3, hookVerdictCount: 0);

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.Null(classification.FailureClassification);
            Assert.Contains("hook", classification.Reason);
            Assert.Contains("3 tool call", classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void The_same_stream_with_a_nonzero_verdict_count_leaves_the_classification_unchanged()
    {
        // Red/green control for the test above: identical tool-call count, but the ledger now shows
        // verdicts happened -- the classification must revert to what it would have been without
        // either count supplied at all (Succeeded), proving the canary keys on the verdict count and
        // not merely on the presence of a tool-call count.
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "plan"), "content");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory,
                toolCallCount: 3, hookVerdictCount: 3);

            Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Zero_tool_calls_with_zero_verdicts_does_not_force_Indeterminate()
    {
        // Nothing to verify: a run that never called a gated tool has no hook activity to be missing.
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "plan"), "content");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory,
                toolCallCount: 0, hookVerdictCount: 0);

            Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Omitting_both_counts_leaves_every_other_caller_unaffected()
    {
        // The counts default to null, so a caller that never learned this vendor's concept of a
        // "tool call" or a "hook verdict" -- claude, or any pre-#1680 call site -- gets byte-for-byte
        // today's classification. This is the regression guard for every call site OutcomeClassifier
        // already had before this change.
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "plan"), "content");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void A_timed_out_run_is_not_reclassified_by_the_canary()
    {
        // Scoped to CoreExitReason.Natural -- a timeout is already classified by its own branch above
        // this one in Classify, and must stay Failed/Succeeded on that branch's own terms rather than
        // being intercepted by a check that runs before the reason switch.
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.TimedOut), contract, directory,
                toolCallCount: 3, hookVerdictCount: 0);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void A_cancelled_run_is_not_reclassified_by_the_canary()
    {
        // #1732 review F3's discriminating control, other direction, corrected by N6: this pins
        // ORDERING only -- CancelRequested returns from its own branch far above where the canary now
        // sits (:362), so control never reaches the canary at all on a cancelled result, and this test
        // passes even if the canary's own `Reason == Natural` guard were deleted outright. It would go
        // red if the canary were hoisted back above the CancelRequested return, which is the regression
        // worth having a control for; it does not, and cannot, discriminate the `Natural` guard itself.
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.CancelRequested), contract, directory,
                toolCallCount: 3, hookVerdictCount: 0);

            Assert.Equal(OutcomeVerdict.Cancelled, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void A_quota_refusal_with_a_dead_hook_count_pair_still_classifies_ExhaustedUntil_not_Indeterminate()
    {
        // #1732 review F3: before the move, this exact shape (ExitCode != 0, canary counts present)
        // returned Indeterminate at the canary guard, which sat ahead of the ExitCode != 0 branch and
        // swallowed the automatic retry a quota refusal is owed. Red before this PR's fix (asserted
        // against the pre-move code by hand -- the canary preempted this and returned Indeterminate);
        // green after, because the canary now only runs after this branch has already returned.
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);
            var now = new DateTimeOffset(2026, 7, 30, 15, 0, 0, TimeSpan.Zero);
            var testTime = new TestTimeProvider(now);
            var specimenStderr = "Error: Individual quota reached. Please upgrade your subscription to increase your limits. Resets in 28m40s.";
            var mockClassifier = new TestQuotaClassifier(specimenStderr, FailureClassification.ExhaustedUntil, now.AddMinutes(28).AddSeconds(40));

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural, specimenStderr),
                contract,
                directory,
                mockClassifier,
                testTime,
                toolCallCount: 3,
                hookVerdictCount: 0);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Equal(FailureClassification.ExhaustedUntil, classification.FailureClassification);
            Assert.Equal(now.AddMinutes(28).AddSeconds(40), classification.RetryNotBefore);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    // #1373: which branch Classify takes GIVEN a reading. The reading itself is measured against real
    // git trees in WorktreeProvisionerTests — a double cannot answer "is this tree dirty", but it is
    // exactly the right instrument for "does a mutated reading foreclose the retry", which is what
    // these pin. See Classify's workspaceMutationProbe parameter doc for the split.

    private static Func<string?, string?, WorkspaceMutationReading?> ProbeReturning(WorkspaceMutationReading? reading) =>
        (_, _) => reading;

    [Fact]
    public void Classify_settles_a_timeout_Indeterminate_when_the_workspace_carries_commits_and_changes()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.TimedOut),
                contract,
                directory,
                worktreePath: "C:/rooms/room/workspaces/implement",
                workspaceMutationProbe: ProbeReturning(WorkspaceMutationReading.FromCounts(changedPathCount: 14, newCommitCount: 2)));

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            // Indeterminate carries no FailureClassification, and no capture: null CapturedResponseFile
            // is what makes StateProjector record IndeterminateProducer.ContractFailure, whose resolve
            // grammar (`--reject --reason`) is the one this shape needs.
            Assert.Null(classification.FailureClassification);
            Assert.Null(classification.CapturedResponseFile);

            var reason = classification.Reason!;
            Assert.Contains("2 new commit(s) and 14 changed/untracked path(s)", reason, StringComparison.Ordinal);
            Assert.Contains("baton resolve --reject", reason, StringComparison.Ordinal);
            // Both markers are load-bearing, not phrasing: WorkflowOutcome.IsTimeoutFailure reads the
            // prefix, and StateProjector.BuildConductorResolvedReason strips the trailing clause. The
            // second is asserted as CONTAINED rather than trailing on purpose — WithStderr appends the
            // worker's stderr after it, so an EndsWith here would pin a fixture with no stderr rather
            // than the product (see BuildTimeoutOnMutatedWorkspaceReason's own note on that
            // pre-existing, cross-producer gap).
            Assert.StartsWith("Execution timed out.", reason, StringComparison.Ordinal);
            Assert.Contains("awaiting conductor resolution.", reason, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    // #1945: the three arms below are one condition apart and are read together — the polarity is the
    // point. Same call, same worktree path, same TimedOut result; only CommitsAheadOfRemote moves.

    [Fact]
    public void Classify_settles_a_timeout_FinishedDuringTeardown_when_the_workspace_is_clean_and_pushed()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.TimedOut),
                contract,
                directory,
                worktreePath: "C:/rooms/room/workspaces/implement",
                workspaceMutationProbe: ProbeReturning(WorkspaceMutationReading.FromCounts(
                    changedPathCount: 0, newCommitCount: 1, commitsAheadOfRemote: 0)));

            // Succeeded-shaped, flagged: the lane committed and pushed inside its box and the kill
            // landed in the pre-push hook. Nothing for a conductor to resolve.
            Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
            Assert.True(classification.FinishedDuringTeardown);
            Assert.Null(classification.Reason);
            Assert.Null(classification.FailureClassification);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_keeps_the_timeout_Indeterminate_when_the_workspace_is_ahead_of_the_remote()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.TimedOut),
                contract,
                directory,
                worktreePath: "C:/rooms/room/workspaces/implement",
                workspaceMutationProbe: ProbeReturning(WorkspaceMutationReading.FromCounts(
                    changedPathCount: 0, newCommitCount: 1, commitsAheadOfRemote: 1)));

            // The discriminating control for the arm above: identical but for the one unpushed commit.
            // Committed-but-not-pushed work is exactly what a conductor still has to push by hand, so
            // today's timed-out reading is the correct one.
            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.False(classification.FinishedDuringTeardown);
            Assert.StartsWith("Execution timed out.", classification.Reason!, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_keeps_the_timeout_Indeterminate_when_the_upstream_count_could_not_be_read()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.TimedOut),
                contract,
                directory,
                worktreePath: "C:/rooms/room/workspaces/implement",
                // No upstream configured, a detached HEAD, a git failure: null, never a fabricated 0.
                workspaceMutationProbe: ProbeReturning(WorkspaceMutationReading.FromCounts(
                    changedPathCount: 0, newCommitCount: 1, commitsAheadOfRemote: null)));

            // Fails closed: unmeasured is not "pushed".
            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.False(classification.FinishedDuringTeardown);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_never_reads_a_timeout_on_an_untouched_workspace_as_finished_during_teardown()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.TimedOut),
                contract,
                directory,
                worktreePath: "C:/rooms/room/workspaces/implement",
                // The reading Classify's own #1945 remark says the nesting exists to exclude — pinned
                // here because nothing else would notice if that nesting were flattened.
                workspaceMutationProbe: ProbeReturning(WorkspaceMutationReading.FromCounts(
                    changedPathCount: 0, newCommitCount: 0, commitsAheadOfRemote: 0)));

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.False(classification.FinishedDuringTeardown);
            Assert.Equal("Execution timed out.", classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_settles_a_timeout_Indeterminate_when_the_workspace_could_not_be_read()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.TimedOut),
                contract,
                directory,
                changesTree: true,
                changesTreeWorkingDirectory: "C:/lanes/w1373",
                workspaceMutationProbe: ProbeReturning(WorkspaceMutationReading.Unmeasurable));

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.Contains("could not be read", classification.Reason!, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_keeps_the_retryable_Failed_verdict_for_a_timeout_on_an_unmutated_workspace()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.TimedOut),
                contract,
                directory,
                worktreePath: "C:/rooms/room/workspaces/implement",
                workspaceMutationProbe: ProbeReturning(WorkspaceMutationReading.FromCounts(changedPathCount: 0, newCommitCount: 0)));

            // The discriminating control: same call, same path, only the reading differs. Without this
            // arm the two above would pass against an unconditional Indeterminate settlement.
            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Equal("Execution timed out.", classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_never_probes_a_timeout_that_had_no_workspace_to_leave_work_in()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);
            var probed = false;

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.TimedOut),
                contract,
                directory,
                // No provisioned worktree and not a tree-changing role: F4 (#1593 review) forbids
                // handing this decision the operator's own working directory, so there is no path to
                // probe and the retry stands.
                workspaceMutationProbe: (_, _) =>
                {
                    probed = true;
                    return WorkspaceMutationReading.Unmeasurable;
                });

            Assert.False(probed);
            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_still_succeeds_a_finished_then_hung_timeout_on_a_mutated_workspace()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "plan"), "content");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.TimedOut, TerminalSuccessObserved: true),
                contract,
                directory,
                worktreePath: "C:/rooms/room/workspaces/implement",
                workspaceMutationProbe: ProbeReturning(WorkspaceMutationReading.FromCounts(changedPathCount: 3, newCommitCount: 1)));

            // #1089's guard is upstream of this branch and stays upstream of it: a worker that declared
            // success and satisfied its contract finished, and a mutated tree is what finishing LOOKS
            // like. Settling that Indeterminate would send every clean tree-changing run to a conductor.
            Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_leaves_an_ordinary_non_timeout_failure_retryable_on_a_mutated_workspace()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);
            var probed = false;

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural),
                contract,
                directory,
                worktreePath: "C:/rooms/room/workspaces/implement",
                workspaceMutationProbe: (_, _) =>
                {
                    probed = true;
                    return WorkspaceMutationReading.FromCounts(changedPathCount: 14, newCommitCount: 2);
                });

            // Polarity (#1373 scope): this ruling is about the TIMEOUT arm alone. An exit-1 worker on a
            // mutated workspace retries exactly as it did before, and the probe is never even consulted.
            Assert.False(probed);
            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);

            // Asserted through the real retry predicate rather than on the classification field: a null
            // FailureClassification IS the ordinary retryable shape (RetryEngine.MayRetry defaults it),
            // so pinning the field would have measured a spelling instead of the behaviour that matters.
            var step = new StepState(
                new StepId("implement"),
                StepStatus.Failed,
                new ExecutionId("exec-1"),
                new Dictionary<StepId, ExecutionId>(),
                ConsecutiveFailureCount: 1,
                LatestFailureClassification: classification.FailureClassification,
                LatestFailureReason: classification.Reason);

            Assert.True(Baton.Scheduling.RetryEngine.MayRetry(step, new RetryPolicy(3)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void A_timeout_on_a_mutated_workspace_is_still_recognised_as_a_timeout_by_status_surfaces()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.TimedOut),
                contract,
                directory,
                worktreePath: "C:/rooms/room/workspaces/implement",
                workspaceMutationProbe: ProbeReturning(WorkspaceMutationReading.FromCounts(changedPathCount: 14, newCommitCount: 2)));

            // The prefix is the only signal any surface has for "this was a timeout" — there is no
            // FailureClassification value for one. Pinned through the real reader rather than by
            // re-asserting the literal, so a reword of the sentence fails HERE rather than silently
            // reclassifying every mutated timeout as an ordinary failure downstream.
            var step = new StepState(
                new StepId("implement"),
                StepStatus.Failed,
                new ExecutionId("exec-1"),
                new Dictionary<StepId, ExecutionId>(),
                LatestFailureReason: classification.Reason);

            Assert.True(WorkflowOutcome.IsTimeoutFailure(step));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }
}

