using Baton.Vendors;
using Baton.Domain;

namespace Baton.Cli.Tests;

/// <summary>
/// The binding-inheritance rule behind <c>baton redispatch</c> with no <c>--spec</c> (#1441):
/// <see cref="RedispatchCommand.InheritBinding"/> starts from the parent's exact recorded entry and
/// applies only the axes the operator actually passed. Exercised as a pure unit against a hand-built
/// <see cref="WorkerBindingConfigEntry"/>, the same reusable-primitive testing
/// <see cref="Baton.Vendors.Tests.RoleDispatchTests"/> already does for <see cref="RoleDispatch.ToBinding"/>.
/// </summary>
public class RedispatchBindingTests
{
    private static WorkerBindingConfigEntry ParentEntry(
        string adapter = "claude", string? model = "opus", string? effort = "careful",
        string? workingDirectory = "/repo", WorktreeWorkspace? worktree = null,
        TimeSpan? timeout = null) =>
        new(
            Adapter: adapter,
            Contract: new WorkerContract("advise", [], [new ProducedOutput("advice.md")], []),
            PromptTemplate: "Weigh the options.\n\nRequired outputs:\n- Write advice.md",
            Timeout: timeout ?? TimeSpan.FromMinutes(30),
            Model: model,
            Effort: effort,
            WorkingDirectory: worktree is null ? workingDirectory : null,
            Worktree: worktree,
            SessionId: "prior-session-id",
            ResumeSession: false);

    [Fact]
    public void With_no_overrides_every_axis_is_inherited_verbatim()
    {
        var parent = ParentEntry();
        var options = new RedispatchOptions("parent-room", "new-room");

        var entry = RedispatchCommand.InheritBinding(parent, options);

        Assert.Equal(parent.Adapter, entry.Adapter);
        Assert.Equal(parent.Model, entry.Model);
        Assert.Equal(parent.Effort, entry.Effort);
        Assert.Equal(parent.WorkingDirectory, entry.WorkingDirectory);
        Assert.Equal(parent.Timeout, entry.Timeout);
        Assert.Equal(parent.PromptTemplate, entry.PromptTemplate);
        Assert.Equal(parent.Contract, entry.Contract);
    }

    [Fact]
    public void A_fresh_binding_never_inherits_the_parents_resumed_session_state()
    {
        var parent = ParentEntry();
        var entry = RedispatchCommand.InheritBinding(parent, new RedispatchOptions("parent-room", "new-room"));

        Assert.Null(entry.SessionId);
        Assert.False(entry.ResumeSession);
    }

    [Fact]
    public void An_explicit_adapter_override_wins_over_the_inherited_one()
    {
        var parent = ParentEntry(adapter: "claude");
        var entry = RedispatchCommand.InheritBinding(parent, new RedispatchOptions("parent-room", "new-room", Adapter: "agy"));

        Assert.Equal("agy", entry.Adapter);
    }

    [Fact]
    public void A_differently_cased_adapter_is_normalized_and_is_not_a_vendor_swap()
    {
        // The registry lookup is case-sensitive; ToBinding normalizes its winner, so this path must
        // too — and "Claude" over a "claude" parent is the SAME vendor, so model/effort survive.
        var parent = ParentEntry(adapter: "claude", model: "opus", effort: "careful");
        var entry = RedispatchCommand.InheritBinding(parent, new RedispatchOptions("parent-room", "new-room", Adapter: " Claude "));

        Assert.Equal("claude", entry.Adapter);
        Assert.Equal("opus", entry.Model);
        Assert.Equal("careful", entry.Effort);
    }

