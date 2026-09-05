using Baton.Memory;

namespace Baton.Tests.Memory;

/// <summary>
/// #1852 phase A: one arm per finding kind, each paired with the negative that would fire on a
/// detector that could not tell the two apart. The report builder is pure, so every fixture here is a
/// record literal — no filesystem, no git, no clock.
/// </summary>
public sealed class MemoryAuditReportTests
{
    private static readonly DateTime Sep5 = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    private const string Baton = "github.com/philipreese/baton";
    private const string Basis = "github.com/philipreese/basis";

    private static MemoryFile File(string relativePath, string digest, long size = 10) =>
        new(Path.Combine(@"C:\root", relativePath), relativePath, size, Sep5, digest);

    private static MemoryRootResolution Live(
        string directoryName,
        string? repository,
        params MemoryFile[] files) =>
        Resolution(directoryName, MemoryRootKind.Live, repository, checkoutExists: true, files);

    private static MemoryRootResolution Resolution(
        string directoryName,
        MemoryRootKind kind,
        string? repository,
        bool checkoutExists,
        MemoryFile[] files,
        MemoryPathSource source = MemoryPathSource.SessionCwd,
        string? checkoutPath = null,
        IReadOnlyList<string>? candidates = null)
    {
        var path = checkoutPath ?? $@"C:\checkouts\{directoryName}";
        var root = new MemoryRoot(
            $@"C:\home\{directoryName}",
            directoryName,
            kind,
            kind == MemoryRootKind.Archive ? "2026-09-03" : null,
            kind == MemoryRootKind.Live ? $@"C:\home\{directoryName}\.." : null,
            files);

        return new MemoryRootResolution(
            root,
            new MemoryRootPathResolution(
                source is MemoryPathSource.Ambiguous or MemoryPathSource.Unresolvable ? null : path,
                source,
                candidates ?? [path]),
            checkoutExists,
            repository);
    }

    private static MemoryAuditReport Build(params MemoryRootResolution[] resolutions) =>
        MemoryAuditReport.Build(resolutions, MemorySubjectVocabulary.Default);

    [Fact]
    public void One_digest_in_two_roots_is_a_duplicate_and_the_same_digest_twice_in_one_root_is_not()
    {
        var report = Build(
            Live("C--a", Baton, File("MEMORY.md", "aaaa"), File("copy.md", "aaaa")),
            Live("C--b", Basis, File("MEMORY.md", "aaaa")),
            Live("C--c", "github.com/philipreese/other", File("MEMORY.md", "bbbb")));

        var duplicates = report.Findings.Where(f => f.Kind == MemoryFindingKind.Duplicate).ToList();

        // "aaaa" spans three files but only two roots -> one finding. "bbbb" is in one root -> none.
        var duplicate = Assert.Single(duplicates);
        Assert.Equal(3, duplicate.Paths.Count);
        Assert.DoesNotContain("bbbb", duplicate.Reason, StringComparison.Ordinal);

        // The negative arm: strip the second root and the SAME within-root repeat stops being a
        // finding. Without this, "duplicate" could be firing on any repeated digest at all.
        var single = Build(Live("C--a", Baton, File("MEMORY.md", "aaaa"), File("copy.md", "aaaa")));
        Assert.DoesNotContain(single.Findings, f => f.Kind == MemoryFindingKind.Duplicate);
    }

    [Fact]
    public void A_known_checkout_that_is_gone_is_an_orphan_and_one_that_is_present_is_not()
    {
        var gone = Build(Resolution(
            "C--Users-pbree-source-repos-aer-aer-flow", MemoryRootKind.Live, repository: null,
            checkoutExists: false, files: [File("MEMORY.md", "aaaa")]));

        var orphan = Assert.Single(gone.Findings);
        Assert.Equal(MemoryFindingKind.Orphan, orphan.Kind);

        // Control: identical root, checkout present and probing to an identity -> no finding at all.
        Assert.Empty(Build(Live("C--Users-pbree-source-repos-aer-aer-flow", Baton, File("MEMORY.md", "aaaa"))).Findings);
    }

    [Fact]
    public void A_present_checkout_with_no_repository_identity_is_no_provenance()
    {
        var report = Build(Resolution(
            "C--Users-pbree--baton", MemoryRootKind.Live, repository: null,
            checkoutExists: true, files: [File("MEMORY.md", "aaaa")]));

        var finding = Assert.Single(report.Findings);
        Assert.Equal(MemoryFindingKind.NoProvenance, finding.Kind);

        // Control: the same root with an identity produces nothing, so this is keyed on the identity
        // and not merely on the directory name.
        Assert.Empty(Build(Live("C--Users-pbree--baton", Baton, File("MEMORY.md", "aaaa"))).Findings);
    }

