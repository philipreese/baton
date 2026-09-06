using System.Text.Json;
using Baton.Domain;
using Baton.Mutation;
using Baton.Status;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>--verify-cmd</c>'s CLI surface (#1882): where it is accepted, where it is refused, what the
/// refusal says, what the review prompt gains, and which execution the step's cost is attributed to.
/// The step's own spawning is pinned in <c>Baton.Tests</c>'s <c>VerifyStepRunnerTests</c>; nothing
/// here launches a process.
/// </summary>
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class DispatchVerifyStepTests : IDisposable
{
    /// <summary>
    /// The shared fixture (#1911) — see <see cref="ModelWrittenVerdictFixture"/> for what it is and
    /// why it is not spelled out per test class.
    /// </summary>
    private const string ModelWrittenVerdict = ModelWrittenVerdictFixture.Json;

    private readonly IsolatedBatonHome _batonHome = new();
    private readonly IDisposable _catalogScope;

    // Pin the shipped catalog, for the reason DispatchCommandEndToEndTests' own constructor states.
    public DispatchVerifyStepTests()
    {
        _catalogScope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Current with
        {
            WorkerRolesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerRoles.json"),
            WorkerTiersPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"),
            WorkflowTemplatesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkflowTemplates.json"),
        });
    }

    public void Dispose()
    {
        _catalogScope.Dispose();
        _batonHome.Dispose();
    }

    [Fact]
    public void The_flag_is_repeatable_and_each_value_is_kept_verbatim()
    {
        var options = DispatchOptionsParser.Parse(
        [
            "review", "--spec", "task.md",
            "--verify-cmd", "dotnet build -warnaserror",
            "--verify-cmd", "dotnet test --minimum-expected-tests 1",
            "--verify-timeout", "5",
        ]);

        Assert.Equal(["dotnet build -warnaserror", "dotnet test --minimum-expected-tests 1"], options.VerifyCommands);
        Assert.Equal(TimeSpan.FromMinutes(5), options.VerifyTimeout);
    }

    [Fact]
    public void No_flag_means_no_verify_step_at_all()
    {
        // Nothing runs unless asked (the operator's 2026-09-05 trigger ruling): never implicit, never
        // inferred from the brief.
        var options = DispatchOptionsParser.Parse(["review", "--spec", "task.md"]);

        Assert.Null(options.VerifyCommands);
        Assert.Null(options.VerifyTimeout);
    }

    [Fact]
    public void A_command_outside_the_allowlist_is_refused_at_parse_time_and_named()
    {
        var ex = Assert.Throws<CliArgumentException>(() => DispatchOptionsParser.Parse(
            ["review", "--spec", "task.md", "--verify-cmd", "dotnet build", "--verify-cmd", "git push origin HEAD"]));

        // The refusal names WHICH of the two commands was wrong -- the good one is not what failed.
        Assert.Contains("git push origin HEAD", ex.Message);
        Assert.DoesNotContain("--verify-cmd dotnet build'", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("ten")]
    [InlineData("1441")]
    public void A_malformed_verify_timeout_is_refused(string value)
    {
        var ex = Assert.Throws<CliArgumentException>(() => DispatchOptionsParser.Parse(
            ["review", "--spec", "task.md", "--verify-cmd", "dotnet build", "--verify-timeout", value]));

        Assert.Contains("--verify-timeout", ex.Message);
    }

    [Fact]
    public async Task A_non_review_role_refuses_the_flag_and_says_it_is_not_the_verify_pixi_task_one()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-verify-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var specPath = Path.Combine(testRoot, "spec.md");
            await File.WriteAllTextAsync(specPath, "spec", TestContext.Current.CancellationToken);

            var options = new DispatchOptions(
                "implement", specPath, Path.Combine(testRoot, "room"),
                VerifyCommands: ["dotnet build"]);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                options, WorkerAdapterRegistry.Default, TestContext.Current.CancellationToken));

            Assert.Contains("--verify-cmd", ex.Message);
            // The disambiguation the contract asks for, in the message the operator actually sees:
            // implement already has verify_pixi_task, and the two must not be conflated.
            Assert.Contains("verify_pixi_task", ex.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_workflow_template_refuses_the_flag_rather_than_discarding_it()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-verify-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var options = new DispatchOptions(
                "implement-review", SpecFilePath: null, Path.Combine(testRoot, "room"),
                VerifyCommands: ["dotnet build"]);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                options, WorkerAdapterRegistry.Default, TestContext.Current.CancellationToken));

            Assert.Contains("--verify-cmd", ex.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_verify_timeout_with_no_verify_cmd_is_refused_because_it_would_bound_nothing()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-verify-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var specPath = Path.Combine(testRoot, "spec.md");
            await File.WriteAllTextAsync(specPath, "spec", TestContext.Current.CancellationToken);

            var options = new DispatchOptions(
                "review", specPath, Path.Combine(testRoot, "room"), VerifyTimeout: TimeSpan.FromMinutes(3));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                options, WorkerAdapterRegistry.Default, TestContext.Current.CancellationToken));

            Assert.Contains("--verify-timeout", ex.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void The_review_prompt_gains_the_results_paragraph_only_when_a_step_ran()
    {
        var review = WorkerRoleCatalog.For("review");

        var withoutStep = RoleDispatch.ToBinding(review, "review the branch");
        var withStep = RoleDispatch.ToBinding(
            review, "review the branch", verifyResultsPath: @"C:\rooms\r1\artifacts\verify-results.md");

        // Polarity in both directions (spec/baton.md §9 states the rule): silence without a step,
        // and with one, the exact path plus the citation requirement.
        Assert.DoesNotContain("verify-results.md", withoutStep.PromptTemplate);
        Assert.Contains(@"C:\rooms\r1\artifacts\verify-results.md", withStep.PromptTemplate);
        Assert.Contains("must cite that file", withStep.PromptTemplate);
        // And it stays ABOVE the outputs block -- context for the review, read before findings form.
        Assert.True(
            withStep.PromptTemplate.IndexOf("verify-results.md", StringComparison.Ordinal)
            < withStep.PromptTemplate.IndexOf("Required outputs:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Runs a real <c>review</c> dispatch through the real pump with a fake worker that writes
    /// <see cref="ModelWrittenVerdict"/>, and hands back the <c>verdict.json</c> that ended up on
    /// disk — the same bytes <c>--notify</c> would carry, since <c>WatchFireService.BuildPayload</c>
    /// deserializes that file verbatim into the payload with no schema in between.
    /// </summary>
    private static async Task<(string VerdictPath, JsonElement Verdict)> DispatchReviewAsync(
        string testRoot, IReadOnlyList<string>? verifyCommands)
    {
        var specPath = Path.Combine(testRoot, "spec.md");
        var fixturePath = Path.Combine(testRoot, "worker-verdict.json");
        var workspace = Path.Combine(testRoot, "workspace");
        var roomDirectory = Path.Combine(testRoot, "room");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(specPath, "review the branch", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(fixturePath, ModelWrittenVerdict, TestContext.Current.CancellationToken);

        var adapters = new Dictionary<string, IWorkerAdapter>(StringComparer.Ordinal)
        {
            ["fake"] = new ContractOutputWorkerAdapter(
                satisfyOutputs: true,
                outputFixtures: new Dictionary<string, string>(StringComparer.Ordinal) { ["verdict.json"] = fixturePath }),
        };

        var options = new DispatchOptions(
            "review", specPath, roomDirectory, Adapter: "fake",
            WorkspaceDirectory: workspace, VerifyCommands: verifyCommands);

        var result = await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken);
        var step = Assert.Single(result.State.Steps);
        var verdictPath = Path.Combine(
            roomDirectory, "artifacts", $"execution_{step.LatestExecutionId}", "verdict.json");

        Assert.True(File.Exists(verdictPath));
        return (verdictPath, JsonDocument.Parse(
            await File.ReadAllBytesAsync(verdictPath, TestContext.Current.CancellationToken)).RootElement.Clone());
    }

    [Fact]
    public async Task A_review_dispatched_without_the_flag_has_the_model_written_instruments_stripped()
    {
        // The majority population, and the one the stamp used to skip entirely: no --verify-cmd, so
        // nothing measured anything, so the field must be ABSENT rather than whatever the worker put
        // there. Read off disk, which is also what --notify carries (WatchFireService.BuildPayload
        // deserializes verdict.json verbatim into the payload).
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-verify-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var (_, verdict) = await DispatchReviewAsync(testRoot, verifyCommands: null);

            Assert.False(verdict.TryGetProperty("instruments", out _));
            // The rest of the worker's verdict is untouched -- this strips a field, it does not rewrite
            // the review.
            Assert.Equal("all good", verdict.GetProperty("summary").GetString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_review_dispatched_with_the_flag_gets_the_engine_value_over_the_model_written_one()
    {
        // The polarity arm: same worker, same fabricated instruments, one flag different. The
        // workspace here is not a Baton checkout, so the step records its own refusal rather than
        // spawning a build -- which is exactly what makes this test CI-safe, and the engine's row is
        // still unmistakably the engine's (the command the operator asked for, no exit code).
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-verify-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var (_, verdict) = await DispatchReviewAsync(testRoot, verifyCommands: ["dotnet build -warnaserror"]);

            var instruments = verdict.GetProperty("instruments");
            var instrument = Assert.Single(instruments.EnumerateArray());
            Assert.Equal("dotnet build -warnaserror", instrument.GetProperty("command").GetString());
            // The model's "dotnet test exited 0" is gone, not merged with or appended to.
            Assert.Equal(JsonValueKind.Null, instrument.GetProperty("exitCode").ValueKind);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1895: dispatches a real <c>review</c> WITH <c>--verify-cmd</c> (so the parent room genuinely
    /// carries both the paragraph and an engine-written <c>instruments</c>), settles it terminal, then
    /// redispatches it bare — the exact path an operator takes to rerun a review. Hands back both
    /// rooms' bindings and the child's own result so each arm can assert the parent as its control:
    /// without that half these tests would pass against a redispatch that inherits everything.
    /// </summary>
    private static async Task<(WorkerBindingConfigEntry Parent, WorkerBindingConfigEntry Child,
        string ChildRoom, CommandResult ChildResult)> DispatchThenRedispatchReviewAsync(
        string testRoot, string? amendedSpecPath = null, string briefText = "review the branch")
    {
        var specPath = Path.Combine(testRoot, "spec.md");
        var fixturePath = Path.Combine(testRoot, "worker-verdict.json");
        var workspace = Path.Combine(testRoot, "workspace");
        var parentRoom = Path.Combine(testRoot, "parent");
        var childRoom = Path.Combine(testRoot, "child");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(specPath, briefText, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(fixturePath, ModelWrittenVerdict, TestContext.Current.CancellationToken);

        var adapters = new Dictionary<string, IWorkerAdapter>(StringComparer.Ordinal)
        {
            ["fake"] = new ContractOutputWorkerAdapter(
                satisfyOutputs: true,
                outputFixtures: new Dictionary<string, string>(StringComparer.Ordinal) { ["verdict.json"] = fixturePath }),
        };

        var parentOptions = new DispatchOptions(
            "review", specPath, parentRoom, Adapter: "fake", WorkspaceDirectory: workspace,
            VerifyCommands: ["dotnet build -warnaserror"]);
        var parentResult = await DispatchCommand.ExecuteAsync(parentOptions, adapters, TestContext.Current.CancellationToken);
        await TerminalSentinelWriter.WriteAsync(
            parentRoom,
            WorkflowStatusProjector.Project(parentResult.State, parentResult.Snapshot, parentRoom),
            TestContext.Current.CancellationToken);

        var childResult = await RedispatchCommand.ExecuteAsync(
            new RedispatchOptions(parentRoom, childRoom, SpecFilePath: amendedSpecPath),
            adapters, TestContext.Current.CancellationToken);

        var parentBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
            Path.Combine(parentRoom, "bindings.json"), TestContext.Current.CancellationToken);
        var childBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
            Path.Combine(childRoom, "bindings.json"), TestContext.Current.CancellationToken);

        return (parentBindings["review"], childBindings["review"], childRoom, childResult);
    }

    [Fact]
    public async Task A_redispatched_review_has_the_model_written_instruments_stripped_too()
    {
        // #1895 arm A: `baton redispatch` runs no verify step at all, so the field can only ever be the
        // model's own -- and `baton watch` registers against any room, so it would ride verbatim into a
        // --notify payload. Asserted BOTH on disk and through WatchFireService.BuildPayload, the
        // notifier's own builder, rather than only through a comment claiming they are the same bytes.
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-verify-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var (_, _, childRoom, childResult) = await DispatchThenRedispatchReviewAsync(testRoot);

            var step = Assert.Single(childResult.State.Steps);
            var verdictPath = Path.Combine(
                childRoom, "artifacts", $"execution_{step.LatestExecutionId}", "verdict.json");
            var verdict = JsonDocument.Parse(
                await File.ReadAllBytesAsync(verdictPath, TestContext.Current.CancellationToken)).RootElement;

            Assert.False(verdict.TryGetProperty("instruments", out _));
            // The rest of the worker's verdict is untouched -- this strips a field, it does not rewrite
            // the review -- which is also the control for "the file was actually read and rewritten".
            Assert.Equal("all good", verdict.GetProperty("summary").GetString());

            var payload = WatchFireService.BuildPayload(
                childRoom, WorkflowStatusProjector.Project(childResult.State, childResult.Snapshot, childRoom));
            Assert.NotNull(payload.Verdict);
            Assert.False(payload.Verdict!.Value.TryGetProperty("instruments", out _));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_bare_redispatch_does_not_inherit_the_parents_verify_results_paragraph()
    {
        // #1895 arm B, whose reasoning is RoleDispatch.WithoutVerifyResultsParagraph's own doc and
        // spec/baton.md §9. Polarity in both directions: the parent's prompt must carry the paragraph,
        // or "the child does not" would be true of a prompt that never had one.
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-verify-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var (parent, child, _, _) = await DispatchThenRedispatchReviewAsync(testRoot);

            Assert.Contains("verify-results.md", parent.PromptTemplate, StringComparison.Ordinal);
            Assert.Contains("must cite that file", parent.PromptTemplate, StringComparison.Ordinal);

            Assert.DoesNotContain("verify-results.md", child.PromptTemplate, StringComparison.Ordinal);
            // The whole paragraph goes, not just the sentence carrying the path: the claim that the
            // engine ran commands is the part that would strand the citation requirement.
            Assert.DoesNotContain("allowlisted commands", child.PromptTemplate, StringComparison.Ordinal);
            Assert.DoesNotContain("must cite that file", child.PromptTemplate, StringComparison.Ordinal);

            // And nothing else was dropped with it -- the brief and the outputs block still arrive.
            Assert.StartsWith("review the branch", child.PromptTemplate, StringComparison.Ordinal);
            Assert.Contains("Required outputs:", child.PromptTemplate, StringComparison.Ordinal);
            Assert.Contains("verdict.json", child.PromptTemplate, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// A brief whose OWN second block opens with the verify-results clause — the operator quoting the
    /// engine's paragraph back at the reviewer. Byte-for-byte what #1911's control arm says must
    /// survive a redispatch; it opens the block (rather than sitting mid-sentence) because that is the
    /// shape the pre-#1911 text-only strip removed.
    /// </summary>
    private const string BriefQuotingTheClause =
        "review the branch\n\n"
        + "Before your first turn the engine ran a set of allowlisted commands for you — that sentence "
        + "is the one the operator wants kept, verbatim, in every rerun of this lane.\n\n"
        + "Check the diff.";

    [Fact]
    public async Task A_brief_that_quotes_the_verify_clause_survives_redispatch_while_the_engines_own_paragraph_goes()
    {
        // #1911 low 2: the strip used to remove ANY block opening with the clause, so an operator who
        // quoted the paragraph in the brief lost it silently on every redispatch. Both directions in
        // one arm: the quoted block is still there afterwards AND the engine's own is not.
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-verify-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var (parent, child, _, _) = await DispatchThenRedispatchReviewAsync(
                testRoot, briefText: BriefQuotingTheClause);

            // Control: the parent really does carry both -- the brief's copy and the engine's own.
            Assert.Equal(2, CountOccurrences(parent.PromptTemplate, VerifyClauseOpening));
            Assert.Contains("verify-results.md", parent.PromptTemplate, StringComparison.Ordinal);

            // Exactly one goes, and it is the engine's: the path-carrying sentence is gone, the
            // operator's brief arrives byte-for-byte as written.
            Assert.Equal(1, CountOccurrences(child.PromptTemplate, VerifyClauseOpening));
            Assert.DoesNotContain("verify-results.md", child.PromptTemplate, StringComparison.Ordinal);
            Assert.DoesNotContain("must cite that file", child.PromptTemplate, StringComparison.Ordinal);
            Assert.StartsWith(BriefQuotingTheClause, child.PromptTemplate, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_prompt_carrying_only_the_briefs_own_copy_of_the_clause_is_returned_unchanged()
    {
        // The idempotence half of the same fix, on a REAL engine-built prompt rather than a hand-rolled
        // string: the child above has no engine paragraph left and still quotes the clause in its brief,
        // so a second strip must be a byte-for-byte no-op. This is the arm that was red before #1911 --
        // the old text-only filter took the operator's block on the second pass.
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-verify-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var (_, child, _, _) = await DispatchThenRedispatchReviewAsync(
                testRoot, briefText: BriefQuotingTheClause);

            // Control: without this the equality below would hold for any prompt with no clause at all.
            Assert.Contains(VerifyClauseOpening, child.PromptTemplate, StringComparison.Ordinal);

            Assert.Equal(child.PromptTemplate, RoleDispatch.WithoutVerifyResultsParagraph(child.PromptTemplate));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// The same quoting brief with nothing after it, so the operator's clause block is the LAST one
    /// before the outputs block — the adjacency the position anchor alone could not tell apart from
    /// the engine's own paragraph (#1911 review, low). It carries no citation phrase, which is the
    /// discriminator that now spares it.
    /// </summary>
    private const string BriefWhoseLastBlockQuotesTheClause =
        "review the branch\n\n"
        + "Before your first turn the engine ran a set of allowlisted commands for you — that sentence "
        + "is the one the operator wants kept, verbatim, in every rerun of this lane.";

    [Fact]
    public async Task A_briefs_quoting_block_survives_redispatch_even_when_it_sits_last_before_the_outputs_block()
    {
        // #1911 review, low: position alone condemned whichever block preceded `Required outputs:`, so
        // this brief -- identical to the arm above minus its trailing block -- lost the operator's
        // sentence on the first redispatch. Two reachable shapes had it: a parent dispatched without
        // --verify-cmd, and the second redispatch of a chain, where the child's brief has become
        // adjacent because the engine's paragraph is already gone.
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-verify-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var (parent, child, _, _) = await DispatchThenRedispatchReviewAsync(
                testRoot, briefText: BriefWhoseLastBlockQuotesTheClause);

            // Control: the parent carries both copies, and the engine's is the one between the brief
            // and the outputs block -- so the brief's copy really is the adjacency-losing candidate
            // once the engine's goes.
            Assert.Equal(2, CountOccurrences(parent.PromptTemplate, VerifyClauseOpening));

            Assert.StartsWith(BriefWhoseLastBlockQuotesTheClause, child.PromptTemplate, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(child.PromptTemplate, VerifyClauseOpening));
            Assert.DoesNotContain("verify-results.md", child.PromptTemplate, StringComparison.Ordinal);
            Assert.DoesNotContain("must cite that file", child.PromptTemplate, StringComparison.Ordinal);

            // And the second pass is a no-op on that same prompt: with the engine's paragraph gone the
            // brief's block is now adjacent, which is exactly the chained-redispatch shape.
            Assert.Equal(child.PromptTemplate, RoleDispatch.WithoutVerifyResultsParagraph(child.PromptTemplate));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>The clause both arms count, spelled once here rather than in each assertion.</summary>
    private const string VerifyClauseOpening =
        "Before your first turn the engine ran a set of allowlisted commands for you";

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public async Task A_spec_amended_redispatch_rebuilds_the_prompt_without_the_paragraph_too()
    {
        // The --spec arm is clean by construction -- RoleSpecMaterializer.Materialize is called with no
        // verify-results path, so the rebuilt prompt never gains the paragraph. Pinned rather than left
        // to a reading of the call site: threading a path through that seam later would reopen the same
        // door on the other half of the verb, and nothing else would fail.
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-verify-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var amendedSpecPath = Path.Combine(testRoot, "amended.md");
            await File.WriteAllTextAsync(
                amendedSpecPath, "review the branch again", TestContext.Current.CancellationToken);

            var (parent, child, _, _) = await DispatchThenRedispatchReviewAsync(testRoot, amendedSpecPath);

            Assert.Contains("verify-results.md", parent.PromptTemplate, StringComparison.Ordinal);
            Assert.DoesNotContain("verify-results.md", child.PromptTemplate, StringComparison.Ordinal);
            Assert.StartsWith("review the branch again", child.PromptTemplate, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void The_verify_step_cost_lands_on_the_first_execution_only_never_on_a_retry_as_well()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"verify-usage-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var sidecar = new VerifyStepReport.Sidecar(
                TotalWallClockMs: 125_400, ResultsBytes: 4096, Commands: [new VerifyInstrument("dotnet build", 0, 125_400)]);
            File.WriteAllText(
                Path.Combine(testRoot, VerifyStepReport.SidecarFileName), VerifyStepReport.SerializeSidecar(sidecar));

            var first = new ExecutionId("exec-1");
            var retry = new ExecutionId("exec-2");
            var t0 = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
            var entries = new List<LogEntry>
            {
                // Deliberately journaled retry-first, so a pass would have to be ordering by TIME
                // rather than by whichever key the dictionary happens to yield first.
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(retry, Pid: 2), t0.AddMinutes(10)),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(retry, 0, CoreExitReason.Natural), t0.AddMinutes(11)),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(first, Pid: 1), t0),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(first, 1, CoreExitReason.Natural), t0.AddMinutes(1)),
            };

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default);

            Assert.Equal(125_400, usage["exec-1"].VerifyStepMs);
            Assert.Equal(4096, usage["exec-1"].VerifyResultsBytes);
            // The step ran once. Reporting it on the retry too would double it in #1849's ledger.
            Assert.Null(usage["exec-2"].VerifyStepMs);
            Assert.Null(usage["exec-2"].VerifyResultsBytes);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void A_first_execution_that_never_exited_passes_the_cost_to_the_next_one_rather_than_dropping_it()
    {
        // The discriminating case the test above cannot see (it journals both executions with exits,
        // so it passes whether or not the exit condition exists): a FIRST execution that started and
        // recorded no exit. That execution gets no usage view at all, so the figures land on the next
        // execution that does have one. spec/baton.md §3 states the condition and which situations
        // reach it; without this arm nothing pins it.
        var testRoot = Path.Combine(Path.GetTempPath(), $"verify-usage-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var sidecar = new VerifyStepReport.Sidecar(
                TotalWallClockMs: 125_400, ResultsBytes: 4096, Commands: [new VerifyInstrument("dotnet build", 0, 125_400)]);
            File.WriteAllText(
                Path.Combine(testRoot, VerifyStepReport.SidecarFileName), VerifyStepReport.SerializeSidecar(sidecar));

            var first = new ExecutionId("exec-1");
            var second = new ExecutionId("exec-2");
            var t0 = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
            var entries = new List<LogEntry>
            {
                // exec-1 starts first and never exits; exec-2 starts later and does.
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(first, Pid: 1), t0),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(second, Pid: 2), t0.AddMinutes(10)),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(second, 0, CoreExitReason.Natural), t0.AddMinutes(11)),
            };

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default);

            // No row at all for the execution that never exited -- which is why the fields cannot be
            // attributed to it, and the reason the projector's own condition is what it is.
            Assert.False(usage.ContainsKey("exec-1"));
            Assert.Equal(125_400, usage["exec-2"].VerifyStepMs);
            Assert.Equal(4096, usage["exec-2"].VerifyResultsBytes);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void A_room_with_no_verify_step_reports_neither_telemetry_field()
    {
        // The control arm for the test above: without the sidecar, the same entries must yield no
        // fields at all -- otherwise that test would pass against a projector that always reports them.
        var testRoot = Path.Combine(Path.GetTempPath(), $"verify-usage-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var executionId = new ExecutionId("exec-1");
            var t0 = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
            var entries = new List<LogEntry>
            {
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), t0),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), t0.AddMinutes(1)),
            };

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default);

            Assert.Null(Assert.Single(usage).Value.VerifyStepMs);
            Assert.Null(Assert.Single(usage).Value.VerifyResultsBytes);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }
}