    [Fact]
    public void Stream_json_is_recomputed_for_the_new_adapter_not_inherited()
    {
        // Adapter-derived (#1089, #1540): agy and claude stream, others (e.g. shell) do not.
        var fromAgy = ParentEntry(adapter: "agy") with { StreamJson = true };
        Assert.True(RedispatchCommand.InheritBinding(fromAgy, new RedispatchOptions("parent-room", "new-room", Adapter: "claude")).StreamJson);
        Assert.False(RedispatchCommand.InheritBinding(fromAgy, new RedispatchOptions("parent-room", "new-room", Adapter: "shell")).StreamJson);

        var fromShell = ParentEntry(adapter: "shell") with { StreamJson = false };
        Assert.True(RedispatchCommand.InheritBinding(fromShell, new RedispatchOptions("parent-room", "new-room", Adapter: "agy")).StreamJson);
        Assert.True(RedispatchCommand.InheritBinding(fromShell, new RedispatchOptions("parent-room", "new-room", Adapter: "claude")).StreamJson);
    }

    /// <summary>Pins the axis rule <see cref="RedispatchCommand.InheritBinding"/>'s own comment cites (#1082).</summary>
    [Fact]
    public void An_adapter_swap_with_no_explicit_model_or_effort_drops_both_rather_than_carrying_them_across()
    {
        var parent = ParentEntry(adapter: "claude", model: "opus", effort: "careful");
        var entry = RedispatchCommand.InheritBinding(parent, new RedispatchOptions("parent-room", "new-room", Adapter: "agy"));

        Assert.Null(entry.Model);
        Assert.Null(entry.Effort);
    }

    [Fact]
    public void An_adapter_swap_with_an_explicit_model_and_effort_keeps_them()
    {
        var parent = ParentEntry(adapter: "claude", model: "opus", effort: "careful");
        var options = new RedispatchOptions("parent-room", "new-room", Adapter: "agy", Model: "gemini-x", Effort: "quick");

        var entry = RedispatchCommand.InheritBinding(parent, options);

        Assert.Equal("gemini-x", entry.Model);
        Assert.Equal("quick", entry.Effort);
    }

    [Fact]
    public void Same_adapter_with_no_override_keeps_the_parents_model_and_effort()
    {
        var parent = ParentEntry(adapter: "claude", model: "opus", effort: "careful");
        var entry = RedispatchCommand.InheritBinding(parent, new RedispatchOptions("parent-room", "new-room"));

        Assert.Equal("opus", entry.Model);
        Assert.Equal("careful", entry.Effort);
    }

    [Fact]
    public void A_timeout_override_wins_over_the_inherited_timeout()
    {
        var parent = ParentEntry(timeout: TimeSpan.FromMinutes(30));
        var entry = RedispatchCommand.InheritBinding(
            parent, new RedispatchOptions("parent-room", "new-room", Timeout: TimeSpan.FromMinutes(90)));

        Assert.Equal(TimeSpan.FromMinutes(90), entry.Timeout);
    }

    [Fact]
    public void With_no_label_override_the_parents_label_is_inherited()
    {
        var parent = ParentEntry() with { Label = "env-snapshot lane" };
        var entry = RedispatchCommand.InheritBinding(parent, new RedispatchOptions("parent-room", "new-room"));

        Assert.Equal("env-snapshot lane", entry.Label);
    }

    [Fact]
    public void An_explicit_label_override_wins_over_the_inherited_one()
    {
        var parent = ParentEntry() with { Label = "old label" };
        var entry = RedispatchCommand.InheritBinding(
            parent, new RedispatchOptions("parent-room", "new-room", Label: "new label"));

        Assert.Equal("new label", entry.Label);
    }

    [Fact]
    public void A_specified_blank_label_clears_the_parents_inherited_label()
    {
        var parent = ParentEntry() with { Label = "old label" };
        var entry = RedispatchCommand.InheritBinding(
            parent, new RedispatchOptions("parent-room", "new-room", Label: null, LabelSpecified: true));

        Assert.Null(entry.Label);
    }

    [Fact]
    public void A_parent_with_no_label_stays_unlabeled_when_not_overridden()
    {
        var parent = ParentEntry();
        var entry = RedispatchCommand.InheritBinding(parent, new RedispatchOptions("parent-room", "new-room"));

        Assert.Null(entry.Label);
    }