    [Fact]
    public void An_archived_root_beside_a_live_root_for_the_same_repository_is_stale()
    {
        var report = Build(
            Resolution("archived-baton", MemoryRootKind.Archive, Baton, checkoutExists: true, [File("MEMORY.md", "aaaa")]),
            Live("C--live-baton", Baton, File("MEMORY.md", "bbbb")));

        var stale = Assert.Single(report.Findings, f => f.Kind == MemoryFindingKind.Stale);
        Assert.Equal(2, stale.Paths.Count);
        Assert.Equal(@"C:\home\archived-baton", stale.Paths[0]);

        // The negative that matters, and the reason this is per-root rather than per-filename: both
        // roots carry a MEMORY.md, so a name-collision rule would fire here on two UNRELATED
        // repositories. Same fixture, different repository, no stale row.
        var unrelated = Build(
            Resolution("archived-basis", MemoryRootKind.Archive, Basis, checkoutExists: true, [File("MEMORY.md", "aaaa")]),
            Live("C--live-baton", Baton, File("MEMORY.md", "bbbb")));
        Assert.DoesNotContain(unrelated.Findings, f => f.Kind == MemoryFindingKind.Stale);
    }

    /// <summary>
    /// The <c>alpaca-agent-bot</c> case as ratified: origin says one repository, the filenames name
    /// another, and phase A reports BOTH rather than choosing. The operator's 2026-09-05 ruling keys
    /// such an entry to its subject at import time; that is a write over the entries' text, which this
    /// read-only verb neither has nor performs.
    /// </summary>
    [Fact]
    public void A_root_whose_filenames_name_another_repository_is_ambiguous_with_both_candidates()
    {
        var report = Build(Live(
            "C--Users-pbree-source-repos-alpaca-agent-bot",
            Basis,
            File("MEMORY.md", "aaaa"),
            File("project_baton_direction.md", "bbbb")));

        var finding = Assert.Single(report.Findings);
        Assert.Equal(MemoryFindingKind.Ambiguous, finding.Kind);
        Assert.Equal([Basis, Baton], finding.Candidates);
    }

    /// <summary>
    /// The control arm the subject rule is worthless without: a root holding only the <c>MEMORY.md</c>
    /// every root carries must not be ambiguous against anything. A rule that fires here fires on
    /// every root on the machine.
    /// </summary>
    [Fact]
    public void A_root_holding_only_a_generic_index_is_not_ambiguous()
    {
        var report = Build(Live("C--Users-pbree-source-repos-alpaca-agent-bot", Basis, File("MEMORY.md", "aaaa")));

        Assert.Empty(report.Findings);
    }

    [Fact]
    public void A_repository_that_matches_its_own_filenames_is_not_ambiguous()
    {
        var report = Build(Live("C--Users-pbree-source-repos-baton", Baton, File("project_baton_direction.md", "aaaa")));

        Assert.Empty(report.Findings);
    }

    /// <summary>Whole tokens only — <c>batontown</c> is not Baton.</summary>
    [Fact]
    public void A_substring_of_a_known_repository_name_does_not_name_it()
    {
        var report = Build(Live("C--x", Basis, File("project_batontown_notes.md", "aaaa")));

        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Several_candidate_paths_with_no_ground_truth_is_ambiguous_and_takes_precedence()
    {
        var report = Build(Resolution(
            "C--Users-pbree-source-repos-aer-aer-flow", MemoryRootKind.Live, repository: null,
            checkoutExists: false, files: [File("MEMORY.md", "aaaa")],
            source: MemoryPathSource.Ambiguous,
            candidates: [@"C:\Users\pbree\source\repos\aer\aer-flow", @"C:\Users\pbree\source\repos\aer-aer-flow"]));

        // One status finding, not three: an unpicked path cannot also be reported as gone or as
        // unprovenanced -- both of those are claims about a path this root does not have.
        var finding = Assert.Single(report.Findings);
        Assert.Equal(MemoryFindingKind.Ambiguous, finding.Kind);
        Assert.Equal(2, finding.Candidates!.Count);
    }

    [Fact]
    public void Rows_and_counts_report_what_was_walked()
    {
        var report = Build(
            Live("C--a", Baton, File("MEMORY.md", "aaaa", size: 7), File("b.md", "bbbb", size: 3)),
            Live("C--b", Basis, File("MEMORY.md", "aaaa", size: 7)));

        Assert.Equal(2, report.Counts.Roots);
        Assert.Equal(3, report.Counts.Files);
        Assert.Equal(17, report.Counts.Bytes);
        Assert.Equal(1, report.Counts.FindingsByKind["duplicate"]);

        var row = Assert.Single(report.Roots, r => r.Root.EndsWith("C--a", StringComparison.Ordinal));
        Assert.Equal(2, row.FileCount);
        Assert.Equal(10, row.TotalBytes);
        Assert.Equal(Sep5, row.NewestModifiedUtc);
        Assert.Equal(Baton, row.Repository);
    }

    [Fact]
    public void An_empty_report_has_no_findings_and_no_counts()
    {
        var report = MemoryAuditReport.Build([], MemorySubjectVocabulary.Default);

        Assert.Empty(report.Roots);
        Assert.Empty(report.Findings);
        Assert.Empty(report.Counts.FindingsByKind);
    }
}
