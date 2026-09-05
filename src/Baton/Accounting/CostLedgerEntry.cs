using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Accounting;

/// <summary>
/// Where a cost-ledger row came from — a closed set, so "Baton-launched only" is a trivial filter
/// rather than an inference from which fields happen to be populated (#1849's own requirement).
/// </summary>
/// <remarks>
/// Only <see cref="BatonExecution"/> has a writer today. The other three are phase C's importers of
/// the vendors' own native session logs, present here from day one so a phase-A row is already
/// labelled against them rather than needing a schema migration to say what it always was.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<CostSourceKind>))]
public enum CostSourceKind
{
    [JsonStringEnumMemberName("baton-execution")] BatonExecution,
    [JsonStringEnumMemberName("claude-code-session")] ClaudeCodeSession,
    [JsonStringEnumMemberName("codex-session")] CodexSession,
    [JsonStringEnumMemberName("antigravity-session")] AntigravitySession,
}

/// <summary>
/// Whether a dollar figure on a row is an estimate, and if not, why not. Never an invoice, never a
/// quota reading — the field names say "estimate" and nothing on the row says otherwise (#1849).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<EstimateStatus>))]
public enum EstimateStatus
{
    /// <summary>A number was produced from the cited catalog/factor-table version.</summary>
    [JsonStringEnumMemberName("estimated")] Estimated,

    /// <summary>
    /// No number was produced, and none was guessed at. Either the catalog has no price for this model
    /// (or for a dimension this usage reports), or the tokens could not be ATTRIBUTED to the model whose
    /// rate would have been applied — <see cref="CostLedgerEntry.EstimateReason"/> says which. Never
    /// borrowed from a neighbouring model, on either the rate side or the token side.
    /// </summary>
    [JsonStringEnumMemberName("unpriced")] Unpriced,

    /// <summary>A factor that applies here exists but has no measured value — see <see cref="PlanFactorStatus.Unknown"/>.</summary>
    [JsonStringEnumMemberName("unknown")] Unknown,

    /// <summary>This vendor's plan meter has never been measured, so no plan-meter estimate is even attempted.</summary>
    [JsonStringEnumMemberName("unmeasured")] Unmeasured,
}

/// <summary>
/// How much of the attempt this row actually accounts for. <b>Two labels and an absence</b>: the field
/// itself is omitted for an attempt nothing was read for, which is neither of them (#1883 review F2).
/// <see cref="CostLedgerStore.ResolveCompleteness"/> is the single place that decides between the three
/// and states the case for each.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CostCompleteness>))]
public enum CostCompleteness
{
    /// <summary>
    /// The stream was read end to end: a terminal line parsed AND the replay over the same bytes
    /// reconciled against it (<c>ExecutionUsageView</c>'s #1706 triple is present). The token
    /// dimensions on this row are the whole attempt's.
    /// </summary>
    [JsonStringEnumMemberName("complete")] Complete,

    /// <summary>
    /// The reader could not establish that this row holds the whole attempt's usage —
    /// <see cref="CostLedgerEntry.CompletenessReason"/> carries the stream reader's own reason. Read
    /// this as "not provably whole" rather than "provably truncated":
    /// <see cref="CostLedgerStore.ResolveCompleteness"/> states which reasons land here and why every
    /// one of them does.
    /// </summary>
    [JsonStringEnumMemberName("partial")] Partial,
}

/// <summary>
/// Which <c>baton resolve</c> a conductor recorded on a room (#1901 C1 item 4) — the closed set of
/// what that verb can do, so "how often did a person have to step in, and which way" is a filter
/// rather than a string comparison. Carried on a CORRECTING row, never written back over the
/// execution row it follows: <see cref="CostLedgerStore.BuildResolutionRow"/> states why.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConductorResolution>))]
public enum ConductorResolution
{
    /// <summary><c>--accept-capture</c>: the capture honestly satisfies its declared outputs. The hand-fix.</summary>
    [JsonStringEnumMemberName("accept-capture")] AcceptCapture,

    /// <summary><c>--reject</c>: it does not, and the step settles resolved-but-Failed.</summary>
    [JsonStringEnumMemberName("reject")] Reject,

    /// <summary><c>--close</c>: a settle shape <c>--reject</c> does not admit — no captured response ever existed to judge.</summary>
    [JsonStringEnumMemberName("close")] Close,
}

