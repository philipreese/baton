using System.Text.Json.Serialization;

namespace Baton.Memory;

/// <summary>
/// What <c>baton memory audit</c> can find. The set is closed and the kinds are mutually exclusive
/// where they describe the same object — see <see cref="MemoryAuditReport"/> for the partition and
/// the precedence between the three that describe a root's own status.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MemoryFindingKind>))]
public enum MemoryFindingKind
{
    /// <summary>One content digest present in two or more roots.</summary>
    [JsonStringEnumMemberName("duplicate")] Duplicate,

    /// <summary>A root whose checkout path is known and no longer exists.</summary>
    [JsonStringEnumMemberName("orphan")] Orphan,

    /// <summary>An archived root whose repository still has a live root.</summary>
    [JsonStringEnumMemberName("stale")] Stale,

    /// <summary>A root no repository identity could be derived for.</summary>
    [JsonStringEnumMemberName("no-provenance")] NoProvenance,

    /// <summary>A root with more than one candidate checkout path, or more than one candidate repository.</summary>
    [JsonStringEnumMemberName("ambiguous")] Ambiguous,
}

/// <summary>
/// A root as inventoried and mapped: what it holds and which repository it belongs to.
/// </summary>
/// <param name="Root">The root directory's own path.</param>
/// <param name="Kind">Live or archived.</param>
/// <param name="ArchiveLabel">The archive generation, for an archived root.</param>
/// <param name="CheckoutPath">The checkout this root belongs to, when one was resolved.</param>
/// <param name="PathSource">How <paramref name="CheckoutPath"/> was arrived at.</param>
/// <param name="CheckoutExists">Whether that path is present on this machine.</param>
/// <param name="Repository">
/// <c>RepositoryIdentity.Value</c> for the checkout, when it resolved to one. Absent is <b>unknown</b>,
/// never "none" — a checkout that is gone cannot be probed, so its repository is unrecorded rather
/// than absent.
/// </param>
/// <param name="FileCount">Files under the root.</param>
/// <param name="TotalBytes">Their total size.</param>
/// <param name="NewestModifiedUtc">The most recent modification time among them, or null when the root is empty.</param>
public sealed record MemoryRootRow(
    string Root,
    MemoryRootKind Kind,
    string? ArchiveLabel,
    string? CheckoutPath,
    MemoryPathSource PathSource,
    bool CheckoutExists,
    string? Repository,
    int FileCount,
    long TotalBytes,
    DateTime? NewestModifiedUtc);

/// <summary>
/// One finding. <paramref name="Reason"/> is the sentence an operator reads; <paramref name="Paths"/>
/// and <paramref name="Candidates"/> are what they act on. Never any file content, and never a
/// decision — a finding names what is undecided, it does not resolve it.
/// </summary>
/// <param name="Kind">Which finding this is.</param>
/// <param name="Reason">Why it fired, in one sentence.</param>
/// <param name="Paths">The roots or files involved, ordered.</param>
/// <param name="Candidates">
/// The competing readings, for an ambiguous finding: candidate checkout paths, or the two candidate
/// repository identities. Absent for every other kind.
/// </param>
public sealed record MemoryFinding(
    MemoryFindingKind Kind,
    string Reason,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string>? Candidates = null);

/// <summary>Totals, so the report's own size is readable without counting its rows.</summary>
/// <param name="Roots">Roots inventoried.</param>
/// <param name="Files">Files across them.</param>
/// <param name="Bytes">Their total size.</param>
/// <param name="FindingsByKind">One count per finding kind that fired, keyed by the kind's JSON name.</param>
public sealed record MemoryAuditCounts(
    int Roots,
    int Files,
    long Bytes,
    IReadOnlyDictionary<string, int> FindingsByKind);

/// <summary>
/// A root, its resolved checkout, and its repository — the input <see cref="MemoryAuditReport.Build"/>
/// reasons over. Assembled by the CLI, because resolving a repository means running git and the engine
/// is deliberately git-agnostic (<c>RepositoryIdentity</c>'s own remarks state that split).
/// </summary>
/// <param name="Root">The inventoried root.</param>
/// <param name="Path">Its path resolution.</param>
/// <param name="CheckoutExists">Whether the resolved checkout path exists on this machine.</param>
/// <param name="RepositoryValue">
/// <c>RepositoryIdentity.Value</c>, or <see langword="null"/> when the probe produced none — a gone
/// checkout, or a directory that is not a git repository.
/// </param>
public sealed record MemoryRootResolution(
    MemoryRoot Root,
    MemoryRootPathResolution Path,
    bool CheckoutExists,
    string? RepositoryValue);

