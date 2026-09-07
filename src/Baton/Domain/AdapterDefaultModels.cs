namespace Baton.Domain;

/// <summary>
/// #1927: the model each vendor CLI runs when Baton names none — the one canonical statement of that
/// fact, consulted both by the queue's tier resolution (<c>Queue.QueueTierTable</c>) and by the
/// bind-time resolution that stamps <c>WorkerBindingConfigEntry.ModelResolved</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>An entry is a MEASUREMENT, never a preference.</b> This table exists so a room dispatched with no
/// <c>--model</c> can still say what it is running; a value invented here would make it say so
/// falsely, which is worse than the bare vendor #1927 set out to fix. Each entry carries its own
/// provenance below, and an adapter with no measured default is simply absent — the caller then leaves
/// the resolved model absent rather than guessing, exactly as
/// <c>Baton.Vendors.DepthTierMapping</c> refuses a tier for a model string its table does not carry.
/// </para>
/// <para>
/// <b>claude is deliberately absent.</b> It ships no model-list subcommand
/// (<c>Baton.Vendors.DepthTierMapping</c>'s own remarks), so its default is whatever the operator's
/// account resolves to and nothing in this repository has measured it. Every claude role in
/// <c>WorkerTiers.json</c> names a model anyway, so the tier answers before this table is ever
/// reached.
/// </para>
/// </remarks>
public static class AdapterDefaultModels
{
    /// <summary>
    /// The shipped per-adapter default, keyed by the normalized adapter name.
    /// <list type="bullet">
    /// <item><description>
    /// <c>agy</c> — the <c>balanced</c> placement the operator made on 2026-09-05 (#1925), which is
    /// what the conductor's runner passes for every agy item since; recording it here is what lets a
    /// hand-run <c>baton dispatch --adapter agy</c> name the same model the queue path already does.
    /// </description></item>
    /// <item><description>
    /// <c>codex</c> — <c>gpt-6-astra</c>, the single entry marked <c>"isDefault": true</c> in the
    /// captured <c>model/list</c> answer shipped at
    /// <c>src/Baton.Vendors/codex-model-list-2026-09-04.jsonl</c>. That recording is the record
    /// (<c>docs/vendor-codex-probe-2026-09-04.md</c>), and it can go stale — a codex release that moves
    /// the default makes this entry wrong, and re-running that probe is what corrects it.
    /// </description></item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Shipped =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["agy"] = "gemini-3.8-flash-high",
            ["codex"] = "gpt-6-astra",
        };

    /// <summary>
    /// The default for <paramref name="adapter"/>, or null when none is measured. Case-insensitive,
    /// matching <see cref="Shipped"/>'s own comparer.
    /// </summary>
    public static string? For(string? adapter) =>
        adapter is { Length: > 0 } name && Shipped.TryGetValue(name, out var model) ? model : null;
}
