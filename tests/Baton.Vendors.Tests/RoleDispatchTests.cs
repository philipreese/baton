using System.Linq;
using Baton.Vendors;
using Baton.Domain;

namespace Baton.Vendors.Tests;

/// <summary>
/// The role -> binding mapping behind <c>baton dispatch</c> (#900): a role's declared outputs become the
/// contract the engine enforces, its grant/timeout/model/effort ride along, and its output
/// instructions are appended to the spec so the worker is told to produce exactly what the contract
/// asserts. Exercised against the shipped catalog, since that is what the command actually dispatches.
/// </summary>
[Collection(WorkerRoleCatalogCollection.Name)]
public class RoleDispatchTests
{
    private static WorkerRole Review => WorkerRoleCatalog.For("review");

    [Fact]
    public void A_roles_declared_outputs_become_the_contracts_produced_outputs_with_their_schema()
    {
        var binding = RoleDispatch.ToBinding(Review, "Review the change.");

        var outputs = binding.Contract.ProducedOutputs;
        Assert.Contains(outputs, o => o.Name == "report.md" && o.Schema == OutputSchema.None);
        // The schema is carried through, not dropped to None — a verdict.json that is not a
        // ReviewVerdict must fail the contract, and that only happens if the schema survives the map.
        Assert.Contains(outputs, o => o.Name == "verdict.json" && o.Schema == OutputSchema.ReviewVerdict);
        Assert.Equal(Review.Outputs.Count, outputs.Count);
    }

    /// <summary>
    /// Dispatch turns on stream-json for every structured vendor so a running lane's stdout log
    /// fills incrementally, while agy's terminal result event reaches the timeout guard.
    /// </summary>
    [Fact]
    public void StreamJson_is_enabled_for_each_structured_vendor()
    {
        Assert.True(RoleDispatch.ToBinding(Review, "Review the change.", adapterOverride: "agy").StreamJson);
        Assert.True(RoleDispatch.ToBinding(Review, "Review the change.", adapterOverride: "claude").StreamJson);
        Assert.True(RoleDispatch.ToBinding(Review, "Review the change.", adapterOverride: "codex").StreamJson);
    }

    [Fact]
    public void The_prompt_is_the_spec_followed_by_every_output_instruction()
    {
        var binding = RoleDispatch.ToBinding(Review, "Review the change.");

        Assert.StartsWith("Review the change.", binding.PromptTemplate);
        foreach (var output in Review.Outputs)
        {
            Assert.Contains(output.Instruction, binding.PromptTemplate);
        }
    }

    /// <summary>
    /// #1095: the dispatch prompt carries the one-shot execution contract (its rationale lives on
    /// <see cref="RoleDispatch"/>'s <c>OneShotContract</c>) — a dispatched worker's turn is never
    /// resumed, unlike a chat turn.
    /// </summary>
    [Fact]
    public void The_dispatch_prompt_states_the_one_shot_contract()
    {
        var dispatch = RoleDispatch.ToBinding(Review, "Review the change.").PromptTemplate;
        Assert.Contains("non-interactive turn", dispatch);
    }

    [Fact]
    public void The_binding_carries_the_roles_grant_timeout_model_and_effort()
    {
        // Captured once: WorkerRoleCatalog.For (behind the Review property) resolves fresh on every
        // call ("resolve, never capture" -- see its own doc comment) -- and since #1456 review's grant
        // carries non-null ShellCommandPatterns/DeniedShellCommandPatterns lists, two separate
        // resolutions produce reference-distinct list instances that record equality (which does not
        // do element-wise comparison on IReadOnlyList<string>) reports as unequal, even with identical
        // contents. Comparing binding.PermissionGrant against the SAME resolution's Grant, not a fresh
        // one, is what this test is actually about.
        var review = Review;
        var binding = RoleDispatch.ToBinding(review, "spec");

        Assert.Equal(review.Grant, binding.PermissionGrant);
        Assert.Equal(review.Timeout, binding.Timeout);
        Assert.Equal(review.Model, binding.Model);
        Assert.Equal(review.Effort, binding.Effort);
    }

