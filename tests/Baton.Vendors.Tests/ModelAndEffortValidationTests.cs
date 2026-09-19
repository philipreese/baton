using System.Linq;
using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Vendors.Tests;

/// <summary>
/// #1090: --model/--effort are validated at the adapter boundary (adapter isolation — each adapter
/// owns its vendor's id/effort grammar) before the dispatch pump, so a malformed value fails fast
/// with a clear baton error instead of a cryptic vendor failure the retry loop repeats. Polarity is
/// asserted in both directions: the valid form must still resolve unchanged, or the check is just a
/// blanket reject. Rationale (measured claude behaviour; the agy one-control-two-spellings rule) lives
/// on the two exception types, not restated here.
/// </summary>
[Collection(LaunchConfigCollection.Name)]
public class ModelAndEffortValidationTests
{
    private static readonly WorkerContract Contract = new(
        "architect", ["goal"], [new ProducedOutput("plan.md")], []);

    private static CoreDispatchTarget Resolve(IWorkerAdapter adapter, string? model = null, string? effort = null) =>
        adapter.Resolve(new WorkerInvocation("Draft a plan.", Model: model, Effort: effort), Contract);

    // --- claude: the dash→dot typo is caught; every valid form still resolves ---

    [Fact]
    public void Claude_rejects_a_dot_delimited_model_id_as_the_dash_typo_it_is()
    {
        var ex = Assert.Throws<MalformedVendorModelException>(
            () => Resolve(new ClaudeWorkerAdapter(), model: "claude-opus-4.8"));
        // The message must name the dash correction, or it does not help the operator fix the typo.
        Assert.Contains("claude-opus-4-8", ex.Message);
    }

    [Fact]
    public void Claude_passes_the_dash_delimited_full_id_through_verbatim()
    {
        var target = Resolve(new ClaudeWorkerAdapter(), model: "claude-opus-4-8");
        Assert.Contains(
            target.Args.Zip(target.Args.Skip(1)),
            p => p.First == "--model" && p.Second == "claude-opus-4-8");
    }

    [Fact]
    public void Claude_leaves_an_alias_untouched()
    {
        // Aliases carry no dot, so the typo check must not fire on them.
        var target = Resolve(new ClaudeWorkerAdapter(), model: "opus");
        Assert.Contains(
            target.Args.Zip(target.Args.Skip(1)),
            p => p.First == "--model" && p.Second == "opus");
    }

    // --- agy: effort reconciled against the value set and the model-name suffix ---

    [Fact]
    public void Agy_rejects_an_effort_that_disagrees_with_the_models_own_suffix()
    {
        Assert.Throws<IncoherentVendorEffortException>(
            () => Resolve(new AgyWorkerAdapter(), model: "gemini-3.6-flash-low", effort: "high"));
    }

    [Fact]
    public void Agy_keeps_an_agreeing_suffix_and_effort_pair_byte_for_byte()
    {
        // The measured passing invocation (gemini-3.1-pro-high --effort high → PONG): both flags
        // survive, unchanged. Dropping the redundant --effort would emit an argv nobody measured.
        var target = Resolve(new AgyWorkerAdapter(), model: "gemini-3.1-pro-high", effort: "high");
        Assert.Contains(target.Args.Zip(target.Args.Skip(1)), p => p.First == "--model" && p.Second == "gemini-3.1-pro-high");
        Assert.Contains(target.Args.Zip(target.Args.Skip(1)), p => p.First == "--effort" && p.Second == "high");
    }

    [Fact]
    public void Agy_rejects_an_effort_outside_its_value_set()
    {
        // agy's set is exactly {low, medium, high}; xhigh/max leak onto agy only via an explicit
        // --effort override (RoleDispatch drops effort on a vendor swap but an override wins).
        Assert.Throws<IncoherentVendorEffortException>(
            () => Resolve(new AgyWorkerAdapter(), effort: "xhigh"));
    }

    [Fact]
    public void Agy_leaves_a_suffixed_model_with_no_separate_effort_alone()
    {
        var target = Resolve(new AgyWorkerAdapter(), model: "gemini-3.6-flash-high");
        Assert.Contains(target.Args.Zip(target.Args.Skip(1)), p => p.First == "--model" && p.Second == "gemini-3.6-flash-high");
        Assert.DoesNotContain("--effort", target.Args);
    }

