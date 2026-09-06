using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Status;
using Baton.Templates;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton redispatch &lt;room-dir&gt;</c> end to end (#1441): a TERMINAL room produced by a real
/// <c>baton dispatch</c> is redispatched into a fresh room, driven through the exact pump <c>baton
/// dispatch</c>/<c>baton run</c> share, so the inherited binding is exercised for real rather than just
/// asserted in isolation (that half is <see cref="RedispatchBindingTests"/>). Mirrors
/// <see cref="DispatchCommandEndToEndTests"/>'s catalog-pinning and fake-adapter setup.
/// </summary>
// #1524: kept enrolled solely for Console.Error; see SerializedEnvironmentCollection's remarks.
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class RedispatchCommandEndToEndTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter>
        {
            ["fake"] = new ContractOutputWorkerAdapter(satisfyOutputs: true),
            ["fake-noop"] = new ContractOutputWorkerAdapter(satisfyOutputs: false),
            ["fake-fail"] = new ContractOutputWorkerAdapter(satisfyOutputs: false, failureExitCode: 1),
        };

    private readonly IsolatedBatonHome _batonHome = new();
    private readonly IDisposable _catalogScope;

    // Catalog pinning mirrors DispatchCommandEndToEndTests' own #1524 ctor.
    public RedispatchCommandEndToEndTests()
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
    public async Task Redispatching_without_a_spec_reuses_the_parents_prompt_verbatim()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.");
            var parentBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(parentRoom, "bindings.json"), TestContext.Current.CancellationToken);

            var childRoom = Path.Combine(testRoot, "child");
            var options = new RedispatchOptions(parentRoom, childRoom);

            var result = await RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var childBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(childRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal(parentBindings["advise"].PromptTemplate, childBindings["advise"].PromptTemplate);
            Assert.Equal(parentBindings["advise"].Adapter, childBindings["advise"].Adapter);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1518: a bare redispatch never reads a room-side spec artifact at all -- it reuses <c>workflow.json</c>
    /// plus the parent's own already-built <c>bindings.json</c> <c>PromptTemplate</c> verbatim, the same
    /// path <see cref="Redispatching_without_a_spec_reuses_the_parents_prompt_verbatim"/> exercises for a
    /// file-sourced parent. This mirrors that test with a parent dispatched via <c>--spec-text</c> instead,
    /// to pin that nothing on the bare-redispatch path assumes a parent's spec ever lived in a file --
    /// it goes red only because the parent room cannot exist before <c>--spec-text</c> does, not because
    /// redispatch itself has a file-sourced assumption to find.
    /// </summary>
    [Fact]
    public async Task Redispatching_a_room_dispatched_via_spec_text_reuses_its_prompt_verbatim()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = Path.Combine(testRoot, "parent");
            var dispatchOptions = new DispatchOptions(
                "advise", SpecFilePath: null, parentRoom, Adapter: "fake", SpecText: "Weigh the options for X.");
            var dispatchResult = await DispatchCommand.ExecuteAsync(dispatchOptions, Adapters, TestContext.Current.CancellationToken);
            var parentView = WorkflowStatusProjector.Project(dispatchResult.State, dispatchResult.Snapshot, parentRoom);
            await TerminalSentinelWriter.WriteAsync(parentRoom, parentView, TestContext.Current.CancellationToken);

            var parentBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(parentRoom, "bindings.json"), TestContext.Current.CancellationToken);

            var childRoom = Path.Combine(testRoot, "child");
            var options = new RedispatchOptions(parentRoom, childRoom);

            var result = await RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var childBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(childRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal(parentBindings["advise"].PromptTemplate, childBindings["advise"].PromptTemplate);
            Assert.Contains("Weigh the options for X.", childBindings["advise"].PromptTemplate, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Redispatching_with_an_amended_spec_replaces_the_prompt_without_duplicating_output_instructions()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.");

            var amendedSpecPath = Path.Combine(testRoot, "amended.md");
            await File.WriteAllTextAsync(amendedSpecPath, "Weigh the options for Y instead.", TestContext.Current.CancellationToken);

            var childRoom = Path.Combine(testRoot, "child");
            var options = new RedispatchOptions(parentRoom, childRoom, SpecFilePath: amendedSpecPath, Adapter: "fake");

            var result = await RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var childBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(childRoom, "bindings.json"), TestContext.Current.CancellationToken);
            var prompt = childBindings["advise"].PromptTemplate;
            Assert.StartsWith("Weigh the options for Y instead.", prompt);
            Assert.DoesNotContain("Weigh the options for X.", prompt);
            // The role's output instructions must appear exactly once, not once from the role catalog
            // and again from a stale copy carried over in the parent's already-built prompt.
            var instructionCount = prompt.Split("Required outputs:").Length - 1;
            Assert.Equal(1, instructionCount);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1499: the amended-spec path rebuilds through <c>RoleDispatch.Materialize</c>, which knows
    /// nothing of the parent's label -- <c>RedispatchCommand.ExecuteAsync</c> stamps the
    /// inherit-unless-overridden rule on afterward. This is the one inheritance path
    /// <see cref="RedispatchBindingTests"/> cannot reach, since that suite only exercises
    /// <see cref="RedispatchCommand.InheritBinding"/> directly (the no-spec path).
    /// </summary>
    [Fact]
    public async Task A_label_survives_an_amended_spec_redispatch_unless_overridden()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.", label: "env-snapshot lane");

            var amendedSpecPath = Path.Combine(testRoot, "amended.md");
            await File.WriteAllTextAsync(amendedSpecPath, "Weigh the options for Y instead.", TestContext.Current.CancellationToken);

            var inheritedChildRoom = Path.Combine(testRoot, "child-inherited");
            var inheritedResult = await RedispatchCommand.ExecuteAsync(
                new RedispatchOptions(parentRoom, inheritedChildRoom, SpecFilePath: amendedSpecPath, Adapter: "fake"),
                Adapters, TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, inheritedResult.State.Status);
            var inheritedBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(inheritedChildRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal("env-snapshot lane", inheritedBindings["advise"].Label);

            var overriddenChildRoom = Path.Combine(testRoot, "child-overridden");
            var overriddenResult = await RedispatchCommand.ExecuteAsync(
                new RedispatchOptions(parentRoom, overriddenChildRoom, SpecFilePath: amendedSpecPath, Adapter: "fake", Label: "different lane"),
                Adapters, TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, overriddenResult.State.Status);
            var overriddenBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(overriddenChildRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal("different lane", overriddenBindings["advise"].Label);

            var clearedChildRoom = Path.Combine(testRoot, "child-cleared");
            var clearedResult = await RedispatchCommand.ExecuteAsync(
                new RedispatchOptions(parentRoom, clearedChildRoom, SpecFilePath: amendedSpecPath, Adapter: "fake", Label: null, LabelSpecified: true),
                Adapters, TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, clearedResult.State.Status);
            var clearedBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(clearedChildRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Null(clearedBindings["advise"].Label);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// LOW-1 (#1619 second-reader): <c>--workstream</c>'s parity gap with <c>--label</c> --
    /// <see cref="RedispatchCommand.ExecuteAsync"/>'s amended-spec branch (:130-134) duplicates the
    /// same inherit/override/clear rule <see cref="RedispatchCommand.InheritBinding"/> already applies
    /// on the no-spec path, and only the label half of that duplication had an end-to-end test proving
    /// the duplicated line actually does something -- <see cref="RedispatchBindingTests"/> only reaches
    /// <c>InheritBinding</c> directly. Runs under an isolated <c>BatonPaths.Root</c> (see
    /// <see cref="DispatchCommandEndToEndTests.BeginIsolatedBatonHome"/>): a resolved workstream here
    /// writes an actual directory junction on disk, which must not land under the machine's own
    /// <c>~/.baton</c>.
    /// </summary>
    [Fact]
    public async Task A_workstream_survives_an_amended_spec_redispatch_unless_overridden()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        var (tempHome, scope) = DispatchCommandEndToEndTests.BeginIsolatedBatonHome();
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.", workstream: "w1619");

            var amendedSpecPath = Path.Combine(testRoot, "amended.md");
            await File.WriteAllTextAsync(amendedSpecPath, "Weigh the options for Y instead.", TestContext.Current.CancellationToken);

            var inheritedChildRoom = Path.Combine(testRoot, "child-inherited");
            var inheritedResult = await RedispatchCommand.ExecuteAsync(
                new RedispatchOptions(parentRoom, inheritedChildRoom, SpecFilePath: amendedSpecPath, Adapter: "fake"),
                Adapters, TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, inheritedResult.State.Status);
            var inheritedBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(inheritedChildRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal("w1619", inheritedBindings["advise"].Workstream);

            var overriddenChildRoom = Path.Combine(testRoot, "child-overridden");
            var overriddenResult = await RedispatchCommand.ExecuteAsync(
                new RedispatchOptions(parentRoom, overriddenChildRoom, SpecFilePath: amendedSpecPath, Adapter: "fake", Workstream: "w2024"),
                Adapters, TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, overriddenResult.State.Status);
            var overriddenBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(overriddenChildRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal("w2024", overriddenBindings["advise"].Workstream);

            var clearedChildRoom = Path.Combine(testRoot, "child-cleared");
            var clearedResult = await RedispatchCommand.ExecuteAsync(
                new RedispatchOptions(parentRoom, clearedChildRoom, SpecFilePath: amendedSpecPath, Adapter: "fake", Workstream: null, WorkstreamSpecified: true),
                Adapters, TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, clearedResult.State.Status);
            var clearedBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(clearedChildRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Null(clearedBindings["advise"].Workstream);
        }
        finally
        {
            DispatchCommandEndToEndTests.CleanupWorkstreamJunction("w1619", Path.Combine(testRoot, "parent"));
            DispatchCommandEndToEndTests.CleanupWorkstreamJunction("w1619", Path.Combine(testRoot, "child-inherited"));
            DispatchCommandEndToEndTests.CleanupWorkstreamJunction("w2024", Path.Combine(testRoot, "child-overridden"));
            scope.Dispose();
            DirectoryCleanup.DeleteRecursively(testRoot);
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    [Fact]
    public async Task A_blank_label_clears_the_inherited_label_on_an_unchanged_spec_redispatch()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.", label: "env-snapshot lane");

            var childRoom = Path.Combine(testRoot, "child-cleared");
            var options = new RedispatchOptions(parentRoom, childRoom, Label: null, LabelSpecified: true);

            var result = await RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var childBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(childRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Null(childBindings["advise"].Label);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// See spec/baton.md §2 ("`--workstream` inherits the identical way...") for why a bare
    /// <c>baton redispatch</c> with no <c>--workstream</c> flag must still get its own by-workstream
    /// junction rather than just the parent's (<see cref="RedispatchBindingTests"/> pins the
    /// inheritance rule itself). Runs under an isolated <c>BatonPaths.Root</c>
    /// (<see cref="DispatchCommandEndToEndTests.BeginIsolatedBatonHome"/>) rather than the machine's
    /// real <c>~/.baton</c>.
    /// </summary>
    [Fact]
    public async Task Redispatching_with_an_inherited_workstream_still_creates_its_own_junction()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        var (tempHome, scope) = DispatchCommandEndToEndTests.BeginIsolatedBatonHome();
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.", workstream: "w1619");

            var childRoom = Path.Combine(testRoot, "child-inherited");
            var options = new RedispatchOptions(parentRoom, childRoom);

            var result = await RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var childBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(childRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal("w1619", childBindings["advise"].Workstream);

            var childLinkPath = WorkstreamJunctionLinker.ResolveLinkPath("w1619", childRoom);
            Assert.True(Directory.Exists(childLinkPath), $"expected a by-workstream junction at '{childLinkPath}'");
        }
        finally
        {
            // Unlink both junctions (parent's and the redispatched child's) BEFORE the real room
            // directories they point at are removed -- see CleanupWorkstreamJunction's own doc -- while
            // the scope still resolves BatonPaths.ByWorkstream into tempHome.
            DispatchCommandEndToEndTests.CleanupWorkstreamJunction("w1619", Path.Combine(testRoot, "child-inherited"));
            DispatchCommandEndToEndTests.CleanupWorkstreamJunction("w1619", Path.Combine(testRoot, "parent"));
            scope.Dispose();
            DirectoryCleanup.DeleteRecursively(testRoot);
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    /// <summary>
    /// Runs under an isolated <c>BatonPaths.Root</c>
    /// (<see cref="DispatchCommandEndToEndTests.BeginIsolatedBatonHome"/>): the parent dispatch below
    /// (<c>workstream: "w1619"</c>) links its own by-workstream junction as <c>DispatchCommand</c>'s
    /// side effect, even though the child below clears its own workstream and gets none.
    /// </summary>
    [Fact]
    public async Task A_blank_workstream_clears_the_inherited_workstream_on_an_unchanged_spec_redispatch()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        var (tempHome, scope) = DispatchCommandEndToEndTests.BeginIsolatedBatonHome();
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.", workstream: "w1619");

            var childRoom = Path.Combine(testRoot, "child-cleared");
            var options = new RedispatchOptions(parentRoom, childRoom, Workstream: null, WorkstreamSpecified: true);

            var result = await RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var childBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(childRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Null(childBindings["advise"].Workstream);
        }
        finally
        {
            // Only the parent got a junction -- the child's workstream was cleared, so
            // WorkstreamJunctionLinker never created one for "child-cleared". Still resolved through
            // the active scope, before it is disposed.
            DispatchCommandEndToEndTests.CleanupWorkstreamJunction("w1619", Path.Combine(testRoot, "parent"));
            scope.Dispose();
            DirectoryCleanup.DeleteRecursively(testRoot);
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    [Fact]
    public async Task An_explicit_override_wins_over_the_inherited_binding()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.", timeout: TimeSpan.FromMinutes(30));

            var childRoom = Path.Combine(testRoot, "child");
            var options = new RedispatchOptions(parentRoom, childRoom, Timeout: TimeSpan.FromMinutes(99));

            await RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            var childBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(childRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal(TimeSpan.FromMinutes(99), childBindings["advise"].Timeout);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Lineage_is_recorded_naming_the_parent_room_and_its_execution_id()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.");
            var childRoom = Path.Combine(testRoot, "child");

            await RedispatchCommand.ExecuteAsync(new RedispatchOptions(parentRoom, childRoom), Adapters, TestContext.Current.CancellationToken);

            var markerPath = Path.Combine(childRoom, ".baton", BatonPaths.RoomMetadataFileName);
            Assert.True(File.Exists(markerPath));
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken));
            Assert.Equal(parentRoom, doc.RootElement.GetProperty("ParentRoomDirectoryPath").GetString());
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("ParentExecutionId").GetString()));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Redispatching_a_non_terminal_parent_is_refused()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            // Dispatched but no terminal.json written -- DispatchCommand.ExecuteAsync alone never
            // writes one; that is Program.cs's own post-processing (#1356), which this deliberately
            // skips to leave the room looking mid-flight.
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var parentRoom = Path.Combine(testRoot, "parent");
            await DispatchCommand.ExecuteAsync(
                new DispatchOptions("advise", specPath, parentRoom, Adapter: "fake"), Adapters, TestContext.Current.CancellationToken);

            var childRoom = Path.Combine(testRoot, "child");
            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => RedispatchCommand.ExecuteAsync(
                new RedispatchOptions(parentRoom, childRoom), Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("terminal", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(childRoom));

            // #1586: the refusal must diagnose WHY (no terminal sentinel means the room never
            // settled -- genuinely still running, or its engine died mid-wait) and point at the one
            // verb that actually recovers a dead-engine room, rather than only explaining its own
            // refusal (spec/baton.md §3's `baton run --room-dir` recovery, first said by
            // StatusCommand's parked-status line for #1582).
            Assert.Contains("engine died", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(ex.TryInvocation);
            Assert.Contains("baton run", ex.TryInvocation, StringComparison.Ordinal);
            Assert.Contains("--room-dir", ex.TryInvocation, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Redispatching_a_missing_parent_room_is_a_typed_argument_error()
    {
        var missingParent = Path.Combine(Path.GetTempPath(), $"redispatch-missing-{Guid.NewGuid():N}");
        var childRoom = Path.Combine(Path.GetTempPath(), $"redispatch-child-{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<CliArgumentException>(() => RedispatchCommand.ExecuteAsync(
            new RedispatchOptions(missingParent, childRoom), Adapters, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_terminal_but_not_Succeeded_parent_is_redispatched_with_a_warning_not_a_refusal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        var originalError = Console.Error;
        try
        {
            // fake-fail exits 1, so advise's step -- and the workflow -- lands
            // Failed, not Succeeded or Indeterminate, once terminal.json is written for it below.
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.", adapter: "fake-fail");

            using var stderr = new StringWriter();
            Console.SetError(stderr);

            var childRoom = Path.Combine(testRoot, "child");
            var options = new RedispatchOptions(parentRoom, childRoom, Adapter: "fake");
            var result = await RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.Contains("did not succeed", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// N7 (#1664 re-review): see the selection fix's own remarks
    /// (<c>RedispatchCommand.cs</c>, the <c>indeterminateStep</c> lookup just above the Indeterminate
    /// refusal) for why a rejected step can outrank the real target. This fixture puts the rejected
    /// step FIRST in the array specifically to catch that ordering bug: the refusal must still name
    /// the ContractFailure remedy (reject only), not the CapturedResponse one.
    /// </summary>
    [Fact]
    public async Task A_rejected_step_sorted_before_the_pending_ContractFailure_step_does_not_win_the_remedy()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = Path.Combine(testRoot, "parent");
            Directory.CreateDirectory(parentRoom);
            var bindings = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["advise"] = new("fake", new WorkerContract("advise", [], [new ProducedOutput("advice.md")], []), "prompt", TimeSpan.FromMinutes(30)),
            };
            await WorkerBindingConfigWriter.SaveToFileAsync(
                bindings, BatonPaths.RoomBindingsFile(parentRoom), TestContext.Current.CancellationToken);
            await TerminalSentinelWriter.WriteAsync(
                parentRoom,
                new WorkflowStatusView(
                    WorkflowOutcome.Indeterminate,
                    [
                        // Sorted FIRST: a rejected CapturedResponse step — file survives as audit
                        // trail, producer cleared by CaptureResolved.
                        new WorkflowStatusStepView("rejected", "Failed", "exec-1", CapturedResponseFile: ".captured-response.md"),
                        // Sorted SECOND: the room's real pending target.
                        new WorkflowStatusStepView("pending", "Failed", "exec-2", IndeterminateProducerKind: "ContractFailure"),
                    ],
                    [],
                    null),
                TestContext.Current.CancellationToken);

            var childRoom = Path.Combine(testRoot, "child");
            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => RedispatchCommand.ExecuteAsync(
                new RedispatchOptions(parentRoom, childRoom), Adapters, TestContext.Current.CancellationToken));

            Assert.NotNull(ex.TryInvocation);
            Assert.Contains("nothing to accept", ex.TryInvocation, StringComparison.Ordinal);
            Assert.DoesNotContain("--accept-capture | --reject", ex.TryInvocation, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Redispatching_a_composed_template_room_is_refused()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = Path.Combine(testRoot, "parent");
            Directory.CreateDirectory(parentRoom);

            // A two-worker bindings.json is enough to look template-shaped without materializing a
            // real composed template -- redispatch's refusal keys only on bindings.json's own arity.
            var multiWorkerBindings = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["capture"] = new(
                    "git", new WorkerContract("capture", [], [new ProducedOutput("base.txt")], []), "prompt", TimeSpan.FromMinutes(5)),
                ["advise"] = new(
                    "fake", new WorkerContract("advise", [], [new ProducedOutput("advice.md")], []), "prompt", TimeSpan.FromMinutes(30)),
            };
            await WorkerBindingConfigWriter.SaveToFileAsync(
                multiWorkerBindings, BatonPaths.RoomBindingsFile(parentRoom), TestContext.Current.CancellationToken);
            await TerminalSentinelWriter.WriteAsync(
                parentRoom, new WorkflowStatusView(WorkflowOutcome.Succeeded, [], [], null), TestContext.Current.CancellationToken);

            var childRoom = Path.Combine(testRoot, "child");
            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => RedispatchCommand.ExecuteAsync(
                new RedispatchOptions(parentRoom, childRoom), Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("2 workers", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task An_Indeterminate_parent_refuses_bare_redispatch_with_a_diagnosis()
    {
        // #1586 S1: this fixture writes the sentinel by hand rather than driving a producer, so the
        // CONSUMER side of the vocabulary is proven independently of which producer settled it.
        // #1623/#1644 merge: the step now carries a capturedResponseFile, because that is what makes
        // `baton resolve` the RIGHT remedy to name -- see the polarity partner below, where the same
        // Indeterminate room without one must be sent somewhere else entirely.
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = Path.Combine(testRoot, "parent");
            Directory.CreateDirectory(parentRoom);
            var bindings = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["advise"] = new("fake", new WorkerContract("advise", [], [new ProducedOutput("advice.md")], []), "prompt", TimeSpan.FromMinutes(30)),
            };
            await WorkerBindingConfigWriter.SaveToFileAsync(
                bindings, BatonPaths.RoomBindingsFile(parentRoom), TestContext.Current.CancellationToken);
            await TerminalSentinelWriter.WriteAsync(
                parentRoom,
                new WorkflowStatusView(
                    WorkflowOutcome.Indeterminate,
                    [new WorkflowStatusStepView("a", "Failed", "exec-1", CapturedResponseFile: ".captured-response.md")],
                    [],
                    null),
                TestContext.Current.CancellationToken);

            var childRoom = Path.Combine(testRoot, "child");
            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => RedispatchCommand.ExecuteAsync(
                new RedispatchOptions(parentRoom, childRoom), Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("Indeterminate", ex.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(childRoom));

            // F1 (PR #1644 review): the refusal must name the real resolution verb and its flags,
            // not claim one doesn't exist -- #1608 shipped `baton resolve` in the same PR.
            Assert.NotNull(ex.TryInvocation);
            Assert.Contains(
                $"baton resolve {parentRoom} [--execution <id>] --accept-capture | --reject --reason <text>",
                ex.TryInvocation, StringComparison.Ordinal);
            Assert.DoesNotContain("does not exist", ex.TryInvocation, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task An_Indeterminate_parent_without_a_capture_is_refused_but_NOT_sent_to_baton_resolve()
    {
        // #1623/#1644 merge. Polarity partner of the test above, one field apart (no
        // capturedResponseFile): the refusal is unchanged, but the REMEDY must change. Indeterminate
        // has three producers now and `baton resolve` handles only the captured-response one --
        // MutationInterface.RecordCaptureResolutionAsync refuses the other two outright. Naming it
        // here regardless would hand the operator an invocation guaranteed to throw: a dead end in a
        // user-facing string, which is the specific defect this arm exists to catch.
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = Path.Combine(testRoot, "parent");
            Directory.CreateDirectory(parentRoom);
            var bindings = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["advise"] = new(
                    "fake", new WorkerContract("advise", [], [new ProducedOutput("advice.md")], []), "prompt",
                    TimeSpan.FromMinutes(45), Model: "sonnet", WorkingDirectory: "/repo"),
            };
            await WorkerBindingConfigWriter.SaveToFileAsync(
                bindings, BatonPaths.RoomBindingsFile(parentRoom), TestContext.Current.CancellationToken);
            await TerminalSentinelWriter.WriteAsync(
                parentRoom,
                new WorkflowStatusView(
                    WorkflowOutcome.Indeterminate,
                    [new WorkflowStatusStepView("a", "Failed", "exec-1")],
                    [],
                    null),
                TestContext.Current.CancellationToken);

            var childRoom = Path.Combine(testRoot, "child");
            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => RedispatchCommand.ExecuteAsync(
                new RedispatchOptions(parentRoom, childRoom), Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("Indeterminate", ex.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(childRoom));

            // #1623 re-review U1: the remedy must be a reachable command, not "re-dispatch the
            // parent" (which just names the refused invocation itself) -- a fresh `baton dispatch`
            // carrying the parent's own recorded flags forward.
            Assert.NotNull(ex.TryInvocation);
            // #1622 (d): the remedy DOES name `baton resolve` now -- but `--close`, the verb that
            // admits this producer, never the `--accept-capture`/`--reject` pair that still throws
            // for it. Before #1622 no verb admitted it at all, and naming one was the dead end this
            // arm was written to catch; the dead end, not the verb's name, is what it pins.
            Assert.DoesNotContain("--accept-capture", ex.TryInvocation, StringComparison.Ordinal);
            Assert.DoesNotContain("re-dispatch the parent", ex.TryInvocation, StringComparison.Ordinal);
            Assert.Contains("baton dispatch advise --spec <brief>", ex.TryInvocation, StringComparison.Ordinal);
            Assert.Contains("--adapter fake", ex.TryInvocation, StringComparison.Ordinal);
            Assert.Contains("--timeout 45", ex.TryInvocation, StringComparison.Ordinal);
            Assert.Contains("--model sonnet", ex.TryInvocation, StringComparison.Ordinal);
            Assert.Contains("--workspace /repo", ex.TryInvocation, StringComparison.Ordinal);

            // F4 (#1720 review): the remedy must not claim redispatch stays refused after a
            // `--close`. It does not: `--close` leaves the room Terminal/Failed, Program.cs rewrites
            // terminal.json from the fresh view, and the Indeterminate gate above stops firing --
            // pinned end-to-end in ResolveCommandEndToEndTests
            // .Redispatch_no_longer_refuses_a_verify_failed_room_once_it_has_been_closed.
            Assert.Contains($"baton resolve {parentRoom}", ex.TryInvocation, StringComparison.Ordinal);
            Assert.Contains("--close --reason <text>", ex.TryInvocation, StringComparison.Ordinal);
            Assert.Contains("redispatch this room", ex.TryInvocation, StringComparison.Ordinal);
            Assert.DoesNotContain("still refuses this room", ex.TryInvocation, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task The_same_room_Failed_instead_of_Indeterminate_is_redispatched_with_a_warning_not_a_refusal()
    {
        // Polarity partner: identical fixture, one state string apart, proving the refusal above is
        // about Indeterminate specifically and not incidentally about "any non-Succeeded terminal
        // parent" (that's the existing warn-and-proceed test above).
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        var originalError = Console.Error;
        try
        {
            var parentRoom = Path.Combine(testRoot, "parent");
            Directory.CreateDirectory(parentRoom);
            var bindings = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["advise"] = new("fake", new WorkerContract("advise", [], [new ProducedOutput("advice.md")], []), "prompt", TimeSpan.FromMinutes(30)),
            };
            await WorkerBindingConfigWriter.SaveToFileAsync(
                bindings, BatonPaths.RoomBindingsFile(parentRoom), TestContext.Current.CancellationToken);
            await WorkflowDefinitionWriter.SaveToFileAsync(
                new WorkflowDefinition(
                    new WorkflowTemplateId("wf-1"), WorkflowTemplateVersion: 1,
                    Steps: [new WorkflowStepDefinition(new StepId("advise"), "advise", [], ["advice.md"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]),
                Path.Combine(parentRoom, "workflow.json"), TestContext.Current.CancellationToken);
            await TerminalSentinelWriter.WriteAsync(
                parentRoom, new WorkflowStatusView(WorkflowOutcome.Failed, [], [], "some reason"), TestContext.Current.CancellationToken);

            using var stderr = new StringWriter();
            Console.SetError(stderr);

            var childRoom = Path.Combine(testRoot, "child");
            var options = new RedispatchOptions(parentRoom, childRoom, Adapter: "fake");
            var result = await RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.Contains("did not succeed", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1576: before this fix, <c>RedispatchCommand</c>'s amended-spec path called
    /// <c>RoleDispatch.Materialize</c> directly, skipping the spec/grant lint (#1500) entirely — an
    /// amended brief that instructs something the role's grant withholds got no warning at all, unlike
    /// the identical brief passed to a fresh <c>baton dispatch</c>. Mirrors
    /// <see cref="DispatchCommandEndToEndTests.Spec_grant_mismatch_prints_warning_and_proceeds"/>
    /// exactly, but through <c>redispatch --spec</c>'s rebuild path: <c>advise</c> declares no shell/
    /// network grant, so a `gh issue view` line in the amended brief must warn the same way.
    /// </summary>
    [Fact]
    public async Task Redispatching_with_an_amended_spec_that_needs_a_withheld_grant_prints_the_linters_warning()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        var priorError = Console.Error;
        using var capturedError = new StringWriter();
        Console.SetError(capturedError);

        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.");

            var amendedSpecPath = Path.Combine(testRoot, "amended.md");
            await File.WriteAllTextAsync(
                amendedSpecPath, "Please gh issue view 1500\nProvide advice.", TestContext.Current.CancellationToken);

            var childRoom = Path.Combine(testRoot, "child");
            var options = new RedispatchOptions(parentRoom, childRoom, SpecFilePath: amendedSpecPath, Adapter: "fake");

            var result = await RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var errorOutput = capturedError.ToString();
            Assert.Contains("Warning: Spec line 1", errorOutput);
            Assert.Contains("shell", errorOutput);
            Assert.Contains("network", errorOutput);
            Assert.Contains("advise", errorOutput);
        }
        finally
        {
            Console.SetError(priorError);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1576: before this fix, <c>RedispatchOptions</c> had no <c>Attachments</c> field at all --
    /// <c>--attach</c> did not exist on <c>redispatch</c>. Mirrors
    /// <see cref="DispatchCommandEndToEndTests.Dispatching_with_attachments_copies_files_and_lists_them_in_prompt"/>
    /// through the amended-spec redispatch path instead.
    /// </summary>
    [Fact]
    public async Task Redispatching_with_attach_copies_the_file_into_the_room_and_lists_it_in_the_prompt()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.");

            var amendedSpecPath = Path.Combine(testRoot, "amended.md");
            await File.WriteAllTextAsync(amendedSpecPath, "Weigh the options for Y instead.", TestContext.Current.CancellationToken);

            var contextFile = Path.Combine(testRoot, "context.txt");
            await File.WriteAllTextAsync(contextFile, "Extra context", TestContext.Current.CancellationToken);

            var childRoom = Path.Combine(testRoot, "child");
            var options = new RedispatchOptions(
                parentRoom, childRoom, SpecFilePath: amendedSpecPath, Adapter: "fake", Attachments: [contextFile]);

            var result = await RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var attachmentsDir = Path.Combine(childRoom, "artifacts", "attachments");
            Assert.True(File.Exists(Path.Combine(attachmentsDir, "context.txt")));
            Assert.Equal("Extra context", await File.ReadAllTextAsync(Path.Combine(attachmentsDir, "context.txt"), TestContext.Current.CancellationToken));

            var childBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(childRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Contains($"Attached files (in {attachmentsDir}): context.txt", childBindings["advise"].PromptTemplate);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1576: pins <c>RedispatchCommand</c>'s own <c>--attach</c>-without-<c>--spec</c> refusal, added
    /// just above the amended-spec branch -- see that refusal's comment for why the combination makes
    /// no sense, not restated here.
    /// </summary>
    [Fact]
    public async Task Attach_without_spec_is_refused()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.");
            var contextFile = Path.Combine(testRoot, "context.txt");
            await File.WriteAllTextAsync(contextFile, "Extra context", TestContext.Current.CancellationToken);

            var childRoom = Path.Combine(testRoot, "child");
            var options = new RedispatchOptions(parentRoom, childRoom, Attachments: [contextFile]);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("--attach", ex.Message);
            Assert.False(Directory.Exists(childRoom));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1576 second-reader: the shared <c>RoleSpecMaterializer</c> validation is exercised by
    /// <see cref="DispatchCommandEndToEndTests.Dispatching_with_missing_attachment_file_throws_typed_argument_error"/>
    /// through <c>dispatch</c>, but nothing pinned the identical call path reached through
    /// <c>redispatch --spec --attach</c> until now.
    /// </summary>
    [Fact]
    public async Task Redispatching_with_a_missing_attachment_file_throws_typed_argument_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.");

            var amendedSpecPath = Path.Combine(testRoot, "amended.md");
            await File.WriteAllTextAsync(amendedSpecPath, "Weigh the options for Y instead.", TestContext.Current.CancellationToken);
            var missingFile = Path.Combine(testRoot, "nonexistent.txt");

            var childRoom = Path.Combine(testRoot, "child");
            var options = new RedispatchOptions(
                parentRoom, childRoom, SpecFilePath: amendedSpecPath, Adapter: "fake", Attachments: [missingFile]);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("Attached file", ex.Message);
            Assert.Contains("nonexistent.txt", ex.Message);
            Assert.False(Directory.Exists(childRoom));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>Polarity partner of the missing-file test above: two <c>--attach</c> files colliding on the same destination name.</summary>
    [Fact]
    public async Task Redispatching_with_two_attachments_sharing_a_file_name_throws_typed_argument_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.");

            var amendedSpecPath = Path.Combine(testRoot, "amended.md");
            await File.WriteAllTextAsync(amendedSpecPath, "Weigh the options for Y instead.", TestContext.Current.CancellationToken);

            var subDir = Path.Combine(testRoot, "sub");
            Directory.CreateDirectory(subDir);
            var file1 = Path.Combine(testRoot, "doc.txt");
            var file2 = Path.Combine(subDir, "doc.txt");
            await File.WriteAllTextAsync(file1, "Top-level doc", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(file2, "Sub-directory doc", TestContext.Current.CancellationToken);

            var childRoom = Path.Combine(testRoot, "child");
            var options = new RedispatchOptions(
                parentRoom, childRoom, SpecFilePath: amendedSpecPath, Adapter: "fake", Attachments: [file1, file2]);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("doc.txt", ex.Message);
            Assert.Contains("same file name", ex.Message);
            Assert.False(Directory.Exists(childRoom));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1941 review MEDIUM: <c>ResolveSkills</c> is applied on both redispatch paths, but only the
    /// inherit-binding one was tested — deleting <c>skills: ResolveSkills(...)</c> from the amended-spec
    /// path (<c>RedispatchCommand.RebuildFromAmendedSpecAsync</c>) failed nothing, which is precisely
    /// the hole #1686 review F2 found for <c>--max-tool-steps</c> and the reason the shared predicate
    /// exists. All three arms of the rule run through the amended-spec path here: absent inherits, an
    /// empty flag clears, a named one replaces wholesale.
    /// </summary>
    [Fact]
    public async Task An_amended_spec_redispatch_inherits_clears_and_replaces_the_parents_skills()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var library = Path.Combine(testRoot, "library");
            foreach (var name in new[] { "house-style", "thorough-review" })
            {
                Directory.CreateDirectory(Path.Combine(library, name));
                await File.WriteAllTextAsync(
                    Path.Combine(library, name, "SKILL.md"), $"description: {name}",
                    TestContext.Current.CancellationToken);
            }

            using var skillsScope = BatonEnvironmentSnapshot.BeginScope(
                BatonEnvironmentSnapshot.Current with { SkillsPathOverride = library });

            var parentRoom = await DispatchTerminalParentAsync(
                testRoot, "Weigh the options for X.", skills: ["house-style"]);
            var amendedSpecPath = Path.Combine(testRoot, "amended.md");
            await File.WriteAllTextAsync(
                amendedSpecPath, "Weigh the options for Y instead.", TestContext.Current.CancellationToken);

            async Task<IReadOnlyList<string>?> RedispatchSkillsAsync(
                string childName, IReadOnlyList<string>? skills, bool skillsSpecified)
            {
                var childRoom = Path.Combine(testRoot, childName);
                var options = new RedispatchOptions(
                    parentRoom, childRoom, SpecFilePath: amendedSpecPath, Adapter: "fake",
                    Skills: skills, SkillsSpecified: skillsSpecified);

                await RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

                var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                    Path.Combine(childRoom, "bindings.json"), TestContext.Current.CancellationToken);
                return bindings["advise"].Skills;
            }

            Assert.Equal(
                ["house-style"],
                (await RedispatchSkillsAsync("child-inherit", null, skillsSpecified: false))!.ToArray());
            Assert.Null(await RedispatchSkillsAsync("child-clear", null, skillsSpecified: true));
            Assert.Equal(
                ["thorough-review"],
                (await RedispatchSkillsAsync("child-replace", ["thorough-review"], skillsSpecified: true))!.ToArray());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<string> DispatchTerminalParentAsync(
        string testRoot, string spec, string adapter = "fake", TimeSpan? timeout = null, string? label = null,
        string? workstream = null, IReadOnlyList<string>? skills = null)
    {
        var specPath = await WriteSpecAsync(testRoot, spec);
        var roomDirectory = Path.Combine(testRoot, "parent");
        var options = new DispatchOptions(
            "advise", specPath, roomDirectory, Adapter: adapter, Timeout: timeout, Label: label,
            Workstream: workstream, Skills: skills);

        var result = await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

        // DispatchCommand.ExecuteAsync alone never writes terminal.json -- that is Program.cs's own
        // post-processing (#1356) -- so a test driving the command directly reproduces it here to set
        // up a genuinely terminal parent room.
        var view = WorkflowStatusProjector.Project(result.State, result.Snapshot, roomDirectory);
        await TerminalSentinelWriter.WriteAsync(roomDirectory, view, TestContext.Current.CancellationToken);

        return roomDirectory;
    }

    private static async Task<string> WriteSpecAsync(string directory, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "spec.md");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }
}