    /// <summary>#1442: --timeout is a fourth independent axis alongside adapter/model/effort.</summary>
    [Fact]
    public void A_timeout_override_wins_over_the_roles_own_catalog_timeout()
    {
        Assert.Equal(Review.Timeout, RoleDispatch.ToBinding(Review, "spec").Timeout);

        var overridden = TimeSpan.FromMinutes(180);
        Assert.Equal(overridden, RoleDispatch.ToBinding(Review, "spec", timeoutOverride: overridden).Timeout);
        Assert.NotEqual(Review.Timeout, overridden);
    }

    /// <summary>
    /// #1686 review F12: nothing in the tree asserted a parsed --max-tool-steps value actually reaches
    /// a WorkerBindingConfigEntry -- a build that dropped maxToolStepsOverride in RoleDispatch.ToBinding
    /// (below) would have passed the whole suite before this.
    /// </summary>
    [Fact]
    public void A_max_tool_steps_override_wins_over_the_roles_own_catalog_cap()
    {
        Assert.Equal(Review.MaxToolSteps, RoleDispatch.ToBinding(Review, "spec").MaxToolSteps);
        Assert.Equal(500, RoleDispatch.ToBinding(Review, "spec", maxToolStepsOverride: 500).MaxToolSteps);
        Assert.NotEqual(Review.MaxToolSteps, 500);
    }

    /// <summary>
    /// #1691: the same threading proof for --billed-rate-limit. The role's own value is null (no role
    /// declares one), so the discriminating arm is that an override REACHES the entry — a build
    /// dropping billedRateLimitOverride would leave null here and, unlike the two axes above, there is
    /// no catalog default to mask it.
    /// </summary>
    [Fact]
    public void A_billed_rate_limit_override_reaches_the_binding_where_the_role_declares_none()
    {
        Assert.Null(Review.BilledRateLimit);
        Assert.Null(RoleDispatch.ToBinding(Review, "spec").BilledRateLimit);
        Assert.Equal(250_000, RoleDispatch.ToBinding(Review, "spec", billedRateLimitOverride: 250_000).BilledRateLimit);
    }

    [Fact]
    public void The_adapter_defaults_to_the_roles_tier_but_an_override_wins()
    {
        // review is a claude-tier role; overriding to agy must change it (and normalize case),
        // so the two arms differ regardless of which tier review sits on later.
        Assert.Equal(Review.Adapter, RoleDispatch.ToBinding(Review, "spec").Adapter);
        var overridden = RoleDispatch.ToBinding(Review, "spec", "agy").Adapter;
        Assert.Equal("agy", overridden);
        Assert.NotEqual(Review.Adapter, overridden);
    }

    [Fact]
    public void Materialize_produces_one_step_keyed_by_the_role_id_whose_outputs_mirror_the_contract()
    {
        var (definition, bindings) = RoleDispatch.Materialize(Review, "spec");

        var step = Assert.Single(definition.Steps);
        Assert.Equal("review", step.StepId.Value);
        Assert.Equal("review", step.Worker);
        Assert.Empty(step.DependsOn);
        // Step output names mirror the contract's; this pins that alignment (its rationale lives on RoleDispatch).
        Assert.Equal(
            Review.Outputs.Select(o => o.Name).OrderBy(n => n),
            step.Outputs.OrderBy(n => n));

        var binding = Assert.Contains("review", bindings);
        Assert.Equal(step.Outputs.OrderBy(n => n), binding.Contract.ProducedOutputs.Select(o => o.Name).OrderBy(n => n));
    }

    [Fact]
    public void ToBinding_on_agy_adapter_for_write_files_false_role_with_outputs_materializes_audited_grant()
    {
        var binding = RoleDispatch.ToBinding(Review, "spec", "agy");

        Assert.True(binding.PermissionGrant?.WriteFiles);
        Assert.Equal(GrantAuditMode.AuditedNotEnforced, binding.GrantAuditMode);
    }

