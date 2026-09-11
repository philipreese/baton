using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Memory;
using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// <c>baton memory audit [--format text|json]</c> (#1852 phase A): inventory every Claude memory root
/// on this machine, map each to a canonical repository identity, and report what is duplicated,
/// orphaned, superseded, unprovenanced or ambiguous.
/// </summary>
/// <remarks>
/// <para>
/// <b>This command formats and resolves; it does not decide.</b> Every finding comes from
/// <see cref="MemoryAuditReport.Build"/>, which is pure — this type supplies the two things that
/// cannot be: the filesystem scan (<see cref="MemoryRootInventory.Scan"/>) and the git probe
/// (<see cref="RepositoryIdentityResolver"/>). The probe lives up here rather than in the engine for
/// the same reason <see cref="WorkspaceHead"/> does: the engine stays git-agnostic.
/// </para>
/// <para>
/// <b>Read-only, with no flag saying so.</b> Nothing on this path opens a file for writing — see
/// <see cref="MemoryAuditReport"/>'s own remarks for why that makes <c>--dry-run</c> noise rather
/// than a safety feature, and <see cref="MemoryAuditOptionsParser.HelpLines"/> for where an operator
/// is told.
/// </para>
/// <para>
/// Not a <see cref="CommandResult"/>/<see cref="FlowStateReporter"/> command, for the same reason
/// <see cref="LedgerViewCommand"/> is not: there is no workflow pump here to report on.
/// </para>
/// </remarks>
public static class MemoryAuditCommand
{
    private static readonly AsyncLocal<Action?> CountObservation = new();
    internal static Action? CountObserver
    {
        get => CountObservation.Value;
        set => CountObservation.Value = value;
    }

    /// <summary>
    /// <c>WhenWritingNull</c>, matching every other Baton JSON view: an absent field is absent, never
    /// <c>null</c> and never <c>0</c>. A root with no resolved checkout simply has no
    /// <c>checkoutPath</c>, which is what "unknown" has to look like to a reader.
    /// <para>
    /// Names come from the camel-case policy rather than a <c>JsonPropertyName</c> per property (which
    /// is how <c>CostLedgerEntry</c> spells its contract): that record is written to a file readers
    /// outside this repo parse, so each name there is pinned individually against a rename. This view
    /// is produced and consumed in one place, and one policy is one thing to keep true instead of
    /// thirty. <c>MemoryAuditCommandTests</c> pins the resulting shape.
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions ViewSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <param name="claudeHomeOverride">
    /// Test seam — production callers always use <see cref="MemoryRootInventory.DefaultClaudeHome"/>.
    /// A vendor's own config directory is deliberately not routed through <c>BatonPaths</c>, so there
    /// is no environment override to point this somewhere else with.
    /// </param>
    /// <param name="userHomeOverride">
    /// Test seam for the third-party vendor roots (#1852 phase A2) — production callers always use
    /// <see cref="MemoryRootInventory.DefaultUserHome"/>. Separate from
    /// <paramref name="claudeHomeOverride"/> because the two populations hang off different roots and
    /// a fixture that moved one would otherwise silently move the other.
    /// </param>
    /// <param name="batonRootOverride">
    /// Test seam for the Baton-managed Codex store, which lives under <c>BatonPaths.Root</c> rather
    /// than under the user profile. <b>A fixture that overrides the user home and not this one still
    /// reads the operator's real <c>~/.baton</c></b> — the two are genuinely independent directories
    /// (<c>BATON_HOME</c> moves one and not the other), so they cannot share a seam; a test wanting a
    /// hermetic scan passes both.
    /// </param>
    public static async Task<int> ExecuteAsync(
        MemoryAuditOptions options,
        TextWriter output,
        string? claudeHomeOverride = null,
        CancellationToken cancellationToken = default,
        string? userHomeOverride = null,
        string? batonRootOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        if (options.Help)
        {
            output.WriteLine(MemoryAuditOptionsParser.Usage);
            foreach (var line in MemoryAuditOptionsParser.HelpLines)
            {
                output.WriteLine(line);
            }

            return 0;
        }

        var claudeHome = claudeHomeOverride ?? MemoryRootInventory.DefaultClaudeHome;
        var roots = MemoryRootInventory.Scan(claudeHome, cancellationToken);

        var resolutions = new List<MemoryRootResolution>(roots.Count);
        foreach (var root in roots)
        {
            resolutions.Add(await ClaudeMemoryRootResolver.ResolveAsync(root, cancellationToken).ConfigureAwait(false));
        }

        var report = MemoryAuditReport.Build(resolutions, MemorySubjectVocabulary.Default);

        // Two bases, because they are two different directories: the vendor homes hang off the user
        // profile, and the Baton-managed Codex store hangs off BatonPaths.Root, which BATON_HOME can
        // point somewhere else entirely.
        var userHome = userHomeOverride ?? MemoryRootInventory.DefaultUserHome;
        var batonRoot = batonRootOverride ?? BatonPaths.Root;
        var vendorRoots = MemoryRootInventory.ScanVendorRoots(
            userHome, batonRoot, limits: null, cancellationToken);
        var retractions = await ReadRetractionsAsync(batonRoot, cancellationToken).ConfigureAwait(false);
        var countGeneration = MemoryCanonicalGeneration.Capture(batonRoot);
        var canonicalStores = await ScanCanonicalStoresAsync(batonRoot, options.Repository, cancellationToken)
            .ConfigureAwait(false);
        var health = MemoryImportOperationHealth.Sample(batonRoot);
        var operationProblems = health.Problems;
        canonicalStores = canonicalStores.Select(store => health.Generation != countGeneration
            || operationProblems.Any(problem => problem.Blocks(store.Slug))
            ? store with { EntryCount = null }
            : store).ToList();

        if (options.Format == MemoryAuditOutputFormat.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(
                new MemoryAuditJsonView(
                    claudeHome, userHome, report.Roots, report.Findings, report.Counts, vendorRoots, retractions,
                    canonicalStores, operationProblems),
                ViewSerializerOptions));
            return 0;
        }