    [Fact]
    public void Agy_refuses_a_suffix_less_gemini_model_with_no_effort_up_front()
    {
        // #1596: measured against agy 1.1.24 -- `agy --model gemini-3.7-flash -p ...` (no --effort)
        // spawns and only then refuses with "--model gemini-3.7-flash requires --effort (available:
        // low, medium, high)". This must fire at Resolve, before any process is spawned.
        var ex = Assert.Throws<IncoherentVendorEffortException>(
            () => Resolve(new AgyWorkerAdapter(), model: "gemini-3.7-flash"));
        Assert.Contains("gemini-3.7-flash", ex.Message);
        Assert.Contains("requires --effort", ex.Message);
    }

    [Fact]
    public void Agy_resolves_a_suffix_less_gemini_model_when_effort_is_given()
    {
        // Same model as the refusal test above, but with --effort supplied: the up-front check must
        // only fire on a missing effort, not on the model name itself.
        var target = Resolve(new AgyWorkerAdapter(), model: "gemini-3.7-flash", effort: "high");
        Assert.Contains(target.Args.Zip(target.Args.Skip(1)), p => p.First == "--model" && p.Second == "gemini-3.7-flash");
        Assert.Contains(target.Args.Zip(target.Args.Skip(1)), p => p.First == "--effort" && p.Second == "high");
    }

    [Fact]
    public void Agy_leaves_a_non_gemini_model_with_no_effort_alone()
    {
        // #1596's own scope note covers only the gemini family ("whether every gemini model ...
        // is unmeasured"); whether a non-gemini model requires --effort is simply unmeasured, not
        // measured-negative, so the check stays scoped to `gemini-` and must not fire here --
        // today's behaviour (no check) is preserved rather than guessed at.
        var target = Resolve(new AgyWorkerAdapter(), model: "claude-sonnet-4-6");
        Assert.Contains(target.Args.Zip(target.Args.Skip(1)), p => p.First == "--model" && p.Second == "claude-sonnet-4-6");
        Assert.DoesNotContain("--effort", target.Args);
    }

    [Fact]
    public void Agy_leaves_a_dispatch_with_no_model_at_all_alone()
    {
        // No model means agy's own default is used; the up-front check must not fire on a null model.
        var target = Resolve(new AgyWorkerAdapter());
        Assert.DoesNotContain("--model", target.Args);
        Assert.DoesNotContain("--effort", target.Args);
    }

    [Fact]
    public void The_shipped_catalog_satisfies_the_agy_reconciliation_invariant()
    {
        // Guards a future WorkerTiers.json edit that pins a suffix-model with a disagreeing effort:
        // every shipped agy-tier role must resolve without the reconciliation refusing it.
        var agy = new AgyWorkerAdapter();
        foreach (var role in WorkerRoleCatalog.All.Where(r => string.Equals(r.Adapter, "agy", System.StringComparison.OrdinalIgnoreCase)))
        {
            var ex = Record.Exception(() =>
                agy.Resolve(new WorkerInvocation("spec", Model: role.Model, Effort: role.Effort), Contract));
            Assert.True(ex is null, $"agy role '{role.Id}' (model={role.Model}, effort={role.Effort}) was refused: {ex?.Message}");
        }
    }

    [Fact]
    public void Agy_implements_polymorphic_validate_requested_effort()
    {
        IWorkerAdapter adapter = new AgyWorkerAdapter();
        adapter.ValidateRequestedEffort("gemini-3.1-pro-high", "high");
        adapter.ValidateRequestedEffort("gemini-3.1-pro-high", "careful");

        Assert.Throws<IncoherentVendorEffortException>(
            () => adapter.ValidateRequestedEffort("gemini-3.6-flash-low", "high"));
        Assert.Throws<IncoherentVendorEffortException>(
            () => adapter.ValidateRequestedEffort("gemini-3.7-flash", "xhigh"));
    }

    [Fact]
    public void Agy_implements_polymorphic_validate_requested_invocation()
    {
        IWorkerAdapter adapter = new AgyWorkerAdapter();
        adapter.ValidateRequestedInvocation("gemini-3.7-flash", "high");

        var ex = Assert.Throws<IncoherentVendorEffortException>(
            () => adapter.ValidateRequestedInvocation("gemini-3.7-flash", null));
        Assert.Contains("requires --effort", ex.Message);
    }
}