    [Fact]
    public void ToBinding_on_claude_adapter_for_write_files_false_role_with_outputs_keeps_enforced_grant()
    {
        var binding = RoleDispatch.ToBinding(Review, "spec", "claude");

        Assert.False(binding.PermissionGrant?.WriteFiles);
        Assert.Equal(GrantAuditMode.Enforced, binding.GrantAuditMode);
    }

    private static WorkerRole Advise => WorkerRoleCatalog.For("advise");
    // #1861 moved advise's tier onto claude, so the vendor-swap tests below use janitor -- the role
    // whose tier (cheap) still pins an agy model -- to keep swapping agy -> claude, the measured #1082
    // direction. Janitor's grant is Enforced on claude, so a swap keeps WorkingDirectory in place
    // rather than moving it into a worktree spec the way an audited (withheld-write) role would.
    private static WorkerRole Janitor => WorkerRoleCatalog.For("janitor");

    [Fact]
    public void An_adapter_override_to_a_different_vendor_drops_the_tiers_vendor_specific_model()
    {
        // janitor is an agy-tier role whose tier pins a (gemini) model; running it on claude must NOT
        // carry that vendor-specific string to claude's CLI — the measured #1082 failure. With no
        // explicit --model, the swapped vendor falls back to its own default (null model).
        Assert.False(string.IsNullOrEmpty(Janitor.Model)); // the tier really does pin a model to drop
        Assert.Equal("agy", Janitor.Adapter);              // so "claude" below really is a swap

        var onClaude = RoleDispatch.ToBinding(Janitor, "spec", "claude");
        Assert.Equal("claude", onClaude.Adapter);
        Assert.Null(onClaude.Model);

        // Control — same vendor keeps the tier's own model, so this is about the swap, not a blanket null.
        Assert.Equal(Janitor.Model, RoleDispatch.ToBinding(Janitor, "spec").Model);
    }

    [Fact]
    public void An_explicit_model_override_wins_over_both_the_tier_and_the_vendor_swap()
    {
        // The model is its own axis (0017/0033): an explicit --model is used verbatim, whether or not
        // the vendor is also swapped. Janitor (agy tier) keeps the first arm a REAL swap now that
        // advise's tier is claude (#1861) -- the tier's own model is a gemini string, so "opus" here
        // can only have come from the override.
        Assert.Equal("agy", Janitor.Adapter);
        Assert.Equal("opus", RoleDispatch.ToBinding(Janitor, "spec", "claude", modelOverride: "opus").Model);
        Assert.Equal("gemini-x", RoleDispatch.ToBinding(Janitor, "spec", modelOverride: "gemini-x").Model);
    }

    [Fact]
    public void Effort_is_its_own_axis_dropped_on_a_vendor_swap_but_kept_on_the_same_vendor_and_overridable()
    {
        // The catalog pins raw vendor flag values as effort ("high"/"low"), not the canonical 0023
        // vocabulary an adapter would map — so effort is vendor-specific in practice and, like the model,
        // must not ride a vendor swap (an "xhigh"/"max" tier would leak onto agy, which rejects those).
        Assert.False(string.IsNullOrEmpty(Review.Effort)); // the tier really does pin an effort to drop

        Assert.Null(RoleDispatch.ToBinding(Review, "spec", "agy").Effort);          // swapped: dropped
        Assert.Equal(Review.Effort, RoleDispatch.ToBinding(Review, "spec").Effort); // same vendor: kept
        Assert.Equal("quick", RoleDispatch.ToBinding(Review, "spec", effortOverride: "quick").Effort); // override wins
    }

    [Fact]
    public void ToBinding_pins_the_working_directory_when_given_so_the_worker_can_read_the_project()
    {
        // #1083 polarity: a null binding pins no directory, a given one pins it. The rationale — why an
        // unpinned binding stranded repo reads — lives on RoleDispatch.workingDirectory.
        Assert.Null(RoleDispatch.ToBinding(Review, "spec").WorkingDirectory);
        Assert.Equal("/repo/root", RoleDispatch.ToBinding(Review, "spec", workingDirectory: "/repo/root").WorkingDirectory);
    }

