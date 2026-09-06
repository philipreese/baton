using Baton.Vendors.Tests.TestSupport;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;

namespace Baton.Vendors.Tests;

/// <summary>
/// M11 Phase 1's deliverable: the canonical → <c>CoreDispatchTarget</c> mapping under a fake/echo
/// adapter, and the worker-binding config parsed and resolved into <see cref="WorkerBinding"/>s —
/// no real vendor, no live process.
/// </summary>
[Collection(LaunchConfigCollection.Name)]
public class WorkerBindingResolverTests
{
    private static readonly WorkerContract ArchitectContract = new(
        "architect", ["goal"], [new ProducedOutput("plan")], []);

    [Fact]
    public void An_entry_resolves_to_a_Process_binding_via_its_named_adapter()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5), "claude-opus-4", "write-only"),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };

        var bindings = WorkerBindingResolver.Resolve(config, adapters);

        var binding = Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
        Assert.Same(ArchitectContract, binding.Contract);
        Assert.Equal(TimeSpan.FromMinutes(5), binding.Timeout);
    }

    [Fact]
    public void The_resolved_target_carries_the_invocation_and_contract_fields_the_adapter_received()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5), "claude-opus-4", "write-only"),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };

        var bindings = WorkerBindingResolver.Resolve(config, adapters);

        var binding = (WorkerBinding.Process)bindings["architect"];
        Assert.Equal("echo", binding.Target.Program);
        Assert.Equal(
            ["Draft a plan.", "claude-opus-4", "write-only", "(no-permission-grant)", "architect", "goal", "plan"],
            binding.Target.Args);
    }

    [Fact]
    public void An_entry_with_no_model_or_permission_scope_still_resolves()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry("echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5)),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };

        var bindings = WorkerBindingResolver.Resolve(config, adapters);

        var binding = (WorkerBinding.Process)bindings["architect"];
        Assert.Equal(
            ["Draft a plan.", "(no-model)", "(no-permission-scope)", "(no-permission-grant)", "architect", "goal", "plan"],
            binding.Target.Args);
    }

    [Fact]
    public void An_entry_naming_an_unregistered_adapter_throws()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry("claude", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5)),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };

        var ex = Assert.Throws<UnknownWorkerAdapterException>(() => WorkerBindingResolver.Resolve(config, adapters));
        Assert.Equal("claude", ex.AdapterName);
        Assert.Contains("\"Adapter\": \"echo\"", ex.TryInvocation, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_adapter_registry_leaves_TryInvocation_null_rather_than_an_empty_example()
    {
        // #1382 F7 (see the exception's own constructor comment for the defect this guards).
        var ex = new UnknownWorkerAdapterException("claude", []);

        Assert.Null(ex.TryInvocation);
    }

    [Fact]
    public void Multiple_entries_resolve_independently()
    {
        var criticContract = new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry("echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5)),
            ["critic"] = new WorkerBindingConfigEntry("echo", criticContract, "Review the plan.", TimeSpan.FromMinutes(2)),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };

        var bindings = WorkerBindingResolver.Resolve(config, adapters);

        Assert.Equal(2, bindings.Count);
        Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
        Assert.IsType<WorkerBinding.Process>(bindings["critic"]);
    }

    [Fact]
    public void An_empty_config_resolves_to_an_empty_binding_set()
    {
        var bindings = WorkerBindingResolver.Resolve(
            new Dictionary<string, WorkerBindingConfigEntry>(), new Dictionary<string, IWorkerAdapter>());

        Assert.Empty(bindings);
    }

    // M24 Phase 1 (#262): the live in-turn streaming seam.

    [Fact]
    public void OnWorkerStdoutLine_null_leaves_the_resolved_target_with_no_OnStdoutLine_callback()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry("echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5)),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };

        var bindings = WorkerBindingResolver.Resolve(config, adapters);

        var binding = (WorkerBinding.Process)bindings["architect"];
        Assert.Null(binding.Target.OnStdoutLine);
    }

    [Fact]
    public void OnWorkerStdoutLine_when_supplied_is_wrapped_onto_the_target_with_the_workers_own_name()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry("echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5)),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };
        var received = new List<(string WorkerName, string Line)>();

        var bindings = WorkerBindingResolver.Resolve(
            config, adapters, onWorkerStdoutLine: (workerName, line) => received.Add((workerName, line)));

        var binding = (WorkerBinding.Process)bindings["architect"];
        Assert.NotNull(binding.Target.OnStdoutLine);
        binding.Target.OnStdoutLine!("a raw stdout line");
        Assert.Equal(("architect", "a raw stdout line"), Assert.Single(received));
    }

    [Fact]
    public void OnWorkerStdoutLine_reports_each_entrys_own_worker_name_independently()
    {
        var criticContract = new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry("echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5)),
            ["critic"] = new WorkerBindingConfigEntry("echo", criticContract, "Review the plan.", TimeSpan.FromMinutes(2)),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };
        var received = new List<(string WorkerName, string Line)>();

        var bindings = WorkerBindingResolver.Resolve(
            config, adapters, onWorkerStdoutLine: (workerName, line) => received.Add((workerName, line)));

        ((WorkerBinding.Process)bindings["architect"]).Target.OnStdoutLine!("line from architect");
        ((WorkerBinding.Process)bindings["critic"]).Target.OnStdoutLine!("line from critic");

        Assert.Contains(("architect", "line from architect"), received);
        Assert.Contains(("critic", "line from critic"), received);
    }

    // M23 Phase 3 (#272): WorkingDirectory profile resolution.

    [Fact]
    public void A_rooted_WorkingDirectory_passes_through_unchanged_with_no_profiles_needed()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5), WorkingDirectory: "/home/user/my-project"),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };

        var bindings = WorkerBindingResolver.Resolve(config, adapters);

        var binding = (WorkerBinding.Process)bindings["architect"];
        Assert.Equal("/home/user/my-project", binding.Target.WorkingDirectory);
    }

    [Fact]
    public void A_profile_named_WorkingDirectory_resolves_via_the_supplied_profile_map()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5), WorkingDirectory: "myproject"),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };
        var profiles = new Dictionary<string, string> { ["myproject"] = "/real/machine/path" };

        var bindings = WorkerBindingResolver.Resolve(config, adapters, profiles);

        var binding = (WorkerBinding.Process)bindings["architect"];
        Assert.Equal("/real/machine/path", binding.Target.WorkingDirectory);
    }

    [Fact]
    public void A_profile_named_WorkingDirectory_with_no_matching_profile_throws()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5), WorkingDirectory: "myproject"),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };

        var ex = Assert.Throws<UnknownWorkingDirectoryProfileException>(() =>
            WorkerBindingResolver.Resolve(config, adapters, profiles: null));
        Assert.Equal("architect", ex.WorkerName);
        Assert.Equal("myproject", ex.ProfileName);
    }

    [Fact]
    public void A_profile_named_WorkingDirectory_absent_from_a_non_empty_profile_map_still_throws()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5), WorkingDirectory: "myproject"),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };
        var profiles = new Dictionary<string, string> { ["some-other-project"] = "/real/path" };

        Assert.Throws<UnknownWorkingDirectoryProfileException>(() => WorkerBindingResolver.Resolve(config, adapters, profiles));
    }

    [Fact]
    public void No_WorkingDirectory_at_all_resolves_to_null_regardless_of_profiles()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry("echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5)),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };

        var bindings = WorkerBindingResolver.Resolve(config, adapters, profiles: new Dictionary<string, string>());

        var binding = (WorkerBinding.Process)bindings["architect"];
        Assert.Null(binding.Target.WorkingDirectory);
    }

    /// <summary>
    /// #588: the binding entry's <c>Timeout</c> must reach the adapter, not just
    /// <c>WorkerBinding.Process</c>. <c>agy -p</c> applies its own hardcoded 5-minute print-mode wait
    /// unless told otherwise, so an adapter that cannot see AER's timeout silently caps every long
    /// task at 5 minutes regardless of what the operator configured.
    /// </summary>
    [Fact]
    public void Resolve_hands_the_entrys_Timeout_to_the_adapter_as_well_as_to_the_binding()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "capture", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(20)),
        };
        var adapter = new CapturingWorkerAdapter();
        var adapters = new Dictionary<string, IWorkerAdapter> { ["capture"] = adapter };

        var bindings = WorkerBindingResolver.Resolve(config, adapters);

        // Both halves, because they are separately wrong-able: the binding carrying it while the
        // adapter does not is exactly the pre-#588 state, and that state looked entirely correct
        // from Baton's side.
        Assert.Equal(TimeSpan.FromMinutes(20), adapter.LastInvocation!.Timeout);
        Assert.Equal(TimeSpan.FromMinutes(20), Assert.IsType<WorkerBinding.Process>(bindings["architect"]).Timeout);
    }

    /// <summary>Records the <see cref="WorkerInvocation"/> it was handed, and nothing else.</summary>
    private sealed class CapturingWorkerAdapter : IWorkerAdapter
    {
        public WorkerInvocation? LastInvocation { get; private set; }

        public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
        {
            LastInvocation = invocation;
            return new CoreDispatchTarget("echo", []);
        }
    }
    // ---------------------------------------------------------------------------------------
    // #529 — a granted shell reaches three of the four categories, so withholding one while
    // granting the shell does not withhold it. These assert the bind-time refusal.
    // ---------------------------------------------------------------------------------------

    private static Dictionary<string, IWorkerAdapter> EchoAdapter() =>
        new() { ["echo"] = new FakeEchoWorkerAdapter() };

    private static Dictionary<string, WorkerBindingConfigEntry> ConfigWithGrant(PermissionGrant grant) =>
        new()
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5),
                PermissionGrant: grant),
        };

    [Theory]
    // Each withholds exactly one category a granted Bash reaches. #529 measured the write arm
    // directly: --disallowedTools Edit,Write,NotebookEdit removed those tools and the model
    // created the file with Bash instead.
    [InlineData(false, true, true, "WriteFiles")]
    [InlineData(true, false, true, "ReadFiles")]
    [InlineData(true, true, false, "NetworkAccess")]
    public void A_grant_that_withholds_a_category_a_granted_shell_reaches_is_refused(
        bool writeFiles, bool readFiles, bool networkAccess, string expectedCategoryInMessage)
    {
        var grant = new PermissionGrant(
            ReadFiles: readFiles, WriteFiles: writeFiles,
            RunShellCommands: true, ShellCommandPatterns: [], NetworkAccess: networkAccess);

        var thrown = Assert.Throws<IncoherentPermissionGrantException>(
            () => WorkerBindingResolver.Resolve(ConfigWithGrant(grant), EchoAdapter()));

        // EXACTLY the withheld one. `Assert.Contains` on the message would pass on a resolver that
        // named all three every time, and the sibling test below cannot see that either — its grant
        // withholds all three, so an over-broad list is indistinguishable from a correct one there.
        // The message is the operator-facing artifact: naming a category they already granted tells
        // them to grant it again.
        Assert.Equal([expectedCategoryInMessage], thrown.WithheldCategories);
        Assert.Contains(expectedCategoryInMessage, thrown.Message, StringComparison.Ordinal);
        Assert.Contains("architect", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_grant_with_the_shell_and_every_reachable_category_granted_resolves()
    {
        // The control arm. Without it the check above passes on a resolver that refuses every
        // grant carrying a shell, which would be a different and much worse defect.
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: true,
            RunShellCommands: true, ShellCommandPatterns: [], NetworkAccess: true);

        var bindings = WorkerBindingResolver.Resolve(ConfigWithGrant(grant), EchoAdapter());

        Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
    }

    [Fact]
    public void A_grant_that_withholds_categories_without_the_shell_resolves()
    {
        // The second control. Withholding writes is perfectly coherent when no shell is granted —
        // #529 is about a shell that defeats a withhold, and there is no shell here to defeat one.
        //
        // Deliberately on a contract with no declared outputs. It used to use one that declares
        // "plan", described as "the ordinary read-only reviewer", and that shape is now refused by
        // #629's separate rule — a worker that must produce an artifact and cannot write is
        // unsatisfiable whether or not a shell is involved. Keeping the old contract here would make
        // this control fail for a reason that has nothing to do with what it controls for.
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: false,
            RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false);

        var bindings = WorkerBindingResolver.Resolve(ConfigWith(NoOutputsContract, grant), EchoAdapter());

        Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
    }

    [Fact]
    public void A_shell_command_pattern_allowlist_does_not_exempt_the_grant_from_the_refusal()
    {
        // The tempting exemption, and the reason it is wrong: a pattern list reaches only
        // --allowedTools, which gate.allowedtools-is-preapproval-not-ceiling measured to be
        // pre-approval rather than a ceiling. --disallowedTools has no narrowed Bash(…) form at all,
        // so patterns change what is pre-approved and never what is reachable.
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: false,
            RunShellCommands: true, ShellCommandPatterns: ["git:*"], NetworkAccess: true);

        Assert.Throws<IncoherentPermissionGrantException>(
            () => WorkerBindingResolver.Resolve(ConfigWithGrant(grant), EchoAdapter()));
    }

    [Fact]
    public void ShellCommandsAreReadOnly_exempts_WriteFiles_and_NetworkAccess_from_the_refusal()
    {
        // #1456: the named, author-asserted escape hatch review's grant relies on -- a pattern list
        // ALONE (the test above) still refuses, but a grant that additionally asserts the patterns
        // are read-only resolves without widening WriteFiles/NetworkAccess just to satisfy this check.
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: false,
            RunShellCommands: true, ShellCommandPatterns: ["git diff*"], NetworkAccess: false,
            ShellCommandsAreReadOnly: true);

        var bindings = WorkerBindingResolver.Resolve(ConfigWith(NoOutputsContract, grant), EchoAdapter());

        Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
    }

    [Fact]
    public void ShellCommandsAreReadOnly_without_patterns_exempts_nothing()
    {
        // #1456 second-reader finding 1: the assertion is about a specific, named pattern set — an
        // UNSCOPED shell (no patterns) claiming read-only would have been certified coherent and
        // translated to bare Bash. It must refuse exactly like the flag was never set.
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: false,
            RunShellCommands: true, ShellCommandPatterns: null, NetworkAccess: false,
            ShellCommandsAreReadOnly: true);

        var thrown = Assert.Throws<IncoherentPermissionGrantException>(
            () => WorkerBindingResolver.Resolve(ConfigWithGrant(grant), EchoAdapter()));

        Assert.Contains("WriteFiles", thrown.WithheldCategories);
        Assert.Contains("NetworkAccess", thrown.WithheldCategories);
    }

    [Fact]
    public void ShellCommandsAreReadOnly_does_not_exempt_ReadFiles()
    {
        // Why ReadFiles stays outside the exemption: PermissionGrant.CategoriesDefeatedByTheShell's
        // own comment. Withholding it while granting the shell is still incoherent.
        var grant = new PermissionGrant(
            ReadFiles: false, WriteFiles: true,
            RunShellCommands: true, ShellCommandPatterns: ["git diff*"], NetworkAccess: true,
            ShellCommandsAreReadOnly: true);

        var thrown = Assert.Throws<IncoherentPermissionGrantException>(
            () => WorkerBindingResolver.Resolve(ConfigWithGrant(grant), EchoAdapter()));

        Assert.Equal(["ReadFiles"], thrown.WithheldCategories);
    }

    [Fact]
    public void Every_category_a_shell_defeats_is_named_at_once_rather_than_one_per_run()
    {
        // An operator fixing these one at a time would hit the refusal three times over.
        var grant = new PermissionGrant(
            ReadFiles: false, WriteFiles: false,
            RunShellCommands: true, ShellCommandPatterns: [], NetworkAccess: false);

        var thrown = Assert.Throws<IncoherentPermissionGrantException>(
            () => WorkerBindingResolver.Resolve(ConfigWithGrant(grant), EchoAdapter()));

        Assert.Equal(["ReadFiles", "WriteFiles", "NetworkAccess"], thrown.WithheldCategories);
    }

    [Fact]
    public void An_entry_with_no_structured_grant_at_all_resolves()
    {
        // Third control: the coherence check must not fire on the many entries that carry no
        // PermissionGrant, which is still the common case.
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5)),
        };

        var bindings = WorkerBindingResolver.Resolve(config, EchoAdapter());

        Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
    }

    // ---------------------------------------------------------------------------------------
    // #629 — a step that must produce an artifact, bound to a worker that cannot write one.
    // ---------------------------------------------------------------------------------------

    private static readonly WorkerContract NoOutputsContract = new("architect", ["goal"], [], []);

    private static Dictionary<string, WorkerBindingConfigEntry> ConfigWith(
        WorkerContract contract, PermissionGrant? grant, string adapter = "echo") =>
        new()
        {
            ["architect"] = new WorkerBindingConfigEntry(
                adapter, contract, "Draft a plan.", TimeSpan.FromMinutes(5), PermissionGrant: grant),
        };

    [Fact]
    public void A_contract_with_outputs_bound_to_a_grant_that_cannot_write_is_refused()
    {
        // The only way a vendor worker satisfies ProducedOutputs is by writing the artifact into
        // BATON_OUTPUT_DIR. Withholding the write tools makes the contract unsatisfiable on its face,
        // and before this the run was dispatched, paid for in full, and only then failed the check.
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: false);

        var thrown = Assert.Throws<UnsatisfiableOutputContractException>(
            () => WorkerBindingResolver.Resolve(ConfigWith(ArchitectContract, grant), EchoAdapter()));

        Assert.Equal("architect", thrown.WorkerName);
        Assert.Equal(["plan"], thrown.UnwritableOutputs);
    }

    [Fact]
    public void The_refusal_names_every_output_the_worker_cannot_write()
    {
        var contract = new WorkerContract(
            "architect", ["goal"], [new ProducedOutput("plan"), new ProducedOutput("notes.md")], []);

        var thrown = Assert.Throws<UnsatisfiableOutputContractException>(
            () => WorkerBindingResolver.Resolve(
                ConfigWith(contract, new PermissionGrant(ReadFiles: true, WriteFiles: false)), EchoAdapter()));

        Assert.Equal(["plan", "notes.md"], thrown.UnwritableOutputs);
    }

    [Fact]
    public void An_adapter_whose_withheld_writes_reach_the_outbox_is_not_refused()
    {
        // #649. Every declared output resolves under BATON_OUTPUT_DIR, so "the grant gives no way to
        // write it" is a claim about that one directory. Where a withheld write still reaches it the
        // refusal is false — and it refuses exactly the shape #649 exists for: a read-only reviewer
        // declaring review.md. Before this, the headline case could not bind at all.
        var adapters = new Dictionary<string, IWorkerAdapter> { ["outbox-capable"] = new OutboxCapableAdapter() };
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: false);

        var bindings = WorkerBindingResolver.Resolve(
            ConfigWith(ArchitectContract, grant, adapter: "outbox-capable"), adapters);

        Assert.True(bindings.ContainsKey("architect"));
    }

    [Fact]
    public void The_real_claude_adapter_binds_a_read_only_reviewer_that_declares_an_output()
    {
        // The capability above asserted on the shipped adapter rather than a double, so a change
        // that flipped ClaudeWorkerAdapter's answer to false — restoring the refusal the live run
        // disproved — fails here rather than at dispatch time.
        var adapters = new Dictionary<string, IWorkerAdapter> { ["claude"] = new ClaudeWorkerAdapter() };
        var contract = new WorkerContract("architect", [], [new ProducedOutput("review.md")], []);

        var bindings = WorkerBindingResolver.Resolve(
            ConfigWith(contract, new PermissionGrant(ReadFiles: true, WriteFiles: false), adapter: "claude"),
            adapters);

        Assert.True(bindings.ContainsKey("architect"));
    }

    [Fact]
    public void An_adapter_that_has_not_answered_the_question_still_refuses()
    {
        // The polarity control for both tests above, and the reason the capability defaults to false:
        // an adapter measured against nothing must refuse before the run is paid for, not after. This
        // is the arm that fails if the refusal is deleted outright rather than made adapter-aware.
        Assert.Throws<UnsatisfiableOutputContractException>(
            () => WorkerBindingResolver.Resolve(
                ConfigWith(ArchitectContract, new PermissionGrant(ReadFiles: true, WriteFiles: false)),
                EchoAdapter()));
    }

    // ---------------------------------------------------------------------------------------
    // #662 — ResolveLazily defers every refusal above to first lookup, rather than deleting it.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ResolveLazily_does_not_refuse_an_unsatisfiable_entry_that_is_never_looked_up()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: false);

        var bindings = WorkerBindingResolver.ResolveLazily(ConfigWith(ArchitectContract, grant), EchoAdapter());

        Assert.True(bindings.ContainsKey("architect"));
    }

    [Fact]
    public void ResolveLazily_still_refuses_an_unsatisfiable_entry_once_it_is_looked_up()
    {
        // The polarity control for the test above: deferring resolution must not silently drop the
        // #629 refusal, only delay it to the point some caller actually needs that worker's binding.
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: false);

        var bindings = WorkerBindingResolver.ResolveLazily(ConfigWith(ArchitectContract, grant), EchoAdapter());

        Assert.Throws<UnsatisfiableOutputContractException>(() => bindings["architect"]);
    }

    /// <summary>
    /// A grant-consuming adapter that answers <see cref="IWorkerAdapter.WithheldWritesReachTheOutbox"/>
    /// with true — <see cref="IPermissionGrantTranslator"/> because the refusal only runs for that
    /// population, so a non-translator would pass for the wrong reason.
    /// </summary>
    private sealed class OutboxCapableAdapter : IWorkerAdapter, IPermissionGrantTranslator
    {
        public bool WithheldWritesReachTheOutbox => true;

        public bool TryTranslatePermissionGrant(
            PermissionGrant grant, out string? resolvedValue, out string? gapReason)
        {
            resolvedValue = "Read";
            gapReason = null;
            return true;
        }

        public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract) =>
            new("echo", ["hi"]);
    }

    [Fact]
    public void An_adapter_that_does_not_consume_a_grant_is_not_refused_for_one()
    {
        // The control that killed the first version of this rule. NoOpWorkerAdapter never reads
        // PermissionGrant — AER builds its dispatch itself and it writes its output regardless — so a
        // withheld write there withholds nothing and refusing would reject a binding that works.
        // Unscoped, this rule refused every interactive session, whose anchor is exactly this shape.
        // WorkerAdapterRegistryTests (#651) is what keeps IPermissionGrantTranslator honest as the
        // marker for "this adapter's grant is load-bearing".
        var adapters = new Dictionary<string, IWorkerAdapter>
        {
            [NoOpWorkerAdapter.AdapterName] = new NoOpWorkerAdapter(),
        };
        var grant = new PermissionGrant(ReadFiles: false, WriteFiles: false);

        var bindings = WorkerBindingResolver.Resolve(
            ConfigWith(ArchitectContract, grant, NoOpWorkerAdapter.AdapterName), adapters);

        Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
    }

    [Fact]
    public void The_shell_refusal_is_scoped_to_consuming_adapters_too()
    {
        // Pins a production change this PR makes to #529's rule, not #629's. Before it,
        // RefuseIfShellDefeatsAWithheldCategory ran for EVERY adapter; both refusals are now scoped
        // to adapters that consume a grant, because the same reasoning applies to both — a grant a
        // NoOpWorkerAdapter never reads cannot defeat anything, and it writes its output regardless.
        //
        // Written because a reviewer showed nothing discriminated it: reverting the narrowing left
        // the full suite green. Every pre-existing #529 test runs through FakeEchoWorkerAdapter, and
        // this PR made that fake a translator (it had to, or the #629 tests would never fire), which
        // silently kept them all passing. One rule's scope should not be decided by a test double's
        // interface list.
        var adapters = new Dictionary<string, IWorkerAdapter>
        {
            [NoOpWorkerAdapter.AdapterName] = new NoOpWorkerAdapter(),
        };
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: false, RunShellCommands: true, NetworkAccess: true);

        var bindings = WorkerBindingResolver.Resolve(
            ConfigWith(NoOutputsContract, grant, NoOpWorkerAdapter.AdapterName), adapters);

        Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
    }

    [Fact]
    public void A_contract_with_outputs_and_a_grant_that_can_write_resolves()
    {
        // Without this the checks above pass on a resolver that refuses every contract carrying an
        // output, which would make every real workflow undispatchable.
        var bindings = WorkerBindingResolver.Resolve(
            ConfigWith(ArchitectContract, new PermissionGrant(ReadFiles: true, WriteFiles: true)), EchoAdapter());

        Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
    }

    [Fact]
    public void A_contract_with_no_outputs_at_all_resolves_under_a_read_only_grant()
    {
        // The rule kept honest in the other direction: a worker that declares no outputs has nothing
        // to write, so withholding writes is coherent. This is the shape #650 gave the chat step.
        var bindings = WorkerBindingResolver.Resolve(
            ConfigWith(NoOutputsContract, new PermissionGrant(ReadFiles: true, WriteFiles: false)), EchoAdapter());

        Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
    }

    [Fact]
    public void An_entry_with_outputs_and_no_structured_grant_resolves()
    {
        // An entry using the raw PermissionScope escape hatch carries no grant, so there is nothing
        // to reconcile against the contract. This is also the interactive anchor's shape today (#651).
        var bindings = WorkerBindingResolver.Resolve(
            ConfigWith(ArchitectContract, grant: null), EchoAdapter());

        Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
    }

    [Fact]
    public void The_shell_refusal_wins_when_a_grant_is_both_incoherent_and_unsatisfiable()
    {
        // Both faults at once. The shell one is reported, because it names the mistake the operator
        // actually made: they reached for the shell believing it escaped the write withhold. Told only
        // that the contract is unsatisfiable, they would grant more shell.
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: false, RunShellCommands: true, NetworkAccess: true);

        Assert.Throws<IncoherentPermissionGrantException>(
            () => WorkerBindingResolver.Resolve(ConfigWith(ArchitectContract, grant), EchoAdapter()));
    }

    [Fact]
    public void An_audited_binding_with_AuditedNotEnforced_without_a_worktree_throws_UnisolatedGrantAuditException()
    {
        var adapters = new Dictionary<string, IWorkerAdapter> { ["agy"] = new AgyWorkerAdapter() };
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["review"] = new WorkerBindingConfigEntry(
                "agy", ArchitectContract, "Review", TimeSpan.FromMinutes(5),
                PermissionGrant: grant, GrantAuditMode: GrantAuditMode.AuditedNotEnforced),
        };

        var ex = Assert.Throws<UnisolatedGrantAuditException>(() => WorkerBindingResolver.Resolve(config, adapters));
        Assert.Equal("review", ex.WorkerName);
        Assert.Contains("workspace isolation", ex.Message, StringComparison.Ordinal);
        Assert.Contains("use 'baton dispatch <role>' to auto-provision an isolated workspace", ex.TryInvocation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only a PROVISIONED worktree (the <see cref="WorktreeWorkspaces.Provision"/> stamp) counts
    /// as isolation. The second arm is the hole the second reader found on the first draft: a
    /// declared-but-unprovisioned Worktree spec reaches resolve intact on the callers that skip
    /// Provision (#1012), and treating the spec itself as isolation dispatched an audited worker
    /// into a null working directory.
    /// </summary>
    [Fact]
    public void An_audited_binding_resolves_only_once_its_worktree_is_actually_provisioned()
    {
        var adapters = new Dictionary<string, IWorkerAdapter> { ["agy"] = new AgyWorkerAdapter() };
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true);

        // #1166: both arms below target Path.GetFullPath(".") as WorkingDirectory -- trust it
        // unrestricted so this test's own concern (worktree provisioning) is what decides the outcome.
        ProjectCeilingStore.Set(Path.GetFullPath("."), ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);

        var provisioned = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["review"] = new WorkerBindingConfigEntry(
                "agy", ArchitectContract, "Review", TimeSpan.FromMinutes(5),
                PermissionGrant: grant, WorkingDirectory: Path.GetFullPath("."),
                GrantAuditMode: GrantAuditMode.AuditedNotEnforced, IsWorktree: true),
        };
        var binding = Assert.IsType<WorkerBinding.Process>(
            WorkerBindingResolver.Resolve(provisioned, adapters)["review"]);
        Assert.Equal(GrantAuditMode.AuditedNotEnforced, binding.GrantAuditMode);

        var declaredButUnprovisioned = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["review"] = new WorkerBindingConfigEntry(
                "agy", ArchitectContract, "Review", TimeSpan.FromMinutes(5),
                PermissionGrant: grant, Worktree: new WorktreeWorkspace(Path.GetFullPath("."), "main"),
                GrantAuditMode: GrantAuditMode.AuditedNotEnforced),
        };
        var ex = Assert.Throws<UnisolatedGrantAuditException>(
            () => WorkerBindingResolver.Resolve(declaredButUnprovisioned, adapters));
        Assert.Equal("review", ex.WorkerName);
        Assert.Contains("use 'baton dispatch <role>' to auto-provision an isolated workspace", ex.TryInvocation, StringComparison.Ordinal);
    }

    /// <summary>
    /// P2 (#1664 third re-review): <see cref="WorkerBindingConfigEntry.WorktreeBaseSha"/> must survive
    /// resolution onto <see cref="WorkerBinding.Process.WorktreeBaseSha"/> unchanged — the last hop of
    /// N2's fix, between the value <see cref="WorktreeWorkspaces"/> stamps and the value
    /// <c>MutationInterface</c> actually reads. Nothing previously asserted this hop; dropping the
    /// <c>entry.WorktreeBaseSha</c> argument from the <see cref="WorkerBinding.Process"/> constructor
    /// call at <c>WorkerBindingResolver.cs:156</c> turns this red alone.
    /// </summary>
    [Fact]
    public void ResolveEntry_carries_WorktreeBaseSha_onto_the_resolved_Process_binding()
    {
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };
        const string baseSha = "0369bf3ecafe0369bf3ecafe0369bf3ecafe0369";
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5),
                WorkingDirectory: Path.GetFullPath("."), IsWorktree: true, WorktreeBaseSha: baseSha),
        };

        var binding = Assert.IsType<WorkerBinding.Process>(WorkerBindingResolver.Resolve(config, adapters)["architect"]);

        Assert.Equal(baseSha, binding.WorktreeBaseSha);
        Assert.True(binding.IsWorktree);
    }

    [Fact]
    public void A_hand_authored_non_audited_write_files_false_with_outputs_on_agy_still_throws_unsatisfiable_output_contract()
    {
        var adapters = new Dictionary<string, IWorkerAdapter> { ["agy"] = new AgyWorkerAdapter() };
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: false);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["review"] = new WorkerBindingConfigEntry(
                "agy", ArchitectContract, "Review", TimeSpan.FromMinutes(5),
                PermissionGrant: grant, GrantAuditMode: GrantAuditMode.Enforced),
        };

        Assert.Throws<UnsatisfiableOutputContractException>(() => WorkerBindingResolver.Resolve(config, adapters));
    }

    [Fact]
    public void ResolveFallbacks_returns_only_entries_declaring_FallbackOnExhaustion()
    {
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5),
                FallbackOnExhaustion: new FallbackBinding("echo", "fallback-model")),
            ["review"] = new WorkerBindingConfigEntry("echo", ArchitectContract, "Review.", TimeSpan.FromMinutes(5)),
        };

        var fallbacks = WorkerBindingResolver.ResolveFallbacks(config, adapters);

        Assert.Equal(["architect"], fallbacks.Keys);
        var binding = Assert.IsType<WorkerBinding.Process>(fallbacks["architect"]);
        Assert.Equal("echo", binding.Adapter);
        Assert.Equal("fallback-model", binding.Model);
    }

    [Fact]
    public void ResolveFallbacks_drops_the_primarys_Model_when_the_fallback_declares_none()
    {
        // FallbackBinding's own doc has the #1082 reasoning this pins -- an unset fallback Model
        // must NOT inherit the primary's.
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5), Model: "primary-model",
                FallbackOnExhaustion: new FallbackBinding("echo")),
        };

        var binding = (WorkerBinding.Process)WorkerBindingResolver.ResolveFallbacks(config, adapters)["architect"];

        Assert.Null(binding.Model);
    }

    [Fact]
    public void ResolveFallbacks_refuses_an_unregistered_fallback_adapter_the_same_way_Resolve_does()
    {
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5),
                FallbackOnExhaustion: new FallbackBinding("claude")),
        };

        var ex = Assert.Throws<UnknownWorkerAdapterException>(() => WorkerBindingResolver.ResolveFallbacks(config, adapters));
        Assert.Equal("claude", ex.AdapterName);
    }

    /// <summary>
    /// #1838 (review LOW): <see cref="WorkerBindingResolver.ResolveFallbacks"/> routes a fallback
    /// entry through the exact same private <c>ResolveEntry</c> the primary path uses, which is what
    /// makes it subject to <see cref="ProjectCeilingGate"/> too -- but nothing pinned that for the
    /// fallback path specifically until this test. Mirrors
    /// <c>ClaudeWorkerAdapterTests.An_unseen_project_directory_is_refused_before_any_worker_spawns</c>:
    /// a <see cref="WorkingDirectory"/> <see cref="ProjectCeilingStore"/> has never recorded a
    /// ceiling for refuses with <see cref="ProjectNotTrustedException"/> regardless of what the
    /// declared grant asks for, exactly like the primary binding would.
    /// </summary>
    [Fact]
    public void ResolveFallbacks_is_refused_by_the_project_ceiling_gate_the_same_way_Resolve_does()
    {
        var unseenProject = Path.Combine(Path.GetTempPath(), $"baton-ceiling-fallback-{Guid.NewGuid():N}");
        var adapters = new Dictionary<string, IWorkerAdapter> { ["claude"] = new ClaudeWorkerAdapter() };
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "claude", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5),
                WorkingDirectory: unseenProject,
                FallbackOnExhaustion: new FallbackBinding("claude")),
        };

        var ex = Assert.Throws<ProjectNotTrustedException>(() => WorkerBindingResolver.ResolveFallbacks(config, adapters));
        Assert.Equal(unseenProject, ex.ProjectPath);
    }

    /// <summary>
    /// #1927 re-review LOW: <see cref="WorkerBindingResolver.ToFallbackEntry"/> re-runs the same
    /// bind-time resolution the redispatch paths do rather than copying the primary's display stamps
    /// across the swap — that method's own doc has which paths those are and what the stale copy cost
    /// on disk. Asserted on the entry rather than on the resolved
    /// <see cref="WorkerBinding.Process"/>, which carries none of the four stamps — the reason the
    /// stale copy was invisible until the on-disk readers (<c>RoomBindingStamps</c>,
    /// <c>CostLedgerStore</c>) started reading them.
    /// </summary>
    [Fact]
    public void A_fallbacks_vendor_swap_re_resolves_the_display_stamps_rather_than_copying_the_primarys()
    {
        var entry = new WorkerBindingConfigEntry(
            "claude", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5), Model: "opus",
            Effort: "careful",
            FallbackOnExhaustion: new FallbackBinding("agy")) with
        {
            ModelResolved = "opus",
            ModelSource = BindingValueSource.Requested,
            EffortResolved = "careful",
            EffortSource = BindingValueSource.Requested,
        };

        var swapped = WorkerBindingResolver.ToFallbackEntry(entry, entry.FallbackOnExhaustion!);

        // The agy CLI default, and #1927's suffix rung read off it -- not the claude parent's opus.
        Assert.Equal(AdapterDefaultModels.For("agy"), swapped.ModelResolved);
        Assert.Equal(BindingValueSource.ResolvedDefault, swapped.ModelSource);
        Assert.Equal("high", swapped.EffortResolved);
        Assert.Equal(BindingValueSource.ResolvedDefault, swapped.EffortSource);

        // The polarity control: a fallback naming the same vendor (which WorkerBindingConfigParser
        // refuses, but a hand-built config reaches) keeps stamps that are still true.
        var kept = WorkerBindingResolver.ToFallbackEntry(entry, new FallbackBinding("Claude"));
        Assert.Equal("opus", kept.ModelResolved);
        Assert.Equal(BindingValueSource.Requested, kept.ModelSource);
        Assert.Equal("careful", kept.EffortResolved);
        Assert.Equal(BindingValueSource.Requested, kept.EffortSource);
        // And the adapter field itself is left exactly as authored, casing included: normalizing it
        // here would make a spelling the registry does not carry resolve instead of refusing.
        Assert.Equal("Claude", kept.Adapter);
    }
}

