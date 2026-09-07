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
/// <param name="CommitsAheadOfRemote">
/// #1945: how many commits <c>HEAD</c> carries that its tracking branch does not
/// (<c>git rev-list --count @{upstream}..HEAD</c>), or null when git could not answer — no upstream
/// configured, a detached HEAD, any git failure. Never fabricated as zero: zero is the reading
/// <see cref="FinishedAndPushed"/> treats as "the push landed", so a blind probe must not be able to
/// manufacture it.
/// <para>
/// <b>The tracking ref is not stale for this question</b>, which is the reader's likely prior. The
/// only thing that moves it is a push from this same workspace — the very push the pre-push hook was
/// gating — so no <c>git fetch</c> is needed and none is done: hook passed and the push completed
/// leaves 0, a kill landing before or inside the hook leaves &gt; 0.
/// </para>
/// </param>
public sealed record WorkspaceMutationReading(
    bool Measured,
    int ChangedPathCount,
    int? NewCommitCount,
    bool HasNewCommits,
    int? CommitsAheadOfRemote = null)
{
    /// <summary>The reading for a workspace git could not answer for at all.</summary>
    public static readonly WorkspaceMutationReading Unmeasurable = new(false, 0, null, false);

    /// <summary>An exact reading, both halves counted against a known start ref.</summary>
    public static WorkspaceMutationReading FromCounts(
        int changedPathCount, int newCommitCount, int? commitsAheadOfRemote = null) =>
        new(true, changedPathCount, newCommitCount, newCommitCount > 0, commitsAheadOfRemote);

    /// <summary>
    /// #1945: whether this workspace's work is already committed AND already on the remote — the
    /// signature of a lane that finished inside its box and was killed AFTER its push landed, with
    /// nothing left to push. A kill landing <i>inside</i> this repo's pre-push hook is a kill before
    /// the transfer and reads &gt; 0 here, as <see cref="CommitsAheadOfRemote"/>'s own remark states —
    /// the hook is one thing that runs before the push, never what this predicate detects. Read only
    /// from inside <see cref="Mutated"/>'s arm in
    /// <see cref="Outcomes.OutcomeClassifier"/> — that nesting is what the population this predicate
    /// must never see turns on, and the classifier's own #1945 remark states it.
    /// <para>
    /// Fails closed exactly as <see cref="Mutated"/> does: an unmeasured workspace, or one whose
    /// upstream count git could not produce, is false — a lane is only called finished on positive
    /// evidence that its work left the machine.
    /// </para>
    /// </summary>
    public bool FinishedAndPushed => HeadIsPushed && ChangedPathCount == 0;

    /// <summary>
    /// #1978: whether <c>HEAD</c> is positively known to be on the remote already — the tracking branch
    /// answered, and it answered zero. The commit half of <see cref="FinishedAndPushed"/>, split out
    /// because the timeout summary needs it on its own: a workspace that pushed its head and is still
    /// dirty (or whose declared output never got written) is not finished, but its commits are already
    /// delivered and a conductor must not push them again.
    /// <para>
    /// Fails closed exactly as <see cref="FinishedAndPushed"/> does: unmeasured, or an upstream count git
    /// could not produce, is false.
    /// </para>
    /// </summary>
    public bool HeadIsPushed => Measured && CommitsAheadOfRemote == 0;

    /// <summary>
    /// Whether this workspace holds work no blind retry may run over. True whenever the reading
    /// failed — spec/baton.md §3 (#1373) states why that direction and not the other.
    /// </summary>
    public bool Mutated => !Measured || ChangedPathCount > 0 || HasNewCommits;

    /// <summary>
    /// The bounded phrase the Indeterminate reason names the stakes with. Bounded by construction —
    /// two integers and fixed words, never a path list — so unlike
    /// <see cref="WorktreeProvisioner.DescribeWorkspaceEvidence"/> this needs no caller-side truncation.
    /// <para>
    /// <b>The commit half names what is not on the REMOTE (#1978), not what is not on the base ref.</b>
    /// <see cref="CommitsAheadOfRemote"/> when a tracking branch answered — the number a conductor
    /// actually acts on, since that is what is still stranded on the machine — and only when there is
    /// none does it fall back to <see cref="NewCommitCount"/>'s delta against the probe's start ref,
    /// saying <c>(no upstream)</c> so the two are never read as the same measurement. They are not: a
    /// lane that committed once and pushed reads 0 against its upstream and 1 against <c>main</c>. On
    /// 2026-09-06 a conductor read the second number as the first, hand-pushed a branch that was
    /// already on origin with an open PR, and had to reset the divergent head that produced. The
    /// changed/untracked half is unchanged and is absolute either way.
    /// </para>
    /// </summary>
    public string Describe()
    {
        if (!Measured)
        {
            return "workspace state could not be read, so surviving work cannot be ruled out";
        }

        // The upstream count wins whenever it exists, INCLUDING over an uncounted reflog reading:
        // "how much is not on origin" is the actionable question, and it was answered exactly.
        var commits = CommitsAheadOfRemote is { } ahead
            ? $"{ahead} unpushed commit(s)"
            : $"{DescribeCommitsSinceStartRef()} (no upstream)";

        return $"{commits} and {ChangedPathCount} changed/untracked path(s)";
    }

    /// <summary>
    /// The pre-#1978 commit phrase, now reached only when there is no upstream to measure against —
    /// including the reflog heuristic's uncounted shape, which stays uncounted rather than being
    /// fabricated as a number.
    /// </summary>
    private string DescribeCommitsSinceStartRef() => NewCommitCount is { } count
        ? $"{count} new commit(s)"
        : HasNewCommits ? "new commit(s) (uncounted)" : "0 new commit(s)";
}