    [Fact]
    public void With_no_workstream_override_the_parents_workstream_is_inherited()
    {
        var parent = ParentEntry() with { Workstream = "w1619" };
        var entry = RedispatchCommand.InheritBinding(parent, new RedispatchOptions("parent-room", "new-room"));

        Assert.Equal("w1619", entry.Workstream);
    }

    [Fact]
    public void An_explicit_workstream_override_wins_over_the_inherited_one()
    {
        var parent = ParentEntry() with { Workstream = "old-workstream" };
        var entry = RedispatchCommand.InheritBinding(
            parent, new RedispatchOptions("parent-room", "new-room", Workstream: "new-workstream"));

        Assert.Equal("new-workstream", entry.Workstream);
    }

    [Fact]
    public void A_specified_blank_workstream_clears_the_parents_inherited_workstream()
    {
        var parent = ParentEntry() with { Workstream = "old-workstream" };
        var entry = RedispatchCommand.InheritBinding(
            parent, new RedispatchOptions("parent-room", "new-room", Workstream: null, WorkstreamSpecified: true));

        Assert.Null(entry.Workstream);
    }

    [Fact]
    public void A_parent_with_no_workstream_stays_ungrouped_when_not_overridden()
    {
        var parent = ParentEntry();
        var entry = RedispatchCommand.InheritBinding(parent, new RedispatchOptions("parent-room", "new-room"));

        Assert.Null(entry.Workstream);
    }

    [Fact]
    public void A_workspace_override_replaces_a_plain_working_directory()
    {
        var parent = ParentEntry(workingDirectory: "/repo");
        var entry = RedispatchCommand.InheritBinding(
            parent, new RedispatchOptions("parent-room", "new-room", WorkspaceDirectory: "/other-repo"));

        Assert.Equal("/other-repo", entry.WorkingDirectory);
        Assert.Null(entry.Worktree);
    }

    /// <summary>
    /// A worktree-shaped parent (an audited grant, RoleDispatch.ToBinding's autoProvisionWorktree
    /// branch) records its workspace on <see cref="WorkerBindingConfigEntry.Worktree"/>'s
    /// <c>Repository</c>, not <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> — a
    /// <c>--workspace</c> override must land on whichever one the parent actually populated.
    /// </summary>
    [Fact]
    public void A_workspace_override_replaces_the_repository_of_an_inherited_worktree_spec()
    {
        var parent = ParentEntry(worktree: new WorktreeWorkspace("/repo", "HEAD"));
        var entry = RedispatchCommand.InheritBinding(
            parent, new RedispatchOptions("parent-room", "new-room", WorkspaceDirectory: "/other-repo"));

        Assert.Null(entry.WorkingDirectory);
        Assert.NotNull(entry.Worktree);
        Assert.Equal("/other-repo", entry.Worktree!.Repository);
        Assert.Equal("HEAD", entry.Worktree!.Ref);
    }

    /// <summary>
    /// #1691: the parent's rate limit is carried when no override is passed, and replaced when one is.
    /// This is the axis #1686 review F2 found broken for <c>--max-tool-steps</c> — the no-<c>--spec</c>
    /// path carried it and the amended-spec path dropped it, so an operator's escape hatch did not
    /// survive a redispatch. Both polarities pinned here; <c>RedispatchCommandEndToEndTests</c> is
    /// where the amended-spec path itself is exercised.
    /// </summary>
    [Fact]
    public void A_billed_rate_limit_is_inherited_from_the_parent_and_overridden_when_passed()
    {
        var parent = ParentEntry() with { BilledRateLimit = 250_000 };

        Assert.Equal(250_000, RedispatchCommand.InheritBinding(
            parent, new RedispatchOptions("parent-room", "new-room")).BilledRateLimit);
        Assert.Equal(400_000, RedispatchCommand.InheritBinding(
            parent, new RedispatchOptions("parent-room", "new-room", BilledRateLimit: 400_000)).BilledRateLimit);
    }

