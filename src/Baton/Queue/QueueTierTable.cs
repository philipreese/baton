namespace Baton.Queue;

/// <summary>
/// #1934 Q3: the role-and-scope tier table, and the one place an item's adapter/model/effort is
/// resolved. Before this, those rulings lived in the conductor's notes and the scratchpad runner's
/// comments; here they are data the comparator (#1903) can read.
/// </summary>
/// <remarks>
/// <para>
/// The key shape is spec/baton.md §13's. The invariant that matters here: <see cref="KeyFor"/> is the
/// only thing in the tree that builds one, so a caller never spells a key itself.
/// </para>
/// <para>
/// <b>Nothing here promotes a model</b>, and the enforcement is structural rather than a check: no
/// code path below ever assigns a model the item did not ask for, once it asked for one. What
/// <see cref="QueueTierResolution.IsOverride"/> adds is that the departure is visible to the launch
/// fact, so the choice is auditable as well as honoured.
/// </para>
/// </remarks>
public static class QueueTierTable
{
    /// <summary>The scope-class vocabulary an item may name. Anything else is refused at
    /// <c>baton queue add</c> time rather than resolved to a default tier — a typo'd scope must not
    /// silently launch on the wrong model.</summary>
    public static readonly IReadOnlyList<string> ScopeClasses = ["engine", "tooling", "docs"];

    /// <summary>
    /// The shipped table — the operator's 2026-09-05 rulings, stated once. <c>review-docs</c> carries
    /// the same value as <c>review-tooling</c> because the ruling was "review tooling/docs", i.e. one
    /// tier over two scopes; it is written out rather than fallen back to, so a later divergence is a
    /// one-line edit instead of a change to the resolution rule.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, QueueTierSettings> ShippedDefaults =
        new Dictionary<string, QueueTierSettings>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = new() { Adapter = "claude", Model = "opus", Effort = "high" },
            ["tooling"] = new() { Adapter = "claude", Model = "opus", Effort = "medium" },
            ["docs"] = new() { Adapter = "claude", Model = "opus", Effort = "medium" },
            ["review-engine"] = new() { Adapter = "claude", Model = "opus", Effort = "high" },
            ["review-tooling"] = new() { Adapter = "codex", Model = "gpt-5.6-sol", Effort = "high" },
            ["review-docs"] = new() { Adapter = "codex", Model = "gpt-5.6-sol", Effort = "high" },
        };

    /// <summary>The shipped per-adapter fallback model — today just agy's, which has no model in any
    /// tier above because no tier routes to it by default.</summary>
    public static readonly IReadOnlyDictionary<string, string> ShippedAdapterDefaultModels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["agy"] = "gemini-3.8-flash-high",
        };

    /// <summary>The review role, whose tier keys carry the <c>review-</c> prefix and whose live
    /// weight is zero (<see cref="QueueWeights"/>).</summary>
    public const string ReviewRole = "review";

    /// <summary>
    /// The tier key for <paramref name="role"/> in <paramref name="scopeClass"/>. Case-insensitive on
    /// both, matching the table's own comparer.
    /// </summary>
    public static string KeyFor(string role, string scopeClass)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        ArgumentException.ThrowIfNullOrEmpty(scopeClass);
        return string.Equals(role, ReviewRole, StringComparison.OrdinalIgnoreCase)
            ? $"review-{scopeClass.ToLowerInvariant()}"
            : scopeClass.ToLowerInvariant();
    }

    /// <summary>
    /// The adapter/model/effort <paramref name="item"/> will actually launch on, and whether any axis
    /// was overridden away from its tier.
    /// </summary>
    /// <remarks>
    /// Precedence runs per axis independently (decision 0017's three axes stay three): the item's own
    /// value, then the tier entry's, then — model only — the adapter default. A resolution of all
    /// nulls is a legitimate result, not a failure; spec/baton.md §13 says what a caller does with it.
    /// </remarks>
    public static QueueTierResolution Resolve(QueueItem item, QueueSettings settings)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);

        string? key = null;
        QueueTierSettings? tier = null;
        if (item.ScopeClass is { Length: > 0 } scopeClass)
        {
            key = KeyFor(item.Role, scopeClass);
            tier = LookupTier(key, settings);
        }

        var adapter = item.Adapter ?? tier?.Adapter;
        var model = item.Model ?? tier?.Model;
        var effort = item.Effort ?? tier?.Effort;

        if (model is null && adapter is { Length: > 0 })
        {
            model = LookupAdapterDefaultModel(adapter, settings);
        }

        // An override is an axis the ITEM set to something the tier did not say. An item with no scope
        // class has no tier to differ from, so its explicit axes are not overrides -- there was nothing
        // to override. Ordinal comparison: a model string is a vendor token, not prose.
        var isOverride = tier is not null && (
            Differs(item.Adapter, tier.Adapter)
            || Differs(item.Model, tier.Model)
            || Differs(item.Effort, tier.Effort));

        return new QueueTierResolution(key, adapter, model, effort, isOverride, isOverride ? item.Reason : null);
    }

    private static bool Differs(string? itemValue, string? tierValue) =>
        itemValue is not null && !string.Equals(itemValue, tierValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The tier entry for <paramref name="key"/> after the operator's table is overlaid on
    /// <see cref="ShippedDefaults"/>, or null when neither has it. Null is what makes an unknown scope
    /// class fail closed at the caller rather than resolving to some other tier's model.
    /// </summary>
    public static QueueTierSettings? LookupTier(string key, QueueSettings settings)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Tiers is { } configured)
        {
            // Deserialized dictionaries carry an ordinal comparer; a key an operator typed into
            // settings.json should not have to match ShippedDefaults' casing exactly to take effect.
            foreach (var (configuredKey, value) in configured)
            {
                if (string.Equals(configuredKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }

        return ShippedDefaults.TryGetValue(key, out var shipped) ? shipped : null;
    }

    private static string? LookupAdapterDefaultModel(string adapter, QueueSettings settings)
    {
        if (settings.AdapterDefaultModels is { } configured)
        {
            foreach (var (configuredAdapter, model) in configured)
            {
                if (string.Equals(configuredAdapter, adapter, StringComparison.OrdinalIgnoreCase))
                {
                    return model;
                }
            }
        }

        return ShippedAdapterDefaultModels.TryGetValue(adapter, out var shippedModel) ? shippedModel : null;
    }
}

/// <summary>
/// What <see cref="QueueTierTable.Resolve"/> decided, and enough of why for the launch fact and the
/// room's bindings to record it.
/// </summary>
/// <param name="TierKey">The <see cref="QueueTierTable.KeyFor"/> key consulted, or null when the item named no scope class.</param>
/// <param name="Adapter">The adapter to dispatch on; null defers to the role's own tier.</param>
/// <param name="Model">The model to dispatch on; null defers to the role's own tier.</param>
/// <param name="Effort">The effort to dispatch at; null defers to the role's own tier.</param>
/// <param name="IsOverride">True when the item set an axis to something its tier did not say.</param>
/// <param name="OverrideReason">
/// The item's <c>--reason</c>, present only when <paramref name="IsOverride"/> is true. It lands on
/// the room's bindings via <c>--label</c> so the override is readable from the room itself and not
/// only from the queue ledger.
/// </param>
public sealed record QueueTierResolution(
    string? TierKey,
    string? Adapter,
    string? Model,
    string? Effort,
    bool IsOverride,
    string? OverrideReason);
