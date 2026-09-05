using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Memory;

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
    public static async Task<int> ExecuteAsync(
        MemoryAuditOptions options,
        TextWriter output,
        string? claudeHomeOverride = null,
        CancellationToken cancellationToken = default)
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
        var roots = MemoryRootInventory.Scan(claudeHome);

        var resolutions = new List<MemoryRootResolution>(roots.Count);
        foreach (var root in roots)
        {
            resolutions.Add(await ResolveAsync(root, cancellationToken).ConfigureAwait(false));
        }

        var report = MemoryAuditReport.Build(resolutions, MemorySubjectVocabulary.Default);

        if (options.Format == MemoryAuditOutputFormat.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(
                new MemoryAuditJsonView(claudeHome, report.Roots, report.Findings, report.Counts),
                ViewSerializerOptions));
            return 0;
        }

        WriteText(output, claudeHome, report);
        return 0;
    }

    /// <summary>
    /// One root's checkout and repository. Session <c>cwd</c> is consulted first and the git probe runs
    /// only against a path that exists — a probe of a vanished directory answers nothing, and running
    /// one anyway would spend a process per gone root to learn that.
    /// </summary>
    private static async Task<MemoryRootResolution> ResolveAsync(
        MemoryRoot root, CancellationToken cancellationToken)
    {
        var resolution = MemoryRootPath.Resolve(
            root.DirectoryName,
            MemoryRootPath.ReadSessionWorkingDirectories(root.SessionDirectoryPath),
            Directory.Exists);

        var checkoutExists = resolution.CheckoutPath is { Length: > 0 } path && Directory.Exists(path);

        var repository = checkoutExists
            ? await RepositoryIdentityResolver
                .TryResolveAsync(resolution.CheckoutPath!, cancellationToken).ConfigureAwait(false)
            : null;

        return new MemoryRootResolution(root, resolution, checkoutExists, repository?.Value);
    }

    /// <summary>
    /// The JSON contract: the report plus the root it was taken over, so a stored report says which
    /// machine's Claude home produced it rather than leaving that to the reader's assumption.
    /// </summary>
    private sealed record MemoryAuditJsonView(
        string ClaudeHome,
        IReadOnlyList<MemoryRootRow> Roots,
        IReadOnlyList<MemoryFinding> Findings,
        MemoryAuditCounts Counts);

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
}
