using System.Text.Json.Serialization;

namespace Baton.Accounting;

/// <summary>
/// How many of a subtotal's attempts actually reported each summed dimension — the disclosure that
/// keeps a PARTIAL sum from reading as a complete one (#1893 review M1).
/// </summary>
/// <remarks>
/// <para>
/// Absence is all-or-nothing only within a vendor, and not even always there: claude reports
/// cache-creation and agy does not, so the ALL-VENDOR <c>cacheCreation</c> total is claude's rows
/// alone, printed in the same shape as a figure every row contributed to. A count beside each
/// dimension is what tells those apart, exactly as <see cref="LedgerSubtotal.ApiEquivalentByStatus"/>
/// does for the money.
/// </para>
/// <para>
/// A count of <c>0</c> means the dimension is ABSENT from the subtotal (its sum is
/// <see langword="null"/>); a count equal to <see cref="LedgerSubtotal.Attempts"/> means every attempt
/// contributed. Anything between the two is a partial sum.
/// </para>
/// </remarks>
public sealed record LedgerReportedBy(
    [property: JsonPropertyName("tokensIn")] int TokensIn,
    [property: JsonPropertyName("tokensOut")] int TokensOut,
    [property: JsonPropertyName("cacheRead")] int CacheReadTokens,
    [property: JsonPropertyName("cacheCreation")] int CacheCreationTokens,
    [property: JsonPropertyName("thinking")] int ThinkingTokens,
    [property: JsonPropertyName("apiEquivalentUsd")] int ApiEquivalentUsd,
    [property: JsonPropertyName("planMeterEstimateUsd")] int PlanMeterEstimateUsd);

/// <summary>
/// A subtotal's attempts counted by the row's OWN <see cref="EstimateStatus"/> — one count per state,
/// never a bucket named after one state while holding three (#1893 review M2).
/// </summary>
/// <remarks>
/// The four counts sum to <see cref="LedgerSubtotal.Attempts"/> by construction: the enum is closed and
/// every row carries exactly one of its values. Derived from the recorded status rather than from
/// "is the dollar figure null", because those answer different questions — <i>why</i> there is no
/// number versus <i>how many rows fed</i> the one there is, which is
/// <see cref="LedgerReportedBy"/>'s job. Collapsing them is what made an agy row whose plan meter has
/// never been MEASURED print as <c>unpriced</c>.
/// </remarks>
public sealed record LedgerEstimateStatusCounts(
    [property: JsonPropertyName("estimated")] int Estimated,
    [property: JsonPropertyName("unpriced")] int Unpriced,
    [property: JsonPropertyName("unknown")] int Unknown,
    [property: JsonPropertyName("unmeasured")] int Unmeasured);

