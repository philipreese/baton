using Baton.Tests.TestSupport;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Outcomes;

namespace Baton.Tests.Outcomes;

public class ContractValidatorTests
{
    [Fact]
    public void IsSatisfied_true_when_the_contract_declares_no_outputs()
    {
        var contract = new WorkerContract("worker", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []);

        Assert.True(ContractValidator.IsSatisfied(contract, "/does-not-matter"));
    }

    [Fact]
    public void IsSatisfied_false_when_a_required_output_file_is_missing()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            Assert.False(ContractValidator.IsSatisfied(contract, directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void IsSatisfied_true_when_the_output_file_exists_and_declares_no_condition()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "plan"), "anything");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            Assert.True(ContractValidator.IsSatisfied(contract, directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void IsSatisfied_true_when_the_declared_condition_is_met()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "verdict.json"), """{"status": "approved"}""");
            var condition = new OutputCondition("/status", new JsonScalar.String("approved"));
            var contract = new WorkerContract("worker", [], [new ProducedOutput("verdict.json", condition)], []);

            Assert.True(ContractValidator.IsSatisfied(contract, directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void IsSatisfied_false_when_the_declared_condition_value_does_not_match()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "verdict.json"), """{"status": "needs_revision"}""");
            var condition = new OutputCondition("/status", new JsonScalar.String("approved"));
            var contract = new WorkerContract("worker", [], [new ProducedOutput("verdict.json", condition)], []);

            Assert.False(ContractValidator.IsSatisfied(contract, directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void IsSatisfied_false_when_the_output_file_is_not_valid_json()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "verdict.json"), "not json");
            var condition = new OutputCondition("/status", new JsonScalar.String("approved"));
            var contract = new WorkerContract("worker", [], [new ProducedOutput("verdict.json", condition)], []);

            Assert.False(ContractValidator.IsSatisfied(contract, directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void IsSatisfied_false_when_the_pointer_does_not_resolve()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "verdict.json"), """{"other": "field"}""");
            var condition = new OutputCondition("/status", new JsonScalar.String("approved"));
            var contract = new WorkerContract("worker", [], [new ProducedOutput("verdict.json", condition)], []);

            Assert.False(ContractValidator.IsSatisfied(contract, directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void IsSatisfied_compares_numbers_by_value_not_by_representation()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "score.json"), """{"value": 80}""");
            var condition = new OutputCondition("/value", new JsonScalar.Number(80.0));
            var contract = new WorkerContract("worker", [], [new ProducedOutput("score.json", condition)], []);

            Assert.True(ContractValidator.IsSatisfied(contract, directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void IsSatisfied_requires_all_outputs_when_multiple_are_declared()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "plan"), "anything");
            var contract = new WorkerContract(
                "worker", [], [new ProducedOutput("plan"), new ProducedOutput("review")], []);

