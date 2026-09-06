using System.ComponentModel;
using System.Diagnostics;
using System.Linq;

namespace Baton.Workspaces;

/// <summary>
/// Provisions a git worktree as a worker's workspace and tears it down once the room is Terminal —
/// the engine half of #669, so a reviewer can be dispatched at a branch without a human checking it
/// out anywhere, and without the review and the ongoing work fighting over one tree.
///
/// <para>
/// Vendor-agnostic (Architecture Rule 2): <c>Baton</c> never learns which vendor runs in the tree —
/// git is infrastructure, not an AI vendor, so this belongs beside <c>ArtifactManager</c> in the
/// dispatch layer rather than in <c>Baton.Vendors</c>. <b>Local worktrees only</b> — no clone, no fetch,
/// no network: a worktree of a repository already on disk needs no credential, so Rule 4 (Credential
/// Isolation) is untouched. The moment this grows a clone it acquires a credential problem, which is a
/// different decision (#669).
/// </para>
/// </summary>
public static class WorktreeProvisioner
{
    /// <summary>
    /// The bind-time check, separated so a caller can refuse a bad spec before the pump starts rather
    /// than discovering it at dispatch (#668's class). The repository must be an absolute, fully
    /// qualified path — AER and the worker resolve a relative one against different bases, so the run
    /// would fail its contract after paying in full (#668; <see cref="Path.IsPathFullyQualified(string)"/>,
    /// not <c>IsPathRooted</c>, is the predicate that actually means it, since <c>IsPathRooted("C:x")</c>
    /// is true while the path is still relative to a drive's current directory) — and the ref must be
    /// non-empty.
    /// </summary>
    public static void ValidateSpec(string repository, string reference)
    {
        if (string.IsNullOrWhiteSpace(repository) || !Path.IsPathFullyQualified(repository))
        {
            throw new InvalidWorkspaceSpecException(
                $"A worktree workspace needs an absolute repository path; '{repository}' is not fully " +
                "qualified. A relative path resolves against a different base for AER and the worker, so " +
                "the run would fail its contract after paying in full (#668).");
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new InvalidWorkspaceSpecException(
                "A worktree workspace needs a non-empty git ref (a branch or commit) to check out.");
        }
    }

    /// <summary>
    /// Detects whether <paramref name="directoryPath"/> is a provisioned git worktree by checking
    /// if <c>git rev-parse --git-common-dir</c> differs from <c>--git-dir</c> (#1354). Returns false when
    /// the directory does not exist, git ran and reported the path is not a worktree (a non-git
    /// directory, or a main repository root), or git's output was unreadable.
    /// </summary>
    /// <exception cref="WorktreeProvisioningException">
    /// git itself could not be run (missing from PATH) — a distinct failure from "not a worktree"
    /// (finding 10, #1354/#1380): folding the two together previously reported a missing git the same
    /// way as an ordinary directory, so the caller went on to attempt a provision that would fail again
    /// with a different, less direct message.
    /// </exception>
    public static bool IsWorktree(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return false;
        }

