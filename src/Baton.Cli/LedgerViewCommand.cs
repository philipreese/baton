using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Accounting;
using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// <c>baton ledger [&lt;room-dir&gt;] [filters] [--format text|json|csv] [--drill]</c> (#1849 phase B,
/// operator ruling 2026-09-05): the room and fleet readings of the repository-keyed cost ledger.
/// </summary>
/// <remarks>
/// <para>
/// <b>This command formats; it does not sum.</b> Every number below comes from
/// <see cref="LedgerRollup"/> — the one accounting projection, whose own remarks state what that buys
/// and how a room reading relates to a fleet one — so the two readings and all three formats are
/// arithmetically incapable of disagreeing.
/// </para>
/// <para>
/// <b>Why the order is fixed</b> (spec/baton.md §7 states what it is): a reader who stops after the
/// first screen has read the per-vendor answer, which is the one #1849 says is comparable across
/// vendors. The all-vendor figure comes last because it is the one that must be read together with
/// its label.
/// </para>
/// <para>
/// Not a <see cref="CommandResult"/>/<see cref="FlowStateReporter"/> command, for the same reason
/// <see cref="LedgerCommand"/> is not: there is no workflow pump here to report on.
/// </para>
/// </remarks>
public static class LedgerViewCommand
{
    /// <summary>
    /// <c>WhenWritingNull</c>, matching the ledger file's own serialization: an absent field is absent
    /// in the view too, never <c>null</c> and never <c>0</c>. The record's per-property attributes
    /// already say this for <see cref="CostLedgerEntry"/>; this repeats it for the rollup types so a
    /// filter nobody set is simply not in the echoed <c>query</c>.
    /// </summary>
    private static readonly JsonSerializerOptions ViewSerializerOptions =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false };

    public static async Task<int> ExecuteAsync(
        LedgerViewOptions options,
        TextWriter output,
        string? ledgerFilePathOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        if (options.Help)
        {
            Write(output, LedgerViewOptionsParser.Usage);
            foreach (var line in LedgerViewOptionsParser.HelpLines)
            {
                Write(output, line);
            }

            return 0;
        }

        var ledgerFilePath = ledgerFilePathOverride
            ?? await ResolveLedgerFilePathAsync(
                options.RepositoryIdentityKey, options.RoomDirectoryPath, cancellationToken).ConfigureAwait(false);

        var entries = await CostLedgerStore.ReadAllAsync(ledgerFilePath, cancellationToken).ConfigureAwait(false);

        // CSV is rows OR NOTHING -- it has no subtotal section for --drill to be an alternative to, so
        // gating its rows on that flag would make the obvious `--format csv > out.csv` write an empty
        // export and exit 0. --drill selects the row section of the two formats that have one.
        var rollup = LedgerRollup.Build(
            entries, options.Query, options.Drill || options.Format == LedgerOutputFormat.Csv);

        switch (options.Format)
        {
            case LedgerOutputFormat.Json:
                Write(output, JsonSerializer.Serialize(rollup, ViewSerializerOptions));
                break;
            case LedgerOutputFormat.Csv:
                LedgerCsv.Write(output, rollup.Rows ?? []);
                break;
            default:
                WriteText(output, rollup, ledgerFilePath);
                break;
        }

        return 0;
    }

    /// <summary>
    /// Which repository's ledger file this reading is over.
    /// <list type="bullet">
    /// <item><c>--repo-identity</c> names it explicitly, in either spelling an operator has to hand: the
    /// file's own stem (what <c>ls ~/.baton/ledger</c> shows) when a file by that name exists, else the
    /// canonical identity a row records (<c>github.com/owner/repo</c>), slugged the way the writer
    /// slugged it. Case-folded first, because <c>RepositoryIdentity.From</c> case-folds before hashing
    /// and an unfolded key would digest to a different — and empty — file.</item>
    /// <item>With a <c>&lt;room-dir&gt;</c> and no explicit key, the ROOM's own repository, off its
    /// registry entry — not the working directory's. A room is read from wherever the operator happens
    /// to be standing, including outside any repository at all.</item>
    /// <item>Otherwise the repository the operator is standing in.</item>
    /// </list>
    /// A directory with no repository identity is a <see cref="CliArgumentException"/> naming
    /// <c>--repo-identity</c>, rather than an empty rollup: "no rows" and "you asked the wrong
    /// question" must not print the same thing.
    /// <para>
    /// <c>internal</c> rather than private so <see cref="LedgerExportCommand"/> reaches the same
    /// resolution instead of growing a second one — an export that opened a different file from the
    /// reading it claims to reproduce would be the exact drift this method's rules exist to prevent.
    /// </para>
    /// </summary>
    internal static async Task<string> ResolveLedgerFilePathAsync(
        string? repositoryIdentityKey, string? roomDirectoryPath, CancellationToken cancellationToken)
    {
        if (repositoryIdentityKey is { Length: > 0 } key)
        {
            var trimmed = key.Trim();
            var byFileStem = Path.Combine(BatonPaths.Root, BatonPaths.CostLedgerDirectoryName, $"{trimmed}.jsonl");
            return File.Exists(byFileStem)
                ? byFileStem
                : BatonPaths.CostLedgerFile(RepositoryIdentity.FileSlugFor(trimmed.ToLowerInvariant()));
        }

        var repository = roomDirectoryPath is { Length: > 0 } room
            // A read, not a write: the source says how a ROW was keyed, and this call is only choosing
            // which file to open, so it is discarded rather than surfaced.
            ? (await RepositoryIdentityResolver.TryResolveForRoomAsync(room, cancellationToken).ConfigureAwait(false)).Identity
            : await RepositoryIdentityResolver.TryResolveAsync(Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false);

        if (repository is null)
        {
            throw new CliArgumentException(
                "No repository identity here: git reported neither an 'origin' remote nor a repository for "
                + (roomDirectoryPath is { Length: > 0 } named
                    ? $"room '{named}' (its recorded project root, or this working directory when it has no registry entry). "
                    : $"'{Environment.CurrentDirectory}'. ")
                + "The cost ledger is keyed by repository, so there is no file to read. Name one explicitly.",
                "baton ledger --repo-identity github.com/owner/repo");
        }

        return BatonPaths.CostLedgerFile(repository.FileSlug);
    }

    private static void WriteText(TextWriter output, LedgerRollup rollup, string ledgerFilePath)
    {
        var query = rollup.Query;

        Write(output, $"Cost ledger: {ledgerFilePath}");
        if (!File.Exists(ledgerFilePath))
        {
            // Said out loud: an empty rollup from a file that was never written looks exactly like a
            // repository that spent nothing, and only this line tells the two apart.
            Write(output, "  (no ledger file yet for this repository -- nothing has settled here)");
        }

        if (query.Room is { Length: > 0 } room)
        {
            Write(output, $"Room: {room}");
        }

        Write(output, $"Window: {DescribeWindow(query)}");

        var facets = DescribeFacets(query);
        if (facets is not null)
        {
            Write(output, $"Filters: {facets}");
        }

        Write(
            output,
            $"Rows: {Number(rollup.Total.Attempts)} matched"
                + (query.UndatedExcluded > 0
                    ? $", {Number(query.UndatedExcluded)} excluded by the window for having no endedAt"
                    : string.Empty));
        // Same disclosure as the missing-file line above, one level down: a room key no row carries
        // reads exactly like a room that spent nothing. A mistyped path gets here, and so does a room
        // that was never registered -- the identity probe then falls back to the working directory's
        // repository, which is a real ledger with none of that room's rows in it.
        if (query.Room is { Length: > 0 } filteredRoom && rollup.Total.Attempts == 0)
        {
            Write(
                output,
                $"  (no row in this ledger carries room '{filteredRoom}' -- either nothing has settled "
                    + "there, or that room's work belongs to a different repository's ledger)");
        }

        Write(output, string.Empty);

        // Per-vendor FIRST -- the comparable answer. The all-vendor line follows, never precedes.
        foreach (var vendor in rollup.Vendors)
        {
            WriteSubtotal(output, vendor.Vendor ?? LedgerRollup.UnknownVendor, vendor);
            Write(output, string.Empty);
        }

        WriteSubtotal(output, "all vendors", rollup.Total);
        Write(
            output,
            "  Both figures are ESTIMATES -- API list-price equivalent and modelled plan-meter cost. "
                + "Neither is an invoice, subscription spend, or a quota reading.");

        if (rollup.Rows is not { } rows)
        {
            return;
        }

        Write(output, string.Empty);
        Write(output, $"Rows contributing to the subtotals above ({Number(rows.Count)}):");
        foreach (var row in rows)
        {
            Write(output, $"  {DescribeRow(row)}");
        }
    }

    private static void WriteSubtotal(TextWriter output, string label, LedgerSubtotal subtotal)
    {
        var completeness = new List<string>();
        if (subtotal.Partial > 0)
        {
            completeness.Add($"{Number(subtotal.Partial)} partial");
        }

        if (subtotal.Unread > 0)
        {
            completeness.Add($"{Number(subtotal.Unread)} with no usage read");
        }

        // ATTEMPTS is the execution count, never the row count (#1931 review MEDIUM). A github-backfill
        // row is a merged PR nothing ran for, and folding it in read as "235 attempts, 235 of them with
        // no usage read" after one backfill. The two populations are printed side by side rather than
        // one silently absorbed into the other; LedgerSubtotal carries both counts, so nothing here
        // sums anything (this command formats).
        Write(
            output,
            $"{label} -- {Number(subtotal.Executions)} attempt(s)"
                + (completeness.Count > 0 ? $" ({string.Join(", ", completeness)})" : string.Empty)
                + (subtotal.PullRequests > 0
                    ? $" + {Number(subtotal.PullRequests)} merged-PR row(s), which record an outcome rather "
                        + "than an attempt -- no execution, no usage, no estimate"
                    : string.Empty));
        Write(
            output,
            "  tokens: "
                + $"in {Tokens(subtotal.TokensIn)}, out {Tokens(subtotal.TokensOut)}, "
                + $"cache-read {Tokens(subtotal.CacheReadTokens)}, cache-creation {Tokens(subtotal.CacheCreationTokens)}, "
                + $"thinking {Tokens(subtotal.ThinkingTokens)}");
        var partial = PartialDimensions(subtotal);
        if (partial is not null)
        {
            Write(output, $"  partial -- summed from SOME of the attempts only: {partial}");
        }

        // "row(s)", not "attempt(s)": the denominator is every row in the subtotal, and a merged-PR row
        // is counted into the by-status buckets beside it (spec/baton.md §7 -- unlike `unread`, that is
        // a true statement about a row with no estimate, so it stays).

        Write(
            output,
            $"  API-equivalent estimate: {Money(subtotal.ApiEquivalentUsd)} "
                + Contributors(subtotal.ReportedBy.ApiEquivalentUsd, subtotal.Attempts, subtotal.ApiEquivalentByStatus));
        Write(
            output,
            $"  plan-meter estimate: {Money(subtotal.PlanMeterEstimateUsd)} "
                + Contributors(subtotal.ReportedBy.PlanMeterEstimateUsd, subtotal.Attempts, subtotal.PlanMeterByStatus));
    }

    /// <summary>
    /// The token dimensions SOME but not all of the attempts reported, "n of m" each — the all-vendor
    /// line is where this bites, since a dimension only claude reports is a claude-only sum printed in
    /// the same shape as one every row fed (#1893 review M1). A dimension NO attempt reported is absent
    /// rather than partial and is already printed as <c>-</c> above; one every attempt reported needs no
    /// disclosure at all, so both are left out here.
    /// </summary>
    private static string? PartialDimensions(LedgerSubtotal subtotal)
    {
        (string Name, int Count)[] dimensions =
        [
            ("in", subtotal.ReportedBy.TokensIn),
            ("out", subtotal.ReportedBy.TokensOut),
            ("cache-read", subtotal.ReportedBy.CacheReadTokens),
            ("cache-creation", subtotal.ReportedBy.CacheCreationTokens),
            ("thinking", subtotal.ReportedBy.ThinkingTokens),
        ];

        // Denominator is EXECUTIONS: only an execution row can report a token dimension, so counting
        // merged-PR rows here would print every dimension as partial after one backfill.
        var partial = dimensions
            .Where(d => d.Count > 0 && d.Count < subtotal.Executions)
            .Select(d => $"{d.Name} {Number(d.Count)} of {Number(subtotal.Executions)}")
            .ToList();

        return partial.Count == 0 ? null : string.Join(", ", partial);
    }

    /// <summary>
    /// How many attempts fed a money figure, then why the rest did not — <b>by the row's own recorded
    /// status name</b>, so agy's never-measured plan meter says <c>unmeasured</c> rather than being
    /// filed under <c>unpriced</c> with three other states (#1893 review M2). A state no attempt is in
    /// is omitted rather than printed as a zero.
    /// </summary>
    private static string Contributors(int reportedBy, int rows, LedgerEstimateStatusCounts byStatus)
    {
        (string Name, int Count)[] states =
        [
            ("estimated", byStatus.Estimated),
            ("unpriced", byStatus.Unpriced),
            ("unknown", byStatus.Unknown),
            ("unmeasured", byStatus.Unmeasured),
        ];

        var reasons = states.Where(s => s.Count > 0).Select(s => $"{s.Name}: {Number(s.Count)}").ToList();
        return $"(summed from {Number(reportedBy)} of {Number(rows)} row(s)"
            + (reasons.Count > 0 ? $"; {string.Join(", ", reasons)})" : ")");
    }

    private static string DescribeRow(CostLedgerEntry row)
    {
        var builder = new StringBuilder();
        builder.Append(row.EndedAt is { } endedAt ? Instant(endedAt) : "(no endedAt)".PadRight(20));
        builder.Append("  ").Append(row.Adapter ?? LedgerRollup.UnknownVendor);
        // #1927: what was asked for, and -- only when they DISAGREE -- what the vendor said it ran.
        // A mismatch is a substitution or a quota-driven downgrade, and it is invisible in either
        // field alone; printing the echo unconditionally would bury the one reading worth spotting
        // under a duplicate on every other row. `-> <echo>` renders when the two differ, and when the
        // requested model is absent altogether but an echo exists (a room dispatched with no --model
        // against a binding too old to carry a resolved stamp).
        builder.Append("  ").Append(row.Model ?? "-");
        if (row.ModelEchoed is { Length: > 0 } echoed
            && !string.Equals(echoed, row.Model, StringComparison.OrdinalIgnoreCase))
        {
            builder.Append(" -> ").Append(echoed);
        }

        builder.Append("  ").Append(row.Role ?? "-");
        builder.Append("  ").Append(row.Outcome ?? "-");
        builder.Append("  in ").Append(Tokens(row.TokensIn));
        builder.Append(" out ").Append(Tokens(row.TokensOut));
        builder.Append(" cache-read ").Append(Tokens(row.CacheReadTokens));
        builder.Append(" cache-creation ").Append(Tokens(row.CacheCreationTokens));
        builder.Append(" thinking ").Append(Tokens(row.ThinkingTokens));

        // #1921: the step-budget axis, beside the token axis. Rendered only when the stream reader
        // actually counted -- the digest omits most columns by design, and a row whose stream carried no
        // readable tool activity would otherwise gain three '-' columns saying nothing. The tuple
        // pattern is the invariant made structural: these three are written together or not at all
        // (CostLedgerEntry.ToolSteps), so a row carrying some of them is a bug rather than a case to
        // render around.
        if ((row.ToolSteps, row.RefusedToolSteps, row.RepeatedToolSteps)
            is ({ } toolSteps, { } refusedToolSteps, { } repeatedToolSteps))
        {
            builder.Append("  steps ").Append(Number(toolSteps));
            builder.Append(" refused ").Append(Number(refusedToolSteps));
            builder.Append(" repeated ").Append(Number(repeatedToolSteps));
        }

        builder.Append("  api ").Append(Money(row.ApiEquivalentUsd));
        builder.Append(" plan ").Append(Money(row.PlanMeterEstimateUsd));
        builder.Append("  ").Append(row.Execution ?? "(no execution id)");

        // #1913 review finding 6: without this clause a correcting row renders as an execution attempt
        // nothing was read for, wearing the CORRECTED row's outcome -- 'Succeeded', for an intervention.
        // The digest omits most columns by design; this is the one that says what kind of row it is.
        if (row.Resolution is { } resolution)
        {
            builder.Append("  resolution=").Append(JsonSerializer.Serialize(resolution).Trim('"'));
        }

        // #1931 review MEDIUM, the same clause for the same reason one line up: a github-backfill row
        // renders identically to an execution attempt nothing was read for -- (unknown) vendor, '-'
        // model, '-' role, '-' outcome, every token column '-'. Its `github-pr-<n>` execution id is an
        // incidental tell, which #1913 finding 6 already ruled insufficient for correcting rows.
        if (row.SourceKind == CostSourceKind.GithubBackfill)
        {
            builder.Append("  merged-PR row (github-backfill): no execution behind it");
        }

        return builder.ToString();
    }

    private static string DescribeWindow(LedgerQuery query) => (query.Since, query.Until) switch
    {
        (null, null) => "everything in the file (no --since/--until)",
        ({ } since, null) => $"endedAt >= {Instant(since)} (inclusive)",
        (null, { } until) => $"endedAt < {Instant(until)} (exclusive)",
        ({ } since, { } until) => $"endedAt >= {Instant(since)} (inclusive) and < {Instant(until)} (exclusive)",
    };

    private static string? DescribeFacets(LedgerQuery query)
    {
        var facets = new List<string>();
        void Add(string name, string? value)
        {
            if (value is { Length: > 0 })
            {
                facets.Add($"{name}={value}");
            }
        }

        Add("vendor", query.Vendor);
        Add("model", query.Model);
        Add("role", query.Role);
        Add("project", query.Project);
        Add("outcome", query.Outcome);
        Add("workflow", query.Workflow);
        Add("pr", query.PullRequest);
        Add("issue", query.Issue);
        if (query.SourceKind is { } kind)
        {
            Add("source-kind", JsonSerializer.Serialize(kind).Trim('"'));
        }

        // Printed under the same name the operator typed, including the two that select on presence:
        // a reading filtered to execution attempts alone must say so, or its total reads as the file's.
        if (query.Resolution is { } resolutionKind)
        {
            Add("resolution", JsonSerializer.Serialize(resolutionKind).Trim('"'));
        }
        else if (query.HasResolution is { } hasResolution)
        {
            Add("resolution", hasResolution ? "any" : "none");
        }

        return facets.Count == 0 ? null : string.Join(", ", facets);
    }

    /// <summary>A dimension no row reported prints as <c>-</c>, never <c>0</c> — the subtotal's own absence doctrine, kept in the rendering.</summary>
    private static string Tokens(long? value) =>
        value is { } present ? present.ToString(CultureInfo.InvariantCulture) : "-";

    private static string Money(decimal? value) =>
        value is { } present ? "$" + present.ToString("0.######", CultureInfo.InvariantCulture) : "-";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Rendered in UTC, the frame the ledger records in — an instant that arrived as local time is converted, not relabelled.</summary>
    private static string Instant(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// LF explicitly, not <see cref="TextWriter.WriteLine()"/>: this repo runs on Windows, and the
    /// same query over the same file has to produce the same BYTES wherever it is compared (#1849's
    /// determinism criterion), which a host-dependent line ending would break.
    /// </summary>
    private static void Write(TextWriter output, string line)
    {
        output.Write(line);
        output.Write('\n');
    }
}
