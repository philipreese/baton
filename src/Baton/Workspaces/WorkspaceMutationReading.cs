namespace Baton.Workspaces;

/// <summary>
/// #1373: what a workspace looked like at the moment its execution was killed by the dispatch
/// timeout — the reading <see cref="Outcomes.OutcomeClassifier"/> branches on. The ruling, the probe
/// path, and the safe default all live in spec/baton.md §3's #1373 paragraph, not here.
/// <para>
/// A FOURTH entry point over the same git reads
/// (<see cref="WorktreeProvisioner.IsWorkspaceUntouched"/>,
/// <see cref="WorktreeProvisioner.TryReadWorkspaceChanged"/>,
/// <see cref="WorktreeProvisioner.DescribeWorkspaceEvidence"/>), and deliberately so — #1720 review F2
/// records why one shared entry point cannot serve consumers whose safe defaults differ. It differs
/// from all three siblings in a second way too: it carries the <i>counts</i>, because its consumer's
/// reason text has to tell a conductor how much is at stake, not merely that something is.
/// </para>
/// </summary>
/// <param name="Measured">
/// False when git could not answer at all (not a checkout, git failed, git missing). Everything below
/// is then meaningless and <see cref="Mutated"/> reads true regardless.
/// </param>
/// <param name="ChangedPathCount">
/// Uncommitted and untracked paths, as counted by
/// <see cref="WorktreeProvisioner.ReadWorkspaceMutation"/>. What the count includes, what it excludes
/// and why the git flag is what it is are stated once, on <c>WorktreeProvisioner</c>'s
/// <c>UntrackedFilesArgument</c> and <c>ChangedPathsExcludingEnginePlaced</c> (#1929 review HIGH) —
/// the latter is also where the full list of readers that subtract engine-placed files lives, which
/// since #1929's round-3 MEDIUM includes all three siblings above.
/// </param>
/// <param name="NewCommitCount">
/// Commits since the ref the probe was given, or null when no ref was available and the reflog
/// heuristic answered instead — never fabricated as zero, which is why
/// <paramref name="HasNewCommits"/> is a separate field rather than <c>NewCommitCount > 0</c>.
/// </param>
/// <param name="HasNewCommits">
/// Whether the workspace carries commits the probe's ref does not, however that was established.
/// </param>
public sealed record WorkspaceMutationReading(
    bool Measured,
    int ChangedPathCount,
    int? NewCommitCount,
    bool HasNewCommits)
{
    /// <summary>The reading for a workspace git could not answer for at all.</summary>
    public static readonly WorkspaceMutationReading Unmeasurable = new(false, 0, null, false);

    /// <summary>An exact reading, both halves counted against a known start ref.</summary>
    public static WorkspaceMutationReading FromCounts(int changedPathCount, int newCommitCount) =>
        new(true, changedPathCount, newCommitCount, newCommitCount > 0);

    /// <summary>
    /// Whether this workspace holds work no blind retry may run over. True whenever the reading
    /// failed — spec/baton.md §3 (#1373) states why that direction and not the other.
    /// </summary>
    public bool Mutated => !Measured || ChangedPathCount > 0 || HasNewCommits;

    /// <summary>
    /// The bounded phrase the Indeterminate reason names the stakes with. Bounded by construction —
    /// two integers and fixed words, never a path list — so unlike
    /// <see cref="WorktreeProvisioner.DescribeWorkspaceEvidence"/> this needs no caller-side truncation.
    /// </summary>
    public string Describe()
    {
        if (!Measured)
        {
            return "workspace state could not be read, so surviving work cannot be ruled out";
        }

        var commits = NewCommitCount is { } count
            ? $"{count} new commit(s)"
            : HasNewCommits ? "new commit(s) (uncounted)" : "0 new commit(s)";

        return $"{commits} and {ChangedPathCount} changed/untracked path(s)";
    }
}