        WriteText(output, claudeHome, report);
        WriteVendorRoots(output, vendorRoots);
        WriteRetractions(output, retractions);
        WriteCanonicalStores(output, batonRoot, options.Repository, canonicalStores);
        foreach (var problem in operationProblems)
        {
            output.WriteLine($"  IMPORT {problem.State.ToUpperInvariant()} -- {problem.Detail}");
        }
        return 0;
    }

    /// <summary>
    /// Every retraction in every canonical store under <paramref name="batonRoot"/> (#2113), oldest
    /// first within a store and stores in slug order — so an operator can see what was declared no
    /// longer true, by whom and why, without opening the files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The third population this verb reports, and the first that is Baton's own store.</b> The
    /// Claude roots and the non-Claude vendor roots are what <c>audit</c> was built to inventory; a
    /// retraction is not in any vendor root — it exists only in the canonical store, and the verb an
    /// operator has for "what does the store say" is this one. Still read-only by construction: the
    /// ledger read creates nothing, and a repository directory with no <c>retractions.jsonl</c> simply
    /// contributes no rows.
    /// </para>
    /// <para>
    /// The enumeration is over <c>{batonRoot}/&lt;slug&gt;/memory/retractions.jsonl</c> — the
    /// directories on disk ARE the list of repositories, for the reason <c>MemorySyncCommand</c>
    /// gives for its own enumeration. Built from <paramref name="batonRoot"/> rather than from
    /// <see cref="BatonPaths"/>' root-bound helpers so the test seam that keeps this verb off the
    /// operator's real <c>~/.baton</c> covers this read too.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<MemoryRetraction>> ReadRetractionsAsync(
        string batonRoot, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(batonRoot))
        {
            return [];
        }

        var rows = new List<MemoryRetraction>();
        foreach (var directory in Directory.EnumerateDirectories(batonRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.Combine(directory, BatonPaths.MemoryDirectoryName, BatonPaths.MemoryRetractionsFileName);
            if (!File.Exists(file))
            {
                continue;
            }

            rows.AddRange(await MemoryStore.ReadRetractionsAsync(file, cancellationToken).ConfigureAwait(false));
        }

        return rows;
    }

    /// <summary>
    /// Baton's own canonical stores under <paramref name="batonRoot"/> (#2112): the fleet store and
    /// every repository's, each with its entry count — or just the one <paramref name="repository"/>
    /// selects.
    /// </summary>
    /// <remarks>
    /// <b>Rows are counted and never printed</b>, which keeps this half inside the verb's own
    /// read-only-and-content-blind claim: an entry's text goes nowhere. Durable metadata is the
    /// authoritative subject; a row supplies it only for a genuinely metadata-less legacy store.
    /// A selected store that does not exist is reported as absent rather than dropped, because "no
    /// fleet store yet" is an answer an operator acts on and an empty list is not.
    /// </remarks>
    private static async Task<IReadOnlyList<CanonicalStoreRow>> ScanCanonicalStoresAsync(
        string batonRoot, string? repository, CancellationToken cancellationToken)
    {
        var selectedSlug = repository is { Length: > 0 } ? FleetMemory.SlugFor(repository) : null;
        var rows = new List<CanonicalStoreRow>();
        foreach (var store in CanonicalStoreInventory.Scan(batonRoot))
        {
            if (selectedSlug is not null && !string.Equals(store.Slug, selectedSlug, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CountObserver?.Invoke();
            IReadOnlyList<MemoryEntry> entries;
            try
            {
                entries = await MemoryStore.ReadAllStrictAsync(store.EntriesFile, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
            {
                rows.Add(new CanonicalStoreRow(store.Slug, store.Repository, store.IsFleet,
                    store.EntriesFile, Present: true, EntryCount: null,
                    CountError: $"{ex.GetType().Name}: {ex.Message}"));
                continue;
            }
            var storeRepository = MemoryStoreIdentity.Resolve(store.Slug, store.Repository, entries);
            rows.Add(new CanonicalStoreRow(
                store.Slug,
                storeRepository,
                store.IsFleet,
                store.EntriesFile,
                Present: true,
                store.OperationProblems is { Count: > 0 } ? null : entries.Count));
        }

        if (selectedSlug is not null && rows.Count == 0)
        {
            rows.Add(new CanonicalStoreRow(
                selectedSlug,
                repository,
                FleetMemory.IsFleet(repository),
                Path.Combine(batonRoot, selectedSlug, BatonPaths.MemoryDirectoryName, BatonPaths.MemoryEntriesFileName),
                Present: false,
                EntryCount: 0));
        }

        return rows;
    }

    /// <summary>
    /// One canonical store in the report. <paramref name="Repository"/> is its durable metadata
    /// identity, or the subject its rows carry for a genuinely metadata-less legacy store.
    /// </summary>
    private sealed record CanonicalStoreRow(
        string Slug,
        string? Repository,
        bool IsFleet,
        string EntriesFile,
        bool Present,
        int? EntryCount,
        string? CountError = null);

    /// <summary>
    /// The JSON contract: the report plus the two roots it was taken over, so a stored report says
    /// which machine's homes produced it rather than leaving that to the reader's assumption.
    /// </summary>
    /// <remarks>
    /// <c>vendorRoots</c> is an additive array (#1852 phase A2) and is deliberately NOT merged into
    /// <c>roots</c>, nor counted in <c>counts</c>: those describe repository-keyed Claude roots and
    /// every finding kind in them is a statement about a repository mapping that a per-machine vendor
    /// root has no basis for. <see cref="MemoryRootInventory.ScanVendorRoots"/>'s remarks carry why.
    /// <c>retractions</c> (#2113) is a third additive array, over the canonical store rather than any
    /// vendor root, and is likewise neither a finding nor counted — a retraction is an operator's
    /// recorded decision, not something left open for the import to settle.
    /// </remarks>
    private sealed record MemoryAuditJsonView(
        string ClaudeHome,
        string UserHome,
        IReadOnlyList<MemoryRootRow> Roots,
        IReadOnlyList<MemoryFinding> Findings,
        MemoryAuditCounts Counts,
        IReadOnlyList<VendorMemoryRoot> VendorRoots,
        IReadOnlyList<MemoryRetraction> Retractions,
        IReadOnlyList<CanonicalStoreRow> CanonicalStores,
        IReadOnlyList<MemoryImportOperationProblem> ImportOperations);

    /// <summary>
    /// The retractions, under their own heading, each with the reason and author verbatim — the
    /// report the retract verb's help promises. Printed even when empty, so "none" is a statement
    /// rather than a heading a reader has to notice is missing.
    /// </summary>
    private static void WriteRetractions(TextWriter output, IReadOnlyList<MemoryRetraction> retractions)
    {
        output.WriteLine();
        output.WriteLine(
            "Retractions in the canonical store (#2113) -- entries declared no longer true by 'baton " +
            "memory retract'. Each entry's own row is still in its store; history is never deleted.");

        if (retractions.Count == 0)
        {
            output.WriteLine("  none.");
            return;
        }

        foreach (var retraction in retractions)
        {
            output.WriteLine();
            output.WriteLine($"  {retraction.EntryId}  ({retraction.Repository})");
            output.WriteLine(
                $"    retracted by {retraction.RetractedBy} at " +
                $"{retraction.RetractedAtUtc.ToString("O", CultureInfo.InvariantCulture)}");
            output.WriteLine($"    reason: {retraction.Reason}");
        }
    }

    private static void WriteText(TextWriter output, string claudeHome, MemoryAuditReport report)
    {
        output.WriteLine(
            "baton memory audit -- READ-ONLY. Nothing was written, moved or deleted, and no memory " +
            "file's content was read into this report.");
        output.WriteLine($"Claude home: {claudeHome}");
        output.WriteLine();

        output.WriteLine(
            $"Roots: {report.Counts.Roots}   Files: {report.Counts.Files}   Bytes: " +
            report.Counts.Bytes.ToString("N0", CultureInfo.InvariantCulture));

        foreach (var row in report.Roots)
        {
            output.WriteLine();
            output.WriteLine($"  {row.Root}");
            output.WriteLine(
                $"    kind={MemoryJsonNames.Of(row.Kind)}" +
                (row.ArchiveLabel is { Length: > 0 } label ? $" archive={label}" : string.Empty) +
                $" files={row.FileCount} bytes={row.TotalBytes.ToString("N0", CultureInfo.InvariantCulture)}" +
                (row.NewestModifiedUtc is { } newest ? $" newest={newest:O}" : " newest=(empty)"));
            output.WriteLine(
                $"    checkout={row.CheckoutPath ?? "(unresolved)"} ({MemoryJsonNames.Of(row.PathSource)}, " +
                $"{(row.CheckoutExists ? "present" : "absent")})");
            output.WriteLine($"    repository={row.Repository ?? "(unknown)"}");
        }

        output.WriteLine();
        if (report.Findings.Count == 0)
        {
            output.WriteLine("Findings: none.");
            return;
        }

        output.WriteLine($"Findings: {report.Findings.Count}");
        foreach (var finding in report.Findings)
        {
            output.WriteLine();
            output.WriteLine($"  [{MemoryJsonNames.Of(finding.Kind)}] {finding.Reason}");
            foreach (var path in finding.Paths)
            {
                output.WriteLine($"    {path}");
            }

            if (finding.Candidates is { Count: > 0 } candidates)
            {
                output.WriteLine($"    candidates: {string.Join("  |  ", candidates)}");
            }
        }
    }

    /// <summary>
    /// The non-Claude roots, printed under their own heading and with no findings attached — this
    /// half of the report is an inventory only. Each presence prints differently
    /// (<see cref="VendorMemoryPresence"/> says why), and a family whose files were counted rather
    /// than digested says so on its own line rather than leaving a reader to read an empty file list
    /// as an empty directory.
    /// </summary>
    /// <remarks>
    /// A capped or unreadable row prints <b>no</b> file count, byte total or newest mtime, and says
    /// which of the two it is instead. Printing a partial count next to the word <c>files=</c> is
    /// what a reader has no way to tell from a complete one — the defect this half of the fix is for.
    /// </remarks>
    private static void WriteVendorRoots(TextWriter output, IReadOnlyList<VendorMemoryRoot> roots)
    {
        output.WriteLine();
        output.WriteLine(
            "Non-Claude memory roots (#1852 phase A2) -- INVENTORY ONLY. No finding is attached to " +
            "these: they are per-machine, so they map to no repository and the finding kinds above " +
            "would say nothing about them. Path, size, mtime and SHA-256 only, as above.");

        foreach (var root in roots)
        {
            output.WriteLine();
            output.WriteLine($"  {root.DirectoryPath}");
            output.WriteLine(
                $"    family={root.Family} vendor={root.SourceVendor} " +
                $"scope={MemoryJsonNames.Of(root.SourceScope)} " +
                $"presence={MemoryJsonNames.Of(root.Presence)}");
            if (root.FileCount is not { } fileCount || root.TotalBytes is not { } totalBytes)
            {
                output.WriteLine(DescribeUncountedRow(root));
                continue;
            }

            output.WriteLine(
                $"    files={fileCount} bytes={totalBytes.ToString("N0", CultureInfo.InvariantCulture)}" +
                (root.NewestModifiedUtc is { } newest ? $" newest={newest:O}" : " newest=(none)") +
                (root.Inventoried ? string.Empty : "  [counted only -- no file here was opened]"));
        }
    }

    /// <summary>
    /// Baton's own stores, under their own heading (#2112): the fleet store first, then one per
    /// repository, counts only. A selected store that is absent says so on its own line.
    /// </summary>
    private static void WriteCanonicalStores(
        TextWriter output, string batonRoot, string? repository, IReadOnlyList<CanonicalStoreRow> stores)
    {
        output.WriteLine();
        output.WriteLine(
            $"Canonical stores under {batonRoot} (#2112) -- Baton's own, COUNTS ONLY. The '{FleetMemory.Slug}' " +
            "store holds operator and machine facts that belong to no repository and is merged into every " +
            "repository's projection; each other store is one repository's. No row's text was read into this report.");

        if (stores.Count == 0)
        {
            output.WriteLine();
            output.WriteLine(
                repository is { Length: > 0 }
                    ? $"  (no canonical store for '{repository}')"
                    : "  (no canonical store yet -- 'baton memory add' or 'baton memory import' creates one)");
            return;
        }

        foreach (var store in stores)
        {
            output.WriteLine();
            output.WriteLine($"  {store.EntriesFile}");
            output.WriteLine(
                store.Present
                    ? $"    {(store.IsFleet ? "FLEET" : "repository=" + (store.Repository ?? "(no rows yet)"))} " +
                      $"slug={store.Slug} " + (store.EntryCount is { } count
                          ? $"entries={count.ToString("N0", CultureInfo.InvariantCulture)}"
                          : store.CountError is { } error
                              ? $"entries=(unavailable -- {error})"
                              : "entries=(withheld -- incomplete import ownership; see import diagnostics)")
                    : $"    ABSENT -- no store has been created for '{store.Repository}' on this machine; " +
                      "'baton memory add' or 'baton memory import' creates it.");
        }
    }

    /// <summary>
    /// The <c>files=</c> line for a row that carries no count, naming WHICH bound stopped the walk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A capped walk has two independent causes (<see cref="VendorRootWalkLimits"/>), and printing the
    /// entry ceiling for both is what tells an operator whose <c>brain</c> directory sits behind a
    /// filter driver that the tree holds fifty thousand entries and the ceiling is what to raise. It
    /// held nine hundred and the clock is what to raise. Both branches state the number as the LIMIT,
    /// because that is all either of them is.
    /// </para>
    /// <para>
    /// Extracted from <see cref="WriteVendorRoots"/> so both capped branches and the unreadable one
    /// can be asserted directly: the command's own seam takes no walk limits, so a budget-stopped walk
    /// is not reachable end-to-end from a test.
    /// </para>
    /// </remarks>
    internal static string DescribeUncountedRow(VendorMemoryRoot root)
    {
        if (root.Presence != VendorMemoryPresence.Capped)
        {
            return "    files=(not counted -- this directory could not be read, which is not the " +
                   "same as it holding nothing)";
        }

        if (root.CappedAfter is { } budget)
        {
            return $"    files=(not counted -- the walk ran out of its " +
                   $"{budget.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s time " +
                   "budget and was abandoned there; that is the LIMIT, and it says nothing about how " +
                   "many files this directory holds)";
        }

        return $"    files=(not counted -- the walk was capped at {root.CappedAtEntries} entries and " +
               "abandoned there; that is the LIMIT, not a count of this directory)";
    }
}