            Assert.False(ContractValidator.IsSatisfied(contract, directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// The four ways an output goes unsatisfied must be told apart in the reason, since collapsing
    /// them into one <c>false</c> is the defect #597 exists to fix.
    /// </summary>
    /// <remarks>
    /// <b>Every arm uses the same output name, in its own directory.</b> The first version of this
    /// test gave each arm a different filename — <c>missing.json</c>, <c>invalid.json</c>,
    /// <c>mismatch.json</c> — which made its pairwise <c>NotEqual</c> assertions satisfiable by the
    /// filename alone: an implementation rendering all four cases as <c>'X' is missing</c> passed it
    /// in full, which is exactly the collapse the test is named for. Holding the name constant is
    /// what forces the strings to differ by *kind*. Caught by an independent reviewer.
    /// <para>
    /// The resolved-to-wrong-value and pointer-did-not-resolve arms share a
    /// <see cref="UnsatisfiedOutputReason"/> value, so they are the pair most likely to collapse and
    /// the one the earlier version never compared. They are compared here.
    /// </para>
    /// </remarks>
    [Fact]
    public void ContractValidator_distinguishes_missing_file_invalid_json_and_both_condition_failures()
    {
        const string outputName = "result.json";
        var condition = new OutputCondition("/status", new JsonScalar.String("approved"));
        var contract = new WorkerContract("worker", [], [new ProducedOutput(outputName, condition)], []);
        var missingContract = new WorkerContract("worker", [], [new ProducedOutput(outputName)], []);

        var directories = new List<string>();
        try
        {
            string ClassifyIn(WorkerContract usedContract, string? fileContent)
            {
                var directory = CreateTempDirectory();
                directories.Add(directory);
                if (fileContent is not null)
                {
                    File.WriteAllText(Path.Combine(directory, outputName), fileContent);
                }

                var classification = OutcomeClassifier.Classify(
                    new CoreDispatchResult(0, CoreExitReason.Natural), usedContract, directory);

                Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
                Assert.NotNull(classification.Reason);
                Assert.Contains(outputName, classification.Reason);
                return classification.Reason;
            }

            var missing = ClassifyIn(missingContract, null);
            var notJson = ClassifyIn(contract, "not json");
            var wrongValue = ClassifyIn(contract, """{"status": "needs_revision"}""");
            var didNotResolve = ClassifyIn(contract, """{"other": "value"}""");

            // Each kind says its own thing. These are what make the NotEqual assertions below mean
            // "distinguished by kind" rather than "distinguished by some incidental difference".
            Assert.Contains("is missing", missing);
            Assert.Contains("is not valid JSON", notJson);
            Assert.Contains("resolved to", wrongValue);
            Assert.Contains("did not resolve", didNotResolve);

            // The mismatch arm names both sides of the comparison — the delta is the diagnostic.
            Assert.Contains("needs_revision", wrongValue);
            Assert.Contains("approved", wrongValue);

            Assert.Equal(4, new HashSet<string> { missing, notJson, wrongValue, didNotResolve }.Count);
        }
        finally
        {
            foreach (var directory in directories)
            {
                DirectoryCleanup.DeleteRecursively(directory);
            }
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"contract-validator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// A malformed JSON Pointer is a workflow-authoring fault, and it must be <b>reported</b> rather
    /// than thrown.
    /// </summary>
    /// <remarks>
    /// <c>TryResolvePointer</c> throws <see cref="FormatException"/> for a pointer not starting with
    /// <c>/</c>, and nothing validates <c>OutputCondition.Path</c> when a workflow is parsed. Before
    /// #597 the classifier stopped at the first unsatisfied output, so a malformed pointer on a
    /// *later* output was usually never reached; listing every unsatisfied output removed that
    /// accidental shielding. The escape route mattered: <see cref="OutcomeClassifier.Classify"/>
    /// runs after the process has exited but before its outcome is appended, and
    /// <c>Baton.Cli.Program</c> catches only <c>BatonFlowException</c> — so the throw would abandon the
    /// execution mid-classification and leave a crash-recovery orphan, on every run.
    /// <para>
    /// The first output is deliberately missing: that is what makes the second one reachable only
    /// because the walk continues, which is the exact regression this pins.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_malformed_pointer_on_a_later_output_is_reported_rather_than_thrown()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "second.json"), """{"status":"ok"}""");
            var contract = new WorkerContract(
                "worker",
                [],
                [
                    new ProducedOutput("first.json"),
                    new ProducedOutput("second.json", new OutputCondition("status", new JsonScalar.String("ok"))),
                ],
                []);

            // Neither entry point may throw. IsSatisfied stops at the first failure and Classify
            // does not, so only the second actually reaches the malformed pointer — both are
            // asserted so a later change to either walk cannot reintroduce the escape unnoticed.
            Assert.False(ContractValidator.IsSatisfied(contract, directory));

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.NotNull(classification.Reason);
            Assert.Contains("first.json", classification.Reason);
            Assert.Contains("second.json", classification.Reason);
            Assert.Contains("cannot be evaluated", classification.Reason);

            // Polarity: the same pointer written correctly is satisfied, so the diagnostic above is
            // about the pointer's shape and not about the file or the walk.
            var validContract = new WorkerContract(
                "worker",
                [],
                [new ProducedOutput("second.json", new OutputCondition("/status", new JsonScalar.String("ok")))],
                []);

            Assert.True(ContractValidator.IsSatisfied(validContract, directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// A declared <see cref="OutputSchema"/> makes shape part of contract satisfaction.
    /// Both polarities in one place — a valid verdict document satisfies, a same-named prose file
    /// is a <see cref="UnsatisfiedOutputReason.SchemaViolation"/> carrying the parser's why.
    /// </summary>
    [Fact]
    public void A_schema_declared_output_is_satisfied_by_a_valid_verdict_and_unsatisfied_by_prose()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract(
                "reviewer",
                [],
                [new ProducedOutput("verdict.json", Schema: OutputSchema.ReviewVerdict)],
                []);

            File.WriteAllText(
                Path.Combine(directory, "verdict.json"),
                """
                {"reviewedRef": "branch-x", "decision": "block", "findings": [
                    {"severity": "high", "claim": "off-by-one in pager", "status": "confirmed",
                     "anchor": {"file": "src/P.cs", "line": 42}}
                ]}
                """);
            Assert.True(ContractValidator.IsSatisfied(contract, directory));

            File.WriteAllText(Path.Combine(directory, "verdict.json"), "## Review\nLooks fine to me.");
            var result = ContractValidator.Validate(contract, directory);

            var unsatisfied = Assert.Single(result.UnsatisfiedOutputs);
            Assert.Equal(UnsatisfiedOutputReason.SchemaViolation, unsatisfied.Reason);
            Assert.False(string.IsNullOrWhiteSpace(unsatisfied.Detail));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// A verdict naming no <c>decision</c>, or a word the enum does not have, satisfies this contract:
    /// <c>ReviewVerdictSchema</c> holds the ruling and what refusing it would have cost.
    /// </summary>
    /// <remarks>
    /// The discriminating control is the third document below: a verdict whose finding has no
    /// <c>severity</c> IS refused by the same validator, on the same fixture shape, so the two
    /// acceptances above are about the decision field rather than about a validator that waves every
    /// file through.
    /// </remarks>
    [Fact]
    public void A_verdict_with_no_decision_still_satisfies_the_review_contract()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract(
                "reviewer",
                [],
                [new ProducedOutput("verdict.json", Schema: OutputSchema.ReviewVerdict)],
                []);

            File.WriteAllText(
                Path.Combine(directory, "verdict.json"),
                """
                {"reviewedRef": "branch-x", "findings": [
                    {"severity": "high", "claim": "off-by-one in pager", "status": "confirmed"}
                ]}
                """);

            Assert.Empty(ContractValidator.Validate(contract, directory).UnsatisfiedOutputs);

            // A near-miss word lands identically: the converter reads it as absent rather than
            // throwing, and nothing here refuses that either.
            File.WriteAllText(
                Path.Combine(directory, "verdict.json"),
                """{"reviewedRef": "branch-x", "decision": "approved", "findings": []}""");

            Assert.Empty(ContractValidator.Validate(contract, directory).UnsatisfiedOutputs);

            // The control: a document this schema DOES refuse, so the acceptances above discriminate.
            File.WriteAllText(
                Path.Combine(directory, "verdict.json"),
                """{"reviewedRef": "branch-x", "findings": [{"claim": "x", "status": "confirmed"}]}""");

            Assert.Equal(
                UnsatisfiedOutputReason.SchemaViolation,
                Assert.Single(ContractValidator.Validate(contract, directory).UnsatisfiedOutputs).Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// A parseable-but-hollow document — valid JSON, wrong shape — must fail the schema, not slide
    /// through as "it parsed as JSON". This is the arm that discriminates schema checking from
    /// the NotJson check, which such a file passes.
    /// </summary>
    [Fact]
    public void A_schema_declared_output_that_is_valid_JSON_but_not_the_shape_is_a_SchemaViolation()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "verdict.json"), """{"status": "approved"}""");
            var contract = new WorkerContract(
                "reviewer",
                [],
                [new ProducedOutput("verdict.json", Schema: OutputSchema.ReviewVerdict)],
                []);

            var result = ContractValidator.Validate(contract, directory);

            var unsatisfied = Assert.Single(result.UnsatisfiedOutputs);
            Assert.Equal(UnsatisfiedOutputReason.SchemaViolation, unsatisfied.Reason);
            Assert.Contains("reviewedRef", unsatisfied.Detail);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// The one-report-per-output rule: when a schema'd output also declares a condition and
    /// the schema fails, the condition is not separately evaluated — one output, one diagnostic.
    /// The condition here would itself fail, so a double report is what a regression looks like.
    /// </summary>
    [Fact]
    public void A_failed_schema_reports_once_even_when_the_output_also_declares_a_failing_condition()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "verdict.json"), """{"wrong": "shape"}""");
            var contract = new WorkerContract(
                "reviewer",
                [],
                [
                    new ProducedOutput(
                        "verdict.json",
                        new OutputCondition("/status", new JsonScalar.String("approved")),
                        OutputSchema.ReviewVerdict),
                ],
                []);

            var result = ContractValidator.Validate(contract, directory);

            var unsatisfied = Assert.Single(result.UnsatisfiedOutputs);
            Assert.Equal(UnsatisfiedOutputReason.SchemaViolation, unsatisfied.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// The classifier layer of the schema arm (the #732 review's one MEDIUM: nothing at this layer
    /// would fail if <c>DescribeUnsatisfiedOutput</c>'s SchemaViolation case were deleted or its
    /// Detail dropped). A clean exit 0 with a malformed schema'd output must classify Failed with
    /// the parser's sentence in the Reason; deleting the switch arm faults this test through the
    /// switch's own default throw, and dropping Detail fails the Contains.
    /// </summary>
    [Fact]
    public void Classify_names_the_schema_violation_and_its_detail_in_the_failure_Reason()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "verdict.json"), """{"status": "approved"}""");
            var contract = new WorkerContract(
                "reviewer",
                [],
                [new ProducedOutput("verdict.json", Schema: OutputSchema.ReviewVerdict)],
                []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Indeterminate, classification.Verdict);
            Assert.Contains("'verdict.json' is not a valid document of its declared schema", classification.Reason);
            Assert.Contains("reviewedRef", classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// A schema'd output that passes its schema still has its condition evaluated — declaring a
    /// shape does not exempt an output from condition evaluation. The condition targets a field the schema does not
    /// know, which is also the extra-fields-tolerated claim exercised end to end.
    /// </summary>
    [Fact]
    public void A_passing_schema_does_not_exempt_the_outputs_condition_from_evaluation()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "verdict.json"),
                """{"reviewedRef": "branch-x", "decision": "approve", "findings": [], "gate": "rejected"}""");
            var contract = new WorkerContract(
                "reviewer",
                [],
                [
                    new ProducedOutput(
                        "verdict.json",
                        new OutputCondition("/gate", new JsonScalar.String("approved")),
                        OutputSchema.ReviewVerdict),
                ],
                []);

            var result = ContractValidator.Validate(contract, directory);

            var unsatisfied = Assert.Single(result.UnsatisfiedOutputs);
            Assert.Equal(UnsatisfiedOutputReason.ConditionFailed, unsatisfied.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// A declared <see cref="OutputSchema.Diff"/> output is satisfied by a valid diff
    /// and unsatisfied by garbage with <see cref="UnsatisfiedOutputReason.SchemaViolation"/> carrying
    /// the parser's error sentence. Control: the same garbage file under <see cref="OutputSchema.None"/>
    /// passes either way.
    /// </summary>
    [Fact]
    public void A_diff_schema_declared_output_is_satisfied_by_valid_diff_and_unsatisfied_by_garbage_with_control()
    {
        var directory = CreateTempDirectory();
        try
        {
            var diffContract = new WorkerContract(
                "patcher",
                [],
                [new ProducedOutput("patch.diff", Schema: OutputSchema.Diff)],
                []);

            var noneContract = new WorkerContract(
                "patcher",
                [],
                [new ProducedOutput("patch.diff", Schema: OutputSchema.None)],
                []);

            const string validDiff = "--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+b";
            const string garbageText = "Not a valid diff at all";

            // Valid diff passes under OutputSchema.Diff
            File.WriteAllText(Path.Combine(directory, "patch.diff"), validDiff);
            Assert.True(ContractValidator.IsSatisfied(diffContract, directory));

            // Garbage fails under OutputSchema.Diff carrying parser error sentence
            File.WriteAllText(Path.Combine(directory, "patch.diff"), garbageText);
            var result = ContractValidator.Validate(diffContract, directory);
            var unsatisfied = Assert.Single(result.UnsatisfiedOutputs);
            Assert.Equal(UnsatisfiedOutputReason.SchemaViolation, unsatisfied.Reason);
            Assert.Contains("No hunk header", unsatisfied.Detail);

            // Control: same garbage file passes under OutputSchema.None
            Assert.True(ContractValidator.IsSatisfied(noneContract, directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }
}