    /// <summary>
    /// #1927 review HIGH: the four display stamps are adapter-derived exactly like
    /// <see cref="WorkerBindingConfigEntry.StreamJson"/>, so a vendor swap must RE-resolve them.
    /// Carrying them verbatim made a redispatched agy room display, stamp and ledger the parent's
    /// <c>opus</c> — the vendor-swap axis rule two tests above nulls <c>Model</c>, and every reader
    /// (<c>FleetStatusTool</c>, <c>RoomBindingStamps</c>, <c>CostLedgerStore</c>) then falls back to
    /// the stale <c>ModelResolved</c> precisely because it is null.
    /// </summary>
    [Fact]
    public void A_vendor_swap_re_resolves_the_display_stamps_rather_than_carrying_the_parents_across()
    {
        var parent = ParentEntry(adapter: "claude", model: "opus", effort: "careful") with
        {
            ModelResolved = "opus",
            ModelSource = BindingValueSource.Requested,
            EffortResolved = "careful",
            EffortSource = BindingValueSource.Requested,
        };

        var entry = RedispatchCommand.InheritBinding(
            parent, new RedispatchOptions("parent-room", "new-room", Adapter: "agy"));

        // The agy CLI default (Baton.Domain.AdapterDefaultModels), not the parent's opus.
        Assert.Equal(AdapterDefaultModels.For("agy"), entry.ModelResolved);
        Assert.Equal(BindingValueSource.ResolvedDefault, entry.ModelSource);
        // And #1927's third rung: agy's effort is the model id's own suffix.
        Assert.Equal("high", entry.EffortResolved);
        Assert.Equal(BindingValueSource.ResolvedDefault, entry.EffortSource);
    }

    /// <summary>
    /// The polarity arm for the test above, and the reason the re-resolution is keyed per axis rather
    /// than run unconditionally — <see cref="RedispatchCommand.WithResolvedStamps"/>'s own doc has it.
    /// </summary>
    [Fact]
    public void Same_vendor_with_no_override_keeps_the_parents_display_stamps_including_their_source()
    {
        var parent = ParentEntry(adapter: "claude", model: "opus", effort: "careful") with
        {
            ModelResolved = "opus",
            ModelSource = BindingValueSource.Requested,
            EffortResolved = "careful",
            EffortSource = BindingValueSource.Requested,
        };

        var entry = RedispatchCommand.InheritBinding(parent, new RedispatchOptions("parent-room", "new-room"));

        Assert.Equal("opus", entry.ModelResolved);
        Assert.Equal(BindingValueSource.Requested, entry.ModelSource);
        Assert.Equal("careful", entry.EffortResolved);
        Assert.Equal(BindingValueSource.Requested, entry.EffortSource);
    }

    /// <summary>An explicit <c>--model</c> restamps the axis as requested, on either vendor.</summary>
    [Fact]
    public void An_explicit_model_override_restamps_the_axis_as_requested()
    {
        var parent = ParentEntry(adapter: "claude", model: "opus") with
        {
            ModelResolved = "opus",
            ModelSource = BindingValueSource.ResolvedDefault,
        };

        var entry = RedispatchCommand.InheritBinding(
            parent, new RedispatchOptions("parent-room", "new-room", Model: "gemini-3.8-flash-low"));

        Assert.Equal("gemini-3.8-flash-low", entry.ModelResolved);
        Assert.Equal(BindingValueSource.Requested, entry.ModelSource);
    }

