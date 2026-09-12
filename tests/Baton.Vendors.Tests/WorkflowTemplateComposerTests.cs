using Baton.Vendors;
using Baton.Domain;
using Baton.Templates;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// Proves the composer turns a <see cref="WorkflowTemplate"/> into a Pipeline DAG per decisions 0047
/// and 0025: sequential single-blocker input flow, phase-name keying (a role may appear twice),
/// AskFirst → an approval pause point, and the <c>diff-of-work-so-far</c> capture step spliced in as
/// the declaring phase's blocker rather than added as a second input.
/// </summary>
[Collection(WorkerRoleCatalogCollection.Name)]
public class WorkflowTemplateComposerTests
{
    private static WorkflowTemplatePhase Phase(string name, string roleId, bool askFirst = false, params string[] inputs) =>
        new(name, roleId, $"do {name}", askFirst, inputs);

    [Fact]
    public void Sequential_phases_chain_each_steps_input_to_its_predecessors_output()
    {
        var template = new WorkflowTemplate("t", [Phase("build", "implement"), Phase("check", "review")]);

        var (definition, bindings) = WorkflowTemplateComposer.Materialize(template, adapterOverride: "claude");

        Assert.Equal(template.Id, definition.WorkflowTemplateId.Value);
        Assert.Equal(2, definition.Steps.Count);

        var first = definition.Steps[0];
        Assert.Equal("build", first.StepId.Value);
        Assert.Empty(first.Inputs);
        Assert.Empty(first.DependsOn);
        Assert.NotEmpty(first.Outputs);

        var second = definition.Steps[1];
        Assert.Equal("check", second.StepId.Value);
        Assert.Equal(first.Outputs, second.Inputs);                 // the one blocker's output flows in (0025)
        Assert.Equal(new[] { first.StepId }, second.DependsOn);

        Assert.True(bindings.ContainsKey("build"));
        Assert.True(bindings.ContainsKey("check"));
        Assert.Equal("claude", bindings["build"].Adapter);          // override applied uniformly

        // #1147: the contract must mirror the step's Inputs, or the prompt never discloses the
        // BATON_INPUT_<n> path and the phase is handed its upstream artifact undisclosed.
        Assert.Empty(bindings["build"].Contract.RequiredInputs);
        Assert.Equal(second.Inputs, bindings["check"].Contract.RequiredInputs);
    }

    [Fact]
    public void Every_phase_binding_gets_the_workspace_pinned_so_a_template_phase_can_read_the_repo()
    {
        // #1083 covers a role run as a template phase too, not only `baton dispatch <role>` — without the
        // pin an agy phase (which ignores the process cwd) would be handed no path to the repo.
        var template = new WorkflowTemplate("t", [Phase("build", "implement"), Phase("check", "review")]);

        var (_, pinned) = WorkflowTemplateComposer.Materialize(template, workingDirectory: "/repo/root");
        Assert.Equal("/repo/root", pinned["build"].WorkingDirectory);
        Assert.Equal("/repo/root", pinned["check"].WorkingDirectory);

        // Control — omitting it leaves the pre-#1083 null, so this is about the pin, not a blanket value.
        var (_, unpinned) = WorkflowTemplateComposer.Materialize(template);
        Assert.Null(unpinned["build"].WorkingDirectory);
    }

    /// <summary>
    /// R5 (#1354/#1380, finding 6) — the scope decision itself lives on this composer's own
    /// <c>autoProvisionWorktree: false</c> call site. Pins the resulting shape: WorkingDirectory set
    /// directly, no Worktree spec, so <c>WorkerBindingResolver</c>'s <c>UnisolatedGrantAuditException</c>
    /// is what refuses an audited phase at bind time, not this composer.
    /// </summary>
    [Fact]
    public void An_audited_phase_declares_no_worktree_reverting_to_the_pre_auto_provisioning_bind_time_refusal()
    {
        var template = new WorkflowTemplate("t", [Phase("check", "review")]);

        var (_, bindings) = WorkflowTemplateComposer.Materialize(
            template, adapterOverride: CommandWorkerAdapter.AdapterName, workingDirectory: "/repo/root");

        var binding = bindings["check"];
        Assert.Equal(GrantAuditMode.AuditedNotEnforced, binding.GrantAuditMode);
        Assert.Null(binding.Worktree);
        Assert.Equal("/repo/root", binding.WorkingDirectory);
        Assert.False(binding.IsWorktree);
    }

    [Fact]
    public void Two_phases_naming_the_same_role_key_by_phase_name_without_collision()
    {
        var template = new WorkflowTemplate("t", [Phase("first-look", "review"), Phase("second-look", "review")]);

        var (definition, bindings) = WorkflowTemplateComposer.Materialize(template);

        Assert.Equal(2, definition.Steps.Count);
        Assert.Equal(new[] { "first-look", "second-look" }, definition.Steps.Select(s => s.StepId.Value).ToArray());
        Assert.Equal(2, bindings.Count);
        Assert.True(bindings.ContainsKey("first-look"));
        Assert.True(bindings.ContainsKey("second-look"));
    }

