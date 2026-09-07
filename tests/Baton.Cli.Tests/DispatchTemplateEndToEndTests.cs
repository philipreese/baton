using System.Diagnostics;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Status;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton dispatch &lt;template&gt;</c> end to end (rung-3, #920): a shipped multi-phase template is
/// resolved and composed into a DAG, run through the same real pump <see cref="DispatchCommandEndToEndTests"/>
/// covers for the single-step case — and when the template declares a <c>diff-of-work-so-far</c> phase,
/// the dispatch entrypoint captures the workspace HEAD and
/// injects it as the capture step's base ref. Roles run on a CI-safe fake; the capture step runs on a
/// fake that records the base it was handed, so the whole injection chain (compose → detect the capture
/// binding → capture HEAD → adapter receives it) is proven without a live LLM and without git having to
/// produce a real diff. The one namespace rule (0047 §5) and the role-vs-template spec split are here too.
/// </summary>
// #1524: kept enrolled solely for Console.Out; see SerializedEnvironmentCollection's remarks.
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class DispatchTemplateEndToEndTests : IDisposable
{
    private readonly IsolatedBatonHome _batonHome = new();
    private readonly IDisposable _catalogScope;

    // Pins all three shipped catalogs, same #1524 BeginScope pattern as
    // DispatchCommandEndToEndTests' own ctor.
    public DispatchTemplateEndToEndTests()
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
    public async Task Dispatching_the_shipped_template_composes_runs_and_injects_the_capture_base_ref()
    {
        // The shipped implement-review template, run end to end: implement -> janitor -> review-capture
        // -> review. Its review phase declares diff-of-work-so-far (so the composer splices the capture
        // step) and a schema-checked verdict.json (so this exercises the real per-role contract, #897,
        // not a synthetic markdown-only stand-in). Roles run on the fake; the capture step runs on the
        // fake that records the base it was handed, so the whole injection chain is proven without a
        // live LLM and without git producing a real diff.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-tmpl-{Guid.NewGuid():N}");
        try
        {
            var workspace = Path.Combine(testRoot, "workspace");
            var expectedHead = await InitGitWorkspaceAsync(workspace);

            // A minimal conforming ReviewVerdict (decision 0043: the engine checks only that it PARSES
            // as one — ReviewedRef required, empty Findings valid). Written with a real file API and
            // copied into place by the fake, so no JSON is assembled through a shell echo. The canonical
            // schema is Baton.Domain.ReviewVerdict; this is the smallest document it accepts.
            var verdictFixture = Path.Combine(testRoot, "verdict-fixture.json");
            await File.WriteAllTextAsync(
                verdictFixture, """{"reviewedRef":"HEAD","decision":"approve","findings":[]}""", TestContext.Current.CancellationToken);

            var capture = new BaseRefCapturingWorkerAdapter();
            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["fake"] = new ContractOutputWorkerAdapter(
                    satisfyOutputs: true,
                    outputFixtures: new Dictionary<string, string> { ["verdict.json"] = verdictFixture }),
                [WorkflowTemplateComposer.CaptureAdapter] = capture,
            };
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("implement-review", SpecFilePath: null, roomDirectory, Adapter: "fake");

            var state = (await DispatchCommand.ExecuteAsync(
                options, adapters, TestContext.Current.CancellationToken, workspaceDirectory: workspace)).State;

            Assert.Equal(WorkflowStatus.Terminal, state.Status);
            Assert.Equal(
                new[] { "implement", "janitor", "review-capture", "review" },
                state.Steps.Select(s => s.StepId.Value).ToArray());
            Assert.All(state.Steps, FlowAssert.Succeeded);

            // The base ref the capture adapter was handed is the workspace's HEAD at dispatch time --
            // the injection ran, captured THIS workspace (not an ambient one), and reached the adapter.
            var observed = Assert.Single(capture.ObservedBaseRefs);
            Assert.Equal(expectedHead, observed);

            // And the capture step's own working directory is pinned to that same workspace, so its
            // git diff runs where the base was captured -- not the ambient process cwd. Without this the
            // base (from `workspace`) and the diff (from cwd) could diverge, diffing a SHA against the
            // wrong tree whenever a caller passes a workspace other than the process directory.
            var observedWorkingDirectory = Assert.Single(capture.ObservedWorkingDirectories);
            Assert.Equal(workspace, observedWorkingDirectory);

            // The capture artifact actually landed where the review phase reads it. Without this, an
            // Assert.All(Succeeded) would pass even if the diff never materialized, because review's
            // contract does not require the input file to be present to succeed.
            var captureStep = state.Steps.Single(s => s.StepId.Value == "review-capture");
            var artifactPath = Path.Combine(
                roomDirectory, "artifacts", $"execution_{captureStep.LatestExecutionId}",
                WorkflowTemplateComposer.CaptureOutputName);
            Assert.True(File.Exists(artifactPath), $"capture artifact missing at {artifactPath}");

            // Persisted like any run, so the task is resumable.
            Assert.True(File.Exists(Path.Combine(roomDirectory, "workflow.json")));
            Assert.True(File.Exists(Path.Combine(roomDirectory, "bindings.json")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_composed_template_with_a_label_stamps_it_onto_every_bindings_entry()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-tmpl-label-{Guid.NewGuid():N}");
        try
        {
            var workspace = Path.Combine(testRoot, "workspace");
            await InitGitWorkspaceAsync(workspace);

            var verdictFixture = Path.Combine(testRoot, "verdict-fixture.json");
            await File.WriteAllTextAsync(
                verdictFixture, """{"reviewedRef":"HEAD","decision":"approve","findings":[]}""", TestContext.Current.CancellationToken);

            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["fake"] = new ContractOutputWorkerAdapter(
                    satisfyOutputs: true,
                    outputFixtures: new Dictionary<string, string> { ["verdict.json"] = verdictFixture }),
                [WorkflowTemplateComposer.CaptureAdapter] = new BaseRefCapturingWorkerAdapter(),
            };
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("implement-review", SpecFilePath: null, roomDirectory, Adapter: "fake", Label: "multi-phase lane");

            var state = (await DispatchCommand.ExecuteAsync(
                options, adapters, TestContext.Current.CancellationToken, workspaceDirectory: workspace)).State;

            Assert.Equal(WorkflowStatus.Terminal, state.Status);

            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(roomDirectory, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.True(bindings.Count > 1);
            Assert.All(bindings.Values, entry => Assert.Equal("multi-phase lane", entry.Label));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_composed_templates_capture_step_prints_no_grant_line_while_its_role_siblings_do()
    {
        // F2 (#1355 PR #1385 review): the capture step's adapter (BaseRefCapturingWorkerAdapter, standing
        // in for the real CaptureWorkerAdapter) spawns git directly and is IWorkerAdapter only -- it never
        // consumes a PermissionGrant. Before F2, DispatchCommand printed a "Grant (review-capture): ..."
        // line for it anyway, and "no-shell" in that line was false in the only sense that matters: the
        // step runs a git subprocess regardless of what the grant says. The role phases, bound to a
        // translator-implementing fake here, still get their own lines.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-tmpl-grant-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var workspace = Path.Combine(testRoot, "workspace");
            await InitGitWorkspaceAsync(workspace);

            var verdictFixture = Path.Combine(testRoot, "verdict-fixture.json");
            await File.WriteAllTextAsync(
                verdictFixture, """{"reviewedRef":"HEAD","decision":"approve","findings":[]}""", TestContext.Current.CancellationToken);

            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["fake"] = new GrantConsumingContractOutputWorkerAdapter(
                    satisfyOutputs: true,
                    outputFixtures: new Dictionary<string, string> { ["verdict.json"] = verdictFixture }),
                [WorkflowTemplateComposer.CaptureAdapter] = new BaseRefCapturingWorkerAdapter(),
            };
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("implement-review", SpecFilePath: null, roomDirectory, Adapter: "fake");

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            await DispatchCommand.ExecuteAsync(
                options, adapters, TestContext.Current.CancellationToken, workspaceDirectory: workspace);
            Console.SetOut(originalOut);

            var printed = consoleOutput.ToString();
            Assert.Contains("Grant (implement):", printed);
            Assert.Contains("Grant (janitor):", printed);
            Assert.Contains("Grant (review):", printed);
            Assert.DoesNotContain("review-capture", printed);
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_template_with_a_capture_step_in_a_non_git_workspace_fails_loudly_before_running()
    {
        // The polarity opposite of the test above: a capture template pointed at a non-git workspace has
        // no base ref to diff against, so the entrypoint refuses at injection -- a typed error before any
        // worker runs, not an opaque failure inside the capture step mid-run.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-tmpl-{Guid.NewGuid():N}");
        try
        {
            var workspace = Path.Combine(testRoot, "not-a-repo");
            Directory.CreateDirectory(workspace);

            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["fake"] = new ContractOutputWorkerAdapter(satisfyOutputs: true),
                [WorkflowTemplateComposer.CaptureAdapter] = new BaseRefCapturingWorkerAdapter(),
            };
            var options = new DispatchOptions(
                "implement-review", SpecFilePath: null, Path.Combine(testRoot, "task"), Adapter: "fake");

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                options, adapters, TestContext.Current.CancellationToken, workspaceDirectory: workspace));
            // For the RIGHT reason: the base-ref capture refused a non-git workspace, not some unrelated
            // throw that also happens to be a CliArgumentException.
            Assert.Contains("base ref", ex.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_template_rejects_a_spec_because_its_phases_carry_their_own_instructions()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-tmpl-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var specPath = Path.Combine(testRoot, "spec.md");
            await File.WriteAllTextAsync(specPath, "spec", TestContext.Current.CancellationToken);
            var options = new DispatchOptions("implement-review", specPath, Path.Combine(testRoot, "task"));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                options, WorkerAdapterRegistry.Default, TestContext.Current.CancellationToken));
            Assert.Contains("--spec", ex.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1941 review MEDIUM: the refusal shipped with the flag and had no arm. Why a template refuses
    /// <c>--skill</c> rather than spreading it over the phases is stated at the refusal itself, in
    /// <c>DispatchCommand.MaterializeTemplateAsync</c> (and spec/baton.md §9); this is its arm, alongside
    /// the sibling ones for <c>--attach</c> and <c>--spec</c>.
    /// </summary>
    [Fact]
    public async Task A_template_rejects_a_skill_because_a_single_flag_names_no_phase()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-tmpl-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var options = new DispatchOptions(
                "implement-review", SpecFilePath: null, Path.Combine(testRoot, "task"),
                Skills: ["house-style"]);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                options, WorkerAdapterRegistry.Default, TestContext.Current.CancellationToken));
            // For the right reason: this options record trips no sibling template refusal (no spec, no
            // attachment, no --output, no --timeout), so the message has to name --skill itself.
            Assert.Contains("--skill", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_role_requires_a_spec()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-tmpl-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            // advise is a shipped role; a role needs a task spec, and the parser no longer enforces that
            // (a template takes none), so the command must.
            var options = new DispatchOptions("advise", SpecFilePath: null, Path.Combine(testRoot, "task"));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                options, WorkerAdapterRegistry.Default, TestContext.Current.CancellationToken));
            // Discriminating: this exact phrase is the role-needs-a-spec message, not the
            // template-rejects-a-spec message nor "Spec file 'X' does not exist."
            Assert.Contains("Pass --spec <spec-file>", ex.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_name_that_is_both_a_role_and_a_template_is_refused()
    {
        // The one-namespace rule (0047 §5). Manufacture the collision the shipped catalogs are guarded
        // against (see the architecture guard test) by overriding the template catalog with one whose id
        // equals a real role id, then dispatch it: the command must refuse rather than pick a side.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-tmpl-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var collidingCatalog = Path.Combine(testRoot, "workflow-templates.json");
            await File.WriteAllTextAsync(
                collidingCatalog,
                """
                [
                  { "id": "review", "phases": [
                    { "name": "p", "role_id": "review", "instruction": "x", "ask_first": false, "inputs": [] } ] }
                ]
                """,
                TestContext.Current.CancellationToken);
            using var collidingScope = BatonEnvironmentSnapshot.BeginScope(
                BatonEnvironmentSnapshot.Current with { WorkflowTemplatesPathOverride = collidingCatalog });

            var options = new DispatchOptions("review", SpecFilePath: null, Path.Combine(testRoot, "task"));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                options, WorkerAdapterRegistry.Default, TestContext.Current.CancellationToken));
            Assert.Contains("namespace", ex.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_template_whose_composition_faults_is_a_typed_argument_error_not_a_crash()
    {
        // Discriminates the widened catch in DispatchCommand.MaterializeAsync (#929): a catalog fault
        // raised during *composition* — after the isTemplate/isRole probes have already read the catalog
        // cleanly — must still surface as a typed CliArgumentException, not escape Program's boundary as a
        // raw crash. The probes only enumerate ids on structurally-valid JSON, so they cannot raise this;
        // WorkflowTemplateComposer.Materialize does, when a phase's generated capture-step id collides
        // with a real phase name. This template loads clean (unique phase names, known roles, valid
        // inputs) yet the phase declaring the diff input generates capture id 'review-capture', which
        // collides with the phase literally named 'review-capture' — the composer's guard throws. Before
        // the catch wrapped the whole materialization (it wrapped only the probes), that InvalidOperation
        // escaped MaterializeTemplateAsync raw.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-tmpl-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var faultingCatalog = Path.Combine(testRoot, "workflow-templates.json");
            await File.WriteAllTextAsync(
                faultingCatalog,
                """
                [
                  { "id": "faulting-tmpl", "phases": [
                    { "name": "review-capture", "role_id": "review", "instruction": "x", "ask_first": false, "inputs": [] },
                    { "name": "review", "role_id": "implement", "instruction": "y", "ask_first": false, "inputs": ["diff-of-work-so-far"] } ] }
                ]
                """,
                TestContext.Current.CancellationToken);
            using var faultingScope = BatonEnvironmentSnapshot.BeginScope(
                BatonEnvironmentSnapshot.Current with { WorkflowTemplatesPathOverride = faultingCatalog });

            var options = new DispatchOptions("faulting-tmpl", SpecFilePath: null, Path.Combine(testRoot, "task"));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                options, WorkerAdapterRegistry.Default, TestContext.Current.CancellationToken));
            // Discriminating: the composer's own collision message, proving the fault came from
            // composition (not the both-names or unknown-name branches) and was translated, not masked.
            Assert.Contains("collides with a phase named", ex.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_template_prints_skills_for_each_worker_phase()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-tmpl-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var workspace = Path.Combine(testRoot, "workspace");
            await InitGitWorkspaceAsync(workspace);

            var verdictFixture = Path.Combine(testRoot, "verdict-fixture.json");
            await File.WriteAllTextAsync(
                verdictFixture, """{"reviewedRef":"HEAD","decision":"approve","findings":[]}""", TestContext.Current.CancellationToken);

            var capture = new BaseRefCapturingWorkerAdapter();
            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["fake"] = new ContractOutputWorkerAdapter(
                    satisfyOutputs: true,
                    outputFixtures: new Dictionary<string, string> { ["verdict.json"] = verdictFixture }),
                [WorkflowTemplateComposer.CaptureAdapter] = capture,
            };
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("implement-review", SpecFilePath: null, roomDirectory, Adapter: "fake");

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            await DispatchCommand.ExecuteAsync(
                options, adapters, TestContext.Current.CancellationToken, workspaceDirectory: workspace);
            Console.SetOut(originalOut);

            var output = consoleOutput.ToString();
            Assert.Contains("Skills (implement): none discovered", output);
            Assert.Contains("Skills (janitor): none discovered", output);
            Assert.Contains("Skills (review): none discovered", output);
            // #1512 M6: this test previously asserted only presence, not absence -- it would have
            // passed unchanged if a fourth "Skills (capture...)" line had been printed, so the
            // exclusion itself was never actually tested.
            Assert.DoesNotContain("Skills (capture", output);
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>Creates a git repo at <paramref name="directory"/> with one empty commit; returns its HEAD SHA.</summary>
    private static async Task<string> InitGitWorkspaceAsync(string directory)
    {
        Directory.CreateDirectory(directory);

        // #1623: the shipped catalog's `implement` role now carries a VerifyPixiTask —
        // MutationInterface spawns a REAL `pixi` process against this
        // workspace once the fake worker "succeeds", regardless of which adapter dispatched it (the
        // engine has no notion of a test-only adapter). Without a real, fast, passing `gates-quiet`
        // task here, that spawn fails immediately (no `pixi.toml` found), turning `implement` from
        // Succeeded into Indeterminate and breaking every test built on this fixture. A minimal,
        // dependency-free manifest keeps the spawn real (proving the wiring, not stubbing around it)
        // while staying fast (~0.2s, no environment solve — empty `channels`).
        await File.WriteAllTextAsync(
            Path.Combine(directory, "pixi.toml"),
            """
            [workspace]
            name = "verify-fixture"
            version = "0.1.0"
            channels = []
            platforms = ["win-64"]

            [tasks]
            gates-quiet = { cmd = "cmd /c exit 0" }
            """);

        await RunGitAsync(directory, "init", "-q");
        // -c identity keeps the commit independent of any (absent) global git config on the runner.
        await RunGitAsync(
            directory, "-c", "user.email=test@example.invalid", "-c", "user.name=Test",
            "commit", "--allow-empty", "-q", "-m", "base");
        return await RunGitAsync(directory, "rev-parse", "HEAD");
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git — is it on PATH? These tests need git.");
        var (stdout, stderr) = await BoundedProcessWait.RunToExitAsync(
            process, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.Trim()}");
        }

        return stdout.Trim();
    }
}
