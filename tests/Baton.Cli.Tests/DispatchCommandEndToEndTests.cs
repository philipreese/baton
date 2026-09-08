using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Status;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton dispatch &lt;role&gt;</c> end to end (#900): a real shipped catalog role is materialized into
/// a single-step workflow and driven through the exact pump <c>baton run</c> uses, so the outputs the
/// role declares become a contract the engine enforces — satisfied means Succeeded, a silent no-op
/// means Failed. The fake adapter (<see cref="ContractOutputWorkerAdapter"/>) stands in for the worker
/// so no live LLM is needed; the role, its outputs, and the contract are the real ones.
/// </summary>
// #1524: stays enrolled for its Console.Out mutation, not env vars anymore -- see
// SerializedEnvironmentCollection's own remarks.
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class DispatchCommandEndToEndTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter>
        {
            ["fake"] = new ContractOutputWorkerAdapter(satisfyOutputs: true),
            ["fake-noop"] = new ContractOutputWorkerAdapter(satisfyOutputs: false),
        };

    private readonly IsolatedBatonHome _batonHome = new();
    private readonly IDisposable _catalogScope;

    // Pin the shipped catalog. Without this these tests resolve through ResolvePath's middle rung
    // ({BatonPaths.Root}/worker-roles.json) and would silently read an operator's local override on a
    // machine that has one -- the exact hazard WorkerRoleCatalogTests.ShippedDefault documents and
    // guards. An isolated BatonEnvironmentSnapshot.BeginScope (#1524) replaces the process-global env
    // edit this used to be -- built from BatonEnvironmentSnapshot.Current so it layers on top of
    // _batonHome's own HomeOverride scope rather than clobbering it. Templates are pinned too (#1380,
    // finding 7's test): DispatchCommand.MaterializeAsync probes WorkflowTemplateCatalog.All to decide
    // role-vs-template even for a role dispatch.
    public DispatchCommandEndToEndTests()
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
    public async Task Dispatching_a_role_whose_worker_writes_its_declared_output_succeeds()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake");

            var state = (await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken)).State;

            Assert.Equal(WorkflowStatus.Terminal, state.Status);
            var step = Assert.Single(state.Steps);
            Assert.Equal("advise", step.StepId.Value);
            FlowAssert.Succeeded(step);

            // advise declares advice.md; the contract the engine enforced is the role's own.
            var advicePath = Path.Combine(
                roomDirectory, "artifacts", $"execution_{step.LatestExecutionId}", "advice.md");
            Assert.True(File.Exists(advicePath));

            // The dispatch persisted the same files a template run would, so the task is resumable.
            Assert.True(File.Exists(Path.Combine(roomDirectory, "workflow.json")));
            Assert.True(File.Exists(Path.Combine(roomDirectory, "bindings.json")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_role_records_the_session_id_reported_by_its_adapter()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-session-id-{Guid.NewGuid():N}");
        try
        {
            var adapter = new SessionIdEmittingWorkerAdapter("session-from-worker");
            var adapters = new Dictionary<string, IWorkerAdapter> { ["fake"] = adapter };
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");

            await DispatchCommand.ExecuteAsync(
                new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake"),
                adapters,
                TestContext.Current.CancellationToken);

            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(roomDirectory, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal("session-from-worker", bindings["advise"].SessionId);
            Assert.False(bindings["advise"].ResumeSession);
            Assert.Equal(1, adapter.ResolveCallCount);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_role_that_reports_no_session_id_leaves_the_binding_empty()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-session-id-{Guid.NewGuid():N}");
        try
        {
            var adapter = new SessionIdEmittingWorkerAdapter(null);
            var adapters = new Dictionary<string, IWorkerAdapter> { ["fake"] = adapter };
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");

            await DispatchCommand.ExecuteAsync(
                new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake"),
                adapters,
                TestContext.Current.CancellationToken);

            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(roomDirectory, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Null(bindings["advise"].SessionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_role_records_the_latest_session_id_reported_by_an_attempt()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-session-id-latest-{Guid.NewGuid():N}");
        try
        {
            var adapter = new SessionIdEmittingWorkerAdapter("first-session", laterSessionId: "latest-session");
            var adapters = new Dictionary<string, IWorkerAdapter> { ["fake"] = adapter };
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");

            await DispatchCommand.ExecuteAsync(
                new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake"),
                adapters,
                TestContext.Current.CancellationToken);

            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(roomDirectory, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal("latest-session", bindings["advise"].SessionId);
            Assert.Equal(1, adapter.ResolveCallCount);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Session_id_persistence_failure_does_not_hide_the_dispatch_result()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-session-id-write-failure-{Guid.NewGuid():N}");
        var originalError = Console.Error;
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var bindingsPath = Path.Combine(roomDirectory, "bindings.json");
            var adapter = new SessionIdEmittingWorkerAdapter(
                "session-from-worker",
                () =>
                {
                    FileCleanup.EnsureDeleted(bindingsPath);
                    Directory.CreateDirectory(bindingsPath);
                });
            var adapters = new Dictionary<string, IWorkerAdapter> { ["fake"] = adapter };
            using var error = new StringWriter();
            Console.SetError(error);

            var result = await DispatchCommand.ExecuteAsync(
                new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake"),
                adapters,
                TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.Contains("could not record the vendor session id", error.ToString());
            Assert.Contains("the newly reported id was not recorded", error.ToString());
            Assert.Contains("may refuse when no prior session id exists", error.ToString());
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1518: there is no on-disk spec artifact for a file dispatch to land in today -- the spec becomes
    /// <c>PromptTemplate</c> inside <c>bindings.json</c> via <see cref="Baton.Vendors.RoleDispatch.Materialize"/>,
    /// which takes the spec as a plain string. "Identical shape to a file-based dispatch" therefore means
    /// identical <c>PromptTemplate</c> bytes for identical content, whichever of the three sources supplied
    /// it -- this pins that parity for <c>--spec-text</c> against a file carrying the exact same content.
    /// </summary>
    [Fact]
    public async Task Dispatching_with_spec_text_produces_the_same_prompt_as_an_equivalent_spec_file()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            const string specContent = "Weigh the options for X.";

            var specPath = await WriteSpecAsync(testRoot, specContent);
            var fileRoomDirectory = Path.Combine(testRoot, "file-task");
            var fileOptions = new DispatchOptions("advise", specPath, fileRoomDirectory, Adapter: "fake");
            await DispatchCommand.ExecuteAsync(fileOptions, Adapters, TestContext.Current.CancellationToken);
            var fileBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(fileRoomDirectory, "bindings.json"), TestContext.Current.CancellationToken);

            var textRoomDirectory = Path.Combine(testRoot, "text-task");
            var textOptions = new DispatchOptions(
                "advise", SpecFilePath: null, textRoomDirectory, Adapter: "fake", SpecText: specContent);
            var state = (await DispatchCommand.ExecuteAsync(textOptions, Adapters, TestContext.Current.CancellationToken)).State;
            var textBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(textRoomDirectory, "bindings.json"), TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, state.Status);
            // A positive anchor, not just cross-arm equality -- two identically-EMPTY prompts would also
            // satisfy Assert.Equal below, so this pins that the spec content genuinely reached the built
            // prompt on both paths, not just that they happen to match each other.
            Assert.Contains(specContent, textBindings["advise"].PromptTemplate, StringComparison.Ordinal);
            Assert.Equal(fileBindings["advise"].PromptTemplate, textBindings["advise"].PromptTemplate);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1518's second source: <c>--spec -</c> reads the same content off stdin instead of a file --
    /// same byte-parity claim as the <c>--spec-text</c> arm above, via <see cref="Console.SetIn"/> rather
    /// than an actual pipe (the test process's own stdin is already redirected by the test host, so the
    /// <c>Console.IsInputRedirected</c> guard in <see cref="DispatchCommand.ResolveSpecAsync"/> does not
    /// fire here — that guard's own TTY-hang refusal has no automatable repro and is unverified live).
    /// </summary>
    [Fact]
    public async Task Dispatching_with_spec_dash_reads_stdin_and_produces_the_same_prompt_as_a_spec_file()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var priorIn = Console.In;
        try
        {
            const string specContent = "Weigh the options for X.";

            var specPath = await WriteSpecAsync(testRoot, specContent);
            var fileRoomDirectory = Path.Combine(testRoot, "file-task");
            var fileOptions = new DispatchOptions("advise", specPath, fileRoomDirectory, Adapter: "fake");
            await DispatchCommand.ExecuteAsync(fileOptions, Adapters, TestContext.Current.CancellationToken);
            var fileBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(fileRoomDirectory, "bindings.json"), TestContext.Current.CancellationToken);

            Console.SetIn(new StringReader(specContent));
            var stdinRoomDirectory = Path.Combine(testRoot, "stdin-task");
            var stdinOptions = new DispatchOptions(
                "advise", SpecFilePath: null, stdinRoomDirectory, Adapter: "fake", SpecFromStdin: true);
            var state = (await DispatchCommand.ExecuteAsync(stdinOptions, Adapters, TestContext.Current.CancellationToken)).State;
            var stdinBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(stdinRoomDirectory, "bindings.json"), TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, state.Status);
            // Same positive-anchor rationale as the --spec-text arm above -- rules out two
            // identically-empty prompts satisfying the cross-arm equality below.
            Assert.Contains(specContent, stdinBindings["advise"].PromptTemplate, StringComparison.Ordinal);
            Assert.Equal(fileBindings["advise"].PromptTemplate, stdinBindings["advise"].PromptTemplate);
        }
        finally
        {
            Console.SetIn(priorIn);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1518: the spec/grant lint (#1500, <see cref="DispatchSpecLinter"/>) reads the spec as a plain
    /// string, the same string <see cref="Baton.Vendors.RoleDispatch.BuildPrompt"/> consumes -- so it is
    /// source-agnostic already, with no --spec-text-specific code path to diverge. This pins that
    /// directly: both arms actually run, each into its own captured stderr, and the two outputs must be
    /// EQUAL, not just each independently contain the expected substrings -- the substring-only shape a
    /// prior draft of this test used would still pass if the two arms diverged in some way neither
    /// asserted substring happens to cover. Uses <c>advise</c>'s actual grant (no-shell, no-network,
    /// write allowed) -- not a generically "read-only" role, since a write-withheld role (e.g.
    /// <c>patch</c>) would instead take <see cref="Baton.Vendors.RoleDispatch"/>'s audited-worktree
    /// branch against the fake adapter, which needs a real git repo this test does not set up.
    /// </summary>
    [Fact]
    public async Task Dispatching_with_spec_text_produces_the_same_lint_warning_as_the_equivalent_spec_file()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var priorError = Console.Error;
        try
        {
            const string specContent = "Please gh issue view 1500\nProvide advice.";

            using var fileError = new StringWriter();
            Console.SetError(fileError);
            var specPath = await WriteSpecAsync(testRoot, specContent);
            var fileRoomDirectory = Path.Combine(testRoot, "file-task");
            var fileOptions = new DispatchOptions("advise", specPath, fileRoomDirectory, Adapter: "fake");
            var fileState = (await DispatchCommand.ExecuteAsync(fileOptions, Adapters, TestContext.Current.CancellationToken)).State;
            var fileErrorOutput = fileError.ToString();

            using var textError = new StringWriter();
            Console.SetError(textError);
            var textRoomDirectory = Path.Combine(testRoot, "text-task");
            var textOptions = new DispatchOptions(
                "advise", SpecFilePath: null, textRoomDirectory, Adapter: "fake", SpecText: specContent);
            var textState = (await DispatchCommand.ExecuteAsync(textOptions, Adapters, TestContext.Current.CancellationToken)).State;
            var textErrorOutput = textError.ToString();

            Assert.Equal(WorkflowStatus.Terminal, fileState.Status);
            Assert.Equal(WorkflowStatus.Terminal, textState.Status);

            // Positive anchors first, so a broken lint (warns on nothing) doesn't slip through a
            // same-empty-string equality check below.
            Assert.Contains("Warning: Spec line 1", fileErrorOutput);
            Assert.Contains("shell", fileErrorOutput);
            Assert.Contains("network", fileErrorOutput);
            Assert.Contains("advise", fileErrorOutput);
            Assert.Equal(fileErrorOutput, textErrorOutput);
        }
        finally
        {
            Console.SetError(priorError);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>#1518: --spec-text/--spec - are two more spellings of the same refusal --spec &lt;file&gt; already gets on a template.</summary>
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "inline text")]
    public async Task Dispatching_a_template_with_an_inline_spec_source_is_refused(bool fromStdin, string? specText)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var options = new DispatchOptions(
                "implement-review", SpecFilePath: null, Path.Combine(testRoot, "task"),
                SpecText: specText, SpecFromStdin: fromStdin);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));
            Assert.Contains("workflow template", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1518 R6 parity: <c>--output</c>'s retry-invocation line (<see cref="DispatchCommand.ValidateOutputOverride"/>)
    /// must name whichever spec source was actually used -- rendering the null <c>SpecFilePath</c> a
    /// <c>--spec-text</c> dispatch carries would print an unrunnable <c>--spec  --output ...</c>, the
    /// exact #1382 F6 class that field's own comment already names.
    /// </summary>
    [Fact]
    public async Task An_output_collision_on_a_spec_text_dispatch_names_spec_text_in_the_retry_invocation()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions(
                "advise", SpecFilePath: null, roomDirectory, Adapter: "fake",
                SpecText: "Weigh the options for X.", OutputPath: Path.Combine(testRoot, "prompt.txt"));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("--spec-text <text>", ex.TryInvocation, StringComparison.Ordinal);
            Assert.DoesNotContain("--spec  --output", ex.TryInvocation, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1499: <c>--label</c> is stamped onto the dispatched role's own <c>bindings.json</c> entry --
    /// the room-scoped file <see cref="Baton.Cli.Mcp.FleetStatusTool"/> reads it back off, on both of
    /// its own read paths (that half is <c>FleetStatusToolTests</c>'s job, not this one's).
    /// </summary>
    [Fact]
    public async Task Dispatching_with_a_label_persists_it_onto_the_roles_bindings_entry()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake", Label: "env-snapshot lane");

            await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(roomDirectory, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal("env-snapshot lane", bindings["advise"].Label);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_with_no_label_leaves_the_bindings_entry_unlabeled()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake");

            await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(roomDirectory, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Null(bindings["advise"].Label);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1619: <c>--workstream</c> is stamped onto the dispatched role's own <c>bindings.json</c> entry,
    /// the same mechanism <see cref="Dispatching_with_a_label_persists_it_onto_the_roles_bindings_entry"/>
    /// pins for <c>--label</c> (that half is <c>FleetStatusToolTests</c>'s job, not this one's). Runs
    /// under an isolated <c>BatonPaths.Root</c> (see <see cref="BeginIsolatedBatonHome"/>) because a
    /// non-null <c>Workstream</c> makes <c>DispatchCommand</c> create a real by-workstream junction as
    /// a side effect, and that must never land in the machine's actual <c>~/.baton</c>.
    /// </summary>
    [Fact]
    public async Task Dispatching_with_a_workstream_persists_it_onto_the_roles_bindings_entry()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var (tempHome, scope) = BeginIsolatedBatonHome();
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake", Workstream: "w1619");

            await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(roomDirectory, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal("w1619", bindings["advise"].Workstream);
        }
        finally
        {
            // Unlink before tearing down -- see CleanupWorkstreamJunction's own doc for why the order
            // matters -- while the scope (and so BatonPaths.ByWorkstream) still resolves into tempHome.
            DispatchCommandEndToEndTests.CleanupWorkstreamJunction("w1619", Path.Combine(testRoot, "task"));
            scope.Dispose();
            DirectoryCleanup.DeleteRecursively(testRoot);
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    [Fact]
    public async Task Dispatching_with_no_workstream_leaves_the_bindings_entry_ungrouped()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake");

            await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(roomDirectory, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Null(bindings["advise"].Workstream);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1619's navigational half: dispatching with <c>--workstream</c> also creates a junction under
    /// <c>BatonPaths.ByWorkstream/&lt;slug&gt;/&lt;room-name&gt;</c> pointing at the real room
    /// directory — even though <paramref name="options"/>'s <c>RoomDirectoryPath</c> here lives under a
    /// throwaway test root rather than <c>BatonPaths.Rooms</c>, since <c>WorkstreamJunctionLinker</c>
    /// links whatever room directory it is handed. Runs under an isolated <c>BatonPaths.Root</c> (see
    /// <see cref="BeginIsolatedBatonHome"/>) rather than the machine's real <c>~/.baton</c>.
    /// </summary>
    [Fact]
    public async Task Dispatching_with_a_workstream_creates_a_by_workstream_junction_to_the_room()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var (tempHome, scope) = BeginIsolatedBatonHome();
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake", Workstream: "w1619");

            await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            var linkPath = WorkstreamJunctionLinker.ResolveLinkPath("w1619", roomDirectory);
            Assert.True(Directory.Exists(linkPath), $"expected a by-workstream junction at '{linkPath}'");
            Assert.True(
                File.Exists(Path.Combine(linkPath, "bindings.json")),
                "the junction must resolve into the real room directory's own files");
        }
        finally
        {
            // Unlink before the real room directory is removed -- see CleanupWorkstreamJunction's own
            // doc for why the order matters here too.
            CleanupWorkstreamJunction("w1619", Path.Combine(testRoot, "task"));
            scope.Dispose();
            DirectoryCleanup.DeleteRecursively(testRoot);
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    /// <summary>
    /// HIGH-1 (#1619 second-reader): the junction's own name used to be the room's leaf name alone,
    /// which collides whenever an explicit <c>--room-dir</c> under two different parents shares a leaf
    /// -- exactly the pattern every invoking harness uses (<c>docs/agents/invoking-baton.md</c>). Two
    /// rooms named "lane" under different roots, dispatched into the same workstream, must each get
    /// their own junction that resolves back into their own room, never into each other's.
    /// </summary>
    [Fact]
    public async Task Dispatching_two_rooms_with_the_same_leaf_name_under_one_workstream_each_get_their_own_junction()
    {
        var testRootA = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-a-{Guid.NewGuid():N}");
        var testRootB = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-b-{Guid.NewGuid():N}");
        var (tempHome, scope) = BeginIsolatedBatonHome();
        try
        {
            var specPathA = await WriteSpecAsync(testRootA, "Weigh the options for X.");
            var roomDirectoryA = Path.Combine(testRootA, "lane");
            var optionsA = new DispatchOptions("advise", specPathA, roomDirectoryA, Adapter: "fake", Workstream: "w1619");
            await DispatchCommand.ExecuteAsync(optionsA, Adapters, TestContext.Current.CancellationToken);

            var specPathB = await WriteSpecAsync(testRootB, "Weigh the options for Y.");
            var roomDirectoryB = Path.Combine(testRootB, "lane");
            var optionsB = new DispatchOptions("advise", specPathB, roomDirectoryB, Adapter: "fake", Workstream: "w1619");
            await DispatchCommand.ExecuteAsync(optionsB, Adapters, TestContext.Current.CancellationToken);

            var linkPathA = WorkstreamJunctionLinker.ResolveLinkPath("w1619", roomDirectoryA);
            var linkPathB = WorkstreamJunctionLinker.ResolveLinkPath("w1619", roomDirectoryB);

            Assert.NotEqual(linkPathA, linkPathB);
            Assert.True(Directory.Exists(linkPathA), $"expected a by-workstream junction at '{linkPathA}'");
            Assert.True(Directory.Exists(linkPathB), $"expected a by-workstream junction at '{linkPathB}'");

            Assert.Equal(
                BatonPaths.RecordKey(roomDirectoryA),
                BatonPaths.RecordKey(new DirectoryInfo(linkPathA).LinkTarget!));
            Assert.Equal(
                BatonPaths.RecordKey(roomDirectoryB),
                BatonPaths.RecordKey(new DirectoryInfo(linkPathB).LinkTarget!));

            var bindingsThroughA = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(linkPathA, "bindings.json"), TestContext.Current.CancellationToken);
            var bindingsThroughB = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(linkPathB, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Contains("for X", bindingsThroughA["advise"].PromptTemplate, StringComparison.Ordinal);
            Assert.Contains("for Y", bindingsThroughB["advise"].PromptTemplate, StringComparison.Ordinal);
        }
        finally
        {
            CleanupWorkstreamJunction("w1619", Path.Combine(testRootA, "lane"));
            CleanupWorkstreamJunction("w1619", Path.Combine(testRootB, "lane"));
            scope.Dispose();
            DirectoryCleanup.DeleteRecursively(testRootA);
            DirectoryCleanup.DeleteRecursively(testRootB);
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    /// <summary>
    /// MED-1 (#1619 second-reader): a discriminating control for the class's own "never fails the
    /// dispatch" contract (<see cref="WorkstreamJunctionLinker.CreateIfRequested"/>'s doc). Pre-occupying
    /// the exact link name with a plain file (not a directory) makes <c>mklink /J</c> itself refuse --
    /// the mklink-exit-code warning branch, not the class's own catch clause -- and the dispatch must
    /// still reach Terminal, with the failure surfaced on stderr rather than swallowed.
    /// </summary>
    [Fact]
    public async Task A_junction_that_cannot_be_created_warns_on_stderr_without_failing_the_dispatch()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var (tempHome, scope) = BeginIsolatedBatonHome();
        var originalError = Console.Error;
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var linkPath = WorkstreamJunctionLinker.ResolveLinkPath("w1619", roomDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            await File.WriteAllTextAsync(linkPath, "occupied", TestContext.Current.CancellationToken);

            using var stderr = new StringWriter();
            Console.SetError(stderr);

            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake", Workstream: "w1619");
            var result = await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.Contains("could not create the by-workstream link", stderr.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(roomDirectory, "bindings.json")), "the room itself must still be usable");
        }
        finally
        {
            Console.SetError(originalError);
            var linkPath = WorkstreamJunctionLinker.ResolveLinkPath("w1619", Path.Combine(testRoot, "task"));
            if (File.Exists(linkPath))
            {
                FileCleanup.Delete(linkPath);
            }

            scope.Dispose();
            DirectoryCleanup.DeleteRecursively(testRoot);
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    /// <summary>
    /// Runs under an isolated <c>BatonPaths.Root</c> (see <see cref="BeginIsolatedBatonHome"/>): without
    /// it, this assertion reads the machine's real <c>~/.baton/by-workstream</c>, which starts failing
    /// the moment an operator anywhere has actually used <c>--workstream</c> for real on this machine --
    /// a test that fails because the feature it covers succeeded.
    /// </summary>
    [Fact]
    public async Task Dispatching_with_no_workstream_creates_no_by_workstream_directory()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var (tempHome, scope) = BeginIsolatedBatonHome();
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake");

            await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.False(Directory.Exists(BatonPaths.ByWorkstream));
        }
        finally
        {
            scope.Dispose();
            DirectoryCleanup.DeleteRecursively(testRoot);
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    /// <summary>
    /// spec/baton.md §8's writer: <c>baton dispatch</c> registers the room into
    /// <see cref="RoomRegistryStore"/> keyed on its resolved workspace, not the process cwd -- the two
    /// can differ (<c>--workspace</c>), and it is exactly that difference the registry exists to close
    /// (<see cref="FleetStatusToolTests.RegistryEntry_OutsideEveryScannedRoot_IsStillFoundByFleetStatus"/>
    /// is the reader-side half of the same invariant).
    /// </summary>
    [Fact]
    public async Task Dispatching_registers_the_room_under_its_workspace_as_the_project_root()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var workspace = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-workspace-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(workspace);
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for Y.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake", WorkspaceDirectory: workspace);

            await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(
                BatonPaths.RoomRegistryFile, TestContext.Current.CancellationToken);
            var entry = Assert.Single(entries, e => e.RoomPath == BatonPaths.RecordKey(roomDirectory));
            Assert.Equal(BatonPaths.RecordKey(workspace), entry.ProjectRoot);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    [Fact]
    public async Task Dispatching_a_role_whose_worker_writes_nothing_fails_the_contract()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            // Exits 0 but produces no advice.md — the floor a per-role output exists to catch.
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake-noop");

            var state = (await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken)).State;

            var step = Assert.Single(state.Steps);
            Assert.Equal(StepStatus.Failed, step.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_an_unknown_role_is_a_typed_argument_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "spec");
            var options = new DispatchOptions("no-such-role", specPath, Path.Combine(testRoot, "task"));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));
            Assert.Contains("no-such-role", ex.Message);
            Assert.Contains("run 'baton templates'", ex.TryInvocation, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_role_without_a_spec_is_a_typed_argument_error_naming_the_fix()
    {
        // #1382 F2: the highest-traffic dispatch rejection -- 'baton dispatch <role>' with no --spec --
        // must carry a Try line an invoking agent can follow literally.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var options = new DispatchOptions("advise", SpecFilePath: null, Path.Combine(testRoot, "task"));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));
            Assert.Equal("baton dispatch advise --spec <spec-file>", ex.TryInvocation);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task An_unreadable_catalog_is_a_typed_argument_error_not_a_crash()
    {
        // A typo'd env override or a hand-broken worker-roles.json must exit cleanly, not dump an
        // unhandled JsonException; before the broadened catch (see DispatchCommand) this escaped
        // Program's boundary as exit 127.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var badCatalog = Path.Combine(testRoot, "worker-roles.json");
            await File.WriteAllTextAsync(badCatalog, "{ not valid json", TestContext.Current.CancellationToken);
            using var badCatalogScope = BatonEnvironmentSnapshot.BeginScope(
                BatonEnvironmentSnapshot.Current with { WorkerRolesPathOverride = badCatalog });

            var specPath = await WriteSpecAsync(testRoot, "spec");
            var options = new DispatchOptions("advise", specPath, Path.Combine(testRoot, "task"));

            await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_with_a_missing_spec_file_is_a_typed_argument_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var options = new DispatchOptions(
                "advise", Path.Combine(testRoot, "does-not-exist.md"), Path.Combine(testRoot, "task"));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));
            Assert.Contains("does-not-exist.md", ex.Message, StringComparison.Ordinal);
            Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1518 second-reader: pins the precedence between two things wrong at once -- a missing spec
    /// source is what makes the invocation invalid in the first place, so it must be reported ahead of
    /// a secondary <c>--output</c> collision, not the other way around -- the spec-content check inside
    /// <c>DispatchCommand.MaterializeRoleAsync</c> runs before its <c>--output</c> validation for
    /// exactly this reason. This test is what would catch a future reordering silently flipping it.
    /// </summary>
    [Fact]
    public async Task A_missing_spec_file_is_reported_ahead_of_an_unrelated_output_collision()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var options = new DispatchOptions(
                "advise", Path.Combine(testRoot, "does-not-exist.md"), Path.Combine(testRoot, "task"),
                OutputPath: Path.Combine(testRoot, "prompt.txt"));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));
            Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("--output", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_role_with_output_override_copies_artifact_to_output_path()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var customOutputPath = Path.Combine(testRoot, "custom-output.md");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake", OutputPath: customOutputPath);

            var state = (await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken)).State;

            Assert.Equal(WorkflowStatus.Terminal, state.Status);
            Assert.True(File.Exists(customOutputPath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_role_with_a_timeout_override_records_it_in_bindings_json()
    {
        // #1442: the override must land in the RECORDED bindings.json, not just influence the live
        // run in memory -- WorkerBindingConfigEntry.Timeout (not workflow.json's WorkflowDefinition,
        // which deliberately keeps a worker's timeout off the frozen step, see RoleDispatch's own doc)
        // is what the engine actually resolves the per-execution timeout from.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions(
                "advise", specPath, roomDirectory, Adapter: "fake", Timeout: TimeSpan.FromMinutes(99));

            var state = (await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken)).State;

            Assert.Equal(WorkflowStatus.Terminal, state.Status);
            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(roomDirectory, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal(TimeSpan.FromMinutes(99), bindings["advise"].Timeout);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Omitting_the_timeout_flag_keeps_the_role_s_own_catalog_timeout()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake");

            await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(roomDirectory, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal(WorkerRoleCatalog.For("advise").Timeout, bindings["advise"].Timeout);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_timeout_override_above_the_2h_caution_threshold_is_a_stderr_warning_not_a_refusal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalError = Console.Error;
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions(
                "advise", specPath, roomDirectory, Adapter: "fake", Timeout: TimeSpan.FromMinutes(180));

            using var stderrCapture = new StringWriter();
            Console.SetError(stderrCapture);
            var state = (await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken)).State;
            Console.SetError(originalError);

            Assert.Equal(WorkflowStatus.Terminal, state.Status);
            Assert.Contains("Warning", stderrCapture.ToString(), StringComparison.Ordinal);
            Assert.Contains("--timeout", stderrCapture.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_template_with_a_timeout_override_is_a_typed_argument_error()
    {
        // Mirrors --output's template refusal — why in spec/baton.md §2.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var options = new DispatchOptions(
                "implement-review", SpecFilePath: null, Path.Combine(testRoot, "task"), Timeout: TimeSpan.FromMinutes(30));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));
            Assert.Contains("--timeout", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Output_pointing_at_an_existing_directory_survives_and_still_reaches_terminal()
    {
        // R3 (#1354/#1380, finding 3): the red test for the copy crashing before Program's
        // terminal-sentinel write. File.Copy(src, existingDirectoryPath, overwrite: true) throws
        // (UnauthorizedAccessException on Windows) -- which does not derive from
        // BatonFlowException, so before the fix this escaped ExecuteAsync raw. The run must still reach
        // Terminal and ExecuteAsync must still return normally, since that return is what lets Program
        // go on to write terminal.json.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalError = Console.Error;
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var existingDirectoryAsDestination = Path.Combine(testRoot, "already-a-directory");
            Directory.CreateDirectory(existingDirectoryAsDestination);
            var options = new DispatchOptions(
                "advise", specPath, roomDirectory, Adapter: "fake", OutputPath: existingDirectoryAsDestination);

            using var stderrCapture = new StringWriter();
            Console.SetError(stderrCapture);
            var result = await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);
            Console.SetError(originalError);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var step = Assert.Single(result.State.Steps);
            FlowAssert.Succeeded(step);
            Assert.Contains("Could not copy", stderrCapture.ToString());

            // The declared output the copy tried to move still exists where the engine wrote it,
            // regardless of the copy's own failure. --output renames the role's primary declared output
            // to its own filename (RoleDispatch's outputOverride), so the real artifact is named after
            // the destination, not "advice.md".
            var declaredOutputName = Path.GetFileName(existingDirectoryAsDestination);
            var realOutputPath = Path.Combine(
                roomDirectory, "artifacts", $"execution_{step.LatestExecutionId}", declaredOutputName);
            Assert.True(File.Exists(realOutputPath));
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Without_output_the_printed_fact_names_the_artifacts_directory_not_a_fabricated_file_path()
    {
        // R4 (#1354/#1380, finding 4): the prior line printed a per-execution file path
        // (room/artifacts/advice.md) that never exists -- real outputs land one level deeper, under
        // room/artifacts/execution_<id>/advice.md, a path not known until the run actually happens.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake");

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            var result = await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);
            Console.SetOut(originalOut);

            var printed = consoleOutput.ToString();
            var artifactsDirectory = Path.Combine(roomDirectory, "artifacts");
            Assert.Contains($"Artifacts directory: {artifactsDirectory}", printed);
            Assert.DoesNotContain("Output path:", printed);

            // The printed directory is genuinely the parent of where the real output landed.
            var step = Assert.Single(result.State.Steps);
            var realOutputPath = Path.Combine(artifactsDirectory, $"execution_{step.LatestExecutionId}", "advice.md");
            Assert.True(File.Exists(realOutputPath));
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_role_prints_its_least_privilege_grant_profile_before_the_run_starts()
    {
        // #1355: the invoking agent needs this line to relay the actual grant to its own permission
        // layer honestly -- review is read-shaped (write_files: false) on the "fake" adapter, which
        // WorkerAdapterRegistry.Default does not know, so ToBinding never flips the audited branch and
        // the printed line reflects the role's plain catalog grant.
        //
        // F2 needs the bound adapter to actually consume a grant (IPermissionGrantTranslator) for the
        // line to print at all, so this test registers GrantConsumingContractOutputWorkerAdapter under
        // "fake" rather than the class-level Adapters -- ContractOutputWorkerAdapter deliberately sits
        // outside that population so the many other dispatch tests never pay for grant refusal checks.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Review the change.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("review", specPath, roomDirectory, Adapter: "fake");
            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["fake"] = new GrantConsumingContractOutputWorkerAdapter(satisfyOutputs: true),
            };

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken);
            Console.SetOut(originalOut);

            // #1456: review now carries a read-only-scoped shell grant (spec/baton.md §9), so the
            // negated-arms baseline this test used to assert no longer describes it -- DescribeGrant
            // spells out the scope rather than printing a bare "shell" that would understate it.
            Assert.Contains(
                // #1683 F1 dropped `git grep*` from this list; see spec/baton.md §9 for why. What this
                // arm is for: the line reports the ALLOW patterns only, as it always has -- neither
                // `denied_shell_command_patterns` nor #1683's `denied_shell_option_tokens` appears here,
                // so it is a ceiling, not the full grant.
                "Grant: read, no-write, shell (scoped: git diff*, git log*, git show*, git blame*, "
                + "git status*, git rev-parse*, git merge-base*, git ls-files*, "
                + "git branch --list*, gh pr view*, gh pr diff*, gh pr checks*, gh issue view*), no-network",
                consoleOutput.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_shell_and_network_granting_role_prints_shell_and_network_not_the_negations()
    {
        // F3: the two tests above only ever assert the negated arms (no-write / no-shell / no-network)
        // for DescribeGrant's shell/network categories -- someone could flip those two ternaries to
        // always emit the negation and every existing test would stay green. "implement" is the
        // catalog's one role that grants both (WorkerRoles.json), so dispatching it is the control that
        // discriminates in the dangerous direction.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            // #1623: "implement" is also the catalog's one role carrying a VerifyPixiTask
            // -- with no workspace override, DispatchCommand defaults to
            // Directory.GetCurrentDirectory() (this test assembly's own output directory), and pixi
            // walks UP parent directories looking for a manifest, which would find and run THIS repo's
            // real, multi-minute gates-quiet. An isolated workspace with its own minimal, fast,
            // passing fixture manifest keeps the verify spawn real without that hazard --
            // DispatchTemplateEndToEndTests.InitGitWorkspaceAsync's own comment explains the shape.
            var workspace = Path.Combine(testRoot, "workspace");
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "pixi.toml"),
                """
                [workspace]
                name = "verify-fixture"
                version = "0.1.0"
                channels = []
                platforms = ["win-64"]

                [tasks]
                gates-quiet = { cmd = "cmd /c exit 0" }
                """,
                TestContext.Current.CancellationToken);

            var specPath = await WriteSpecAsync(testRoot, "Make the bounded change.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("implement", specPath, roomDirectory, Adapter: "fake");
            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["fake"] = new GrantConsumingContractOutputWorkerAdapter(satisfyOutputs: true),
            };

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken, workspaceDirectory: workspace);
            Console.SetOut(originalOut);

            Assert.Contains("Grant: read, write, shell, network", consoleOutput.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_implement_against_a_foreign_workspace_without_gates_quiet_settles_Succeeded_and_still_delivers_output()
    {
        // #1702, the measured defect: a foreign (non-baton) workspace's pixi.toml has no gates-quiet
        // task -- "implement"'s own baked-in verify_pixi_task (WorkerRoles.json). Before this fix, the
        // engine ran `pixi run gates-quiet` anyway, got "command not found", settled the room
        // Indeterminate, and --output was never written (CopyPrimaryOutputToOverride's own
        // Status==Succeeded gate skipped a step whose terminal status the verify failure had flipped
        // to Failed). This is the CLI-level end-to-end proof both halves are fixed: the room settles
        // Succeeded, and the worker's declared output lands at --output regardless.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var workspace = Path.Combine(testRoot, "workspace");
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "pixi.toml"),
                """
                [workspace]
                name = "foreign-fixture"
                version = "0.1.0"
                channels = []
                platforms = ["win-64"]

                [tasks]
                check = { cmd = "cmd /c exit 0" }
                """,
                TestContext.Current.CancellationToken);

            var specPath = await WriteSpecAsync(testRoot, "Make the bounded change.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var outputPath = Path.Combine(testRoot, "changes-out.md");
            var options = new DispatchOptions("implement", specPath, roomDirectory, Adapter: "fake", OutputPath: outputPath);
            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["fake"] = new GrantConsumingContractOutputWorkerAdapter(satisfyOutputs: true),
            };

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            var result = await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken, workspaceDirectory: workspace);
            Console.SetOut(originalOut);

            var step = Assert.Single(result.State.Steps);
            Assert.Equal(StepStatus.Succeeded, step.Status);
            Assert.False(step.IndeterminateAwaitingResolution);
            Assert.Equal("task absent: gates-quiet", step.VerifyNotRunReason);
            Assert.True(File.Exists(outputPath), $"expected the declared output copied to '{outputPath}'.");
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1708 M2, red-first: the shape #1702's own PR body describes ("got <c>command not found</c>",
    /// which reads like an absent MANIFEST rather than an absent task) — a foreign workspace carrying no
    /// pixi manifest anywhere above it. Between #1708 H2 and M2 the engine spawned the real
    /// <c>pixi run gates-quiet</c> there, it failed, and the step settled <c>Indeterminate</c>: #1702's
    /// measured symptom, restored. It must settle the same way the missing-task shape above does —
    /// <c>VerifyNotRun</c>, <c>Succeeded</c>, output delivered.
    /// </summary>
    [Fact]
    public async Task Dispatching_implement_against_a_workspace_that_is_not_a_pixi_project_settles_Succeeded_and_still_delivers_output()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            // Deliberately NO pixi.toml and no pyproject.toml, here or in any ancestor (the temp root
            // is not inside a pixi project) -- that absence is the whole fixture.
            var workspace = Path.Combine(testRoot, "workspace");
            Directory.CreateDirectory(workspace);

            var specPath = await WriteSpecAsync(testRoot, "Make the bounded change.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var outputPath = Path.Combine(testRoot, "changes-out.md");
            var options = new DispatchOptions("implement", specPath, roomDirectory, Adapter: "fake", OutputPath: outputPath);
            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["fake"] = new GrantConsumingContractOutputWorkerAdapter(satisfyOutputs: true),
            };

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            var result = await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken, workspaceDirectory: workspace);
            Console.SetOut(originalOut);

            var step = Assert.Single(result.State.Steps);
            Assert.Equal(StepStatus.Succeeded, step.Status);
            Assert.False(step.IndeterminateAwaitingResolution);
            Assert.Equal("no pixi project: gates-quiet", step.VerifyNotRunReason);
            Assert.True(File.Exists(outputPath), $"expected the declared output copied to '{outputPath}'.");
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1708 M1: the widened half of #1702's <c>--output</c> fix, pinned. The copy is keyed on the step
    /// having executed and the file existing — not on a natural exit — so a CANCELLED execution's
    /// half-written report is delivered too. That is deliberate (spec/baton.md §3): a partial report is
    /// better evidence than none, and the room word and process exit code both still say the run did not
    /// succeed, so nothing here reads as a pass.
    /// </summary>
    [Fact]
    public async Task Dispatching_implement_that_is_cancelled_mid_write_still_delivers_the_partial_output()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var workspace = Path.Combine(testRoot, "workspace");
            Directory.CreateDirectory(workspace);

            var specPath = await WriteSpecAsync(testRoot, "Make the bounded change.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var outputPath = Path.Combine(testRoot, "changes-out.md");
            var options = new DispatchOptions("implement", specPath, roomDirectory, Adapter: "fake", OutputPath: outputPath);
            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["fake"] = new PartialOutputThenBlockingWorkerAdapter(),
            };

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            // The cancel is what ends this test, not the delay -- so the delay only has to outlast a
            // process spawn plus one `echo` under build-lock contention. Generous on purpose: a short
            // window's failure mode is File.Exists(outputPath) == false, which reads as a regression in
            // the very thing this pins rather than as the flake it would be.
            cancellation.CancelAfter(TimeSpan.FromSeconds(10));

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            var result = await DispatchCommand.ExecuteAsync(options, adapters, cancellation.Token, workspaceDirectory: workspace);
            Console.SetOut(originalOut);

            var step = Assert.Single(result.State.Steps);
            Assert.NotEqual(StepStatus.Succeeded, step.Status);
            Assert.True(
                File.Exists(outputPath),
                $"expected the partial output copied to '{outputPath}' even though the execution was cancelled.");
            Assert.Contains("half-written", await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_implement_whose_verify_actually_runs_and_goes_red_still_settles_Indeterminate_but_still_delivers_output()
    {
        // #1702 item 3's discriminating control: verify RUNNING and going red must still fail the room
        // exactly as before -- what changed is only the "never ran at all" case above. Before this fix
        // the same CopyPrimaryOutputToOverride gate (Status == Succeeded) ALSO dropped the output here,
        // even though the worker wrote it before the (later, engine-run) verify step ever ran.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var workspace = Path.Combine(testRoot, "workspace");
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "pixi.toml"),
                """
                [workspace]
                name = "verify-red-fixture"
                version = "0.1.0"
                channels = []
                platforms = ["win-64"]

                [tasks]
                gates-quiet = { cmd = "cmd /c exit 1" }
                """,
                TestContext.Current.CancellationToken);

            var specPath = await WriteSpecAsync(testRoot, "Make the bounded change.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var outputPath = Path.Combine(testRoot, "changes-out.md");
            var options = new DispatchOptions("implement", specPath, roomDirectory, Adapter: "fake", OutputPath: outputPath);
            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["fake"] = new GrantConsumingContractOutputWorkerAdapter(satisfyOutputs: true),
            };

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            var result = await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken, workspaceDirectory: workspace);
            Console.SetOut(originalOut);

            var step = Assert.Single(result.State.Steps);
            Assert.Equal(StepStatus.Failed, step.Status);
            Assert.True(step.IndeterminateAwaitingResolution);
            Assert.NotNull(step.IndeterminateReason);
            Assert.True(File.Exists(outputPath), $"expected the declared output copied to '{outputPath}' even though verify failed.");
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Output_ending_in_a_directory_separator_is_refused_before_any_fact_is_printed()
    {
        // R6 (#1354/#1380, finding 8) -- see ValidateOutputOverride's own doc for what a trailing
        // separator would otherwise cost. Must be refused before the room directory even exists.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions(
                "advise", specPath, roomDirectory, Adapter: "fake",
                OutputPath: Path.Combine(testRoot, "reports") + Path.DirectorySeparatorChar);

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));
            Console.SetOut(originalOut);

            Assert.Contains("names no file", ex.Message);
            Assert.Contains("pass a file path instead of a directory", ex.TryInvocation, StringComparison.Ordinal);
            Assert.Empty(consoleOutput.ToString());
            Assert.False(Directory.Exists(roomDirectory), "a refused dispatch must not have created the room directory");
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Output_on_a_template_dispatch_is_refused_up_front_like_spec_already_is()
    {
        // R5 (#1354/#1380, finding 7) -- see MaterializeTemplateAsync's own comment for why. Mirrors
        // the existing --spec-on-a-template refusal exercised elsewhere in this class.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions(
                "implement-review", SpecFilePath: null, roomDirectory, Adapter: "fake",
                OutputPath: Path.Combine(testRoot, "out.md"));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("--output", ex.Message);
            Assert.Contains("remove the --output flag", ex.TryInvocation, StringComparison.Ordinal);
            Assert.False(Directory.Exists(roomDirectory), "a refused dispatch must not have created the room directory");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task The_suggested_output_rename_actually_clears_the_collision_it_follows_from()
    {
        // #1382 F10.1 (see DispatchOptionsParserTests for what this class of test guards): the
        // --output-collides-with-declared-output refusal's suggested
        // "baton dispatch <role> --spec <spec-file> --output <different-file-name>" shape, proven to
        // actually clear the check it follows from.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Review the diff.");
            var roomDirectory = Path.Combine(testRoot, "task");
            // "review" declares report.md (primary, --output's target) and verdict.json (secondary) --
            // renaming the primary onto the secondary's own name is the Skip(1) collision this refusal
            // guards.
            var collidingOptions = new DispatchOptions(
                "review", specPath, roomDirectory, Adapter: "fake", OutputPath: Path.Combine(testRoot, "verdict.json"));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(collidingOptions, Adapters, TestContext.Current.CancellationToken));
            Assert.Contains("collides with role 'review'", ex.Message, StringComparison.Ordinal);
            Assert.Equal($"baton dispatch review --spec {specPath} --output <different-file-name>", ex.TryInvocation);

            var correctedRoomDirectory = Path.Combine(testRoot, "task-corrected");
            var correctedOptions = new DispatchOptions(
                "review", specPath, correctedRoomDirectory, Adapter: "fake", OutputPath: Path.Combine(testRoot, "renamed-report.md"));

            var state = (await DispatchCommand.ExecuteAsync(correctedOptions, Adapters, TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, state.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_role_prints_none_discovered_when_no_skills_are_found()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            // #2110: the scan arm is what this test pins, so the role's own default is opted out --
            // with it attached the roster prints the declared line instead.
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake", NoDefaultSkills: true);

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);
            Console.SetOut(originalOut);

            Assert.Contains("Skills: none discovered", consoleOutput.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_with_attachments_copies_files_and_lists_them_in_prompt()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Analyze the attached context.");
            var file1 = Path.Combine(testRoot, "doc.txt");
            var file2 = Path.Combine(testRoot, "notes.md");
            await File.WriteAllTextAsync(file1, "Context document 1", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(file2, "Context notes 2", TestContext.Current.CancellationToken);

            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions(
                "advise", specPath, roomDirectory, Adapter: "fake",
                Attachments: [file1, file2]);

            var state = (await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, state.Status);

            var attachmentsDir = Path.Combine(roomDirectory, "artifacts", "attachments");
            Assert.True(File.Exists(Path.Combine(attachmentsDir, "doc.txt")));
            Assert.True(File.Exists(Path.Combine(attachmentsDir, "notes.md")));
            Assert.Equal("Context document 1", await File.ReadAllTextAsync(Path.Combine(attachmentsDir, "doc.txt"), TestContext.Current.CancellationToken));
            Assert.Equal("Context notes 2", await File.ReadAllTextAsync(Path.Combine(attachmentsDir, "notes.md"), TestContext.Current.CancellationToken));

            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(roomDirectory, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Contains($"Attached files (in {attachmentsDir}): doc.txt, notes.md", bindings["advise"].PromptTemplate);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_with_missing_attachment_file_throws_typed_argument_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Analyze context.");
            var missingFile = Path.Combine(testRoot, "nonexistent.txt");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions(
                "advise", specPath, roomDirectory, Adapter: "fake",
                Attachments: [missingFile]);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("Attached file", ex.Message);
            Assert.Contains("nonexistent.txt", ex.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_with_two_attachments_sharing_a_file_name_throws_typed_argument_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Analyze context.");
            var subDir = Path.Combine(testRoot, "sub");
            Directory.CreateDirectory(subDir);
            var file1 = Path.Combine(testRoot, "doc.txt");
            var file2 = Path.Combine(subDir, "doc.txt");
            await File.WriteAllTextAsync(file1, "Top-level doc", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(file2, "Sub-directory doc", TestContext.Current.CancellationToken);

            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions(
                "advise", specPath, roomDirectory, Adapter: "fake",
                Attachments: [file1, file2]);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("doc.txt", ex.Message);
            Assert.Contains("same file name", ex.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_template_with_attach_throws_typed_argument_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var file1 = Path.Combine(testRoot, "doc.txt");
            Directory.CreateDirectory(testRoot);
            await File.WriteAllTextAsync(file1, "doc", TestContext.Current.CancellationToken);

            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions(
                "implement-review", SpecFilePath: null, roomDirectory, Adapter: "fake",
                Attachments: [file1]);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("--attach", ex.Message);
            Assert.Contains("remove the --attach flag", ex.TryInvocation);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Spec_grant_mismatch_prints_warning_and_proceeds()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var priorError = Console.Error;
        using var capturedError = new StringWriter();
        Console.SetError(capturedError);

        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Please gh issue view 1500\nProvide advice.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake");

            var state = (await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, state.Status);

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

    [Fact]
    public async Task Dispatching_a_role_prints_discovered_skill_names_when_present()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            // #2110: scan arm, so the role default is opted out (see the test above).
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake-skills", NoDefaultSkills: true);
            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["fake-skills"] = new ContractOutputWorkerAdapter(
                    satisfyOutputs: true,
                    capabilities: new List<WorkerCapabilityItem>
                    {
                        new("artifact-design", "skill", "Design artifacts"),
                        new("run-checks", "skill", "Run checks"),
                        new("/compact", "command", "Compact command"),
                    }),
            };

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken);
            Console.SetOut(originalOut);

            Assert.Contains("Skills: artifact-design, run-checks", consoleOutput.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_role_prints_the_grant_line_before_the_skills_line()
    {
        // #1512 M6: the two single-role skill tests above only ever Assert.Contains on the whole
        // buffer, which passes regardless of position -- neither tests the ordering the preamble
        // actually claims ("Grant" lines, then "Skills" lines). This asserts the ordered sequence
        // directly. Needs GrantConsumingContractOutputWorkerAdapter (IPermissionGrantTranslator) so a
        // Grant line prints at all -- the plain ContractOutputWorkerAdapter the other skills tests use
        // sits outside that population (see M5's comment in DispatchCommand.cs).
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Review the change.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("review", specPath, roomDirectory, Adapter: "fake");
            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["fake"] = new GrantConsumingContractOutputWorkerAdapter(satisfyOutputs: true),
            };

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken);
            Console.SetOut(originalOut);

            var output = consoleOutput.ToString();
            var grantIndex = output.IndexOf("Grant:", StringComparison.Ordinal);
            // #2110: `review` carries a default skill, so the line is the declared form
            // (`Skills (declared): baton-review`); the ordering claim is the same either way.
            var skillsIndex = output.IndexOf("Skills (declared):", StringComparison.Ordinal);
            Assert.True(grantIndex >= 0, "expected a Grant line");
            Assert.True(skillsIndex >= 0, "expected a Skills line");
            Assert.True(grantIndex < skillsIndex, "the Grant line must print before the Skills line");
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_with_list_capabilities_prints_capabilities_and_succeeds()
    {
        // #1500 second-reader LOW-4: the prior version of this test asserted only Terminal status. It
        // did not pin the exit code, that anything was printed (despite the test's own name), or that
        // no room directory was created — all three hold today, but nothing regressed if they stopped.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "capabilities-room");
        var priorOut = Console.Out;
        using var capturedOut = new StringWriter();
        Console.SetOut(capturedOut);

        try
        {
            var options = new DispatchOptions("", SpecFilePath: null, roomDirectory, ListCapabilities: true);
            var result = await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.Equal(RunExitCode.Succeeded, RunExitCodeResolver.Resolve(result));
            Assert.Null(result.RoomDirectoryPath);
            Assert.False(Directory.Exists(roomDirectory));

            var printed = capturedOut.ToString();
            Assert.Contains("Adapters, Models & Efforts:", printed);
            Assert.Contains("Role Timebox Defaults:", printed);
        }
        finally
        {
            Console.SetOut(priorOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_throwing_heuristic_is_caught_and_the_dispatch_still_reaches_terminal()
    {
        // #1500 second-reader MED-4: "WARN, never fail" is asserted in three places (DispatchSpecLinter's
        // class doc, docs/dispatch.md, the PR body) and was enforced by none — DispatchCommand called
        // Lint with no try/catch, so the first heuristic that throws would refuse a dispatch the lint
        // promised only to warn about. This proves the wrapping catches it: the room still reaches
        // Terminal, and stderr names the skip rather than a raw stack trace reaching the caller.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var priorError = Console.Error;
        using var capturedError = new StringWriter();
        Console.SetError(capturedError);

        DispatchSpecLinter.HeuristicsOverrideForTests =
        [
            new SpecGrantHeuristic(
                "boom", GrantCategory.Shell, _ => throw new InvalidOperationException("deliberate test failure"), "throws"),
        ];

        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Provide advice on the design.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake");

            var result = await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);

            var errorOutput = capturedError.ToString();
            Assert.Contains("spec/grant lint failed and was skipped", errorOutput);
            Assert.Contains("InvalidOperationException", errorOutput);
        }
        finally
        {
            DispatchSpecLinter.HeuristicsOverrideForTests = null;
            Console.SetError(priorError);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<string> WriteSpecAsync(string directory, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "spec.md");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }

    /// <summary>
    /// #1619: isolates <see cref="BatonPaths.Root"/> (and so <see cref="BatonPaths.ByWorkstream"/>)
    /// into a throwaway temp directory for the duration of a test that dispatches with
    /// <c>--workstream</c> -- otherwise <see cref="WorkstreamJunctionLinker"/> writes a real directory
    /// junction under whatever machine runs the test's actual <c>~/.baton/by-workstream</c>, exactly
    /// the per-run isolation <see cref="BatonEnvironmentSnapshot.BeginScope"/> exists to give (see that
    /// type's own remarks, and <c>FleetStatusToolTests</c>'s identical pattern). The returned scope
    /// must stay undisposed -- and the returned <c>TempHome</c> undeleted -- until after any
    /// <see cref="CleanupWorkstreamJunction"/> call in the caller's own <c>finally</c> block, since that
    /// helper resolves <see cref="BatonPaths.ByWorkstream"/> through whichever scope is active. Shared
    /// with <c>RedispatchCommandEndToEndTests</c>, which redispatches against a workstream too.
    /// </summary>
    internal static (string TempHome, IDisposable Scope) BeginIsolatedBatonHome()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"baton-workstream-test-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        return (tempHome, scope);
    }

    /// <summary>
    /// Unlinks a by-workstream junction created by <see cref="WorkstreamJunctionLinker"/> (and its
    /// now-empty slug/root parents, so a sibling test's "nothing was created" assertion never trips
    /// over an empty directory a prior test left behind) -- non-recursive
    /// <see cref="Directory.Delete(string, bool)"/> only, since deleting a junction whose target has
    /// already been removed throws <see cref="UnauthorizedAccessException"/> even non-recursively, so
    /// this must run before the real room directory it points at is deleted. Resolves
    /// <see cref="BatonPaths.ByWorkstream"/> through whatever <see cref="BatonEnvironmentSnapshot"/>
    /// scope is active on the caller -- see <see cref="BeginIsolatedBatonHome"/>. Takes the room's own
    /// directory path, not its leaf name, because <see cref="WorkstreamJunctionLinker.ResolveLinkPath"/>
    /// keys the link's own name on a hash of that full path (HIGH-1) -- the leaf alone no longer
    /// determines where the junction landed.
    /// </summary>
    internal static void CleanupWorkstreamJunction(string slug, string roomDirectoryPath)
    {
        var slugDir = Path.Combine(BatonPaths.ByWorkstream, slug);
        var linkPath = WorkstreamJunctionLinker.ResolveLinkPath(slug, roomDirectoryPath);
        if (Directory.Exists(linkPath))
        {
            Directory.Delete(linkPath, recursive: false);
        }

        if (Directory.Exists(slugDir) && !Directory.EnumerateFileSystemEntries(slugDir).Any())
        {
            Directory.Delete(slugDir, recursive: false);
        }

        if (Directory.Exists(BatonPaths.ByWorkstream) && !Directory.EnumerateFileSystemEntries(BatonPaths.ByWorkstream).Any())
        {
            Directory.Delete(BatonPaths.ByWorkstream, recursive: false);
        }
    }
}