/// <summary>
/// A pinned table of repository <b>subjects</b> recognisable from a memory file's NAME.
/// </summary>
/// <remarks>
/// <para>
/// #1852's live case: the <c>alpaca-agent-bot</c> checkout's <c>origin</c> is unambiguously one
/// repository while its memory files are named after another (<c>project_baton_direction.md</c>,
/// <c>reference_baton_runbook.md</c>). Origin provenance and subject are different facts, and phase A
/// cannot adjudicate the second — deciding whose memory an entry is requires reading it, which this
/// verb does not do and a vanished checkout would not permit anyway. So it reports both candidates
/// and resolves nothing (the issue's operator ruling of 2026-09-05 keys such an entry at IMPORT time,
/// which is phase B's write, not this read).
/// </para>
/// <para>
/// <b>Pinned, not derived from the population.</b> An earlier shape inferred the vocabulary from
/// whatever other roots happened to be on the machine, which made a root's own report depend on its
/// neighbours — the wrong property for an audit. This table is an explicit constant, so the same root
/// reports the same way on any machine, and the negative is stated rather than left to inference:
/// <b>a repository whose name is not in this table cannot be detected as a subject at all</b>. Growing
/// it is a deliberate edit, not a side effect.
/// </para>
/// </remarks>
/// <param name="IdentityByToken">
/// Lower-case filename token (matched whole, against a name split on <c>-</c>, <c>_</c> and <c>.</c>)
/// to the canonical repository identity it names.
/// </param>
public sealed record MemorySubjectVocabulary(IReadOnlyDictionary<string, string> IdentityByToken)
{
    /// <summary>
    /// The one subject Baton can recognise: its own. Baton's memory is the population #1852 exists to
    /// consolidate, and it is the only repository this tool has any basis for naming.
    /// </summary>
    public static MemorySubjectVocabulary Default { get; } = new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["baton"] = "github.com/philipreese/baton",
        });
}