/// <summary>
/// One vendor's — or, with a <see langword="null"/> <see cref="Vendor"/>, the whole selection's —
/// arithmetic over a set of cost-ledger rows (#1849 phase B).
/// </summary>
/// <remarks>
/// <b>A token dimension no row reported is ABSENT, not zero.</b> The sum is over the rows that
/// carried the dimension, and when none did there is no number — the same doctrine
/// <see cref="CostLedgerEntry"/> keeps per row, which would otherwise be destroyed by the first
/// addition: agy reports no cache-creation at all, and a <c>0</c> there would read as "agy created no
/// cache" rather than "agy does not report it".
/// </remarks>
/// <param name="Vendor">
/// The row's <see cref="CostLedgerEntry.Adapter"/>. <see langword="null"/> on the all-vendor total,
/// and also the grouping key for rows carrying no adapter at all — <see cref="LedgerRollup.UnknownVendor"/>
/// is what those group under, so "we do not know which vendor" is never silently merged into a named one.
/// </param>
/// <param name="Attempts">
/// Rows in this subtotal — <b>every</b> row, priced or not. A row that produced no estimate is counted
/// here and disclosed in <see cref="ApiEquivalentByStatus"/> under the reason it produced none; it is
/// never dropped to make a cost total look tidy. The four counts in
/// <see cref="ApiEquivalentByStatus"/> sum to this, always, and so do
/// <see cref="PlanMeterByStatus"/>'s.
/// </param>
/// <param name="Partial">
/// How many of <paramref name="Attempts"/> carry <see cref="CostCompleteness.Partial"/> — i.e. the
/// stream reader could not establish that the row holds the whole attempt's usage. Read a subtotal
/// with a nonzero count here as a floor on what was spent, not a measurement of it.
/// </param>
/// <param name="Unread">
/// How many <b>execution</b> rows carry NO completeness label at all: nothing was read for them (no
/// parser for the adapter, no captured stream). Distinct from <paramref name="Partial"/> on purpose —
/// <see cref="CostLedgerStore.ResolveCompleteness"/> states the three-state split.
/// <para>
/// <b><see cref="CostSourceKind.GithubBackfill"/> rows are excluded, and that is this field's own
/// definition being honoured rather than an arithmetic preference</b> (#1931 review MEDIUM): nothing
/// ran behind a merged PR, so the sentence above is false about one. They are counted in
/// <paramref name="PullRequests"/> instead; spec/baton.md §7 carries the measurement that forced it
/// and what the rest of a reading still counts them into.
/// </para>
/// <para>
/// <b>A CORRECTING row is knowingly left counted here</b>, though the same sentence is equally false
/// about it: spec/baton.md §7 discloses that case and names <c>--resolution none</c> as its remedy,
/// and #1931's ruling was scoped to the merged-PR population. The asymmetry is a decision, not an
/// oversight — closing it means amending that disclosure, not editing this line alone.
/// </para>
/// </param>
/// <param name="Executions">
/// How many of <paramref name="Attempts"/> are about something Baton ran — every row that is not a
/// <see cref="CostSourceKind.GithubBackfill"/> one, correcting rows included (spec/baton.md §7's
/// ruling is that an intervention stays counted with the attempts it corrects).
/// </param>
/// <param name="PullRequests">
/// How many of <paramref name="Attempts"/> are <see cref="CostSourceKind.GithubBackfill"/> rows: a
/// merged pull request recovered from GitHub, with no execution, no usage and no estimate behind it.
/// <b>Carried here so a surface can print the two populations apart without summing anything itself</b>
/// — <c>Executions + PullRequests == Attempts</c>, always.
/// </param>
public sealed record LedgerSubtotal(
    [property: JsonPropertyName("adapter")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Vendor,
    [property: JsonPropertyName("attempts")]
    int Attempts,
    [property: JsonPropertyName("partial")]
    int Partial,
    [property: JsonPropertyName("unread")]
    int Unread,
    [property: JsonPropertyName("executions")]
    int Executions,
    [property: JsonPropertyName("pullRequests")]
    int PullRequests,
    [property: JsonPropertyName("tokensIn")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TokensIn,
    [property: JsonPropertyName("tokensOut")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TokensOut,
    [property: JsonPropertyName("cacheRead")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? CacheReadTokens,
    [property: JsonPropertyName("cacheCreation")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? CacheCreationTokens,
    [property: JsonPropertyName("thinking")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? ThinkingTokens,
    /// <summary>Sum of the rows that HAVE an API-equivalent estimate. An estimate at list price, never an invoice and never subscription spend.</summary>
    [property: JsonPropertyName("apiEquivalentUsd")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    decimal? ApiEquivalentUsd,
    /// <summary>Sum of the rows that HAVE a plan-meter estimate. Also an estimate; also never a quota reading.</summary>
    [property: JsonPropertyName("planMeterEstimateUsd")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    decimal? PlanMeterEstimateUsd,
    /// <summary>How many attempts fed each sum above — see <see cref="LedgerReportedBy"/> for why a sum without it can mislead.</summary>
    [property: JsonPropertyName("reportedBy")]
    LedgerReportedBy ReportedBy,
    /// <summary>The attempts by <see cref="CostLedgerEntry.EstimateStatus"/>, the API-equivalent half.</summary>
    [property: JsonPropertyName("apiEquivalentByStatus")]
    LedgerEstimateStatusCounts ApiEquivalentByStatus,
    /// <summary>The attempts by <see cref="CostLedgerEntry.PlanMeterEstimateStatus"/> — where <c>unmeasured</c> (agy) and <c>unpriced</c> (a missing rate) are different answers.</summary>
    [property: JsonPropertyName("planMeterByStatus")]
    LedgerEstimateStatusCounts PlanMeterByStatus);

/// <summary>
/// <b>The one accounting projection</b> (#1849 phase B, operator ruling 2026-09-05): the arithmetic
/// behind every cost-ledger view — room and fleet, text, JSON and CSV — lives here, and each surface
/// formats what this returns rather than summing rows of its own. A room view IS the fleet view with
/// <see cref="LedgerQuery.Room"/> set; there is no second code path for it, which is what makes the
/// two answers incapable of disagreeing.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Total"/> is computed over the rows, never by adding <see cref="Vendors"/> up.</b>
/// Summing subtotals would have to invent an answer for "absent in one vendor, present in another",
/// and the two arithmetics would then be free to drift — the exact thing this type exists to prevent.
/// Row-summing does not invent one, but it does return a PARTIAL one, which is why every subtotal
/// carries <see cref="LedgerSubtotal.ReportedBy"/>: a cross-vendor token total is only as complete as
/// the count beside it says (#1893 review M1).
/// </para>
/// <para>
/// <b>Determinism is a promise of this type, not of its callers</b> (#1849's acceptance criterion:
/// the same window over the same file yields the same totals). <see cref="Rows"/> is ordered by
/// <c>endedAt</c>, then execution id, then the row's position in the file — a total order even for
/// two undated rows with no execution id, which <see cref="CostLedgerStore.AppendAsync"/> explicitly
/// permits. Undated rows sort last rather than first, which is <see cref="DateTime.MaxValue"/>'s job
/// below: LINQ's ordering puts a null FIRST, and "unknown when" reading as "earliest" would put it at
/// the top of every drill-down.
/// </para>
/// </remarks>
/// <param name="Query">
/// The selection these totals are over, echoed back — including
/// <see cref="LedgerQuery.UndatedExcluded"/>, which this method fills.
/// </param>
/// <param name="Vendors">
/// Per-vendor subtotals, ordered by vendor name; the unknown-vendor group sorts last so a named
/// vendor's position never depends on whether an unlabelled row happened to be in the window.
/// </param>
/// <param name="Rows">
/// The contributing rows, in the order above — <see langword="null"/> unless the caller asked for
/// them (<c>--drill</c>). Absent rather than empty, so "not requested" and "none matched" stay
/// distinguishable in the JSON.
/// </param>
public sealed record LedgerRollup(
    [property: JsonPropertyName("query")] LedgerQuery Query,
    [property: JsonPropertyName("vendors")] IReadOnlyList<LedgerSubtotal> Vendors,
    [property: JsonPropertyName("total")] LedgerSubtotal Total,
    [property: JsonPropertyName("rows")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<CostLedgerEntry>? Rows)
{
    /// <summary>The <see cref="LedgerSubtotal.Vendor"/> rows with no adapter group under. A literal, so a vendor that ever calls itself this cannot collide silently — no adapter is named this.</summary>
    public const string UnknownVendor = "(unknown)";

    /// <summary>
    /// Filters <paramref name="entries"/> by <paramref name="query"/>, orders what survives, and rolls
    /// it up per vendor and once overall.
    /// </summary>
    /// <param name="includeRows">Whether to carry the contributing rows in <see cref="Rows"/> (<c>--drill</c>).</param>
    public static LedgerRollup Build(
        IReadOnlyList<CostLedgerEntry> entries, LedgerQuery query, bool includeRows = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(query);

        var matched = new List<(CostLedgerEntry Entry, int FileOrder)>();
        var undatedExcluded = 0;

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (query.Matches(entry))
            {
                matched.Add((entry, i));
                continue;
            }

            // Counted, not silently dropped: a row this window could not place is the difference
            // between a windowed total and a complete one, and only this branch can see it.
            if (entry.EndedAt is null && !query.TimeMatches(entry) && MatchesIgnoringTime(query, entry))
            {
                undatedExcluded++;
            }
        }

        // Ordered on the SAME normalisation the window filters on (LedgerQuery.ToUtc): a row whose
        // endedAt deserialised as Kind.Local would otherwise be selected by its UTC instant and placed
        // by its wall clock. DateTime.MaxValue is a sentinel, not an instant, so it skips the
        // conversion rather than being shifted by an offset.
        var ordered = matched
            .OrderBy(m => m.Entry.EndedAt is { } endedAt ? LedgerQuery.ToUtc(endedAt) : DateTime.MaxValue)
            .ThenBy(m => m.Entry.Execution ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(m => m.FileOrder)
            .Select(m => m.Entry)
            .ToList();

        var vendors = ordered
            .GroupBy(e => e.Adapter is { Length: > 0 } adapter ? adapter : UnknownVendor, StringComparer.Ordinal)
            .OrderBy(g => g.Key == UnknownVendor ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => Summarize(g.Key, g.ToList()))
            .ToList();

        return new LedgerRollup(
            query with { UndatedExcluded = undatedExcluded },
            vendors,
            Summarize(null, ordered),
            includeRows ? ordered : null);
    }

    /// <summary>
    /// Every facet except the time window. Used only to decide whether an undated row was excluded
    /// BY THE WINDOW — a row a facet already rejected is not a casualty of the time filter and must
    /// not inflate <see cref="LedgerQuery.UndatedExcluded"/>.
    /// </summary>
    private static bool MatchesIgnoringTime(LedgerQuery query, CostLedgerEntry entry) =>
        (query with { Since = null, Until = null }).Matches(entry);

    private static LedgerSubtotal Summarize(string? vendor, IReadOnlyList<CostLedgerEntry> rows) =>
        new(
            Vendor: vendor,
            Attempts: rows.Count,
            Partial: rows.Count(r => r.Completeness == CostCompleteness.Partial),
            Unread: rows.Count(r => r.Completeness is null && r.SourceKind != CostSourceKind.GithubBackfill),
            Executions: rows.Count(r => r.SourceKind != CostSourceKind.GithubBackfill),
            PullRequests: rows.Count(r => r.SourceKind == CostSourceKind.GithubBackfill),
            TokensIn: SumPresent(rows, r => r.TokensIn),
            TokensOut: SumPresent(rows, r => r.TokensOut),
            CacheReadTokens: SumPresent(rows, r => r.CacheReadTokens),
            CacheCreationTokens: SumPresent(rows, r => r.CacheCreationTokens),
            ThinkingTokens: SumPresent(rows, r => r.ThinkingTokens),
            ApiEquivalentUsd: SumPresent(rows, r => r.ApiEquivalentUsd),
            PlanMeterEstimateUsd: SumPresent(rows, r => r.PlanMeterEstimateUsd),
            ReportedBy: new LedgerReportedBy(
                TokensIn: rows.Count(r => r.TokensIn is not null),
                TokensOut: rows.Count(r => r.TokensOut is not null),
                CacheReadTokens: rows.Count(r => r.CacheReadTokens is not null),
                CacheCreationTokens: rows.Count(r => r.CacheCreationTokens is not null),
                ThinkingTokens: rows.Count(r => r.ThinkingTokens is not null),
                ApiEquivalentUsd: rows.Count(r => r.ApiEquivalentUsd is not null),
                PlanMeterEstimateUsd: rows.Count(r => r.PlanMeterEstimateUsd is not null)),
            ApiEquivalentByStatus: CountByStatus(rows, r => r.EstimateStatus),
            PlanMeterByStatus: CountByStatus(rows, r => r.PlanMeterEstimateStatus));

    /// <summary>
    /// One count per <see cref="EstimateStatus"/> value, from the row's own recorded status — never
    /// inferred from whether the dollar figure is <see langword="null"/>, which cannot tell
    /// <c>unpriced</c>, <c>unknown</c> and <c>unmeasured</c> apart.
    /// </summary>
    private static LedgerEstimateStatusCounts CountByStatus(
        IReadOnlyList<CostLedgerEntry> rows, Func<CostLedgerEntry, EstimateStatus> select) =>
        new(
            Estimated: rows.Count(r => select(r) == EstimateStatus.Estimated),
            Unpriced: rows.Count(r => select(r) == EstimateStatus.Unpriced),
            Unknown: rows.Count(r => select(r) == EstimateStatus.Unknown),
            Unmeasured: rows.Count(r => select(r) == EstimateStatus.Unmeasured));

    /// <summary>Sum over the rows that HAVE the value, or <see langword="null"/> when none does — see the type remarks for why that is not zero.</summary>
    private static long? SumPresent(IReadOnlyList<CostLedgerEntry> rows, Func<CostLedgerEntry, long?> select)
    {
        long total = 0;
        var any = false;
        foreach (var row in rows)
        {
            if (select(row) is { } value)
            {
                total += value;
                any = true;
            }
        }

        return any ? total : null;
    }

    private static decimal? SumPresent(IReadOnlyList<CostLedgerEntry> rows, Func<CostLedgerEntry, decimal?> select)
    {
        decimal total = 0m;
        var any = false;
        foreach (var row in rows)
        {
            if (select(row) is { } value)
            {
                total += value;
                any = true;
            }
        }

        return any ? total : null;
    }
}
