using Baton.Queue;

namespace Baton.Tests.Queue;

/// <summary>
/// #1934 slice 1, item 6: tier resolution — the class default, an override with its reason, and the
/// rule that no model is ever promoted. #1863 adds the rows that follow a <c>WorkerTiers.json</c> tier
/// by NAME.
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

    /// <summary>
    /// A stand-in tier register naming only <c>standard</c>. <c>Baton.Tests</c> does not reference
    /// <c>Baton.Vendors</c>, where <c>WorkerTiers.json</c> is actually read, so this file asserts the
    /// FLATTENING rule in isolation; the production delegate
    /// (<c>WorkerRoleCatalog.QueueTierFor</c>) is driven against a real fixture tier file in
    /// <c>Baton.Vendors.Tests.QueueTierFollowsWorkerTiersTests</c>. Two tests, two jobs — a lambda here
    /// could not discriminate the production wiring, and a fixture file there could not isolate the
    /// per-axis rule.
    /// </summary>
    private static Func<string, QueueTierSettings?> NamedTiers(
        string adapter = "codex", string model = "gpt-6-astra", string effort = "medium") =>
        name => string.Equals(name, "standard", StringComparison.Ordinal)
            ? new QueueTierSettings { Tier = name, Adapter = adapter, Model = model, Effort = effort }
            : null;

    /// <summary>A register that carries no tier at all — the fail-closed arm's input.</summary>
    private static readonly Func<string, QueueTierSettings?> NoNamedTiers = _ => null;

    [Theory]
    // The shipped table, stated as the operator ruled it on 2026-09-05. If a default here changes, the
    // change is deliberate — this is the test that makes it so rather than letting it drift silently.
    // `tooling` is deliberately absent: it names no triple of its own since #1863, so its value is
    // asserted below against the tier register it follows rather than retyped here.
    [InlineData("implement", "engine", "claude", "opus", "high")]
    [InlineData("implement", "docs", "claude", "opus", "medium")]
    [InlineData("review", "engine", "claude", "opus", "high")]
    [InlineData("review", "tooling", "codex", "gpt-5.6-sol", "high")]
    [InlineData("review", "docs", "codex", "gpt-5.6-sol", "high")]
    public void A_scope_class_resolves_to_its_shipped_tier(
        string role, string scope, string adapter, string model, string effort)
    {
        // NoNamedTiers on purpose: a row that states its own triple must not need the tier register at
        // all, which is the control for the two arms below.
        var resolved = QueueTierTable.Resolve(Item(role, scope), new QueueSettings(), NoNamedTiers);

        Assert.Equal(adapter, resolved.Adapter);
        Assert.Equal(model, resolved.Model);
        Assert.Equal(effort, resolved.Effort);
        Assert.False(resolved.IsOverride);
        Assert.Null(resolved.OverrideReason);
    }

    /// <summary>
    /// #1863: the `tooling` row IS the `standard` tier rather than a second copy of it. Driven with two
    /// different registers, because a single one could be matched by a hard-coded triple that happened
    /// to agree — which is exactly the state this change fixes.
    /// </summary>
    [Fact]
    public void The_tooling_row_follows_whatever_the_standard_tier_says()
    {
        var shipped = QueueTierTable.Resolve(
            Item("implement", "tooling"), new QueueSettings(), NamedTiers());
        var moved = QueueTierTable.Resolve(
            Item("implement", "tooling"), new QueueSettings(),
            NamedTiers(adapter: "claude", model: "opus", effort: "low"));

        Assert.Equal(("codex", "gpt-6-astra", "medium"), (shipped.Adapter, shipped.Model, shipped.Effort));
        Assert.Equal(("claude", "opus", "low"), (moved.Adapter, moved.Model, moved.Effort));
        // Following a tier is not departing from one: the item asked for nothing.
        Assert.False(shipped.IsOverride);
        Assert.False(moved.IsOverride);
    }

    /// <summary>
    /// The fail-closed arm. What returning the raw row instead would cost is on
    /// <see cref="QueueTierTable.LookupTier"/>'s own remarks; this pins the behaviour, not the reason.
    /// </summary>
    [Fact]
    public void A_row_whose_named_tier_is_missing_looks_up_to_null_rather_than_to_a_bare_row()
    {
        Assert.Null(QueueTierTable.LookupTier("tooling", new QueueSettings(), NoNamedTiers));
        // Control: the SAME key resolves once the register carries the tier, so the null above is
        // measuring the missing tier and not the key.
        Assert.NotNull(QueueTierTable.LookupTier("tooling", new QueueSettings(), NamedTiers()));
    }

    /// <summary>
    /// An axis the row states itself wins over the tier it follows — the same per-axis precedence
    /// <see cref="QueueTierTable.Resolve"/> applies between an item and its tier (decision 0017).
    /// </summary>
    [Fact]
    public void A_row_that_follows_a_tier_may_still_state_one_axis_of_its_own()
    {
        var settings = new QueueSettings
        {
            Tiers = new Dictionary<string, QueueTierSettings>
            {
                ["tooling"] = new() { Tier = "standard", Effort = "high" },
            },
        };

        var resolved = QueueTierTable.Resolve(Item("implement", "tooling"), settings, NamedTiers());

        Assert.Equal("codex", resolved.Adapter);
        Assert.Equal("gpt-6-astra", resolved.Model);
        Assert.Equal("high", resolved.Effort);
    }

    [Fact]
    public void An_item_that_overrides_an_axis_is_marked_an_override_and_carries_its_reason()
    {
        var tiered = QueueTierTable.Resolve(Item("implement", "engine"), new QueueSettings(), NoNamedTiers);
        var overridden = QueueTierTable.Resolve(
            Item("implement", "engine", model: "sonnet", reason: "cheap sweep, no judgment needed"),
            new QueueSettings(),
            NoNamedTiers);

        // Control: the same item without the override is NOT marked one, so the flag is measuring the
        // override rather than the presence of a scope class.
        Assert.False(tiered.IsOverride);
        Assert.True(overridden.IsOverride);
        Assert.Equal("cheap sweep, no judgment needed", overridden.OverrideReason);
    }

    /// <summary>
    /// Override detection reads the FLATTENED tier, not the row: an item naming the tier's own vendor
    /// is not an override, and one naming a different vendor is. Both arms, because getting the
    /// flatten-then-compare order backwards makes every axis on a tier-following row read as a
    /// departure — a fabricated override on the launch fact.
    /// </summary>
    [Fact]
    public void An_override_on_a_tier_following_row_is_judged_against_the_tier_it_followed()
    {
        var matching = QueueTierTable.Resolve(
            Item("implement", "tooling", adapter: "codex"), new QueueSettings(), NamedTiers());
        var departing = QueueTierTable.Resolve(
            Item("implement", "tooling", adapter: "claude", reason: "deliberate"),
            new QueueSettings(),
            NamedTiers());

        Assert.False(matching.IsOverride);
        Assert.True(departing.IsOverride);
        Assert.Equal("deliberate", departing.OverrideReason);
    }

    [Fact]
    public void Sonnet_is_never_promoted_to_the_tiers_opus()
    {
        var resolved = QueueTierTable.Resolve(
            Item("implement", "engine", model: "sonnet", reason: "deliberate"),
            new QueueSettings(),
            NoNamedTiers);

        // The whole point: an item that asks for sonnet GETS sonnet, and the resolution says the tier
        // was departed from so the launch fact records it.
        Assert.Equal("sonnet", resolved.Model);
        Assert.True(resolved.IsOverride);
    }

    [Fact]
    public void The_axes_stay_independent_so_overriding_one_keeps_the_tiers_other_two()
    {
        var resolved = QueueTierTable.Resolve(
            Item("implement", "engine", effort: "low", reason: "trivial"), new QueueSettings(), NoNamedTiers);

        Assert.Equal("claude", resolved.Adapter);
        Assert.Equal("opus", resolved.Model);
        Assert.Equal("low", resolved.Effort);
    }

    [Fact]
    public void An_agy_item_with_no_model_gets_the_shipped_agy_default()
    {
        var resolved = QueueTierTable.Resolve(Item(adapter: "agy"), new QueueSettings(), NoNamedTiers);

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
        var resolved = QueueTierTable.Resolve(Item(adapter: "codex"), new QueueSettings(), NoNamedTiers);

        Assert.Equal("codex", resolved.Adapter);
        Assert.Null(resolved.Model);
    }

    [Fact]
    public void An_item_with_no_scope_class_resolves_to_nulls_and_is_not_an_override()
    {
        var resolved = QueueTierTable.Resolve(Item(), new QueueSettings(), NoNamedTiers);

        // Nulls are the legitimate "defer to the role" result, and IsOverride must stay false with
        // them — an item flagged as departing from a tier it never consulted would put a fabricated
        // override on the launch fact.
        Assert.Null(resolved.TierKey);
        Assert.Null(resolved.Adapter);
        Assert.Null(resolved.Model);
        Assert.False(resolved.IsOverride);
    }

    [Fact]
    public void An_unscoped_item_resolves_the_role_tier_without_becoming_a_scope_override()
    {
        var resolved = QueueTierTable.Resolve(
            Item(),
            new QueueSettings(),
            NoNamedTiers,
            _ => new QueueTierSettings { Adapter = "codex", Model = "gpt-6-astra", Effort = "medium" });

        Assert.Null(resolved.TierKey);
        Assert.Equal(("codex", "gpt-6-astra", "medium"), (resolved.Adapter, resolved.Model, resolved.Effort));
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

        Assert.Equal(
            "codex", QueueTierTable.Resolve(Item("implement", "engine"), settings, NamedTiers()).Adapter);
        // The five keys the operator did not name keep their shipped values, rather than the table
        // replacing the whole default — `tooling` among them, still following `standard`.
        Assert.Equal(
            "codex", QueueTierTable.Resolve(Item("implement", "tooling"), settings, NamedTiers()).Adapter);
        Assert.Equal(
            "claude", QueueTierTable.Resolve(Item("implement", "docs"), settings, NamedTiers()).Adapter);
    }

    [Fact]
    public void An_unknown_tier_key_looks_up_to_null_so_the_caller_can_fail_closed()
    {
        Assert.Null(QueueTierTable.LookupTier("review-nonsense", new QueueSettings(), NoNamedTiers));
        Assert.NotNull(QueueTierTable.LookupTier("review-engine", new QueueSettings(), NoNamedTiers));
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
