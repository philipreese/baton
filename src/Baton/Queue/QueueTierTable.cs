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
    /// <remarks>
    /// <b>Why the rows are two different shapes (#1863).</b> <c>tooling</c> names the
    /// <c>WorkerTiers.json</c> tier <c>standard</c> rather than a vendor triple, because both describe
    /// the same thing — the tooling-shaped implement work — and stating it twice is what let them
    /// disagree: <c>standard</c> moved to codex/<c>gpt-6-astra</c>/medium on 2026-09-06 while this row
    /// and spec/baton.md §13 still read claude/opus/medium, so an item with <c>--scope tooling</c>
    /// launched on the pin the ruling had retired. Naming the tier makes the disagreement
    /// unrepresentable rather than merely corrected.
    /// <para>
    /// The other five rows keep their triples, and NOT out of caution. spec/baton.md §13 states why,
    /// per row; it is the register for that ruling and this comment does not restate it.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, QueueTierSettings> ShippedDefaults =
        new Dictionary<string, QueueTierSettings>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = new() { Adapter = "claude", Model = "opus", Effort = "high" },
            ["tooling"] = new() { Tier = "standard" },
            ["docs"] = new() { Adapter = "claude", Model = "opus", Effort = "medium" },
            ["review-engine"] = new() { Adapter = "claude", Model = "opus", Effort = "high" },
            ["review-tooling"] = new() { Adapter = "codex", Model = "gpt-5.6-sol", Effort = "high" },
            ["review-docs"] = new() { Adapter = "codex", Model = "gpt-5.6-sol", Effort = "high" },
        };

    /// <summary>
    /// The shipped per-adapter fallback model, for an item whose tier names none — a deliberate SUBSET
    /// of <see cref="Domain.AdapterDefaultModels.Shipped"/>, whose values it reads rather than restates
    /// (#1927).
    /// </summary>
    /// <remarks>
    /// <b>Why a subset and not that table itself.</b> This value becomes the vendor CLI's own
    /// <c>--model</c> (<c>Baton.Cli.Daemon.QueueLauncher</c>); that one is display-only. agy's entry
    /// belongs in both: it is an operator PLACEMENT (#1925) made in order to be passed, and the
    /// conductor's runner already passes it. codex's does not: it is a captured reading of what that
    /// CLI picks for itself, and <c>docs/vendor-codex-probe-2026-09-04.md</c> says in terms that the
    /// model list "should be discovered rather than frozen" and that defaults can change — so freezing
    /// it into an argv would start naming a model on the queue's behalf that Baton had been content to
    /// let codex choose. Stale display is visibly stale; a stale flag value is not.
    /// <para>
    /// Pinned by <c>QueueTierTableTests</c>, because a comment is not what keeps codex out of here.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> ShippedAdapterDefaultModels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["agy"] = Domain.AdapterDefaultModels.Shipped["agy"],
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
    /// <param name="item">The queued item being resolved.</param>
    /// <param name="settings">The queue settings whose table overlays <see cref="ShippedDefaults"/>.</param>
    /// <param name="namedTiers">
    /// Resolves a <c>WorkerTiers.json</c> tier name to its three axes, for a row that names a tier
    /// instead of a triple. Required rather than defaulted: this project (<c>Baton</c>) does not
    /// reference <c>Baton.Vendors</c>, where that register is read, so the catalog arrives as a
    /// delegate — and a defaulted null is exactly how the two production call sites would drift into
    /// resolving the same item differently. <c>Baton.Vendors.WorkerRoleCatalog.QueueTierFor</c> is the
    /// one production implementation.
    /// </param>
    /// <param name="roleTiers">Resolves the role's dispatch tier when the item names no scope class.</param>
    public static QueueTierResolution Resolve(
        QueueItem item,
        QueueSettings settings,
        Func<string, QueueTierSettings?> namedTiers,
        Func<string, QueueTierSettings?> roleTiers)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(namedTiers);
        ArgumentNullException.ThrowIfNull(roleTiers);

        string? key = null;
        QueueTierSettings? tier = null;
        if (item.ScopeClass is { Length: > 0 } scopeClass)
        {
            key = KeyFor(item.Role, scopeClass);
            tier = LookupTier(key, settings, namedTiers);
        }
        else
        {
            tier = roleTiers(item.Role);
        }

        var adapter = item.Adapter ?? tier?.Adapter;
        var model = item.Model ?? tier?.Model;
        var effort = item.Effort ?? tier?.Effort;

        if (model is null && adapter is { Length: > 0 })
        {
            model = LookupAdapterDefaultModel(adapter, settings);
        }

        // An override is an axis the ITEM set to something the scope tier did not say. A role tier
        // fills an unscoped item's defaults, but is not a scope tier an item can depart from.
        var isOverride = key is not null && tier is not null && (
            Differs(item.Adapter, tier.Adapter)
            || Differs(item.Model, tier.Model)
            || Differs(item.Effort, tier.Effort));

        return new QueueTierResolution(key, adapter, model, effort, isOverride, isOverride ? item.Reason : null);
    }

    /// <summary>
    /// Resolves a scope tier only. Callers that can dispatch an unscoped item use the overload with
    /// the role-tier resolver so its recorded result is the dispatch result.
    /// </summary>
    public static QueueTierResolution Resolve(
        QueueItem item, QueueSettings settings, Func<string, QueueTierSettings?> namedTiers) =>
        Resolve(item, settings, namedTiers, _ => null);

    private static bool Differs(string? itemValue, string? tierValue) =>
        itemValue is not null && !string.Equals(itemValue, tierValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The tier entry for <paramref name="key"/> after the operator's table is overlaid on
    /// <see cref="ShippedDefaults"/> and any named tier is flattened into it, or null when neither
    /// table has the key. Null is what makes an unknown scope class fail closed at the caller rather
    /// than resolving to some other tier's model.
    /// </summary>
    /// <remarks>
    /// A row naming a tier <paramref name="namedTiers"/> cannot resolve also returns null, and for the
    /// same reason: returning the unflattened row would report the key as "configured" to
    /// <c>QueueSchedulerService</c>'s fail-closed check, which would then let the item through to
    /// resolve to all nulls and launch on the ROLE's own tier — the silent-wrong-model failure this
    /// table exists to prevent, recorded with <see cref="QueueTierResolution.IsOverride"/> false.
    /// </remarks>
    public static QueueTierSettings? LookupTier(
        string key, QueueSettings settings, Func<string, QueueTierSettings?> namedTiers)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(namedTiers);

        if (settings.Tiers is { } configured)
        {
            // Deserialized dictionaries carry an ordinal comparer; a key an operator typed into
            // settings.json should not have to match ShippedDefaults' casing exactly to take effect.
            foreach (var (configuredKey, value) in configured)
            {
                if (string.Equals(configuredKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    return Flatten(value, namedTiers);
                }
            }
        }

        return ShippedDefaults.TryGetValue(key, out var shipped) ? Flatten(shipped, namedTiers) : null;
    }

    /// <summary>
    /// <paramref name="entry"/> with its named tier's axes filled in, or null when it names a tier
    /// <paramref name="namedTiers"/> does not carry. An axis the entry states itself wins over the
    /// named tier's, so a row may follow a tier and still depart from it on one axis — the same
    /// per-axis precedence <see cref="Resolve"/> applies between the item and its tier.
    /// </summary>
    private static QueueTierSettings? Flatten(
        QueueTierSettings entry, Func<string, QueueTierSettings?> namedTiers)
    {
        if (entry.Tier is not { Length: > 0 } tierName)
        {
            return entry;
        }

        if (namedTiers(tierName) is not { } named)
        {
            return null;
        }

        return entry with
        {
            Adapter = entry.Adapter ?? named.Adapter,
            Model = entry.Model ?? named.Model,
            Effort = entry.Effort ?? named.Effort,
        };
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
/// <param name="Adapter">The adapter to dispatch on.</param>
/// <param name="Model">The model to dispatch on.</param>
/// <param name="Effort">The effort to dispatch at.</param>
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