    [Fact]
    public void AskFirst_maps_to_an_approval_pause_and_false_maps_to_none()
    {
        var template = new WorkflowTemplate("t", [Phase("gated", "implement", askFirst: true), Phase("open", "review", askFirst: false)]);

        var (definition, _) = WorkflowTemplateComposer.Materialize(template);

        var gated = definition.Steps.Single(s => s.StepId.Value == "gated");
        Assert.NotNull(gated.PausePoint);
        Assert.Empty(gated.PausePoint!.SupersedeTargets);           // plain approval gate; no supersede targets in the model yet
        Assert.Equal(PausePointKind.ReadyForReview, gated.PausePoint.Kind);

        var open = definition.Steps.Single(s => s.StepId.Value == "open");
        Assert.Null(open.PausePoint);
    }

    [Fact]
    public void Diff_of_work_so_far_splices_a_capture_step_as_the_declaring_phases_blocker()
    {
        var template = new WorkflowTemplate("t",
        [
            Phase("impl", "implement"),
            Phase("commit", "janitor"),
            Phase("review", "review", inputs: WorkflowTemplateComposer.DiffOfWorkSoFarInput),
        ]);

        var (definition, bindings) = WorkflowTemplateComposer.Materialize(template);

        // impl -> commit -> review-capture -> review
        Assert.Equal(new[] { "impl", "commit", "review-capture", "review" },
            definition.Steps.Select(s => s.StepId.Value).ToArray());

        var capture = definition.Steps.Single(s => s.StepId.Value == "review-capture");
        Assert.Empty(capture.Inputs);                               // capture runs git; reads no artifact
        Assert.Equal(new[] { WorkflowTemplateComposer.CaptureOutputName }, capture.Outputs.ToArray());
        Assert.Equal(new[] { new StepId("commit") }, capture.DependsOn); // ordered after the prior phase
        Assert.Null(capture.PausePoint);

        var review = definition.Steps.Single(s => s.StepId.Value == "review");
        Assert.Equal(new[] { new StepId("review-capture") }, review.DependsOn); // blocks on capture, not on commit
        Assert.Equal(new[] { WorkflowTemplateComposer.CaptureOutputName }, review.Inputs.ToArray()); // the diff flows in

        Assert.Equal(WorkflowTemplateComposer.CaptureAdapter, bindings["review-capture"].Adapter);
        Assert.False(bindings["review-capture"].PermissionGrant!.RunShellCommands); // engine-run, no vendor grant

        // #1147: the reviewing phase's contract discloses the captured diff — the feature is
        // decorative if the reviewer is never told where the diff landed.
        Assert.Equal(review.Inputs, bindings["review"].Contract.RequiredInputs);
    }

    [Fact]
    public void A_phase_naming_an_unknown_role_throws()
    {
        var template = new WorkflowTemplate("t", [Phase("bad", "no-such-role")]);

        Assert.Throws<KeyNotFoundException>(() => WorkflowTemplateComposer.Materialize(template));
    }

    [Fact]
    public void A_capture_declaring_first_phase_gets_a_capture_step_with_no_dependency()
    {
        // The one branch where the capture has no prior phase to order after (blockerId is null).
        var template = new WorkflowTemplate("t",
            [Phase("review", "review", inputs: WorkflowTemplateComposer.DiffOfWorkSoFarInput)]);

        var (definition, _) = WorkflowTemplateComposer.Materialize(template);

        Assert.Equal(new[] { "review-capture", "review" }, definition.Steps.Select(s => s.StepId.Value).ToArray());
        Assert.Empty(definition.Steps[0].DependsOn);                             // no prior phase
        Assert.Equal(new[] { new StepId("review-capture") }, definition.Steps[1].DependsOn);
        WorkflowDefinitionValidator.Validate(definition);                        // still a valid DAG
    }

    [Fact]
    public void A_phase_named_like_a_generated_capture_id_is_rejected()
    {
        var template = new WorkflowTemplate("t",
        [
            Phase("review-capture", "review"),
            Phase("review", "review", inputs: WorkflowTemplateComposer.DiffOfWorkSoFarInput),
        ]);

        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowTemplateComposer.Materialize(template));
        Assert.Contains("collides", ex.Message);
    }

    [Fact]
    public void Composed_definitions_satisfy_the_engines_own_WorkflowDefinitionValidator()
    {
        // The structural asserts above encode the composer's intended shape; this checks the ENGINE's
        // own rules (unique step ids, every DependsOn points at a real transitive ancestor, pause
        // targets are ancestors) accept it — so a composed template is runnable, not merely shaped as
        // expected. Uses the capture-splice + a gate, the shapes most likely to violate a rule.
        var withCapture = new WorkflowTemplate("cap",
        [
            Phase("impl", "implement"),
            Phase("commit", "janitor"),
            Phase("review", "review", askFirst: true, inputs: WorkflowTemplateComposer.DiffOfWorkSoFarInput),
        ]);

        var (definition, _) = WorkflowTemplateComposer.Materialize(withCapture);

        WorkflowDefinitionValidator.Validate(definition); // throws WorkflowDefinitionValidationException if invalid
    }
}