        (int ExitCode, string StdOut, string StdErr) result;
        try
        {
            result = RunGit(directoryPath, "rev-parse", "--git-common-dir", "--git-dir");
        }
        catch (WorktreeProvisioningException ex)
        {
            throw new WorktreeProvisioningException(
                $"Could not determine whether '{directoryPath}' is a worktree: {ex.Message}");
        }

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return false;
        }

        var lines = result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
        {
            return false;
        }

        var commonDir = Path.GetFullPath(Path.Combine(directoryPath, lines[0]));
        var gitDir = Path.GetFullPath(Path.Combine(directoryPath, lines[1]));

        return !string.Equals(
            NormalizeForComparison(commonDir),
            NormalizeForComparison(gitDir),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// N2 (#1664 re-review): resolves <paramref name="reference"/> to a commit SHA against
    /// <paramref name="repository"/> — never the worktree, and never after it exists — so the value
    /// callers carry forward is fixed at provisioning time. A symbolic ref like <c>HEAD</c> read back
    /// out of the *worktree* later is <c>HEAD..HEAD ≡ 0</c>, degenerate and unable to see a worker's
    /// own commit; resolving to a SHA first, here, against the SOURCE repository, is what
    /// <see cref="IsWorkspaceUntouched"/>'s <c>rev-list --count &lt;sha&gt;..HEAD</c> arm needs to mean
    /// anything. Returns null (rather than throwing) on any git failure — a caller loses only the
    /// stronger check and falls back to the reflog heuristic <see cref="IsWorkspaceUntouched"/> already
    /// has for "no base ref available"; <see cref="Provision"/> itself is what surfaces a genuinely bad
    /// ref as a refusal.
    /// </summary>
    public static string? ResolveBaseCommit(string repository, string reference)
    {
        try
        {
            var (exitCode, stdout, _) = RunGit(repository, "rev-parse", "--verify", $"{reference}^{{commit}}");
            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                return null;
            }

            return stdout.Trim();
        }
        catch (WorktreeProvisioningException)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates a git worktree of <paramref name="repository"/> at <paramref name="reference"/> at the
    /// absolute <paramref name="worktreePath"/> — the value the worker's WorkingDirectory then points
    /// at. The caller owns the path so a room with several workers gives each its own tree (one
    /// worktree per worker, never shared). Validates the spec first (<see cref="ValidateSpec"/>); a git
    /// failure (an unknown ref, a ref already checked out elsewhere) throws
    /// <see cref="WorktreeProvisioningException"/>.
    /// </summary>
    public static void Provision(string worktreePath, string repository, string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ValidateSpec(repository, reference);

        var parent = Path.GetDirectoryName(worktreePath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent); // git worktree add needs the leaf's parent to exist
        }

        var (exitCode, _, stderr) = RunGit(repository, "worktree", "add", worktreePath, reference);
        if (exitCode != 0)
        {
            // Serialized against concurrent provisioning by the room's ConcurrencyGuard; this check also
            // handles a leftover worktree from a prior crashed run whose teardown did not complete.
            if (IsRegisteredWorktreeForRef(repository, worktreePath, reference))
            {
                return;
            }

            throw new WorktreeProvisioningException(
                $"Provisioning a worktree of '{reference}' from '{repository}' failed (git worktree add, " +
                $"exit {exitCode}): {stderr.Trim()}");
        }
    }

    /// <summary>
    /// Removes the worktree at <paramref name="worktreePath"/> once the room is Terminal. <b>Never
    /// throws</b> — a teardown fault must not fail a room that has already completed. Two of the three
    /// outcomes are not a removal: a tree carrying <b>uncommitted changes is kept</b> (discarding a
    /// worker's only output is worse than leaving a directory behind), and a removal <b>blocked by a
    /// still-held file</b> — a live build process holding an output, observed repeatedly on this host —
    /// is reported rather than forced. A path that is already gone is reported as removed.
    /// </summary>
    public static WorktreeTeardownResult Teardown(string repository, string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return new WorktreeTeardownResult(WorktreeTeardownOutcome.Removed, worktreePath, null);
        }

        try
        {
            // `git status --porcelain` prints one line per dirty path and nothing at all when clean.
            var (statusCode, statusOut, _) = RunGit(worktreePath, "status", "--porcelain");
            if (statusCode == 0 && !string.IsNullOrWhiteSpace(statusOut))
            {
                return new WorktreeTeardownResult(
                    WorktreeTeardownOutcome.KeptUncommitted, worktreePath,
                    "kept: the worktree carries uncommitted changes, and discarding a worker's only output " +
                    "is worse than leaving a directory behind");
            }

            var (removeCode, _, removeErr) = RunGit(repository, "worktree", "remove", worktreePath);
            return removeCode == 0
                ? new WorktreeTeardownResult(WorktreeTeardownOutcome.Removed, worktreePath, null)
                : new WorktreeTeardownResult(
                    WorktreeTeardownOutcome.RemovalBlocked, worktreePath,
                    $"removal did not complete (typically a live build process still holds a file under it): " +
                    removeErr.Trim());
        }
        catch (Exception ex) when (ex is WorktreeProvisioningException or IOException)
        {
            // The "never throws" half of the contract: a git that could not even run (missing from PATH,
            // or a transient IO fault reading its output) becomes a reported blocked removal, never an
            // exception out of a run that has already reached Terminal.
            return new WorktreeTeardownResult(
                WorktreeTeardownOutcome.RemovalBlocked, worktreePath, "removal could not run git: " + ex.Message);
        }
    }

    /// <summary>
    /// Audits a provisioned worktree after an execution exit-0 natural completion (#901).
    /// Runs <c>git status --porcelain</c> inside <paramref name="worktreePath"/>.
    /// Returns clean if no uncommitted/stray paths exist; otherwise returns dirty with a diagnostic
    /// reason naming up to 10 stray paths and total count. A git error fails closed.
    /// </summary>
    public static WorktreeAuditResult Audit(string? worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return new WorktreeAuditResult(
                IsClean: false,
                FailureReason: $"Grant audit failed: worktree directory '{worktreePath}' does not exist or is missing.");
        }

        try
        {
            var (exitCode, stdout, stderr) = RunGit(worktreePath, "status", "--porcelain");
            if (exitCode != 0)
            {
                return new WorktreeAuditResult(
                    IsClean: false,
                    FailureReason: $"Grant audit failed: git status --porcelain failed (exit code {exitCode}): {stderr.Trim()}");
            }

            var strayPaths = DescribeStrayPaths(stdout);
            if (strayPaths is null)
            {
                return new WorktreeAuditResult(IsClean: true, FailureReason: null);
            }

            return new WorktreeAuditResult(IsClean: false, FailureReason: $"Grant audit failed: worktree {strayPaths} outside declared outputs.");
        }
        catch (Exception ex)
        {
            return new WorktreeAuditResult(
                IsClean: false,
                FailureReason: $"Grant audit failed: exception running git status --porcelain ({ex.Message})");
        }
    }

    /// <summary>
    /// The bounded "carries N uncommitted/stray path(s): …" fragment <see cref="Audit"/> composes its
    /// own message from — factored out so F2 (#1593 review) can reuse the identical git-status read and
    /// formatting for a different audience (a room fact for a human, not a grant-enforcement refusal)
    /// without duplicating the bounding logic. Null when <paramref name="porcelainOutput"/> names no
    /// stray paths. Lists up to 10 paths, with the remaining count summarised as <c>(+N more)</c> —
    /// same cap <see cref="Audit"/> already used before this refactor.
    /// </summary>
    private static string? DescribeStrayPaths(string porcelainOutput)
    {
        var lines = porcelainOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        const int maxListed = 10;
        var totalCount = lines.Length;
        var strayPaths = lines
            .Select(l => l.Length > 3 ? l[3..].Trim() : l)
            .Take(maxListed)
            // N1 (#1664 re-review): bounded by COUNT above, not by LENGTH — a real repo-relative path
            // is unbounded, and ten of them past this point used to be able to blow the 500-char
            // reason budget on their own. See Outcomes.ContractValidator.ClampRenderedValue's own
            // remarks for the shared per-value clamp this reuses.
            .Select(Outcomes.ContractValidator.ClampRenderedValue)
            .ToList();

        var overflow = totalCount - strayPaths.Count;
        var pathsFormatted = string.Join(", ", strayPaths);
        return overflow > 0
            ? $"carries {totalCount} uncommitted/stray path(s): {pathsFormatted} (+{overflow} more)"
            : $"carries {totalCount} uncommitted/stray path(s): {pathsFormatted}";
    }

    /// <summary>
    /// F2 (#1593 review): a bounded, human-readable account of what survives in
    /// <paramref name="worktreePath"/> — closes #1593's second acceptance bullet (spec/baton.md §3,
    /// "Workspace evidence in the reason"), which <see cref="Audit"/> alone did not since its message
    /// is grant-enforcement phrasing and this call site is descriptive, not a refusal. Deliberately NOT
    /// the false-positive source <see cref="Outcomes.OutcomeClassifier.DescribeSubstantialWorkEvidence"/>'s
    /// own remarks reject a worktree-dirty read for: this string decides nothing, so an operator's own
    /// uncommitted edits showing up in it is noise for a human, not a behaviour-changing false positive.
    /// Combines <see cref="DescribeStrayPaths"/> with a commits-over-<paramref name="baseRef"/> count.
    /// Null when the workspace is null/missing, or genuinely has nothing to report.
    /// </summary>
    public static string? DescribeWorkspaceEvidence(string? worktreePath, string? baseRef)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return null;
        }

        try
        {
            var (statusCode, statusOut, _) = RunGit(worktreePath, "status", "--porcelain");
            var strayPaths = statusCode == 0 ? DescribeStrayPaths(statusOut) : "carries an unreadable git status";

            string? commitsOverBase = null;
            if (!string.IsNullOrWhiteSpace(baseRef) && IsWorktree(worktreePath))
            {
                var (countCode, countOut, _) = RunGit(worktreePath, "rev-list", "--count", $"{baseRef}..HEAD");
                if (countCode == 0 && int.TryParse(countOut.Trim(), out var count) && count > 0)
                {
                    commitsOverBase = $"{count} commit(s) over base";
                }
            }

            if (strayPaths is null && commitsOverBase is null)
            {
                return null;
            }

            return string.Join("; ", new[] { strayPaths, commitsOverBase }.Where(part => part is not null));
        }
        catch (Exception ex) when (ex is WorktreeProvisioningException or IOException)
        {
            return $"carries unreadable workspace state ({ex.Message})";
        }
    }

    /// <summary>
    /// Checks whether <paramref name="worktreePath"/> is untouched (no commits over base, clean tree)
    /// for #1593/#1622 (spec/baton.md §3 Producers): a dead worker that exited 0 without output may
    /// keep the retry path only if untouched; otherwise it settles Indeterminate.
    /// Fails closed (returns false) if <paramref name="worktreePath"/> is null/missing, not a git directory,
    /// git fails, or changes/commits exist.
    /// </summary>
    /// <param name="baseRef">
    /// F4/F5 (#1593 review): the ref this worktree was provisioned from (<see cref="Provision"/>'s own
    /// <c>reference</c> argument), used to run <c>git rev-list --count &lt;baseRef&gt;..HEAD</c> rather
    /// than reading the reflog's newest entry — a worker that commits and then does anything else that
    /// moves HEAD (rebase, pull, a second checkout) is invisible to a reflog-head read but not to a
    /// count against the real base. Null falls back to the reflog heuristic for a worktree (still
    /// fail-closed on git failure, unlike before this fix) and to <c>@{upstream}</c> for a non-worktree
    /// directory — the same "no ref to compare against" case F5 renamed from fail-OPEN to fail-closed:
    /// an unset upstream is the normal state of a locally-created branch, not evidence nothing happened.
    /// </param>
    public static bool IsWorkspaceUntouched(string? worktreePath, string? baseRef = null)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return false;
        }

        try
        {
            // --untracked-files=normal is explicit rather than left to the ambient default: a host or
            // repo config carrying `status.showUntrackedFiles = no` would otherwise make an
            // untracked-only work product invisible to this probe (#1720 review Finding B).
            var (statusCode, statusOut, _) = RunGit(worktreePath, "status", "--porcelain", "--untracked-files=normal");
            if (statusCode != 0 || !string.IsNullOrWhiteSpace(statusOut))
            {
                return false;
            }

            if (IsWorktree(worktreePath))
            {
                if (!string.IsNullOrWhiteSpace(baseRef))
                {
                    var (countCode, countOut, _) = RunGit(worktreePath, "rev-list", "--count", $"{baseRef}..HEAD");
                    if (countCode != 0 || !int.TryParse(countOut.Trim(), out var count) || count > 0)
                    {
                        // F5: a git failure on the commit check must read as touched, not untouched —
                        // the previous behaviour fell through to `return true` here, reporting a
                        // workspace clean when the check that would have caught a commit could not run.
                        return false;
                    }

                    return true;
                }

                // No provisioned base ref available (an older replayed journal, or a caller that never
                // threaded one) — fall back to the reflog heuristic, but fail closed on git failure
                // rather than the previous fall-through-to-true.
                var (refCode, refOut, _) = RunGit(worktreePath, "log", "-g", "-n", "1", "--format=%gs");
                if (refCode != 0)
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(refOut)
                    && refOut.Trim().StartsWith("commit", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return true;
            }

            // Not a worktree: the operator's own repository. Check upstream exists and whether there
            // are commits ahead of it.
            var (upCode, upOut, _) = RunGit(worktreePath, "rev-parse", "--abbrev-ref", "@{upstream}");
            if (upCode != 0 || string.IsNullOrWhiteSpace(upOut))
            {
                // F5: an unset @{upstream} is the ordinary state of a locally-created branch, not
                // evidence the workspace is untouched — fails closed rather than treating "nothing to
                // compare against" as "nothing happened".
                return false;
            }

            var (upstreamCountCode, upstreamCountOut, _) = RunGit(worktreePath, "rev-list", "--count", "@{upstream}..HEAD");
            if (upstreamCountCode != 0 || !int.TryParse(upstreamCountOut.Trim(), out var upstreamCount) || upstreamCount > 0)
            {
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is WorktreeProvisioningException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// #1373: reads <paramref name="workspacePath"/> for surviving work — see
    /// <see cref="WorkspaceMutationReading"/> for what the result means and why this is a fourth entry
    /// point rather than a fifth caller of <see cref="IsWorkspaceUntouched"/>.
    /// <para>
    /// Returns <see langword="null"/> — distinct from <see cref="WorkspaceMutationReading.Unmeasurable"/>
    /// — when there is no workspace to read at all: no path, or a path that does not exist. "This
    /// execution had nowhere to leave work" and "this execution's workspace could not be read" are
    /// opposite answers to the retry question, and folding them together would foreclose the retry of
    /// every timed-out execution that never had a workspace in the first place.
    /// </para>
    /// </summary>
    /// <param name="sinceRef">
    /// The commit the workspace was at when this attempt started
    /// (<c>Mutation.MutationInterface.DispatchAndRecordOutcomeAsync</c> reads it just before spawning),
    /// falling back to the worktree's provisioned base. Null drops to the same reflog heuristic
    /// <see cref="IsWorkspaceUntouched"/> falls back to, which can only answer whether a commit
    /// happened, not how many — <see cref="WorkspaceMutationReading.NewCommitCount"/> stays null there
    /// rather than being fabricated.
    /// </param>
    /// <param name="enginePlacedPaths">
    /// #1929 review HIGH — see <see cref="ChangedPathsExcludingEnginePlaced"/> for what these are, what
    /// subtracting them buys, and what it deliberately does not.
    /// </param>
    public static WorkspaceMutationReading? ReadWorkspaceMutation(
        string? workspacePath, string? sinceRef, IReadOnlyCollection<string>? enginePlacedPaths = null)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return null;
        }

        try
        {
            var (statusCode, statusOut, _) = RunGit(workspacePath, "status", "--porcelain", UntrackedFilesArgument);
            if (statusCode != 0)
            {
                return WorkspaceMutationReading.Unmeasurable;
            }

            var changedPathCount =
                ChangedPathsExcludingEnginePlaced(statusOut, workspacePath, enginePlacedPaths).Count;

            if (!string.IsNullOrWhiteSpace(sinceRef))
            {
                var (countCode, countOut, _) = RunGit(workspacePath, "rev-list", "--count", $"{sinceRef}..HEAD");
                if (countCode != 0 || !int.TryParse(countOut.Trim(), out var newCommitCount))
                {
                    // The status read succeeded, but half a reading is not a reading: a workspace whose
                    // commit count could not be established is exactly the "cannot rule work out" case.
                    return WorkspaceMutationReading.Unmeasurable;
                }

                return WorkspaceMutationReading.FromCounts(changedPathCount, newCommitCount);
            }

            var (refCode, refOut, _) = RunGit(workspacePath, "log", "-g", "-n", "1", "--format=%gs");
            if (refCode != 0)
            {
                return WorkspaceMutationReading.Unmeasurable;
            }

            var committed = !string.IsNullOrWhiteSpace(refOut)
                && refOut.Trim().StartsWith("commit", StringComparison.OrdinalIgnoreCase);
            return new WorkspaceMutationReading(true, changedPathCount, NewCommitCount: null, HasNewCommits: committed);
        }
        catch (Exception ex) when (ex is WorktreeProvisioningException or IOException)
        {
            return WorkspaceMutationReading.Unmeasurable;
        }
    }

    /// <summary>
    /// The TRI-STATE reading of the same question <see cref="IsWorkspaceUntouched"/> answers, for the
    /// #1390 work-product evidence (<c>workspaceChanged</c>/<c>hollow</c>, spec/baton.md §3): returns
    /// false when git could not answer at all — <paramref name="worktreePath"/> null/missing, not a
    /// git checkout, an unset <c>@{upstream}</c> on a plain checkout, any git failure — and otherwise
    /// sets <paramref name="changed"/> to what was measured.
    /// <para>
    /// Deliberately NOT <c>!IsWorkspaceUntouched(...)</c> (#1720 review F2). That helper fails CLOSED
    /// to false for its own consumer, the #1593 retry carve-out, where "could not measure" must read
    /// as "do not take the carve-out"; negated for this consumer the same false becomes a FABRICATED
    /// positive — the engine asserting the worker changed the tree on no evidence, and pinning
    /// <c>hollow</c> false exactly where the probe is blind. Two consumers, opposite safe defaults,
    /// so two entry points; the git reads themselves are the same ones, kept in the same order.
    /// </para>
    /// </summary>
    /// <param name="enginePlacedPaths">
    /// #1929 review HIGH — see <see cref="ChangedPathsExcludingEnginePlaced"/>.
    /// </param>
    public static bool TryReadWorkspaceChanged(
        string? worktreePath, string? baseRef, out bool changed, IReadOnlyCollection<string>? enginePlacedPaths = null)
    {
        changed = false;

        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return false;
        }

        try
        {
            // UntrackedFilesArgument, matching IsWorkspaceUntouched's intent, so an ambient
            // `status.showUntrackedFiles = no` cannot turn an untracked-only work product into a
            // measured `changed: false` here (#1720 review Finding B).
            var (statusCode, statusOut, _) = RunGit(worktreePath, "status", "--porcelain", UntrackedFilesArgument);
            if (statusCode != 0)
            {
                // Not a git checkout, or git could not run here: unmeasurable, not "unchanged".
                return false;
            }

            if (ChangedPathsExcludingEnginePlaced(statusOut, worktreePath, enginePlacedPaths).Count > 0)
            {
                // Uncommitted changes are conclusive on their own — no ref to compare against is
                // needed, so this arm measures cleanly even where the commit probes below cannot.
                changed = true;
                return true;
            }

            if (IsWorktree(worktreePath))
            {
                if (!string.IsNullOrWhiteSpace(baseRef))
                {
                    var (countCode, countOut, _) = RunGit(worktreePath, "rev-list", "--count", $"{baseRef}..HEAD");
                    if (countCode != 0 || !int.TryParse(countOut.Trim(), out var count))
                    {
                        return false;
                    }

                    changed = count > 0;
                    return true;
                }

                var (refCode, refOut, _) = RunGit(worktreePath, "log", "-g", "-n", "1", "--format=%gs");
                if (refCode != 0)
                {
                    return false;
                }

                changed = !string.IsNullOrWhiteSpace(refOut)
                    && refOut.Trim().StartsWith("commit", StringComparison.OrdinalIgnoreCase);
                return true;
            }

            var (upCode, upOut, _) = RunGit(worktreePath, "rev-parse", "--abbrev-ref", "@{upstream}");
            if (upCode != 0 || string.IsNullOrWhiteSpace(upOut))
            {
                // The ordinary state of a locally-created branch: nothing to compare HEAD against,
                // so whether it carries commits is unknown rather than known-false.
                return false;
            }

            var (upstreamCountCode, upstreamCountOut, _) = RunGit(worktreePath, "rev-list", "--count", "@{upstream}..HEAD");
            if (upstreamCountCode != 0 || !int.TryParse(upstreamCountOut.Trim(), out var upstreamCount))
            {
                return false;
            }

            changed = upstreamCount > 0;
            return true;
        }
        catch (Exception ex) when (ex is WorktreeProvisioningException or IOException)
        {
            changed = false;
            return false;
        }
    }

    /// <summary>
    /// <c>--untracked-files=all</c> for the two readers that subtract engine-placed paths
    /// (<see cref="ReadWorkspaceMutation"/>, <see cref="TryReadWorkspaceChanged"/>).
    /// </summary>
    /// <remarks>
    /// <b>Not a widening of what counts as changed, and not <c>=no</c>.</b> It was <c>=normal</c>, which
    /// collapses a wholly-untracked directory to ONE line naming the directory
    /// (<c>?? .claude/</c>) instead of enumerating the files under it — measured directly against a temp
    /// repository while fixing #1929's HIGH. An exact-path exclusion list cannot match a collapsed line,
    /// so under <c>=normal</c> the subtraction below would silently match nothing while a happy-path test
    /// still passed. <c>=all</c> enumerates, which is what makes the list usable.
    /// <para>
    /// Splitting one line into many can never flip <see cref="WorkspaceMutationReading.Mutated"/> or
    /// <see cref="TryReadWorkspaceChanged"/>'s <c>changed</c> — both are "is there anything at all",
    /// not a threshold — so the only observable difference where no engine-placed path exists is a
    /// larger <see cref="WorkspaceMutationReading.ChangedPathCount"/>, i.e. the reason text a conductor
    /// reads names files rather than a directory. The #1720 Finding B property that made <c>=normal</c>
    /// explicit (an ambient <c>status.showUntrackedFiles = no</c> must not hide an untracked-only work
    /// product) is preserved: <c>=all</c> is strictly more untracked visibility, not less.
    /// </para>
    /// </remarks>
    private const string UntrackedFilesArgument = "--untracked-files=all";

    /// <summary>
    /// The <c>git status --porcelain</c> lines of <paramref name="statusOut"/> minus the ones naming a
    /// path AER itself placed in the workspace before the worker was spawned (#1929 review HIGH).
    /// </summary>
    /// <remarks>
    /// The engine's work-product evidence (<c>workspaceChanged</c>/<c>hollow</c>) and the #1373
    /// timeout-retry guard both read this tree ABSOLUTELY, with no baseline taken at spawn. Anything
    /// AER writes there before spawning therefore reads back as the worker's own work — the engine
    /// asserting a change on evidence the engine created, which is the same fabricated positive
    /// <see cref="TryReadWorkspaceChanged"/>'s own remarks exist to prevent from the other direction.
    /// The claude adapter's canonical-skill projection (#1151) is the first writer of that shape.
    /// <para>
    /// <b>Exact paths only, and it fails toward counting.</b> A line whose path cannot be attributed with
    /// certainty — a rename/copy (<c>old -&gt; new</c>), or a path git quoted because it carries special
    /// characters — is KEPT, never dropped: over-counting costs a fabricated positive of the kind that was
    /// already possible, where over-excluding would suppress the worker's real work product, which is the
    /// evidence this whole path exists to preserve. AER's own destinations are composed from package
    /// directory names and are neither renames nor, in practice, quoted.
    /// </para>
    /// <para>
    /// <b>Both dispatch paths, on the same evidence.</b> The live path passes the paths the dispatcher
    /// just wrote; the crash-recovery path, which rebuilds <c>CoreDispatchResult</c> from a recorded
    /// exit, refills them from the journaled <c>FlowEvent.EngineFilesPlaced</c> read back through the
    /// projection (#1933). An execution with no such fact recorded subtracts nothing and so counts
    /// everything, which is the same failure direction the paragraph above chooses.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> ChangedPathsExcludingEnginePlaced(
        string statusOut, string workspacePath, IReadOnlyCollection<string>? enginePlacedPaths)
    {
        var lines = statusOut
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (enginePlacedPaths is null || enginePlacedPaths.Count == 0 || lines.Count == 0)
        {
            return lines;
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var placed = new HashSet<string>(comparer);
        foreach (var path in enginePlacedPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                placed.Add(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // An unusable entry costs its own exclusion, in the counting direction — never the read.
            }
        }

        return [.. lines.Where(line => PorcelainFullPath(workspacePath, line) is not { } full || !placed.Contains(full))];
    }

    /// <summary>
    /// The absolute path a <c>git status --porcelain</c> line names, or <see langword="null"/> when the
    /// line cannot be attributed to exactly one path — see
    /// <see cref="ChangedPathsExcludingEnginePlaced"/>'s remarks for why null means "count it".
    /// </summary>
    private static string? PorcelainFullPath(string workspacePath, string porcelainLine)
    {
        // Porcelain v1: two status characters, one space, then the path. Deliberately not trimmed --
        // a worktree-modified line is " M path", whose leading space is part of the status field.
        if (porcelainLine.Length <= 3)
        {
            return null;
        }

        var path = porcelainLine[3..];

        // A quoted path (core.quotePath) carries C-style escapes this does not decode, and a rename or
        // copy names two paths. Neither can be an AER placement; both stay counted.
        if (path.StartsWith('"') || path.Contains(" -> ", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(workspacePath, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tear down provisioned worktrees only once the run is Terminal — a Paused run must keep its
    /// tree for the resume, and this deliberately runs on the success path (not in a finally) so a
    /// crashed or cancelled run leaves the worker's tree intact too. Teardown never throws; a tree
    /// kept for uncommitted changes or a blocked removal is surfaced on the result, not swallowed.
    /// </summary>
    public static IReadOnlyList<WorktreeTeardownResult> TeardownIfTerminal(
        Domain.WorkflowStatus status, IReadOnlyList<ProvisionedWorktree> provisionedWorktrees)
    {
        if (status == Domain.WorkflowStatus.Terminal && provisionedWorktrees.Count > 0)
        {
            return
            [
                .. provisionedWorktrees
                    .Select(w => Teardown(w.Repository, w.WorktreePath))
                    .Where(r => r.Outcome != WorktreeTeardownOutcome.Removed)
            ];
        }

        return [];
    }

    private static (int ExitCode, string StdOut, string StdErr) RunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new WorktreeProvisioningException("could not start 'git' — is it installed and on PATH?");
        }
        catch (Win32Exception ex)
        {
            // Process.Start throws (rather than returning null) when the executable is not found. Map it
            // to the typed exception so Provision fails loud and clean, and Teardown's catch can turn it
            // into a reported blocked removal rather than throwing out of a completed run.
            throw new WorktreeProvisioningException(
                $"could not start 'git' — is it installed and on PATH? ({ex.Message})");
        }

        // Drain both streams concurrently before waiting: reading one to end while the other's buffer
        // fills would deadlock on a chatty git command.
        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            Task.WaitAll(stdout, stderr);
            process.WaitForExit();
            return (process.ExitCode, stdout.Result, stderr.Result);
        }
    }

    // Note: the match is commit-only (HEAD sha vs ref sha), not ref-name, so two refs pointing at the same commit match identically.
    private static bool IsRegisteredWorktreeForRef(string repository, string worktreePath, string reference)
    {
        var (refExit, refSha, _) = RunGit(repository, "rev-parse", "--verify", $"{reference}^{{commit}}");
        if (refExit != 0 || string.IsNullOrWhiteSpace(refSha))
        {
            return false;
        }
        refSha = refSha.Trim();

        var (listExit, listOut, _) = RunGit(repository, "worktree", "list", "--porcelain");
        if (listExit != 0 || string.IsNullOrWhiteSpace(listOut))
        {
            return false;
        }

        string? currentPath = null;
        string? currentHead = null;

        var lines = listOut.Split(['\r', '\n']);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("worktree ", StringComparison.Ordinal))
            {
                if (currentPath != null && currentHead != null)
                {
                    if (PathsEqual(currentPath, worktreePath) && string.Equals(currentHead, refSha, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                currentPath = trimmed["worktree ".Length..].Trim();
                currentHead = null;
            }
            else if (trimmed.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                currentHead = trimmed["HEAD ".Length..].Trim();
            }
        }

        if (currentPath != null && currentHead != null)
        {
            if (PathsEqual(currentPath, worktreePath) && string.Equals(currentHead, refSha, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool PathsEqual(string path1, string path2)
    {
        try
        {
            var full1 = NormalizeForComparison(Path.GetFullPath(path1));
            var full2 = NormalizeForComparison(Path.GetFullPath(path2));
            return string.Equals(full1, full2, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// <see cref="Path.GetFullPath(string)"/> never resolves symlinks, and on macOS the standard
    /// temp roots (<c>/var</c>, <c>/tmp</c>, <c>/etc</c>) are symlinks into <c>/private</c> — git
    /// prints the resolved spelling in <c>worktree list</c>, so a caller-supplied <c>/var/...</c>
    /// path must compare equal to git's <c>/private/var/...</c> or the idempotence check (#1023)
    /// can never recognise its own worktree there (#1103, fixing what was then a macOS CI failure;
    /// no longer exercised on any CI leg now that the matrix is Windows-only, #1405, but harmless to
    /// keep -- a Windows path never starts with <c>/private/</c>, so this is a no-op there).
    /// Accepted edge: on non-macOS, a literal <c>/private/</c>-rooted directory would compare
    /// equal to its stripped twin — a layout nothing here produces, priced below the original fix.
    /// </summary>
    internal static string NormalizeForComparison(string fullPath)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(fullPath);
        return trimmed.StartsWith("/private/", StringComparison.Ordinal)
            ? trimmed["/private".Length..]
            : trimmed;
    }
}

/// <summary>What <see cref="WorktreeProvisioner.Teardown"/> did — the three honest outcomes.</summary>
public enum WorktreeTeardownOutcome
{
    /// <summary>The worktree was removed (or was already gone).</summary>
    Removed,

    /// <summary>Uncommitted changes were present, so the worktree was kept rather than discarded.</summary>
    KeptUncommitted,

    /// <summary><c>git worktree remove</c> could not complete — typically a still-held build output.</summary>
    RemovalBlocked,
}

/// <summary>
/// The result of a <see cref="WorktreeProvisioner.Teardown"/> — surfaced, never thrown, so a teardown
/// fault cannot fail a room that already reached Terminal. <paramref name="Detail"/> is null for a
/// clean removal and carries the reason otherwise.
/// </summary>
public sealed record WorktreeTeardownResult(WorktreeTeardownOutcome Outcome, string WorktreePath, string? Detail);

/// <summary>The result of a post-run grant audit on a provisioned worktree.</summary>
public sealed record WorktreeAuditResult(bool IsClean, string? FailureReason);

/// <summary>
/// A worktree provisioned for a run, held so <c>WorktreeProvisioner.Teardown</c> can be called on it
/// once the run reaches Terminal.
/// </summary>
public sealed record ProvisionedWorktree(string Repository, string WorktreePath);