/// <summary>
/// The whole read-only inventory: every Claude memory root on a machine, the repository each maps to,
/// and every finding about them. #1852 phase A.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only by construction, so there is no <c>--dry-run</c>.</b> Nothing in this namespace opens
/// a file for writing, moves one, or deletes one; the only bytes read out of a memory file are read
/// into a digest and discarded. A flag that can only ever be on is noise, and offering one would
/// imply an off position that does not exist.
/// </para>
/// <para>
/// <b>The partition.</b> Three kinds describe a root's own status and are mutually exclusive, in this
/// precedence — the first that applies is the one reported, because a root with two candidate paths
/// cannot also be meaningfully said to have a gone path or a missing identity:
/// </para>
/// <list type="number">
/// <item><see cref="MemoryFindingKind.Ambiguous"/> — more than one candidate checkout path with no
/// ground truth picking one, or an origin-derived repository that the root's own filenames name a
/// different subject than.</item>
/// <item><see cref="MemoryFindingKind.Orphan"/> — the checkout path is known and gone.</item>
/// <item><see cref="MemoryFindingKind.NoProvenance"/> — no repository identity could be derived: the
/// path exists but is not a git repository, or no path could be decoded at all.</item>
/// </list>
/// <para>
/// Two further kinds describe relationships between roots and are independent of the three above:
/// <see cref="MemoryFindingKind.Duplicate"/> (one digest in two or more roots) and
/// <see cref="MemoryFindingKind.Stale"/> (an archived root whose repository still has a live root, so
/// the archived copies are candidates for supersession). <b>Neither is a ruling.</b> Stale in
/// particular is per-root rather than per-file: deciding which specific archived entry a live entry
/// supersedes is phase B's import, which has the entries' text; a name-collision heuristic here would
/// fire on the <c>MEMORY.md</c> every root carries and report noise as a finding.
/// </para>
/// </remarks>
public sealed record MemoryAuditReport(
    IReadOnlyList<MemoryRootRow> Roots,
    IReadOnlyList<MemoryFinding> Findings,
    MemoryAuditCounts Counts)
{
    /// <summary>
    /// Builds the report from already-resolved roots. Pure: no filesystem, no git, no clock — the same
    /// input produces the same report, and the ordering below is total, so two runs over an unchanged
    /// machine differ only where the machine did.
    /// </summary>
    public static MemoryAuditReport Build(
        IReadOnlyList<MemoryRootResolution> resolutions, MemorySubjectVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(resolutions);
        ArgumentNullException.ThrowIfNull(vocabulary);

        var rows = new List<MemoryRootRow>();
        var findings = new List<MemoryFinding>();

        foreach (var resolution in resolutions.OrderBy(r => r.Root.DirectoryPath, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(BuildRow(resolution));
            if (BuildRootStatusFinding(resolution, vocabulary) is { } finding)
            {
                findings.Add(finding);
            }
        }

        findings.AddRange(BuildStaleFindings(resolutions));
        findings.AddRange(BuildDuplicateFindings(resolutions));

        var ordered = findings
            .OrderBy(f => f.Kind)
            .ThenBy(f => f.Paths.Count > 0 ? f.Paths[0] : string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var counts = new MemoryAuditCounts(
            rows.Count,
            rows.Sum(r => r.FileCount),
            rows.Sum(r => r.TotalBytes),
            ordered.GroupBy(f => f.Kind)
                .OrderBy(g => g.Key)
                .ToDictionary(g => JsonNameOf(g.Key), g => g.Count()));

        return new MemoryAuditReport(rows, ordered, counts);
    }

    private static MemoryRootRow BuildRow(MemoryRootResolution resolution)
    {
        var files = resolution.Root.Files;
        return new MemoryRootRow(
            resolution.Root.DirectoryPath,
            resolution.Root.Kind,
            resolution.Root.ArchiveLabel,
            resolution.Path.CheckoutPath,
            resolution.Path.Source,
            resolution.CheckoutExists,
            resolution.RepositoryValue,
            files.Count,
            files.Sum(f => f.SizeBytes),
            files.Count == 0 ? null : files.Max(f => f.ModifiedUtc));
    }

    /// <summary>The one status finding a root gets, in the precedence the type remarks state, or none.</summary>
    private static MemoryFinding? BuildRootStatusFinding(
        MemoryRootResolution resolution, MemorySubjectVocabulary vocabulary)
    {
        var root = resolution.Root.DirectoryPath;

        if (resolution.Path.Source == MemoryPathSource.Ambiguous)
        {
            return new MemoryFinding(
                MemoryFindingKind.Ambiguous,
                $"'{resolution.Root.DirectoryName}' decodes to more than one checkout path and nothing here " +
                "picks between them, so no repository was assigned.",
                [root],
                resolution.Path.Candidates);
        }

        if (FindSubjectAmbiguity(resolution, vocabulary) is { } subjectFinding)
        {
            return subjectFinding;
        }

        if (resolution.Path.CheckoutPath is { Length: > 0 } checkoutPath && !resolution.CheckoutExists)
        {
            return new MemoryFinding(
                MemoryFindingKind.Orphan,
                $"The checkout this memory belongs to is gone: '{checkoutPath}' does not exist on this machine.",
                [root]);
        }

        if (resolution.RepositoryValue is null)
        {
            return new MemoryFinding(
                MemoryFindingKind.NoProvenance,
                resolution.Path.CheckoutPath is { Length: > 0 } path
                    ? $"'{path}' yields no repository identity, so this memory cannot be filed under one."
                    : $"'{resolution.Root.DirectoryName}' decodes to no usable checkout path, so this memory " +
                      "cannot be filed under a repository.",
                [root]);
        }

        return null;
    }

    /// <summary>
    /// The <c>alpaca-agent-bot</c> shape: a root whose origin-derived repository is one thing and whose
    /// filenames name another. Both candidates are reported and neither is chosen — see
    /// <see cref="MemorySubjectVocabulary"/> for why the vocabulary is pinned and what it cannot see.
    /// </summary>
    private static MemoryFinding? FindSubjectAmbiguity(
        MemoryRootResolution resolution, MemorySubjectVocabulary vocabulary)
    {
        if (resolution.RepositoryValue is not { Length: > 0 } derived)
        {
            return null;
        }

        foreach (var file in resolution.Root.Files)
        {
            foreach (var token in SubjectTokens(file.RelativePath))
            {
                if (!vocabulary.IdentityByToken.TryGetValue(token, out var named) ||
                    string.Equals(named, derived, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return new MemoryFinding(
                    MemoryFindingKind.Ambiguous,
                    $"This root's checkout resolves to '{derived}', but it holds file(s) named after " +
                    $"'{named}' (e.g. '{file.RelativePath}'). Whose memory these are is not decidable from " +
                    "names, paths and hashes alone, so both candidates are reported and neither is chosen.",
                    [resolution.Root.DirectoryPath],
                    [derived, named]);
            }
        }

        return null;
    }

    /// <summary>
    /// The whole tokens of a memory file's name — split on the three separators the observed
    /// kind-prefixed filenames use (<c>feedback_dispatch_and_vendor_rules.md</c>,
    /// <c>project-baton-direction.md</c>). Whole tokens only: a substring match would make
    /// <c>batontown</c> name Baton.
    /// </summary>
    private static IEnumerable<string> SubjectTokens(string relativePath) =>
        relativePath.Split(['-', '_', '.', '/', ' '], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// One row per archived root whose repository still has a live root — the archived copies are
    /// candidates for supersession, which is phase B's decision and not this verb's.
    /// </summary>
    private static IEnumerable<MemoryFinding> BuildStaleFindings(IReadOnlyList<MemoryRootResolution> resolutions)
    {
        var liveByRepository = resolutions
            .Where(r => r.Root.Kind == MemoryRootKind.Live && r.RepositoryValue is { Length: > 0 })
            .GroupBy(r => r.RepositoryValue!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Root.DirectoryPath).ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var archived in resolutions
                     .Where(r => r.Root.Kind == MemoryRootKind.Archive && r.RepositoryValue is { Length: > 0 })
                     .OrderBy(r => r.Root.DirectoryPath, StringComparer.OrdinalIgnoreCase))
        {
            if (!liveByRepository.TryGetValue(archived.RepositoryValue!, out var live))
            {
                continue;
            }

            yield return new MemoryFinding(
                MemoryFindingKind.Stale,
                $"An archived root for '{archived.RepositoryValue}' sits beside a live root for the same " +
                "repository, so its entries are candidates for supersession. Which entry supersedes which " +
                "needs the entries themselves and is not decided here.",
                [archived.Root.DirectoryPath, .. live.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)]);
        }
    }

    /// <summary>One row per content digest that appears in two or more DISTINCT roots.</summary>
    /// <remarks>
    /// Two copies inside one root are not reported: that is one root's own shape, not the cross-root
    /// duplication #1852 is consolidating, and reporting it would bury the finding that matters under
    /// the finding that does not.
    /// </remarks>
    private static IEnumerable<MemoryFinding> BuildDuplicateFindings(IReadOnlyList<MemoryRootResolution> resolutions)
    {
        var byDigest = new Dictionary<string, List<(string Root, string Path)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var resolution in resolutions)
        {
            foreach (var file in resolution.Root.Files)
            {
                if (!byDigest.TryGetValue(file.Sha256, out var list))
                {
                    list = [];
                    byDigest[file.Sha256] = list;
                }

                list.Add((resolution.Root.DirectoryPath, file.Path));
            }
        }

        foreach (var (digest, occurrences) in byDigest.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var distinctRoots = occurrences.Select(o => o.Root).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (distinctRoots < 2)
            {
                continue;
            }

            yield return new MemoryFinding(
                MemoryFindingKind.Duplicate,
                $"One identical file ({(digest.Length > 12 ? digest[..12] + "…" : digest)}) is present in " +
                $"{distinctRoots} roots.",
                occurrences.Select(o => o.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList());
        }
    }

    /// <summary>A finding kind's JSON spelling — see <see cref="MemoryJsonNames"/>.</summary>
    public static string JsonNameOf(MemoryFindingKind kind) => MemoryJsonNames.Of(kind);
}

/// <summary>
/// The one place an enum in this namespace is spelled for a human or a machine.
/// </summary>
/// <remarks>
/// Read off each member's own <c>JsonStringEnumMemberName</c> attribute — the same attribute
/// <c>System.Text.Json</c> serializes through — rather than a second table beside it. The counts map,
/// the JSON view and the text view all go through here, so none of them can name a kind differently
/// from the others, and a kind added to an enum is spelled correctly everywhere without an edit.
/// </remarks>
public static class MemoryJsonNames
{
    /// <summary>
    /// <paramref name="value"/>'s <c>JsonStringEnumMemberName</c>, falling back to its lower-cased
    /// member name when it carries none.
    /// </summary>
    public static string Of<TEnum>(TEnum value)
        where TEnum : struct, Enum
        => typeof(TEnum).GetField(value.ToString() ?? string.Empty)
               ?.GetCustomAttributes(typeof(JsonStringEnumMemberNameAttribute), inherit: false)
               .OfType<JsonStringEnumMemberNameAttribute>()
               .FirstOrDefault()
               ?.Name
           ?? value.ToString()?.ToLowerInvariant()
           ?? string.Empty;
}
