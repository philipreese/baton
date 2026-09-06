using Baton.Queue;

namespace Baton.Tests.Queue;

/// <summary>
/// #1934 slice 1, item 6: tier resolution — the class default, an override with its reason, and the
/// rule that no model is ever promoted.
/// </summary>
public sealed class QueueTierTableTests
{
    private static QueueItem Item(
        string role = "implement", string? scope = null, string? adapter = null, string? model = null,
        string? effort = null, string? reason = null) =>
        new()
        {
            Tag = "t1",
            Role = role,
            ScopeClass = scope,
            Adapter = adapter,
            Model = model,
            Effort = effort,
            Reason = reason,
            Workspace = @"C:\repos\w1",
            SpecFile = @"C:\baton\queue\specs\t1.md",
        };

    [Theory]
    // The shipped table, stated as the operator ruled it on 2026-09-05. If a default here changes, the
    // change is deliberate — this is the test that makes it so rather than letting it drift silently.
    [InlineData("implement", "engine", "claude", "opus", "high")]
    [InlineData("implement", "tooling", "claude", "opus", "medium")]
    [InlineData("implement", "docs", "claude", "opus", "medium")]
    [InlineData("review", "engine", "claude", "opus", "high")]
    [InlineData("review", "tooling", "codex", "gpt-5.6-sol", "high")]
    [InlineData("review", "docs", "codex", "gpt-5.6-sol", "high")]
    public void A_scope_class_resolves_to_its_shipped_tier(
        string role, string scope, string adapter, string model, string effort)
    {
        var resolved = QueueTierTable.Resolve(Item(role, scope), new QueueSettings());

        Assert.Equal(adapter, resolved.Adapter);
        Assert.Equal(model, resolved.Model);
        Assert.Equal(effort, resolved.Effort);
        Assert.False(resolved.IsOverride);
        Assert.Null(resolved.OverrideReason);
    }

    [Fact]
    public void An_item_that_overrides_an_axis_is_marked_an_override_and_carries_its_reason()
    {
        var tiered = QueueTierTable.Resolve(Item("implement", "engine"), new QueueSettings());
        var overridden = QueueTierTable.Resolve(
            Item("implement", "engine", model: "sonnet", reason: "cheap sweep, no judgment needed"),
            new QueueSettings());

        // Control: the same item without the override is NOT marked one, so the flag is measuring the
        // override rather than the presence of a scope class.
        Assert.False(tiered.IsOverride);
        Assert.True(overridden.IsOverride);
        Assert.Equal("cheap sweep, no judgment needed", overridden.OverrideReason);
    }

    [Fact]
    public void Sonnet_is_never_promoted_to_the_tiers_opus()
    {
        var resolved = QueueTierTable.Resolve(
            Item("implement", "engine", model: "sonnet", reason: "deliberate"), new QueueSettings());

        // The whole point: an item that asks for sonnet GETS sonnet, and the resolution says the tier
        // was departed from so the launch fact records it.
        Assert.Equal("sonnet", resolved.Model);
        Assert.True(resolved.IsOverride);
    }

    [Fact]
    public void The_axes_stay_independent_so_overriding_one_keeps_the_tiers_other_two()
    {
        var resolved = QueueTierTable.Resolve(
            Item("implement", "engine", effort: "low", reason: "trivial"), new QueueSettings());

        Assert.Equal("claude", resolved.Adapter);
        Assert.Equal("opus", resolved.Model);
        Assert.Equal("low", resolved.Effort);
    }

    [Fact]
    public void An_agy_item_with_no_model_gets_the_shipped_agy_default()
    {
        var resolved = QueueTierTable.Resolve(Item(adapter: "agy"), new QueueSettings());

        Assert.Equal("gemini-3.8-flash-high", resolved.Model);
    }

    /// <summary>
    /// #1927: this table is a deliberate SUBSET of <c>AdapterDefaultModels.Shipped</c>, and the arm
    /// that keeps it one. That type gained a codex entry so a room can DISPLAY what codex will run;
    /// this table's value becomes the CLI's own <c>--model</c>, and naming codex's default here would
    /// start passing a frozen 2026-09-04 reading as a flag — see <c>ShippedAdapterDefaultModels</c>'s
    /// own remarks. The agy arm above is the control: the two adapters must NOT resolve alike here,
    /// which is exactly what a future "just use the shared table" edit would make them do.
    /// </summary>
    [Fact]
    public void A_codex_item_with_no_model_is_left_for_the_cli_to_decide_rather_than_given_a_frozen_default()
    {
        var resolved = QueueTierTable.Resolve(Item(adapter: "codex"), new QueueSettings());

        Assert.Equal("codex", resolved.Adapter);
        Assert.Null(resolved.Model);
    }

    [Fact]
    public void An_item_with_no_scope_class_resolves_to_nulls_and_is_not_an_override()
    {
        var resolved = QueueTierTable.Resolve(Item(), new QueueSettings());

        // Nulls are the legitimate "defer to the role" result, and IsOverride must stay false with
        // them — an item flagged as departing from a tier it never consulted would put a fabricated
        // override on the launch fact.
        Assert.Null(resolved.TierKey);
        Assert.Null(resolved.Adapter);
        Assert.Null(resolved.Model);
        Assert.False(resolved.IsOverride);
    }

    [Fact]
    public void An_operators_table_overlays_the_shipped_one_entry_by_entry()
    {
        var settings = new QueueSettings
        {
            Tiers = new Dictionary<string, QueueTierSettings>
            {
                ["engine"] = new() { Adapter = "codex", Model = "gpt-5.6-sol", Effort = "high" },
            },
        };

        Assert.Equal("codex", QueueTierTable.Resolve(Item("implement", "engine"), settings).Adapter);
        // The five keys the operator did not name keep their shipped values, rather than the table
        // replacing the whole default.
        Assert.Equal("claude", QueueTierTable.Resolve(Item("implement", "tooling"), settings).Adapter);
    }

    [Fact]
    public void An_unknown_tier_key_looks_up_to_null_so_the_caller_can_fail_closed()
    {
        Assert.Null(QueueTierTable.LookupTier("review-nonsense", new QueueSettings()));
        Assert.NotNull(QueueTierTable.LookupTier("review-engine", new QueueSettings()));
    }

    [Fact]
    public void The_review_role_is_what_prefixes_a_tier_key()
    {
        Assert.Equal("review-engine", QueueTierTable.KeyFor("review", "engine"));
        Assert.Equal("engine", QueueTierTable.KeyFor("implement", "engine"));
        // Case is the operator's business, not the key's.
        Assert.Equal("review-tooling", QueueTierTable.KeyFor("Review", "Tooling"));
    }
}