    [Fact]
    public void Materialize_threads_the_working_directory_and_axis_overrides_onto_the_binding()
    {
        var (_, bindings) = RoleDispatch.Materialize(
            Janitor, "spec", "claude", workingDirectory: "/w", effortOverride: "careful");
        var binding = Assert.Contains("janitor", bindings);

        Assert.Equal("claude", binding.Adapter);
        Assert.Null(binding.Model);              // vendor swapped, no explicit --model
        Assert.Equal("careful", binding.Effort);
        Assert.Equal("/w", binding.WorkingDirectory);
    }

    [Fact]
    public void Patch_role_resolves_with_expected_contract_and_grant_polarity_per_adapter()
    {
        var patchRole = WorkerRoleCatalog.For("patch");
        Assert.Equal("patch", patchRole.Id);

        var claudeBinding = RoleDispatch.ToBinding(patchRole, "Propose a patch.", "claude");
        Assert.Single(claudeBinding.Contract.ProducedOutputs);
        Assert.Equal("patch.diff", claudeBinding.Contract.ProducedOutputs[0].Name);
        Assert.Equal(OutputSchema.Diff, claudeBinding.Contract.ProducedOutputs[0].Schema);
        Assert.False(claudeBinding.PermissionGrant?.WriteFiles);
        Assert.Equal(GrantAuditMode.Enforced, claudeBinding.GrantAuditMode);

        var agyBinding = RoleDispatch.ToBinding(patchRole, "Propose a patch.", "agy");
        Assert.True(agyBinding.PermissionGrant?.WriteFiles);
        Assert.Equal(GrantAuditMode.AuditedNotEnforced, agyBinding.GrantAuditMode);
    }

    [Fact]
    public void OutputOverride_replaces_primary_output_name_and_updates_prompt_instructions()
    {
        var binding = RoleDispatch.ToBinding(Advise, "spec", outputOverride: "custom-advice.md");
        Assert.Equal("custom-advice.md", binding.Contract.ProducedOutputs[0].Name);
        Assert.Contains("custom-advice.md", binding.PromptTemplate);
    }

    /// <summary>
    /// R1's polarity, per <see cref="RoleDispatch.ToBinding"/>'s <c>autoProvisionWorktree</c> doc — this
    /// mapping step declares the worktree spec but never stamps <see cref="WorkerBindingConfigEntry.IsWorktree"/>
    /// itself, so a hand-authored or prematurely-set <c>true</c> can never claim an isolation this step
    /// did not provide.
    /// </summary>
    [Fact]
    public void Worktree_is_always_declared_fresh_for_an_audited_grant_regardless_of_the_callers_directory_shape()
    {
        var binding = RoleDispatch.ToBinding(Review, "spec", adapterOverride: "agy", workingDirectory: "/any/caller/directory");

        Assert.Equal(GrantAuditMode.AuditedNotEnforced, binding.GrantAuditMode);
        Assert.NotNull(binding.Worktree);
        Assert.Equal("/any/caller/directory", binding.Worktree!.Repository);
        Assert.Equal("HEAD", binding.Worktree!.Ref);
        Assert.Null(binding.WorkingDirectory);
        Assert.False(binding.IsWorktree);
    }

    [Fact]
    public void Attached_files_are_listed_in_the_prompt()
    {
        var binding = RoleDispatch.ToBinding(
            Review, "Review the change.",
            attachments: ["C:/test/file1.txt", "C:/test/file2.md"],
            attachmentsDirectory: "C:/room/artifacts/attachments");

        Assert.Contains("Attached files (in C:/room/artifacts/attachments): file1.txt, file2.md", binding.PromptTemplate);
    }