/// <summary>
/// One immutable accounting row per <b>settled execution attempt</b> (#1849 phase A). Consumes the
/// per-execution burn ledger's own source (<c>QuotaLedgerStore</c>, spec/baton.md §7) rather than
/// replacing it: <c>quota-ledger.jsonl</c> stays the per-execution record, and this is the durable,
/// repository-keyed, price-versioned accounting substrate #1391 (drill-down) and #1848 (enforcement)
/// read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every unavailable dimension is omitted, never zero</b> — the same doctrine
/// <c>WorkerUsage</c>/<c>ExecutionUsageView</c>/<c>QuotaLedgerEntry</c> already keep, extended here
/// rather than re-argued. A reader must never be able to tell "the vendor reported nothing" apart
/// from "the vendor reported zero" by accident; absence is the only spelling of the former.
/// </para>
/// <para>
/// <b>Fields reserved with no writer.</b> <see cref="Attempt"/>, <see cref="Effort"/>,
/// <see cref="ParentRoom"/>, <see cref="Workstream"/>,
/// <see cref="ModelEchoed"/> and <see cref="Raw"/> are named here but never populated by <see cref="CostLedgerStore.BuildEntries"/>:
/// none of them is derivable from the events a settle already has in hand, and #1849's telemetry
/// checklist wants the NAME pinned now so a later phase fills a reserved field rather than inventing
/// a competing one. Absent for the same reason every other unavailable dimension is absent.
/// <see cref="Raw"/> in particular is for the vendor's own billed/usage fields <i>verbatim</i>; the
/// vendor parsers reduce their envelope to <c>WorkerUsage</c> and discard the rest, so capturing it
/// verbatim is phase C's work (where whole session logs are read), not something phase A can fake
/// out of Baton-derived arithmetic. What Baton DID derive from the vendor's own figures is on
/// <see cref="BilledTokens"/>/<see cref="LiveBilledTokens"/>/<see cref="BilledUnderReadTokens"/>/<see cref="PeakBilledInWindow"/>,
/// under their own names, so nothing derived is ever mistaken for something raw.
/// </para>
/// </remarks>
/// <param name="Role">
/// Baton's worker name for the step (<c>ExecutionRequest.Worker</c>) — the role the telemetry
/// checklist asks for. One field, not two: Baton has no separate role concept for a workflow step.
/// </param>
/// <param name="Attempt">
/// Reserved ordinal. A retry or redispatch mints a FRESH <c>ExecutionId</c>
/// (<c>MutationInterface</c>'s <c>Guid.NewGuid</c> per dispatch), so <see cref="Execution"/> alone
/// already distinguishes attempts and is what the writer dedupes on; this field exists for a later
/// phase to record lineage ordering, and is absent until one does.
/// </param>
public sealed record CostLedgerEntry(
    [property: JsonPropertyName("sourceKind")]
    CostSourceKind SourceKind,
    [property: JsonPropertyName("repository")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Repository = null,
    [property: JsonPropertyName("room")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Room = null,
    [property: JsonPropertyName("parentRoom")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ParentRoom = null,
    [property: JsonPropertyName("workstream")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Workstream = null,
    [property: JsonPropertyName("workflow")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Workflow = null,
    [property: JsonPropertyName("step")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Step = null,
    [property: JsonPropertyName("execution")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Execution = null,
    [property: JsonPropertyName("attempt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Attempt = null,
    [property: JsonPropertyName("role")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Role = null,
    [property: JsonPropertyName("adapter")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Adapter = null,
    /// <summary>
    /// The model this attempt was REQUESTED at — the accepted <c>ExecutionRequest.Model</c> (plus any
    /// <c>StepRebound</c> override), as <c>ExecutionBindingResolver</c> resolves it. <b>Not the model
    /// the vendor CLI echoed back</b>, which Baton does not record anywhere yet: a substitution or a
    /// quota-driven downgrade is invisible here, so grouping rows by this field groups by intent, not
    /// by what ran (#1883 review F4). <see cref="ModelEchoed"/> is the reserved name for the other one.
    /// </summary>
    [property: JsonPropertyName("model")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Model = null,
    /// <summary>
    /// <b>Reserved, no phase-A writer.</b> The model as the vendor CLI itself echoed it (claude's
    /// <c>system:init</c> line), which is what #1849's telemetry checklist asks for and what
    /// <see cref="Model"/> above is not. Named now so phase C fills a reserved field rather than
    /// inventing a competitor. <see cref="ModelsObserved"/> is a different fact and not a substitute:
    /// it names every model the whole execution TREE billed against, where this names the one the main
    /// conversation ran on.
    /// </summary>
    [property: JsonPropertyName("modelEchoed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ModelEchoed = null,
    /// <summary>
    /// The models this row's token dimensions were summed ACROSS, off the vendor's own per-model
    /// breakdown (claude's terminal <c>modelUsage</c> keys — one entry per model the whole execution
    /// tree used, subagents included). Absent when the vendor reported no breakdown at all, which is
    /// "unknown", never "exactly one model". Pricing is refused unless this is absent or names exactly
    /// the requested <see cref="Model"/> — see <see cref="EstimateReason"/>.
    /// </summary>
    [property: JsonPropertyName("modelsObserved")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? ModelsObserved = null,
    [property: JsonPropertyName("effort")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Effort = null,
    [property: JsonPropertyName("outcome")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Outcome = null,
    /// <summary>
    /// #1901 C1: the issue this attempt's work belongs to, as a bare decimal number with no <c>#</c>
    /// (<see cref="LedgerQuery"/> normalizes both spellings on the filter side, so the writer picks one).
    /// Derived at settle from the leading <c>&lt;n&gt;-</c> of the workspace's checked-out branch — the
    /// ONLY source Baton has, because no room record carries an issue number. <b>Absent means "this
    /// branch does not name an issue", never "no issue"</b>: a branch created any other way than
    /// <c>gh issue develop</c> is unattributable here, and a room whose workspace directory is gone by
    /// settle time (a torn-down worktree) has nothing left to read.
    /// </summary>
    [property: JsonPropertyName("issue")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Issue = null,
    /// <summary>
    /// #1901 C1: the pull request open for that branch, as a bare decimal number, from
    /// <c>gh pr list --head &lt;branch&gt;</c> at settle. <b>Absent means "no PR was found for this
    /// branch at settle time"</b> — including the very common case of a lane that settles BEFORE its PR
    /// is opened, and the case where <c>gh</c> was missing, unauthenticated or offline. Phase C2's
    /// backfill is what fills those in later; nothing here guesses.
    /// </summary>
    [property: JsonPropertyName("pr")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PullRequest = null,
    [property: JsonPropertyName("startedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTime? StartedAt = null,
    [property: JsonPropertyName("endedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTime? EndedAt = null,

    // Token dimensions, exactly as QuotaLedgerEntry carries them -- same names, same nullability, so a
    // reader that already understands quota-ledger.jsonl needs no second vocabulary.
    [property: JsonPropertyName("tokensIn")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TokensIn = null,
    [property: JsonPropertyName("tokensOut")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TokensOut = null,
    [property: JsonPropertyName("cacheRead")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? CacheReadTokens = null,
    [property: JsonPropertyName("cacheCreation")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? CacheCreationTokens = null,
    [property: JsonPropertyName("thinking")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? ThinkingTokens = null,
    [property: JsonPropertyName("turns")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Turns = null,
    [property: JsonPropertyName("wallClockMs")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? WallClockMs = null,

    /// <summary>
    /// #1882's two non-token dimensions, carried through from <c>ExecutionUsageView</c> under the same
    /// names: the wall clock of the room's zero-token pre-turn verify step, and the size of the
    /// <c>verify-results.md</c> the reviewer then reads. Neither is a token figure and neither enters
    /// any estimate — a row carrying them is not priced differently. Which execution they land on, and
    /// why they are present together or not at all, is <c>ExecutionUsageView.VerifyStepMs</c>'s
    /// contract (spec/baton.md §3), not restated here.
    /// </summary>
    [property: JsonPropertyName("verifyStepMs")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? VerifyStepMs = null,
    [property: JsonPropertyName("verifyResultsBytes")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? VerifyResultsBytes = null,

    // The vendor-derived billed figures ExecutionUsageView already owns the definitions of -- carried
    // through under the same names rather than recomputed, so #1706's reconciliation triple means one
    // thing in both files.
    [property: JsonPropertyName("billedTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? BilledTokens = null,
    [property: JsonPropertyName("liveBilledTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? LiveBilledTokens = null,
    [property: JsonPropertyName("billedUnderReadTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? BilledUnderReadTokens = null,
    [property: JsonPropertyName("peakBilledInWindow")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? PeakBilledInWindow = null,
    [property: JsonPropertyName("raw")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, JsonElement>? Raw = null,

    [property: JsonPropertyName("completeness")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    CostCompleteness? Completeness = null,
    /// <summary>
    /// Why <see cref="Completeness"/> is <see cref="CostCompleteness.Partial"/> — whichever string
    /// <c>ExecutionUsageView.BilledReconciliationUnavailable</c> emitted, carried through verbatim
    /// rather than re-spelled. The vocabulary is that field's own
    /// (<c>ExecutionUsageView.KnownUnavailableReasons</c>); every one of its values makes a row partial,
    /// so a reason added there cannot silently land here as <see cref="CostCompleteness.Complete"/>.
    /// </summary>
    [property: JsonPropertyName("completenessReason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CompletenessReason = null,

    /// <summary>The vendor's API list-price equivalent. An ESTIMATE for comparison, never an invoice.</summary>
    [property: JsonPropertyName("apiEquivalentUsd")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    decimal? ApiEquivalentUsd = null,
    [property: JsonPropertyName("estimateStatus")]
    EstimateStatus EstimateStatus = EstimateStatus.Unpriced,
    /// <summary>
    /// The same token dimensions re-weighted by the plan-factor table — what the SUBSCRIPTION meter is
    /// believed to charge, as distinct from list price. Also an estimate; also never a quota reading.
    /// </summary>
    [property: JsonPropertyName("planMeterEstimateUsd")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    decimal? PlanMeterEstimateUsd = null,
    [property: JsonPropertyName("planMeterEstimateStatus")]
    EstimateStatus PlanMeterEstimateStatus = EstimateStatus.Unpriced,
    /// <summary>
    /// Why BOTH estimates above are <see cref="EstimateStatus.Unpriced"/> for a reason other than a
    /// missing rate — one of <c>multi-model-usage</c> (<see cref="ModelsObserved"/> names more than one
    /// model, so the tokens are a sum no single rate applies to) or <c>model-mismatch</c> (it names one
    /// model, and it is not the requested <see cref="Model"/> whose rate would have been used). Absent
    /// whenever pricing was attempted, including when it was attempted and the catalog simply had no
    /// rate: absence here means "the tokens were attributable", never "priced". Per-model rows, which
    /// would let a multi-model tree be priced rather than refused, are phase B's.
    /// </summary>
    [property: JsonPropertyName("estimateReason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? EstimateReason = null,

    // The four provenance stamps that make an estimate reproducible -- PriceCatalog's own remarks state
    // the guarantee they buy, which is #1849's acceptance criterion "price-catalog changes do not
    // retroactively rewrite prior estimated totals".
    [property: JsonPropertyName("priceCatalogId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PriceCatalogId = null,
    [property: JsonPropertyName("priceCatalogVersion")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PriceCatalogVersion = null,
    [property: JsonPropertyName("planFactorTableId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PlanFactorTableId = null,
    [property: JsonPropertyName("planFactorTableVersion")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PlanFactorTableVersion = null,

    /// <summary>
    /// #1848: the operator's reason, verbatim, when this execution was admitted only because a
    /// <c>baton dispatch --override-runway "&lt;reason&gt;"</c> bypassed a runway hold. <b>Absence means
    /// "no override was recorded for this execution", never "no override happened"</b> — a row built
    /// for a room dispatched some other way (<c>baton run</c> against a hand-authored
    /// <c>bindings.json</c>, a room whose bindings file is gone by settle time) simply has nothing to
    /// read, and the settle site's read is deliberately fail-open. A dispatch that passed the flag and
    /// was admitted anyway is also absent here: that override bypassed nothing, and only the room
    /// record distinguishes it (<c>Baton.Vendors.RunwayOverride.Used</c>).
    /// </summary>
    [property: JsonPropertyName("runwayOverrideReason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RunwayOverrideReason = null,

    // #1901 C1 item 3: the diff shape of what this attempt's workspace has pushed, from ONE
    // `git diff --numstat origin/main...HEAD` in that workspace at settle. All four are present
    // together or absent together -- one spawn produces all of them, so a partial set would only ever
    // mean a bug. ABSENT, never zero, when the workspace pushed nothing, is gone by settle time, or is
    // not a git repository; a genuine empty diff (a branch level with origin/main) reports four zeros,
    // which is a measurement rather than an absence.
    /// <summary>Files touched between <c>origin/main</c> and the workspace's HEAD.</summary>
    [property: JsonPropertyName("filesChanged")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? FilesChanged = null,
    /// <summary>Lines added across those files. A binary file contributes to <see cref="FilesChanged"/> and to neither line count — git reports no line counts for one.</summary>
    [property: JsonPropertyName("additions")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? Additions = null,
    /// <summary>Lines removed across those files, with the same binary-file caveat.</summary>
    [property: JsonPropertyName("deletions")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? Deletions = null,
    /// <summary>
    /// How many of <see cref="FilesChanged"/> live under this repository's <c>tests/</c> tree — the
    /// crude "did this change ship a test" reading #1849 wants, deliberately a path prefix rather than
    /// a judgment about whether a file IS a test.
    /// </summary>
    [property: JsonPropertyName("testFilesChanged")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? TestFilesChanged = null,

    // #1901 C1 item 2: what a review execution's own verdict.json says, parsed at settle from the
    // artifact the engine already stamps (#1889). All five are absent together when the execution
    // wrote no verdict.json, or wrote one that does not parse as a ReviewVerdict.
    /// <summary>
    /// <c>ReviewVerdict.ReviewedRef</c> verbatim — a branch, commit or PR reference, whichever the
    /// reviewer named. The durable fact; <see cref="ReviewedPr"/>/<see cref="ReviewedHead"/> below are
    /// only what a positive parse of it could extract.
    /// </summary>
    [property: JsonPropertyName("reviewedRef")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ReviewedRef = null,
    /// <summary>
    /// The PR number <see cref="ReviewedRef"/> names, as a bare decimal, when it positively parses as
    /// one (<c>123</c>, <c>#123</c>, or a <c>.../pull/123</c> URL). <b>Absent means the ref did not name
    /// a PR</b> — a branch-name or commit-SHA review is the ordinary case, not a defect.
    /// </summary>
    [property: JsonPropertyName("reviewedPr")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ReviewedPr = null,
    /// <summary>
    /// The commit <see cref="ReviewedRef"/> names, when it is a bare hex SHA (7–40 characters).
    /// Absent for every other spelling, on the same "positively parsed or nothing" rule as
    /// <see cref="ReviewedPr"/>. At most one of the two is ever written — an ambiguous ref that
    /// satisfies both shapes yields neither, which <see cref="CostLedgerStore.SplitReviewedRef"/>
    /// enforces and explains.
    /// </summary>
    [property: JsonPropertyName("reviewedHead")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ReviewedHead = null,
    /// <summary>
    /// High-severity findings in that verdict. <b>The one place on this row where <c>0</c> is a
    /// measurement rather than an absence</b>: <c>ReviewVerdict.Findings</c>'s own doc says an empty
    /// array is valid and meaningful (the reviewer looked and found nothing), so a verdict that exists
    /// writes all three counts including zeros, and no verdict writes none of them. Three flat fields
    /// rather than one nested object so the CSV view keeps them summable — a nested value renders there
    /// as a quoted JSON blob (<see cref="LedgerCsv"/>'s own rule for <see cref="ModelsObserved"/>).
    /// </summary>
    [property: JsonPropertyName("findingsHigh")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? FindingsHigh = null,
    /// <summary>Medium-severity findings — same present/absent rule as <see cref="FindingsHigh"/>.</summary>
    [property: JsonPropertyName("findingsMedium")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? FindingsMedium = null,
    /// <summary>Low-severity findings — same present/absent rule as <see cref="FindingsHigh"/>.</summary>
    [property: JsonPropertyName("findingsLow")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? FindingsLow = null,

    /// <summary>
    /// #1901 C1 item 4: which <c>baton resolve</c> a conductor recorded on this room. Present ONLY on a
    /// correcting row appended after the execution rows it follows — never on an execution row, which
    /// is immutable once written. <see cref="CostLedgerStore.BuildResolutionRow"/> is the one writer and
    /// states the shape.
    /// </summary>
    [property: JsonPropertyName("resolution")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ConductorResolution? Resolution = null,
    /// <summary>
    /// The conductor's own <c>--reason</c>, verbatim. Absent when none was given, which
    /// <c>ResolveOptionsParser</c> permits only for <c>--accept-capture</c>.
    /// </summary>
    [property: JsonPropertyName("resolutionReason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ResolutionReason = null);