    /// <summary>
    /// #1927 re-review MEDIUM: on agy the effort stamp is NOT an independent axis — it is read off the
    /// resolved model id's own suffix (<see cref="RoleDispatch.ResolveEffortStamp"/>'s third rung) — so
    /// a SAME-VENDOR <c>--model</c> moves the correct effort answer with it. The measured room is the
    /// one #1927 exists for: <c>--adapter agy</c> with no <c>--model</c>/<c>--effort</c>, redispatched
    /// onto a <c>-low</c> id. Inheriting the parent's stamp put <c>high</c> on the glass while the CLI
    /// ran at <c>low</c>, and made redispatch disagree with <see cref="RoleDispatch.ToBinding"/> for
    /// identical inputs.
    /// </summary>
    [Fact]
    public void A_same_vendor_model_override_re_reads_agys_effort_off_the_new_model_id()
    {
        var parent = ParentEntry(adapter: "agy", model: null, effort: null) with
        {
            ModelResolved = AdapterDefaultModels.For("agy"),
            ModelSource = BindingValueSource.ResolvedDefault,
            EffortResolved = "high",
            EffortSource = BindingValueSource.ResolvedDefault,
        };

        var entry = RedispatchCommand.InheritBinding(
            parent, new RedispatchOptions("parent-room", "new-room", Model: "gemini-3.8-flash-low"));

        Assert.Equal("gemini-3.8-flash-low", entry.ModelResolved);
        Assert.Equal("low", entry.EffortResolved);
        Assert.Equal(BindingValueSource.ResolvedDefault, entry.EffortSource);
        // The control that this is the STAMP moving and not the axis: the dispatch input stays null,
        // so the CLI is still handed no --effort of its own, exactly as ToBinding would leave it.
        Assert.Null(entry.Effort);
    }

    /// <summary>
    /// The polarity arm for the test above, and the reason its trigger is <c>--model</c> given AND a
    /// parent that recorded no effort, rather than "the model stamp moved". A parent whose effort WAS
    /// requested keeps both the value and the <see cref="BindingValueSource.Requested"/> source across
    /// a same-vendor <c>--model</c>: re-resolving there would restamp it <c>resolved-default</c> and
    /// claim the child fell back to a value it was actually given.
    /// </summary>
    [Fact]
    public void A_same_vendor_model_override_does_not_demote_an_effort_the_parent_was_asked_for()
    {
        var parent = ParentEntry(adapter: "claude", model: "opus", effort: "careful") with
        {
            ModelResolved = "opus",
            ModelSource = BindingValueSource.Requested,
            EffortResolved = "careful",
            EffortSource = BindingValueSource.Requested,
        };

        var entry = RedispatchCommand.InheritBinding(
            parent, new RedispatchOptions("parent-room", "new-room", Model: "haiku"));

        Assert.Equal("haiku", entry.ModelResolved);
        Assert.Equal(BindingValueSource.Requested, entry.ModelSource);
        Assert.Equal("careful", entry.EffortResolved);
        Assert.Equal(BindingValueSource.Requested, entry.EffortSource);
    }

    /// <summary>
    /// #1927 review HIGH, sub-note: the <c>--spec</c> path passed <c>options.Model ?? parentEntry.Model</c>
    /// straight into <see cref="RoleDispatch.ToBinding"/> as an explicit override, which does NOT apply
    /// the vendor-swap axis rule to it — so an amended-spec redispatch onto agy handed the vendor CLI
    /// the literal string <c>opus</c> as real argv, the #1082 failure in mirror image. Asserted on the
    /// shared predicate both paths now cross, since the amended-spec path itself needs a room on disk.
    /// </summary>
    [Fact]
    public void The_inherited_axes_both_redispatch_paths_share_drop_model_and_effort_on_a_vendor_swap()
    {
        var parent = ParentEntry(adapter: "claude", model: "opus", effort: "xhigh");

        var swapped = RedispatchCommand.InheritedAxes(
            parent, new RedispatchOptions("parent-room", "new-room", SpecFilePath: "amended.md", Adapter: "agy"));
        Assert.Null(swapped.Model);
        Assert.Null(swapped.Effort);

        // The control: on the SAME vendor the very same predicate still inherits both, so the arm
        // above is about the swap rather than about the axes never being carried at all.
        var kept = RedispatchCommand.InheritedAxes(
            parent, new RedispatchOptions("parent-room", "new-room", SpecFilePath: "amended.md"));
        Assert.Equal("opus", kept.Model);
        Assert.Equal("xhigh", kept.Effort);
    }
}
