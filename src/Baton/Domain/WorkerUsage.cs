namespace Baton.Domain;

/// <summary>
/// A vendor CLI's own self-reported per-execution usage (issue #1360), parsed by an adapter from its
/// vendor-specific stream-json terminal line — never fabricated by Flow. Every field is nullable and
/// independently absent: a vendor that reports turns but not tokens, or nothing at all, yields exactly
/// the fields it reported and no others. Wall-clock is deliberately not here — it is a Flow-derived
/// fact (execution start/exit timestamps already in the ledger), not something a vendor reports.
/// </summary>
/// <param name="ContextLevelTokens">
/// #1623 re-review N6: a monitor that tracks a running *level* rather than a single vendor-raw reading
/// (<see cref="Mutation.TokenBudgetMonitor"/>) reports its aggregate here — already the sum of
/// <paramref name="TokensIn"/> + <paramref name="CacheReadTokens"/> + <paramref name="CacheCreationTokens"/>
/// — rather than overloading <paramref name="TokensIn"/> with that aggregate. A consumer computing
/// <c>TokensIn + CacheReadTokens + CacheCreationTokens</c> on a <see cref="WorkerUsage"/> that carries
/// this field would otherwise double-count it. Null for every ordinary vendor-parsed reading
/// (<c>IWorkerUsageParser</c>'s two methods never set it). Kept, unchanged in meaning, as the
/// DISPLAYED context size (#1682) — no longer what a budget arrests on; see <paramref name="BilledTokens"/>.
/// #1666 review F6: on <see cref="Mutation.TokenBudgetMonitor"/>'s own snapshot this is the MAX of two
/// per-bucket levels (parent vs. sub-agent), not the sum of that same snapshot's own
/// <paramref name="TokensIn"/>/<paramref name="CacheCreationTokens"/> — those two stay whichever raw
/// line arrived last, parent or sub-agent, so during a fan-out turn they can describe a different line
/// than the one this field's max came from.
/// </param>
/// <param name="BilledTokens">
/// #1682: the additive quantity a token budget actually arrests on — a running Σ, across every
/// incremental usage line, of <c>TokensIn + TokensOut [+ CacheCreationTokens]</c> for that line,
/// deliberately excluding <paramref name="ThinkingTokens"/> (spec/baton.md §3 has the measured case
/// for the exclusion). Null for an ordinary per-line vendor-parsed reading, same convention as
/// <paramref name="ContextLevelTokens"/>; only <c>Mutation.TokenBudgetMonitor</c>'s own snapshot sets it.
/// </param>
/// <param name="CacheReadTokens">
/// A vendor-parsed reading (<c>ClaudeUsageParser</c>/<c>AgyUsageParser</c>): the raw scalar off that
/// one line/terminal report, same as every other field on an ordinary reading. On
/// <see cref="Mutation.TokenBudgetMonitor"/>'s own snapshot this instead carries a running Σ across
/// every incremental line (#1682) — display-only, never itself compared to a budget — the same
/// per-context duality <paramref name="ContextLevelTokens"/> documents for the input side.
/// </param>
/// <param name="CacheReadLevelTokens">
/// #1812: the LATEST usage line's own <c>CacheReadTokens</c> reading, not the running Σ
/// <paramref name="CacheReadTokens"/> carries on <see cref="Mutation.TokenBudgetMonitor"/>'s own
/// snapshot — pusher.py's derive path treats cache-read the same way it treats
/// <paramref name="ContextLevelTokens"/>'s <c>contextTokens</c> (a LEVEL the caller replaces, never
/// sums), so a projection consumer comparing against that path needs the same level, not the Σ.
/// Null for every ordinary vendor-parsed reading, same convention as <paramref name="ContextLevelTokens"/>;
/// only <c>Mutation.TokenBudgetMonitor</c>'s own snapshot sets it.
/// </param>
/// <param name="MessageId">
/// #1686 (review F6): claude's <c>message.id</c> off an incremental <c>"type":"assistant"</c> line —
/// measured against real captures to repeat across several consecutive lines with the SAME
/// <c>message.usage</c> object (a single API response split across content-block chunks), which would
/// double-count <paramref name="BilledTokens"/> if summed per line rather than per message. Null on
/// every reading agy produces (that vendor's shape has no analogous id).
/// <see cref="Mutation.TokenBudgetMonitor"/> is the sole consumer — it dedupes its own running Σ by
/// this field rather than exposing it as a general-purpose identity.
/// <para>
/// #2020: a SECOND vendor now populates it, on a different shape, and the claude sentence above no
/// longer generalises. Codex's terminal <c>turn.completed</c> restates the last model round-trip that
/// its own <c>turn.usage</c> line already reported, and <c>Status.CodexUsageParser</c> reads BOTH
/// (the terminal line is a pre-emitter stream's only usage line, so refusing it would blank every
/// historical codex room's live figure) — so on that vendor this field is
/// <c>round-trip-{index}</c>, and the restatement carries the SAME one. Which means "null on the
/// terminal-line reading, which is never summed" was true of claude only: codex's terminal line IS
/// summed, and this field is what stops it being summed twice. A per-execution fold sets it back to
/// null — <c>Status.CodexUsageParser.ParseExecutionUsage</c> is where that is stated.
/// </para>
/// </param>
/// <param name="BilledIsFloor">
/// #1706: <see langword="true"/> when the reading this belongs to is a LOWER BOUND on the execution's
/// real billed tokens rather than a measurement of it — some billed component the vendor charges for is
/// not present anywhere in the live stream, so no accumulation over that stream can recover it.
/// Set by <c>ClaudeUsageParser.TryParseIncrementalUsage</c> on every mid-stream reading it produces
/// (that method's own doc has the measurement) and carried forward, sticky, onto
/// <see cref="Mutation.TokenBudgetMonitor"/>'s snapshot: once ANY line on a stream was a floor, the
/// running Σ is a floor. False on agy's incremental reading and on both vendors' terminal reading,
/// which are measurements. Never inverts a comparison — a floor crossing a budget is still a real
/// crossing; what it cannot do is prove a budget was NOT crossed, which is exactly what the arrest
/// text and the glass now say rather than leaving to inference.
/// </param>
/// <param name="IsSubAgentTurn">
/// #1666: <see langword="true"/> when this reading's raw line was a sub-agent's own turn rather than
/// the parent conversation's — spec/baton.md §3 has the measured shape that marks one and why
/// <see cref="Mutation.TokenBudgetMonitor"/> tracks this bucket's level separately rather than letting
/// it replace the parent's larger one. Always false on agy: #1742 measured (a real
/// <c>invoke_subagent</c> capture, docs/vendor-doc-audit.md) that agy's parent stream carries no
/// usage-bearing line for a sub-agent's own turns at all, so every agy reading this field could
/// apply to is structurally a parent-conversation line, not an unmarked one — spec/baton.md §3 has
/// the fuller statement and the exact reader gate this trips.
/// True/false for claude's own sub-agent vs. non-sub-agent turns.
/// </param>
/// <param name="ModelsObserved">
/// #1883 (review F1): the model names the reading's own token figures were summed ACROSS — claude's
/// terminal <c>modelUsage</c> keys, one per model the whole execution tree used, which
/// <c>ClaudeUsageParser.TryParseFinalUsage</c> previously discarded after summing. Set only when that
/// whole-tree read actually supplied the figures; null on every other reading (agy and codex report no
/// per-model breakdown, and claude's top-level <c>usage</c> fallback carries no model name either), and
/// null is therefore "this reading names no model", never "one model". Exists so a consumer pricing
/// these tokens can tell a single-model total from a multi-model sum instead of pricing the sum at one
/// model's rate — <c>Accounting.CostLedgerStore</c> is the consumer that does, and refuses rather than
/// guessing. Never a routing input: Architecture Rule 1 keeps model choice on the workflow config.
/// </param>
public sealed record WorkerUsage(
    long? TokensIn = null,
    long? TokensOut = null,
    int? Turns = null,
    long? CacheReadTokens = null,
    long? CacheCreationTokens = null,
    long? ThinkingTokens = null,
    long? ContextLevelTokens = null,
    long? CacheReadLevelTokens = null,
    long? BilledTokens = null,
    string? MessageId = null,
    bool BilledIsFloor = false,
    bool IsSubAgentTurn = false,
    IReadOnlyList<string>? ModelsObserved = null);