    /// <summary>
    /// #1622 (b)/#1390: pins the exact role set <see cref="WorkerBindingConfigEntry.ChangesTree"/>
    /// derives against the live <see cref="WorkerRoleCatalog.All"/> (never a second hardcoded list,
    /// per that field's own remarks), including that no <c>fix</c> role exists to derive against.
    /// </summary>
    [Fact]
    public void ChangesTree_is_derived_from_the_catalogs_own_write_and_shell_grant_for_every_role()
    {
        var expected = new Dictionary<string, bool>
        {
            ["advise"] = false,
            ["implement"] = true,
            ["review"] = false,
            ["patch"] = false,
            ["fact-check"] = false,
            ["janitor"] = true,
            ["orchestrate"] = false,
        };

        var actualRoleIds = WorkerRoleCatalog.All.Select(role => role.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.Equal(expected.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList(), actualRoleIds);

        foreach (var role in WorkerRoleCatalog.All)
        {
            var binding = RoleDispatch.ToBinding(role, "spec");
            Assert.Equal(expected[role.Id], binding.ChangesTree);
        }
    }

    /// <summary>
    /// The specific defect the derivation avoids, per <see cref="WorkerBindingConfigEntry.ChangesTree"/>'s
    /// own remarks: <c>fact-check</c> forced onto an adapter without outbox support reaches the
    /// widened-grant shape those remarks describe, and <c>ChangesTree</c> must still read false.
    /// </summary>
    [Fact]
    public void ChangesTree_stays_false_even_when_the_grant_widens_write_files_for_a_non_outbox_adapter()
    {
        var factCheck = WorkerRoleCatalog.For("fact-check");
        var binding = RoleDispatch.ToBinding(factCheck, "spec", adapterOverride: "agy");

        Assert.Equal(GrantAuditMode.AuditedNotEnforced, binding.GrantAuditMode);
        Assert.True(binding.PermissionGrant!.WriteFiles, "the widened grant this test targets must actually have fired");
        Assert.False(binding.ChangesTree);
    }

    // #1745: a synthetic role, not a catalog fixture -- ToBinding's resolution of TokenBudgetSpec
    // against the winning adapter needs no JSON, only a WorkerRole with a TokenBudget set.
    private static WorkerRole MakeRole(string id, string tier, string adapter, TokenBudgetSpec? tokenBudget) => new(
        Id: id,
        Tier: tier,
        Adapter: adapter,
        Model: null,
        Effort: null,
        Grant: new PermissionGrant(ReadFiles: true, WriteFiles: true),
        Timeout: TimeSpan.FromMinutes(10),
        ProducesVerdict: false,
        Purpose: "p",
        Outputs: [new WorkerRoleOutput("out.md", OutputSchema.None, "Write to out.md.")],
        TokenBudget: tokenBudget);

    /// <summary>#1745: a role with a single figure keeps resolving to that figure regardless of adapter.</summary>
    [Fact]
    public void A_single_number_token_budget_resolves_the_same_for_every_adapter()
    {
        var role = MakeRole("r", "t", "claude", new TokenBudgetSpec.Fixed(500_000));

        Assert.Equal(500_000, RoleDispatch.ToBinding(role, "spec").TokenBudget);
        Assert.Equal(500_000, RoleDispatch.ToBinding(role, "spec", adapterOverride: "agy").TokenBudget);
    }

    /// <summary>#1745: a role with a per-adapter map resolves the dispatched adapter's own figure.</summary>
    [Fact]
    public void A_per_adapter_token_budget_map_resolves_the_dispatched_adapters_own_figure()
    {
        var role = MakeRole("r", "t", "claude", new TokenBudgetSpec.PerAdapter(
            new Dictionary<string, long> { ["claude"] = 300_000, ["agy"] = 900_000 }));

        Assert.Equal(300_000, RoleDispatch.ToBinding(role, "spec").TokenBudget);
        Assert.Equal(300_000, RoleDispatch.ToBinding(role, "spec", adapterOverride: "claude").TokenBudget);
        Assert.Equal(900_000, RoleDispatch.ToBinding(role, "spec", adapterOverride: "agy").TokenBudget);
    }

    /// <summary>#1745: see TokenBudgetSpec.Resolve's own remarks for the fail-closed case this pins.</summary>
    [Fact]
    public void A_per_adapter_map_missing_the_dispatched_adapter_refuses_at_dispatch()
    {
        var role = MakeRole("r", "t", "claude", new TokenBudgetSpec.PerAdapter(
            new Dictionary<string, long> { ["claude"] = 300_000 }));

        var ex = Assert.Throws<TokenBudgetAdapterNotConfiguredException>(
            () => RoleDispatch.ToBinding(role, "spec", adapterOverride: "agy"));

        Assert.Equal("r", ex.RoleId);
        Assert.Equal("agy", ex.Adapter);
        Assert.Contains("agy", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>#1745: see TokenBudgetSpec.Resolve's own remarks for the unwatched case this pins.</summary>
    [Fact]
    public void A_per_adapter_map_run_on_an_unrecognized_adapter_resolves_to_no_budget_rather_than_refusing()
    {
        var role = MakeRole("r", "t", "claude", new TokenBudgetSpec.PerAdapter(
            new Dictionary<string, long> { ["claude"] = 300_000, ["agy"] = 900_000 }));

        Assert.Null(RoleDispatch.ToBinding(role, "spec", adapterOverride: "fake").TokenBudget);
    }

    /// <summary>#1745: --token-budget wins outright, whether the role carries a single figure or a map.</summary>
    [Fact]
    public void A_token_budget_override_wins_over_either_shape()
    {
        var fixedRole = MakeRole("r1", "t", "claude", new TokenBudgetSpec.Fixed(500_000));
        var mapRole = MakeRole("r2", "t", "claude", new TokenBudgetSpec.PerAdapter(
            new Dictionary<string, long> { ["claude"] = 300_000 }));

        Assert.Equal(1, RoleDispatch.ToBinding(fixedRole, "spec", tokenBudgetOverride: 1).TokenBudget);
        Assert.Equal(2, RoleDispatch.ToBinding(mapRole, "spec", tokenBudgetOverride: 2).TokenBudget);
        // Even an adapter the map has no entry for is never consulted once an override is supplied.
        Assert.Equal(3, RoleDispatch.ToBinding(mapRole, "spec", adapterOverride: "agy", tokenBudgetOverride: 3).TokenBudget);
    }

    /// <summary>
    /// #1927: a dispatch that names no <c>--model</c> still records what it will run on, for each of
    /// the three adapters, and records which rung answered. The measured symptom was a room dispatched
    /// <c>--adapter agy</c> with no model rendering a bare vendor everywhere.
    /// </summary>
    [Theory]
    // No override at all: the role's own tier (frontier -> claude/opus) answers.
    [InlineData(null, "opus")]
    // A vendor swap drops the tier's model, so the vendor's own measured CLI default answers -- the
    // exact dispatch shape that produced the bare vendor.
    [InlineData("agy", "gemini-3.8-flash-high")]
    [InlineData("codex", "gpt-6-astra")]
    public void A_dispatch_with_no_model_records_the_model_it_resolved_and_says_it_was_resolved(
        string? adapterOverride, string expectedModel)
    {
        var binding = RoleDispatch.ToBinding(Review, "spec", adapterOverride: adapterOverride);

        Assert.Equal(expectedModel, binding.ModelResolved);
        Assert.Equal(BindingValueSource.ResolvedDefault, binding.ModelSource);
    }

    /// <summary>
    /// #1927, the polarity arm: an explicitly requested model is recorded as REQUESTED, so a render
    /// surface never marks an operator's own choice as a fallback.
    /// </summary>
    [Fact]
    public void A_dispatch_that_names_a_model_records_it_as_requested_rather_than_resolved()
    {
        var binding = RoleDispatch.ToBinding(Review, "spec", modelOverride: "haiku", effortOverride: "low");

        Assert.Equal("haiku", binding.ModelResolved);
        Assert.Equal(BindingValueSource.Requested, binding.ModelSource);
        Assert.Equal("low", binding.EffortResolved);
        Assert.Equal(BindingValueSource.Requested, binding.EffortSource);
    }

    /// <summary>
    /// #1927: the invariant spec/baton.md §2 rules — a stamp may never reach the fields that become the
    /// vendor's argv. The agy swap is where the two diverge most visibly: the stamp names a model and
    /// the dispatch input stays null.
    /// </summary>
    [Fact]
    public void Resolving_a_default_model_never_changes_what_is_passed_to_the_vendor_cli()
    {
        var swapped = RoleDispatch.ToBinding(Review, "spec", adapterOverride: "agy");

        Assert.Null(swapped.Model);
        Assert.Null(swapped.Effort);
        Assert.Equal("gemini-3.8-flash-high", swapped.ModelResolved);
        // The same invariant on the effort axis, which since the review's MEDIUM does resolve here:
        // the stamp names an effort and the dispatch input stays null.
        Assert.Equal("high", swapped.EffortResolved);
    }

    /// <summary>
    /// #1927 review MEDIUM — the issue's own stated mechanism, "agy's effort is the id suffix", which
    /// went unimplemented and left <c>EffortResolved</c> an exact duplicate of <c>Effort</c> for every
    /// input. Read off the RESOLVED model, not the tier's: the room the rung exists for is
    /// <c>--adapter agy</c> with no <c>--model</c>, where the swap dropped the tier model and the
    /// adapter default is what named one. Rests on <c>AgyWorkerAdapter.GeminiEffortSuffix</c>, the same
    /// rule the adapter already enforces agreement against.
    /// </summary>
    [Fact]
    public void An_agy_swap_resolves_the_effort_the_models_own_id_suffix_encodes()
    {
        var binding = RoleDispatch.ToBinding(Review, "spec", adapterOverride: "agy");

        Assert.Equal("gemini-3.8-flash-high", binding.ModelResolved);
        Assert.Equal("high", binding.EffortResolved);
        // Not "requested" -- ResolveEffortStamp's own doc says why this rung resolves rather than asks.
        Assert.Equal(BindingValueSource.ResolvedDefault, binding.EffortSource);

        // The same rung on an explicitly requested suffixed id, so the arm is about the suffix rather
        // than about the adapter default alone.
        var low = RoleDispatch.ToBinding(Review, "spec", adapterOverride: "agy", modelOverride: "gemini-3.8-flash-low");
        Assert.Equal("low", low.EffortResolved);
        Assert.Equal(BindingValueSource.ResolvedDefault, low.EffortSource);
    }

    /// <summary>
    /// The polarity arm for the rung above, both halves: a model id carrying no effort suffix resolves
    /// no effort, and neither does a NON-agy vendor whose model id happens to end in one — the suffix
    /// rule is agy's, and <c>gpt-oss-120b-medium</c>'s trailing <c>-medium</c> is part of a name rather
    /// than an effort (<c>AgyWorkerAdapter.GeminiEffortSuffix</c>'s own claim-scope note).
    /// </summary>
    [Fact]
    public void A_model_id_with_no_effort_suffix_resolves_no_effort()
    {
        var bare = RoleDispatch.ToBinding(Review, "spec", adapterOverride: "agy", modelOverride: "gpt-oss-120b-medium");
        Assert.Null(bare.EffortResolved);
        Assert.Null(bare.EffortSource);

        var otherVendor = RoleDispatch.ToBinding(Review, "spec", adapterOverride: "codex", modelOverride: "gemini-3.8-flash-high");
        Assert.Null(otherVendor.EffortResolved);
        Assert.Null(otherVendor.EffortSource);
    }

    /// <summary>
    /// #1927: every rung silent yields no stamp at all — see <c>AdapterDefaultModels</c> for why claude
    /// carries no entry. <c>orchestrate</c> is the shipped role whose tier names no model, so this
    /// exercises the case through the real catalog rather than a fabricated role.
    /// </summary>
    [Fact]
    public void An_unmeasured_adapter_default_leaves_the_resolved_model_absent()
    {
        var binding = RoleDispatch.ToBinding(WorkerRoleCatalog.For("orchestrate"), "spec");

        Assert.Equal("claude", binding.Adapter);
        Assert.Null(binding.ModelResolved);
        Assert.Null(binding.ModelSource);
    }
}

