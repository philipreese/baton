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
            resolutions.Add(await ResolveAsync(root, cancellationToken).ConfigureAwait(false));
        }

        var report = MemoryAuditReport.Build(resolutions, MemorySubjectVocabulary.Default);

        // Two bases, because they are two different directories: the vendor homes hang off the user
        // profile, and the Baton-managed Codex store hangs off BatonPaths.Root, which BATON_HOME can
        // point somewhere else entirely.
        var userHome = userHomeOverride ?? MemoryRootInventory.DefaultUserHome;
        var vendorRoots = MemoryRootInventory.ScanVendorRoots(
            userHome, batonRootOverride ?? BatonPaths.Root, limits: null, cancellationToken);

        if (options.Format == MemoryAuditOutputFormat.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(
                new MemoryAuditJsonView(
                    claudeHome, userHome, report.Roots, report.Findings, report.Counts, vendorRoots),
                ViewSerializerOptions));
            return 0;
        }

        WriteText(output, claudeHome, report);
        WriteVendorRoots(output, vendorRoots);
        return 0;
    }

    /// <summary>
    /// One root's checkout and repository. Session <c>cwd</c> is consulted first and the git probe runs
    /// only against a path that exists — a probe of a vanished directory answers nothing, and running
    /// one anyway would spend a process per gone root to learn that.
    /// </summary>
    /// <remarks>
    /// The decoder's tie-break is handed <see cref="RepositoryIdentityResolver.IsWorkTreeRoot"/> rather
    /// than <see cref="Directory.Exists(string)"/> — <see cref="MemoryRootPath.Resolve"/>'s own comment
    /// states what each weaker predicate got wrong. The part only visible from this site is the
    /// asymmetry: a session <c>cwd</c> is deliberately NOT filtered that way. It is the value the
    /// directory name was derived from, so a session run from inside a checkout belongs to that
    /// checkout; the narrow predicate is for a GUESSED reading, not a recorded one.
    /// </remarks>
    private static async Task<MemoryRootResolution> ResolveAsync(
        MemoryRoot root, CancellationToken cancellationToken)
    {
        var resolution = MemoryRootPath.Resolve(
            root.DirectoryName,
            MemoryRootPath.ReadSessionWorkingDirectories(root.SessionDirectoryPath),
            RepositoryIdentityResolver.IsWorkTreeRoot);

        var checkoutExists = resolution.CheckoutPath is { Length: > 0 } path && Directory.Exists(path);

        var repository = checkoutExists
            ? await RepositoryIdentityResolver
                .TryResolveAsync(resolution.CheckoutPath!, cancellationToken).ConfigureAwait(false)
            : null;

        return new MemoryRootResolution(root, resolution, checkoutExists, repository?.Value);
    }

    /// <summary>
    /// The JSON contract: the report plus the two roots it was taken over, so a stored report says
    /// which machine's homes produced it rather than leaving that to the reader's assumption.
    /// </summary>
    /// <remarks>
    /// <c>vendorRoots</c> is an additive array (#1852 phase A2) and is deliberately NOT merged into
    /// <c>roots</c>, nor counted in <c>counts</c>: those describe repository-keyed Claude roots and
    /// every finding kind in them is a statement about a repository mapping that a per-machine vendor
    /// root has no basis for. <see cref="MemoryRootInventory.ScanVendorRoots"/>'s remarks carry why.
    /// </remarks>
    private sealed record MemoryAuditJsonView(
        string ClaudeHome,
        string UserHome,
        IReadOnlyList<MemoryRootRow> Roots,
        IReadOnlyList<MemoryFinding> Findings,
        MemoryAuditCounts Counts,
        IReadOnlyList<VendorMemoryRoot> VendorRoots);

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
